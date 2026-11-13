using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cabinet.API.Services.Identity;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Local accounts, the user directory and sessions through the real API process: sign-in,
/// MFA, revocation and the admin API all depend on middleware, cookies and DI working together.
/// </summary>
public sealed class IdentityApiTests : IDisposable
{
    internal const string AdminKey = "test-admin-key";
    internal const string BasicKey = "test-basic-key";
    internal const string Password = "tangerine-orbit-quietly-42";

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-identity-").FullName;

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

    internal static AccessControlApiTests.ApiProcess StartApi(string directory, Dictionary<string, string>? settings = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["Storage__Type"] = "SQLite",
            ["Storage__ConnectionString"] = $"Data Source={Path.Combine(directory, "logs.db")}",
            ["Storage__RetentionDays"] = "0",
            ["ApiKeys__Keys__0__Key"] = AdminKey,
            ["ApiKeys__Keys__0__Tier"] = "Admin",
            ["ApiKeys__Keys__1__Key"] = BasicKey,
            ["ApiKeys__Keys__1__Tier"] = "Basic",
            ["Accounts__DirectoryCacheSeconds"] = "0",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return new AccessControlApiTests.ApiProcess(directory, environment);
    }

    internal static async Task<JsonElement> AdminAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode? expect = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-API-Key", AdminKey);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (expect is { } status)
        {
            response.StatusCode.Should().Be(status, text);
        }
        else
        {
            response.IsSuccessStatusCode.Should().BeTrue($"{method} {path} returned {response.StatusCode}: {text}");
        }

        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A browser: keeps cookies, and sends the dashboard's header unless told not to.</summary>
    private sealed class Browser : IDisposable
    {
        private readonly HttpClient _client;

