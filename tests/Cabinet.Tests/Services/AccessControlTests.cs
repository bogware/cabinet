using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.API.Authentication;
using Cabinet.API.Controllers;
using Cabinet.API.Middleware;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Npgsql;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class EndpointCapabilityTests
{
    // Probes and metrics have to work without a key. "me" only tells callers about themselves.
    private static readonly HashSet<string> Exempt = new()
    {
        "RolesController.GetCurrentPrincipal",
    };

    [Fact]
    public void EveryEndpoint_RequiresACapability()
    {
        var unprotected = typeof(LogsController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract && type != typeof(HealthController))
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
                .Where(method => method.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                .Where(method => method.GetCustomAttribute<RequireCapabilityAttribute>() == null
                    && type.GetCustomAttribute<RequireCapabilityAttribute>() == null)
                .Select(method => $"{type.Name}.{method.Name}"))
            .Where(name => !Exempt.Contains(name))
            .ToList();

        unprotected.Should().BeEmpty("a new endpoint has no RBAC check unless it asks for one");
    }

    [Theory]
    [InlineData(SystemRole.SuperAdmin, Capability.UserManagement, true)]
    [InlineData(SystemRole.SuperAdmin, Capability.IndexManagement, true)]
    [InlineData(SystemRole.Admin, Capability.IndexManagement, true)]
    [InlineData(SystemRole.Admin, Capability.UserManagement, false)]
    [InlineData(SystemRole.Analyst, Capability.IndexManagement, false)]
    [InlineData(SystemRole.Viewer, Capability.Ingest, false)]
    public void EveryCapability_BelongsToSomeRole(SystemRole role, Capability capability, bool expected)
    {
        new RbacPrincipal { Id = "x", Role = role }.HasCapability(capability).Should().Be(expected);
    }

    [Fact]
    public void Grants_LimitDataCapabilities_ToTheirIndexes()
    {
        var principal = new RbacPrincipal
        {
            Id = "x",
            Role = SystemRole.Analyst,
            IndexPermissions = [new IndexPermission { IndexPattern = "team-*", Capabilities = Capability.Search }],
        };

        principal.HasCapability(Capability.Search, "team-a").Should().BeTrue();
        principal.HasCapability(Capability.Search, "other").Should().BeFalse();
        principal.HasCapability(Capability.Ingest, "team-a").Should().BeFalse();
        principal.HasCapability(Capability.SavedQueries, "other").Should().BeTrue("it isn't about an index's data");
        principal.IndexesWith(Capability.Search).Should().Equal("team-*");
        principal.HasCapabilityOnAnyIndex(Capability.Search).Should().BeTrue();
        principal.HasCapabilityOnAnyIndex(Capability.Export).Should().BeFalse();
    }

    [Fact]
    public void NoGrants_MeansTheRoleAppliesEverywhere()
    {
        var principal = new RbacPrincipal { Id = "x", Role = SystemRole.Viewer };

        principal.IndexesWith(Capability.Search).Should().BeNull();
        principal.HasCapability(Capability.Search, "anything").Should().BeTrue();
    }

    [Fact]
    public async Task RestrictedStorage_FiltersEveryRead()
    {
        var inner = new InMemoryStorage();
        var secret = new LogEvent { RawLine = "secret", Index = "secret", Timestamp = DateTime.UtcNow };
        var visible = new LogEvent { RawLine = "visible", Index = "team-a", Timestamp = DateTime.UtcNow };
        await inner.StoreBatchAsync([secret, visible]);
        var storage = new IndexRestrictedLogStorage(inner, new IndexAccessScope { ReadableIndexes = ["team-*"] });

        var read = new List<LogEvent>();
        await foreach (var logEvent in storage.QueryAsync(new QueryContext()))
        {
            read.Add(logEvent);
        }

        read.Select(e => e.RawLine).Should().Equal("visible");
        (await storage.GetCountAsync()).Should().Be(1);
        (await storage.GetByIdAsync(secret.Id)).Should().BeNull();
        (await storage.GetByIdAsync(visible.Id)).Should().NotBeNull();
        (await storage.GetStatsAsync()).TotalEvents.Should().Be(1);

        var aimedAtSecret = new List<LogEvent>();
        await foreach (var logEvent in storage.QueryAsync(new QueryContext { Index = "secret" }))
        {
            aimedAtSecret.Add(logEvent);
        }

        aimedAtSecret.Should().BeEmpty();
    }

    [Fact]
    public void ClaimMapper_UnmatchedUser_WithoutAutoProvision_GetsNoRole()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "someone")], "test");
        var config = new SsoConfiguration { Enabled = true, AutoProvision = false };

        new ClaimRoleMapper(NullLogger<ClaimRoleMapper>.Instance).MapClaims(identity, config);

        identity.FindFirst(PrincipalResolver.RoleClaim).Should().BeNull();
        identity.FindFirst("ClientId")!.Value.Should().Be("oidc-someone");
    }

    [Fact]
    public async Task ClaimMapper_MatchedGroup_ResolvesToItsRoleAndIndexes()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "someone"), new Claim("groups", "other"), new Claim("groups", "ops")], "test");
        var config = new SsoConfiguration
        {
            Enabled = true,
            AutoProvision = false,
            RoleMappings =
            [
                new ClaimRoleMapping
                {
                    ClaimType = "groups",
                    ClaimValue = "ops",
                    Role = SystemRole.Analyst,
                    IndexPermissions = [new IndexPermission { IndexPattern = "ops-*", Capabilities = Capability.Search | Capability.Ingest }],
                },
            ],
        };
        new ClaimRoleMapper(NullLogger<ClaimRoleMapper>.Instance).MapClaims(identity, config);

        var principal = await PrincipalResolver.ResolveAsync(new ClaimsPrincipal(identity), Mock.Of<IRbacService>());

        principal!.Role.Should().Be(SystemRole.Analyst);
        principal.HasCapability(Capability.Ingest, "ops-1").Should().BeTrue();
        principal.HasCapability(Capability.Search, "other").Should().BeFalse();
    }
}

public abstract class DocumentStoreTests
{
    protected abstract IDocumentStore CreateStore();

    protected virtual bool IsAvailable => true;

    private sealed record Item(string Name, int Count);

    [SkippableFact]
    public async Task Documents_RoundTrip_AndStayInTheirCollection()
    {
        Skip.IfNot(IsAvailable);
        var store = CreateStore();
        await store.InitializeAsync();
        var collection = $"test-{Guid.NewGuid():N}";

        await store.PutAsync(collection, "a", new Item("first", 1));
        await store.PutAsync(collection, "a", new Item("replaced", 2));
        await store.PutAsync(collection, "b", new Item("second", 3));
        await store.PutAsync($"{collection}-other", "a", new Item("elsewhere", 4));

        (await store.GetAsync<Item>(collection, "a")).Should().Be(new Item("replaced", 2));
        (await store.GetAsync<Item>(collection, "missing")).Should().BeNull();
        (await store.ListAsync<Item>(collection)).Should().HaveCount(2);
        (await store.DeleteAsync(collection, "a")).Should().BeTrue();
        (await store.DeleteAsync(collection, "a")).Should().BeFalse();
        (await store.ListAsync<Item>(collection)).Should().ContainSingle().Which.Name.Should().Be("second");
    }

    [SkippableFact]
    public async Task SearchJobs_KeepResultsOutOfTheListing()
    {
        Skip.IfNot(IsAvailable);
        var store = CreateStore();
        var jobs = new DocumentSearchJobStorage(store);
        await jobs.InitializeAsync();
        var owner = $"owner-{Guid.NewGuid():N}";

        var job = new SearchJob { Query = "find *", Owner = owner, ResultsJson = "[1,2,3]", AllowedIndexes = ["team-*"] };
        await jobs.CreateAsync(job);

        (await jobs.GetAllAsync(owner)).Should().ContainSingle().Which.ResultsJson.Should().BeNull();
        var read = await jobs.GetByIdAsync(job.Id);
        read!.ResultsJson.Should().Be("[1,2,3]");
        read.AllowedIndexes.Should().Equal("team-*");
        (await jobs.DeleteAsync(job.Id)).Should().BeTrue();
    }
}

public sealed class InMemoryDocumentStoreTests : DocumentStoreTests
{
    protected override IDocumentStore CreateStore() => new InMemoryDocumentStore();
}