        public Browser(Uri baseAddress)
        {
            _client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = baseAddress };
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null, bool header = true)
        {
            using var request = new HttpRequestMessage(method, path);
            if (header)
            {
                request.Headers.Add("X-Cabinet-Request", "1");
            }

            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length == 0 || !text.TrimStart().StartsWith('{') ? default : JsonDocument.Parse(text).RootElement.Clone());
        }

        public Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body = null, bool header = true) => SendAsync(HttpMethod.Post, path, body ?? new { }, header);

        public async Task<HttpStatusCode> MeAsync() => (await SendAsync(HttpMethod.Get, "/api/v1/roles/me")).Status;

        public void Dispose() => _client.Dispose();
    }

    private static async Task<string> AddLocalUserAsync(HttpClient client, string name, string role = "Analyst")
    {
        var created = await AdminAsync(client, HttpMethod.Post, "/api/v1/users", new { userName = name, password = Password, mustChangePassword = false, role, email = $"{name}@example.com" });
        created.TryGetProperty("temporaryPassword", out var temporary).Should().BeTrue();
        temporary.ValueKind.Should().Be(JsonValueKind.Null);
        return created.GetProperty("user").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task LocalSignIn_StartsASession_ThatDeactivationAndResetsEnd()
    {
        await using var api = StartApi(_directory);
        await api.WaitUntilReadyAsync();
        var id = await AddLocalUserAsync(api.Client, "alice");
        using var browser = new Browser(api.Client.BaseAddress!);

        (await api.Client.GetStringAsync("/auth/providers")).Should().Contain("\"local\":true");
        (await browser.PostAsync("/auth/login/local", new { userName = "alice", password = Password }, header: false)).Status.Should().Be(HttpStatusCode.BadRequest, "sign-in has to come from the dashboard");
        (await browser.PostAsync("/auth/login/local", new { userName = "alice", password = "wrong password!!" })).Status.Should().Be(HttpStatusCode.Unauthorized);

        var signIn = await browser.PostAsync("/auth/login/local", new { userName = "ALICE", password = Password });
        signIn.Status.Should().Be(HttpStatusCode.OK);
        signIn.Body.GetProperty("status").GetString().Should().Be("signed-in");
        var me = await browser.SendAsync(HttpMethod.Get, "/api/v1/roles/me");
        me.Body.GetProperty("clientId").GetString().Should().Be($"local-{id}");
        me.Body.GetProperty("role").GetString().Should().Be("Analyst");
        (await browser.SendAsync(HttpMethod.Get, "/api/v1/users")).Status.Should().Be(HttpStatusCode.Forbidden, "an analyst can't manage users");

        // Deactivating ends the session on its next request
        await AdminAsync(api.Client, HttpMethod.Put, $"/api/v1/users/{id}", new { active = false, role = "Analyst" });
        (await browser.MeAsync()).Should().Be(HttpStatusCode.Unauthorized);
        (await browser.PostAsync("/auth/login/local", new { userName = "alice", password = Password })).Status.Should().Be(HttpStatusCode.Forbidden);

        // So does a password reset, and the temporary password must be changed first
        await AdminAsync(api.Client, HttpMethod.Put, $"/api/v1/users/{id}", new { active = true, role = "Analyst" });
        (await browser.PostAsync("/auth/login/local", new { userName = "alice", password = Password })).Status.Should().Be(HttpStatusCode.OK);
        (await browser.MeAsync()).Should().Be(HttpStatusCode.OK);
        var reset = await AdminAsync(api.Client, HttpMethod.Post, $"/api/v1/users/{id}/reset-password");
        (await browser.MeAsync()).Should().Be(HttpStatusCode.Unauthorized);

        var temporary = await browser.PostAsync("/auth/login/local", new { userName = "alice", password = reset.GetProperty("password").GetString() });
        temporary.Body.GetProperty("status").GetString().Should().Be("password-change-required");
        (await browser.MeAsync()).Should().Be(HttpStatusCode.Unauthorized, "a challenge isn't a session");
        var changed = await browser.PostAsync("/auth/login/password", new { challenge = temporary.Body.GetProperty("challenge").GetString(), newPassword = "a brand new long passphrase" });
        changed.Body.GetProperty("status").GetString().Should().Be("signed-in");
        (await browser.MeAsync()).Should().Be(HttpStatusCode.OK);

        // And signing out everywhere
        await AdminAsync(api.Client, HttpMethod.Post, $"/api/v1/users/{id}/sign-out");
        (await browser.MeAsync()).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mfa_IsSetUpBySelfService_AndThenAskedFor_WithChallengesThatCantBeReplayed()
    {
        await using var api = StartApi(_directory);
        await api.WaitUntilReadyAsync();
        await AddLocalUserAsync(api.Client, "bob");
        using var browser = new Browser(api.Client.BaseAddress!);
        (await browser.PostAsync("/auth/login/local", new { userName = "bob", password = Password })).Status.Should().Be(HttpStatusCode.OK);

        (await browser.PostAsync("/auth/account/mfa/enroll", header: false)).Status.Should().Be(HttpStatusCode.Unauthorized, "a cookie request that changes something needs the dashboard's header");
        var enroll = await browser.PostAsync("/auth/account/mfa/enroll");
        enroll.Body.GetProperty("otpauthUri").GetString().Should().StartWith("otpauth://totp/Cabinet:bob?secret=");
        Base32.TryDecode(enroll.Body.GetProperty("secret").GetString(), out var secret).Should().BeTrue();
        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        var confirmed = await browser.PostAsync("/auth/account/mfa/confirm", new { code = Totp.Code(secret, step) });
        confirmed.Status.Should().Be(HttpStatusCode.OK);
        confirmed.Body.GetProperty("recoveryCodes").GetArrayLength().Should().Be(10);

        using var second = new Browser(api.Client.BaseAddress!);
        var first = await second.PostAsync("/auth/login/local", new { userName = "bob", password = Password });
        first.Body.GetProperty("status").GetString().Should().Be("mfa-required");
        (await second.MeAsync()).Should().Be(HttpStatusCode.Unauthorized);
        var challenge = first.Body.GetProperty("challenge").GetString();

        var done = await second.PostAsync("/auth/login/mfa", new { challenge, code = Totp.Code(secret, step + 1) });
        done.Body.GetProperty("status").GetString().Should().Be("signed-in");
        (await second.MeAsync()).Should().Be(HttpStatusCode.OK);

        using var third = new Browser(api.Client.BaseAddress!);
        (await third.PostAsync("/auth/login/mfa", new { challenge, code = Totp.Code(secret, step + 1) })).Status.Should().Be(HttpStatusCode.Unauthorized, "the challenge was used");
        (await third.MeAsync()).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BearerTokens_ForDirectoryUsers_GetTheDirectorysRole_AndAreRefusedOnceDeactivated()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        await using var provider = await AccessControlApiTests.OidcTestProvider.StartAsync(new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "test-key" });
        await using var api = StartApi(_directory, new Dictionary<string, string>
        {
            ["Sso__Enabled"] = "true",
            ["Sso__Oidc__Authority"] = provider.Authority,
            ["Sso__Oidc__ClientId"] = "cabinet",
            ["Sso__RoleMappings__0__ClaimType"] = "groups",
            ["Sso__RoleMappings__0__ClaimValue"] = "team",
            ["Sso__RoleMappings__0__Role"] = "Viewer",
        });
        await api.WaitUntilReadyAsync();

        var dave = await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/users", new { userName = "dave@example.com", sso = true, externalId = "oid-dave" });
        var daveId = dave.GetProperty("user").GetProperty("id").GetString();
        var admins = await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/groups", new { displayName = "cabinet-admins", role = "Admin", members = new[] { daveId } });
        var erin = await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/users", new { userName = "erin@example.com", sso = true });

        async Task<(HttpStatusCode Status, string Body)> MeWithAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roles/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await api.Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // Matched by the provider's object ID; the directory's group gives the role, not the token's groups
        var asDave = await MeWithAsync(provider.CreateToken("dave-sub", "team", "cabinet", new() { ["oid"] = "oid-dave" }));
        asDave.Status.Should().Be(HttpStatusCode.OK);
        asDave.Body.Should().Contain("\"role\":\"Admin\"");

        // Matched by sign-in name; with no role in the directory, the claim mappings still apply
        var asErin = await MeWithAsync(provider.CreateToken("erin-sub", "team", "cabinet", new() { ["preferred_username"] = "Erin@Example.com" }));
        asErin.Body.Should().Contain("\"role\":\"Viewer\"");

        // A token can't name a directory user for itself
        var forged = await MeWithAsync(provider.CreateToken("mallory", "nothing", "cabinet", new() { ["cabinet.uid"] = daveId!, ["cabinet.sv"] = "1" }));
        forged.Status.Should().Be(HttpStatusCode.Forbidden);

        // Deactivated in the directory: refused, whatever the identity provider says
        await AdminAsync(api.Client, HttpMethod.Put, $"/api/v1/users/{daveId}", new { active = false });
        (await MeWithAsync(provider.CreateToken("dave-sub", "team", "cabinet", new() { ["oid"] = "oid-dave" }))).Status.Should().Be(HttpStatusCode.Unauthorized);
        await AdminAsync(api.Client, HttpMethod.Put, $"/api/v1/users/{erin.GetProperty("user").GetProperty("id").GetString()}", new { active = false });
        (await MeWithAsync(provider.CreateToken("erin-sub", "team", "cabinet", new() { ["preferred_username"] = "erin@example.com" }))).Status.Should().Be(HttpStatusCode.Unauthorized);

        // A principal stored through the roles API still wins over the directory
        await AdminAsync(api.Client, HttpMethod.Put, $"/api/v1/users/{daveId}", new { active = true });
        await AdminAsync(api.Client, HttpMethod.Put, "/api/v1/roles/oidc-dave-sub", new { role = "Viewer" });
        (await MeWithAsync(provider.CreateToken("dave-sub", "team", "cabinet", new() { ["oid"] = "oid-dave" }))).Body.Should().Contain("\"role\":\"Viewer\"");
        admins.GetProperty("id").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task AccountEndpoints_AreOnlyForLocalAccounts_AndUserManagementNeedsTheCapability()
    {
        await using var api = StartApi(_directory);
        await api.WaitUntilReadyAsync();

        using var withKey = new HttpRequestMessage(HttpMethod.Get, "/auth/account");
        withKey.Headers.Add("X-API-Key", AdminKey);
        (await api.Client.SendAsync(withKey)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var basic = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users");
        basic.Headers.Add("X-API-Key", BasicKey);
        (await api.Client.SendAsync(basic)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/users", new { userName = "carol", password = "password1234", role = "Viewer" }, HttpStatusCode.BadRequest);
        var created = await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/users", new { userName = "carol", role = "Viewer" });
        created.GetProperty("temporaryPassword").GetString().Should().HaveLength(16);
        created.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/users", new { userName = "CAROL", role = "Viewer" }, HttpStatusCode.Conflict);

        var group = await AdminAsync(api.Client, HttpMethod.Post, "/api/v1/groups", new
        {
            displayName = "ops",
            role = "Admin",
            members = new[] { created.GetProperty("user").GetProperty("id").GetString() },
        });
        group.GetProperty("members").GetArrayLength().Should().Be(1);
        var carol = await AdminAsync(api.Client, HttpMethod.Get, $"/api/v1/users/{created.GetProperty("user").GetProperty("id").GetString()}");
        carol.GetProperty("effectiveRole").GetString().Should().Be("Admin", "the group's role is higher");
        carol.GetProperty("groups")[0].GetProperty("displayName").GetString().Should().Be("ops");
        carol.ToString().Should().NotContain("passwordHash").And.NotContain("pbkdf2");
    }
}