public sealed class SqliteDocumentStoreTests : DocumentStoreTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cabinet-docs-{Guid.NewGuid():N}.db");

    protected override IDocumentStore CreateStore()
    {
        var configuration = new Cabinet.Core.Configuration.CabinetConfiguration();
        configuration.Storage.ConnectionString = $"Data Source={_path}";
        return new SqliteDocumentStore(Microsoft.Extensions.Options.Options.Create(configuration));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            File.Delete(file);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresDocumentStoreTests : DocumentStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    protected override bool IsAvailable => !string.IsNullOrEmpty(ConnectionString);

    protected override IDocumentStore CreateStore() =>
        new PostgresDocumentStore(ConnectionString!, NullLogger<PostgresDocumentStore>.Instance);
}

[Trait("Category", "Integration")]
public sealed class PostgresLeaderLeaseTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    [SkippableFact]
    public async Task OneInstanceLeads_AndAnotherTakesOverWhenItStops()
    {
        Skip.If(string.IsNullOrEmpty(ConnectionString), "Set CABINET_TEST_POSTGRES_CONNSTR to enable.");

        // Its own database, since other tests' API processes compete for the same lock
        var database = $"cabinet_lease_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var isolated = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;
        var first = new PostgresLeaderLease(isolated, NullLogger<PostgresLeaderLease>.Instance);
        var second = new PostgresLeaderLease(isolated, NullLogger<PostgresLeaderLease>.Instance);
        try
        {
            await WaitUntilAsync(() => first.IsLeader || second.IsLeader);
            await Task.Delay(TimeSpan.FromSeconds(1));
            (first.IsLeader && second.IsLeader).Should().BeFalse("a lock has one holder");

            var (leader, standby) = first.IsLeader ? (first, second) : (second, first);
            await leader.DisposeAsync();

            await WaitUntilAsync(() => standby.IsLeader);
            standby.IsLeader.Should().BeTrue();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Leader_WhoseConnectionIsKilled_StepsDown_AndLeadsAgain()
    {
        Skip.If(string.IsNullOrEmpty(ConnectionString), "Set CABINET_TEST_POSTGRES_CONNSTR to enable.");

        var database = $"cabinet_lease_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var isolated = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;
        var lease = new PostgresLeaderLease(isolated, NullLogger<PostgresLeaderLease>.Instance);
        try
        {
            await WaitUntilAsync(() => lease.IsLeader);
            lease.IsLeader.Should().BeTrue();

            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            async Task<HashSet<int>> BackendsAsync()
            {
                await using var list = new NpgsqlCommand($"SELECT pid FROM pg_stat_activity WHERE datname = '{database}'", admin);
                var pids = new HashSet<int>();
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync()) pids.Add(reader.GetInt32(0));
                return pids;
            }

            var before = await BackendsAsync();
            var killed = 0;
            foreach (var pid in before)
            {
                await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
                kill.Parameters.AddWithValue("pid", pid);
                killed += await kill.ExecuteScalarAsync() is true ? 1 : 0;
            }

            // The lock went with the connection; the loop has to reconnect and take it again,
            // which can happen faster than a poll here could see it step down
            var reconnected = false;
            var after = before;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!reconnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                after = await BackendsAsync();
                reconnected = lease.IsLeader && after.Except(before).Any();
            }

            reconnected.Should().BeTrue(
                $"the loop keeps trying after losing the connection, and leads on a new one (killed {killed}, before [{string.Join(',', before)}], after [{string.Join(',', after)}], leader {lease.IsLeader})");
        }
        finally
        {
            await lease.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}

/// <summary>
/// Access control through the real API process, since it depends on middleware, filters and DI
/// all being wired together.
/// </summary>
public sealed class AccessControlApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string EnterpriseKey = "test-enterprise-key";
    private const string ProfessionalKey = "test-professional-key";
    private const string BasicKey = "test-basic-key";
    private const string LimitedKey = "test-limited-key";

    private static readonly DateTime OlderThan = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-access-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 9)
            {
                // Windows holds files briefly after a process is killed
                Task.Delay(500).Wait();
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-API-Key", key);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string path, string key, object? body = null)
    {
        using var response = await client.SendAsync(Request(method, path, key, body));
        return response.StatusCode;
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, HttpMethod method, string path, string key, object? body = null)
    {
        using var response = await client.SendAsync(Request(method, path, key, body));
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{method} {path} returned {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task StoreAsync(HttpClient client, string index, string line) =>
        JsonAsync(client, HttpMethod.Post, "/services/collector/event", AdminKey, new { index, @event = line });

    private static async Task<List<string>> QueryLinesAsync(HttpClient client, string key, string query, bool useCache = false)
    {
        var result = await JsonAsync(client, HttpMethod.Post, "/api/v1/query/execute", key, new { query, useCache });
        return result.GetProperty("results").EnumerateArray()
            .Select(row => row.TryGetProperty("RawLine", out var line) ? line.GetString() ?? string.Empty : row.ToString())
            .ToList();
    }

    private static async Task LimitToTeamIndexesAsync(HttpClient client)
    {
        var me = await JsonAsync(client, HttpMethod.Get, "/api/v1/roles/me", LimitedKey);
        var id = me.GetProperty("clientId").GetString();
        await JsonAsync(client, HttpMethod.Put, $"/api/v1/roles/{id}", AdminKey, new
        {
            role = "Analyst",
            indexPermissions = new[] { new { indexPattern = "team-*", capabilities = new[] { "Search", "Ingest" } } },
        });
    }

    private ApiProcess StartApi(Dictionary<string, string>? settings = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["Storage__Type"] = "SQLite",
            ["Storage__ConnectionString"] = $"Data Source={Path.Combine(_directory, "logs.db")}",
            ["Storage__RetentionDays"] = "0",
            ["ApiKeys__Keys__0__Key"] = AdminKey,
            ["ApiKeys__Keys__0__Tier"] = "Admin",
            ["ApiKeys__Keys__1__Key"] = EnterpriseKey,
            ["ApiKeys__Keys__1__Tier"] = "Enterprise",
            ["ApiKeys__Keys__2__Key"] = ProfessionalKey,
            ["ApiKeys__Keys__2__Tier"] = "Professional",
            ["ApiKeys__Keys__3__Key"] = BasicKey,
            ["ApiKeys__Keys__3__Tier"] = "Basic",
            ["ApiKeys__Keys__4__Key"] = LimitedKey,
            ["ApiKeys__Keys__4__Tier"] = "Professional",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return new ApiProcess(_directory, environment);
    }

    [Fact]
    public async Task ManagementEndpoints_WorkForSomeTier_AndOnlyTheRightOnes()
    {
        await using var api = StartApi();
        await api.WaitUntilReadyAsync();
        var client = api.Client;
        var purge = $"/api/v1/logs?olderThan={OlderThan:O}";

        (await StatusAsync(client, HttpMethod.Delete, purge, AdminKey)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, HttpMethod.Delete, purge, EnterpriseKey)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, HttpMethod.Delete, purge, ProfessionalKey)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Get, "/api/v1/roles", AdminKey)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, HttpMethod.Get, "/api/v1/roles", EnterpriseKey)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Post, "/api/v1/query/stream", BasicKey, new { query = "find *" })).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Put, "/api/v1/retention/main", ProfessionalKey, new { maxAgeDays = 1 })).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Delete, "/api/v1/knowledge/lookups/x", BasicKey)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IndexGrants_LimitEveryWayOfReadingAndWriting()
    {
        await using var api = StartApi();
        await api.WaitUntilReadyAsync();
        var client = api.Client;
        await LimitToTeamIndexesAsync(client);

        await StoreAsync(client, "team-a", "visible line");
        await StoreAsync(client, "secret", "classified line");

        // The admin runs it first, so it would be in the cache for a leak to find
        (await QueryLinesAsync(client, AdminKey, "find *", useCache: true)).Should().HaveCount(2);

        (await QueryLinesAsync(client, LimitedKey, "find *", useCache: true)).Should().Equal("visible line");
        (await QueryLinesAsync(client, LimitedKey, "find \"classified\"")).Should().BeEmpty();
        (await QueryLinesAsync(client, LimitedKey, "find \"visible\" | append [find \"classified\"]")).Should().Equal("visible line");
        (await QueryLinesAsync(client, LimitedKey, "| multisearch [search \"classified\"] [search \"visible\"]")).Should().Equal("visible line");

        var adminView = await JsonAsync(client, HttpMethod.Get, "/api/v1/logs?count=10", AdminKey);
        var secretId = adminView.EnumerateArray().Single(row => row.GetProperty("rawLine").GetString() == "classified line").GetProperty("id").GetString();
        (await StatusAsync(client, HttpMethod.Get, $"/api/v1/logs/{secretId}", LimitedKey)).Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(client, HttpMethod.Get, $"/api/v1/logs/{secretId}", AdminKey)).Should().Be(HttpStatusCode.OK);
        var limitedView = await JsonAsync(client, HttpMethod.Get, "/api/v1/logs?count=10", LimitedKey);
        limitedView.GetArrayLength().Should().Be(1);

        (await StatusAsync(client, HttpMethod.Post, "/services/collector/event", LimitedKey, new { index = "team-b", @event = "ok" })).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, HttpMethod.Post, "/services/collector/event", LimitedKey, new { index = "secret", @event = "no" })).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Delete, "/api/v1/indexes/secret", LimitedKey)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, HttpMethod.Post, "/api/v1/scheduled-searches", LimitedKey, new { name = "x", query = "find *", cronExpression = "* * * * *" })).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SearchJobs_AreVisibleOnlyToTheirOwner_AndRunWithTheirAccess()
    {
        await using var api = StartApi();
        await api.WaitUntilReadyAsync();
        var client = api.Client;
        await LimitToTeamIndexesAsync(client);
        await StoreAsync(client, "team-a", "visible line");
        await StoreAsync(client, "secret", "classified line");

        var job = await JsonAsync(client, HttpMethod.Post, "/api/v1/query/jobs", LimitedKey, new { query = "find *", maxResults = 100, ttlMinutes = 5 });
        var id = job.GetProperty("id").GetString();

        (await StatusAsync(client, HttpMethod.Get, $"/api/v1/query/jobs/{id}", BasicKey)).Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(client, HttpMethod.Get, $"/api/v1/query/jobs/{id}/results", BasicKey)).Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(client, HttpMethod.Delete, $"/api/v1/query/jobs/{id}", BasicKey)).Should().Be(HttpStatusCode.NotFound);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? status = null;
        while (status != "Completed" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            var read = await JsonAsync(client, HttpMethod.Get, $"/api/v1/query/jobs/{id}", LimitedKey);
            status = read.GetProperty("status").GetString();
        }

        status.Should().Be("Completed");
        var results = await JsonAsync(client, HttpMethod.Get, $"/api/v1/query/jobs/{id}/results", LimitedKey);
        results.ToString().Should().Contain("visible line").And.NotContain("classified line");
    }

    [Fact]
    public async Task RolesAndAlertState_SurviveARestart()
    {
        string id;
        await using (var api = StartApi())
        {
            await api.WaitUntilReadyAsync();
            await LimitToTeamIndexesAsync(api.Client);
            var me = await JsonAsync(api.Client, HttpMethod.Get, "/api/v1/roles/me", LimitedKey);
            id = me.GetProperty("clientId").GetString()!;
        }

        await using var restarted = StartApi();
        await restarted.WaitUntilReadyAsync();

        var after = await JsonAsync(restarted.Client, HttpMethod.Get, "/api/v1/roles/me", LimitedKey);
        after.GetProperty("clientId").GetString().Should().Be(id, "a key's ID comes from the key");
        after.GetProperty("indexPermissions").GetArrayLength().Should().Be(1);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TwoInstances_ShareRolesDashboardsAndJobs()
    {
        var connection = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");
        Skip.If(string.IsNullOrEmpty(connection), "Set CABINET_TEST_POSTGRES_CONNSTR to enable.");

        var settings = new Dictionary<string, string>
        {
            ["Storage__Type"] = "PostgreSQL",
            ["Storage__ConnectionString"] = connection!,
        };
        await using var first = StartApi(settings);
        await using var second = StartApi(settings);
        await first.WaitUntilReadyAsync();
        await second.WaitUntilReadyAsync();

        await LimitToTeamIndexesAsync(first.Client);
        var viaSecond = await JsonAsync(second.Client, HttpMethod.Get, "/api/v1/roles/me", LimitedKey);
        viaSecond.GetProperty("indexPermissions").GetArrayLength().Should().Be(1);

        var job = await JsonAsync(first.Client, HttpMethod.Post, "/api/v1/query/jobs", AdminKey, new { query = "find * | head 1", maxResults = 1, ttlMinutes = 5 });
        var id = job.GetProperty("id").GetString();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? status = null;
        while (status is not ("Completed" or "Failed") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var read = await JsonAsync(second.Client, HttpMethod.Get, $"/api/v1/query/jobs/{id}", AdminKey);
            status = read.GetProperty("status").GetString();
        }

        status.Should().Be("Completed", "one of the instances leads, and either can report it");

        var me = await JsonAsync(first.Client, HttpMethod.Get, "/api/v1/roles/me", LimitedKey);
        await StatusAsync(first.Client, HttpMethod.Delete, $"/api/v1/roles/{me.GetProperty("clientId").GetString()}", AdminKey);
    }

    [Fact]
    public async Task OidcTokens_GetTheirMappedRole_AndOthersGetNothing()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        await using var provider = await OidcTestProvider.StartAsync(key);

        await using var api = StartApi(new Dictionary<string, string>
        {
            ["Sso__Enabled"] = "true",
            ["Sso__Provider"] = "Oidc",
            ["Sso__AutoProvision"] = "false",
            ["Sso__Oidc__Authority"] = provider.Authority,
            ["Sso__Oidc__ClientId"] = "cabinet",
            ["Sso__RoleMappings__0__ClaimType"] = "groups",
            ["Sso__RoleMappings__0__ClaimValue"] = "ops",
            ["Sso__RoleMappings__0__Role"] = "Admin",
            ["Sso__RoleMappings__1__ClaimType"] = "groups",
            ["Sso__RoleMappings__1__ClaimValue"] = "team",
            ["Sso__RoleMappings__1__Role"] = "Analyst",
            ["Sso__RoleMappings__1__IndexPermissions__0__IndexPattern"] = "team-*",
            ["Sso__RoleMappings__1__IndexPermissions__0__Capabilities"] = "Search",
        });
        await api.WaitUntilReadyAsync();
        var client = api.Client;
        await StoreAsync(client, "team-a", "visible line");
        await StoreAsync(client, "secret", "classified line");

        async Task<HttpResponseMessage> QueryWithAsync(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query/execute")
            {
                Content = JsonContent.Create(new { query = "find *" }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }

        (await QueryWithAsync(provider.CreateToken("alice", "ops", "cabinet"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var limited = await QueryWithAsync(provider.CreateToken("bob", "team", "cabinet"));
        limited.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await limited.Content.ReadAsStringAsync();
        body.Should().Contain("visible line").And.NotContain("classified line");

        (await QueryWithAsync(provider.CreateToken("carol", "nothing", "cabinet"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await QueryWithAsync(provider.CreateToken("dave", "ops", "another-app"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await QueryWithAsync("not.a.token")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DashboardSignIn_WithOidc_RedirectsToTheProvider_WithPkce()
    {
        using var rsa = RSA.Create(2048);
        await using var provider = await OidcTestProvider.StartAsync(new RsaSecurityKey(rsa) { KeyId = "test-key" });
        await using var api = StartApi(new Dictionary<string, string>
        {
            ["Sso__Enabled"] = "true",
            ["Sso__Oidc__Authority"] = provider.Authority,
            ["Sso__Oidc__ClientId"] = "cabinet",
            ["Sso__Oidc__ClientSecret"] = "test-secret",
        });
        await api.WaitUntilReadyAsync();
        using var noRedirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = api.Client.BaseAddress };

        var providers = await (await noRedirects.GetAsync("/auth/providers")).Content.ReadAsStringAsync();
        providers.Should().Contain("\"oidc\":true").And.Contain("\"signedIn\":false");

        using var login = await noRedirects.GetAsync("/auth/login/oidc?returnUrl=https://evil.example/");
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = login.Headers.Location!.ToString();
        location.Should().StartWith($"{provider.Authority}/authorize").And.Contain("code_challenge=").And.Contain("response_type=code");

        // A session cookie someone made up doesn't sign anyone in
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roles/me");
        forged.Headers.Add("Cookie", "cabinet_session=forged");
        (await noRedirects.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RulesAsCode_SigmaAndCoverage_WorkThroughTheApi()
    {
        await using var api = StartApi();
        await api.WaitUntilReadyAsync();
        var client = api.Client;

        // What cabinet rules push does: a new ID adds the rule
        var put = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/rules/repo-ssh", AdminKey, new { name = "SSH guessing", pattern = "Failed password", severity = "Error", technique = "T1110.001" }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(client, HttpMethod.Get, "/api/v1/rules/repo-ssh", AdminKey)).GetProperty("name").GetString().Should().Be("SSH guessing");

        using var sigma = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rules/sigma?save=true")
        {
            Content = new StringContent("title: Whoami\ntags: [attack.t1033]\ndetection:\n  selection:\n    CommandLine|contains: whoami\n  condition: selection\nlevel: low\n", Encoding.UTF8, "text/plain"),
        };
        sigma.Headers.Add("X-API-Key", AdminKey);
        var imported = await client.SendAsync(sigma);
        (await imported.Content.ReadAsStringAsync()).Should().Contain("\"saved\":true");

        var coverage = await JsonAsync(client, HttpMethod.Get, "/api/v1/rules/coverage", AdminKey);
        coverage.ToString().Should().Contain("repo-ssh").And.Contain("T1033");
    }

    [Fact]
    public async Task DashboardSignIn_WithoutSso_OffersOnlyApiKeys()
    {
        await using var api = StartApi();
        await api.WaitUntilReadyAsync();

        var providers = await api.Client.GetStringAsync("/auth/providers");

        providers.Should().Contain("\"oidc\":false").And.Contain("\"ldap\":false").And.Contain("\"saml\":false");
        (await api.Client.GetAsync("/auth/login/oidc")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.Client.PostAsJsonAsync("/auth/login/ldap", new { username = "a", password = "b" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A running API process.</summary>
    private sealed class ApiProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        public ApiProcess(string directory, Dictionary<string, string> environment)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(typeof(LogsController).Assembly.Location);
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            startInfo.Environment["RateLimiting__PreAuth__MaxRequests"] = "100000";
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }

            _process = Process.Start(startInfo)!;
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
        }

        public HttpClient Client { get; }

        public async Task WaitUntilReadyAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    var output = await _stdout;
                    throw new InvalidOperationException(
                        $"The API exited with {_process.ExitCode}: {output[Math.Max(0, output.Length - 2000)..]}");
                }

                try
                {
                    using var response = await Client.GetAsync("/health/ready");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("The API didn't become ready");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await Task.WhenAll(_stdout, _stderr);
            _process.Dispose();
        }
    }

    /// <summary>The two documents a JWT validator fetches from an identity provider, and tokens to match.</summary>
    private sealed class OidcTestProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;
        private readonly CancellationTokenSource _stopping = new();
        private readonly RsaSecurityKey _key;

        private OidcTestProvider(TcpListener listener, RsaSecurityKey key, string authority)
        {
            _listener = listener;
            _key = key;
            Authority = authority;
            _serving = ServeAsync(_stopping.Token);
        }

        public string Authority { get; }

        public static Task<OidcTestProvider> StartAsync(RsaSecurityKey key)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new OidcTestProvider(listener, key, $"http://localhost:{port}"));
        }

        public string CreateToken(string subject, string group, string audience) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["groups"] = group },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping
                return;
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                    // Skip the headers
                }

                var parameters = _key.Rsa.ExportParameters(false);
                var body = requestLine.Contains("jwks", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new
                    {
                        keys = new[]
                        {
                            new
                            {
                                kty = "RSA",
                                use = "sig",
                                alg = "RS256",
                                kid = _key.KeyId,
                                n = Base64UrlEncoder.Encode(parameters.Modulus),
                                e = Base64UrlEncoder.Encode(parameters.Exponent),
                            },
                        },
                    })
                    : JsonSerializer.Serialize(new
                    {
                        issuer = Authority,
                        jwks_uri = $"{Authority}/jwks",
                        authorization_endpoint = $"{Authority}/authorize",
                        token_endpoint = $"{Authority}/token",
                        response_types_supported = new[] { "code" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" },
                    });
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _serving;
            _stopping.Dispose();
        }
    }
}
