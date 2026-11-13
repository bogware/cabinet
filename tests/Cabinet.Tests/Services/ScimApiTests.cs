using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>One API process and SCIM token, shared by the SCIM tests.</summary>
public sealed class ScimApiFixture : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-scim-").FullName;

    internal AccessControlApiTests.ApiProcess Api { get; private set; } = null!;

    internal string Token { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Api = IdentityApiTests.StartApi(_directory);
        await Api.WaitUntilReadyAsync();
        var created = await IdentityApiTests.AdminAsync(Api.Client, HttpMethod.Post, "/api/v1/scim-tokens", new { name = "Entra ID" });
        Token = created.GetProperty("token").GetString()!;
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
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
                await Task.Delay(500);
            }
        }
    }
}

/// <summary>
/// SCIM 2.0 through the real API, with requests written as Entra ID and Okta send them.
/// </summary>
public sealed class ScimApiTests : IClassFixture<ScimApiFixture>
{
    private readonly ScimApiFixture _fixture;

    public ScimApiTests(ScimApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient Client => _fixture.Api.Client;

    private async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> ScimAsync(HttpMethod method, string path, string? json = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _fixture.Token);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/scim+json");
        }

        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private async Task<string> CreateEntraUserAsync(string userName, string externalId)
    {
        var created = await ScimAsync(HttpMethod.Post, "/scim/v2/Users", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User","urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"],
             "externalId":"{{externalId}}","userName":"{{userName}}","active":true,
             "emails":[{"primary":true,"type":"work","value":"{{userName}}"}],
             "meta":{"resourceType":"User"},
             "name":{"formatted":"givenName familyName","familyName":"familyName","givenName":"givenName"},
             "roles":[]}
            """);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        return created.Body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Discovery_DescribesWhatsSupported()
    {
        var config = await ScimAsync(HttpMethod.Get, "/scim/v2/ServiceProviderConfig");
        config.Status.Should().Be(HttpStatusCode.OK);
        config.Response.Content.Headers.ContentType!.MediaType.Should().Be("application/scim+json");
        config.Body.GetProperty("patch").GetProperty("supported").GetBoolean().Should().BeTrue();
        config.Body.GetProperty("bulk").GetProperty("supported").GetBoolean().Should().BeFalse();

        var types = await ScimAsync(HttpMethod.Get, "/scim/v2/ResourceTypes");
        types.Body.GetProperty("Resources").EnumerateArray().Select(type => type.GetProperty("name").GetString()).Should().Equal("User", "Group");
        (await ScimAsync(HttpMethod.Get, "/scim/v2/ResourceTypes/User")).Body.GetProperty("endpoint").GetString().Should().Be("/Users");

        var schemas = await ScimAsync(HttpMethod.Get, "/scim/v2/Schemas");
        schemas.Body.GetProperty("totalResults").GetInt32().Should().Be(3);
        (await ScimAsync(HttpMethod.Get, "/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:User")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EntrasValidationCalls_WithUnknownNames_GetEmptyLists_Not404()
    {
        var users = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString($"userName eq \"{Guid.NewGuid()}\"")}");
        users.Status.Should().Be(HttpStatusCode.OK);
        users.Body.GetProperty("schemas")[0].GetString().Should().Be("urn:ietf:params:scim:api:messages:2.0:ListResponse");
        users.Body.GetProperty("totalResults").GetInt32().Should().Be(0);
        users.Body.GetProperty("Resources").GetArrayLength().Should().Be(0);

        var byExternalId = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString($"externalId eq \"{Guid.NewGuid()}\"")}");
        byExternalId.Body.GetProperty("totalResults").GetInt32().Should().Be(0);

        var groups = await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups?excludedAttributes=members&filter={Uri.EscapeDataString($"displayName eq \"{Guid.NewGuid()}\"")}");
        groups.Status.Should().Be(HttpStatusCode.OK);
        groups.Body.GetProperty("totalResults").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Entra_CreatesFindsAndUpdatesUsers_TheWayItSendsThem()
    {
        var userName = Unique("Test_User") + "@testuser.com";
        var externalId = Guid.NewGuid().ToString();
        var id = await CreateEntraUserAsync(userName, externalId);

        var duplicate = await ScimAsync(HttpMethod.Post, "/scim/v2/Users", $$"""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"{{userName.ToUpperInvariant()}}","active":true}""");
        duplicate.Status.Should().Be(HttpStatusCode.Conflict);
        duplicate.Body.GetProperty("scimType").GetString().Should().Be("uniqueness");
        duplicate.Body.GetProperty("schemas")[0].GetString().Should().Be("urn:ietf:params:scim:api:messages:2.0:Error");
        duplicate.Body.GetProperty("status").GetString().Should().Be("409");

        var found = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString($"userName eq \"{userName}\"")}");
        found.Body.GetProperty("totalResults").GetInt32().Should().Be(1);
        found.Body.GetProperty("Resources")[0].GetProperty("id").GetString().Should().Be(id);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString($"externalId eq \"{externalId}\"")}")).Body.GetProperty("totalResults").GetInt32().Should().Be(1);

        var patched = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
             "Operations":[
               {"op":"Replace","path":"emails[type eq \"work\"].value","value":"updatedEmail@microsoft.com"},
               {"op":"Replace","path":"name.familyName","value":"updatedFamilyName"},
               {"op":"Add","path":"urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department","value":"Finance"},
               {"op":"Add","path":"title","value":"Engineer"},
               {"op":"Add","path":"phoneNumbers[type eq \"work\"].value","value":"555-0100"}]}
            """);
        patched.Status.Should().Be(HttpStatusCode.OK, patched.Body.ToString());

        var user = (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users/{id}")).Body;
        user.GetProperty("emails")[0].GetProperty("value").GetString().Should().Be("updatedEmail@microsoft.com");
        user.GetProperty("emails")[0].GetProperty("type").GetString().Should().Be("work");
        user.GetProperty("name").GetProperty("familyName").GetString().Should().Be("updatedFamilyName");
        user.GetProperty("name").GetProperty("givenName").GetString().Should().Be("givenName");
        user.GetProperty("title").GetString().Should().Be("Engineer");
        user.GetProperty("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User").GetProperty("department").GetString().Should().Be("Finance");
        user.GetProperty("meta").GetProperty("resourceType").GetString().Should().Be("User");
    }

    [Fact]
    public async Task Entra_PatchWithoutAPath_SetsDottedAndExtensionAttributes()
    {
        var id = await CreateEntraUserAsync(Unique("nopath") + "@testuser.com", Guid.NewGuid().ToString());

        var patched = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
             "Operations":[{"op":"replace","value":{
                "displayName":"Renamed Person",
                "name.givenName":"Renamed",
                "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department":"Operations",
                "active":"True"}}]}
            """);
        patched.Status.Should().Be(HttpStatusCode.OK, patched.Body.ToString());

        var user = (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users/{id}")).Body;
        user.GetProperty("displayName").GetString().Should().Be("Renamed Person");
        user.GetProperty("name").GetProperty("givenName").GetString().Should().Be("Renamed");
        user.GetProperty("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User").GetProperty("department").GetString().Should().Be("Operations");
    }

    [Fact]
    public async Task Entra_DeactivatesWithActiveAsTheString_False_AndTheUserIsKept()
    {
        var id = await CreateEntraUserAsync(Unique("leaver") + "@testuser.com", Guid.NewGuid().ToString());

        var patched = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Replace","path":"active","value":"False"}]}
            """);

        patched.Status.Should().Be(HttpStatusCode.OK);
        patched.Body.GetProperty("active").GetBoolean().Should().BeFalse();
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users/{id}")).Body.GetProperty("active").GetBoolean().Should().BeFalse();
        var admin = await IdentityApiTests.AdminAsync(Client, HttpMethod.Get, $"/api/v1/users/{id}");
        admin.GetProperty("active").GetBoolean().Should().BeFalse();
        admin.GetProperty("source").GetString().Should().Be("Scim");

        var bad = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Replace","path":"active","value":"maybe"}]}""");
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        bad.Body.GetProperty("scimType").GetString().Should().Be("invalidValue");
    }

    [Fact]
    public async Task Entra_GroupsAndMembers_AddedAndRemovedEitherWay()
    {
        var alice = await CreateEntraUserAsync(Unique("member") + "@testuser.com", Guid.NewGuid().ToString());
        var bob = await CreateEntraUserAsync(Unique("member") + "@testuser.com", Guid.NewGuid().ToString());
        var name = Unique("Group");
        var created = await ScimAsync(HttpMethod.Post, "/scim/v2/Groups", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:Group"],"externalId":"{{Guid.NewGuid()}}","displayName":"{{name}}","meta":{"resourceType":"Group"} }
            """);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        var group = created.Body.GetProperty("id").GetString();

        var added = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Add","path":"members","value":[{"value":"{{alice}}"},{"value":"{{bob}}"}]}]}
            """);
        added.Status.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups/{group}")).Body.GetProperty("members").GetArrayLength().Should().Be(2);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users/{alice}")).Body.GetProperty("groups")[0].GetProperty("display").GetString().Should().Be(name);

        // Entra removes a member with the path "members" and the member as the value
        await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Remove","path":"members","value":[{"value":"{{alice}}"}]}]}
            """);
        var afterOne = (await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups/{group}")).Body.GetProperty("members");
        afterOne.EnumerateArray().Select(member => member.GetProperty("value").GetString()).Should().Equal(bob);

        // And with a filter in the path
        await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Remove","path":"members[value eq \"{{bob}}\"]"}]}
            """);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups/{group}")).Body.GetProperty("members").GetArrayLength().Should().Be(0);

        var withoutMembers = await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups?excludedAttributes=members&filter={Uri.EscapeDataString($"displayName eq \"{name}\"")}");
        withoutMembers.Body.GetProperty("totalResults").GetInt32().Should().Be(1);
        withoutMembers.Body.GetProperty("Resources")[0].TryGetProperty("members", out _).Should().BeFalse();

        (await ScimAsync(HttpMethod.Delete, $"/scim/v2/Groups/{group}")).Status.Should().Be(HttpStatusCode.NoContent);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Groups/{group}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Okta_CreatesWithPost_UpdatesWithPut_AndDeactivatesWithActiveFalse()
    {
        var userName = Unique("test.user") + "@okta.local";
        var created = await ScimAsync(HttpMethod.Post, "/scim/v2/Users", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"{{userName}}",
             "name":{"givenName":"Test","familyName":"User"},
             "emails":[{"primary":true,"value":"{{userName}}","type":"work"}],
             "displayName":"Test User","locale":"en-US","externalId":"00ujl29u0le5T6Aj10h7","groups":[],"password":"1mz050nq","active":true}
            """);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        var id = created.Body.GetProperty("id").GetString();
        created.Response.Headers.Location!.ToString().Should().EndWith($"/scim/v2/Users/{id}");

        var listed = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter=userName%20eq%20%22{Uri.EscapeDataString(userName)}%22&startIndex=1&count=100");
        listed.Body.GetProperty("totalResults").GetInt32().Should().Be(1);
        listed.Body.GetProperty("startIndex").GetInt32().Should().Be(1);

        var put = await ScimAsync(HttpMethod.Put, $"/scim/v2/Users/{id}", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"id":"{{id}}","userName":"{{userName}}",
             "name":{"givenName":"Another","familyName":"User"},
             "emails":[{"primary":true,"value":"{{userName}}","type":"work"}],
             "displayName":"Another User","active":true,"groups":[],"meta":{"resourceType":"User"} }
            """);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        put.Body.GetProperty("name").GetProperty("givenName").GetString().Should().Be("Another");
        put.Body.TryGetProperty("externalId", out _).Should().BeFalse("PUT replaces the attributes it's given, and that one was left out");

        var deactivated = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","value":{"active":false}}]}
            """);
        deactivated.Body.GetProperty("active").GetBoolean().Should().BeFalse();

        var reactivated = await ScimAsync(HttpMethod.Put, $"/scim/v2/Users/{id}", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"id":"{{id}}","userName":"{{userName}}","active":true}
            """);
        reactivated.Body.GetProperty("active").GetBoolean().Should().BeTrue();
        (await ScimAsync(HttpMethod.Delete, $"/scim/v2/Users/{id}")).Status.Should().Be(HttpStatusCode.NoContent);
        (await ScimAsync(HttpMethod.Get, $"/scim/v2/Users/{id}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Okta_PushesGroups_AndCabinetsRoleMappingSurvivesThem()
    {
        var user = await CreateEntraUserAsync(Unique("pushed") + "@okta.local", Guid.NewGuid().ToString());
        var created = await ScimAsync(HttpMethod.Post, "/scim/v2/Groups", """
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:Group"],"displayName":"Okta Analysts","members":[]}
            """);
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        var group = created.Body.GetProperty("id").GetString();

        // An administrator maps the provisioned group to a role in Cabinet
        await IdentityApiTests.AdminAsync(Client, HttpMethod.Put, $"/api/v1/groups/{group}", new { role = "Analyst" });

        await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"add","path":"members","value":[{"value":"{{user}}","display":"pushed"}]}]}
            """);
        (await IdentityApiTests.AdminAsync(Client, HttpMethod.Get, $"/api/v1/users/{user}")).GetProperty("effectiveRole").GetString().Should().Be("Analyst");

        var renamed = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","value":{"id":"{{group}}","displayName":"Okta Analysts Renamed"} }]}
            """);
        renamed.Status.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        var put = await ScimAsync(HttpMethod.Put, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:Group"],"id":"{{group}}","displayName":"Okta Analysts Renamed","members":[{"value":"{{user}}"}]}
            """);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());

        var mapped = await IdentityApiTests.AdminAsync(Client, HttpMethod.Get, $"/api/v1/groups/{group}");
        mapped.GetProperty("role").GetString().Should().Be("Analyst", "SCIM replaces the name and members, never the role mapping");
        mapped.GetProperty("displayName").GetString().Should().Be("Okta Analysts Renamed");
        mapped.GetProperty("source").GetString().Should().Be("Scim");

        await ScimAsync(HttpMethod.Patch, $"/scim/v2/Groups/{group}", $$"""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"remove","path":"members[value eq \"{{user}}\"]"}]}
            """);
        (await IdentityApiTests.AdminAsync(Client, HttpMethod.Get, $"/api/v1/users/{user}")).GetProperty("effectiveRole").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Paging_FollowsStartIndexAndCount()
    {
        var prefix = Unique("page");
        foreach (var n in new[] { 1, 2, 3 })
        {
            await CreateEntraUserAsync($"{prefix}-{n}@example.com", Guid.NewGuid().ToString());
        }

        var page = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString($"userName sw \"{prefix}\"")}&startIndex=2&count=1");

        page.Body.GetProperty("totalResults").GetInt32().Should().Be(3);
        page.Body.GetProperty("startIndex").GetInt32().Should().Be(2);
        page.Body.GetProperty("itemsPerPage").GetInt32().Should().Be(1);
        page.Body.GetProperty("Resources")[0].GetProperty("userName").GetString().Should().Be($"{prefix}-2@example.com");
    }

    [Fact]
    public async Task Errors_UseTheScimErrorSchema()
    {
        var missing = await ScimAsync(HttpMethod.Get, "/scim/v2/Users/01JNOSUCHUSER0000000000000");
        missing.Status.Should().Be(HttpStatusCode.NotFound);
        missing.Body.GetProperty("schemas")[0].GetString().Should().Be("urn:ietf:params:scim:api:messages:2.0:Error");

        var filter = await ScimAsync(HttpMethod.Get, $"/scim/v2/Users?filter={Uri.EscapeDataString("userName eq")}");
        filter.Status.Should().Be(HttpStatusCode.BadRequest);
        filter.Body.GetProperty("scimType").GetString().Should().Be("invalidFilter");

        var syntax = await ScimAsync(HttpMethod.Post, "/scim/v2/Users", "{not json");
        syntax.Status.Should().Be(HttpStatusCode.BadRequest);
        syntax.Body.GetProperty("scimType").GetString().Should().Be("invalidSyntax");

        var noUserName = await ScimAsync(HttpMethod.Post, "/scim/v2/Users", """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"active":true}""");
        noUserName.Body.GetProperty("scimType").GetString().Should().Be("invalidValue");

        var id = await CreateEntraUserAsync(Unique("errors") + "@example.com", Guid.NewGuid().ToString());
        var badOp = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """{"Operations":[{"op":"Move","path":"active","value":true}]}""");
        badOp.Status.Should().Be(HttpStatusCode.BadRequest);
        badOp.Body.GetProperty("scimType").GetString().Should().Be("invalidSyntax");
        var removeUserName = await ScimAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", """{"Operations":[{"op":"remove","path":"userName"}]}""");
        removeUserName.Body.GetProperty("scimType").GetString().Should().Be("mutability");
    }

    [Fact]
    public async Task ScimTokens_OnlyCallScim_AndApiKeys_NeverDo()
    {
        // No token, an API key, or a revoked token: refused
        using (var anonymous = new HttpRequestMessage(HttpMethod.Get, "/scim/v2/Users"))
        {
            (await Client.SendAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var withKey = new HttpRequestMessage(HttpMethod.Get, "/scim/v2/Users"))
        {
            withKey.Headers.Add("X-API-Key", IdentityApiTests.AdminKey);
            (await Client.SendAsync(withKey)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await ScimAsync(HttpMethod.Get, "/scim/v2/Users", token: "scim_not-a-real-token")).Status.Should().Be(HttpStatusCode.Unauthorized);

        // The SCIM token on the rest of the API
        foreach (var path in new[] { "/api/v1/roles/me", "/api/v1/users", "/api/v1/logs?count=1", "/auth/account" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.Token);
            (await Client.SendAsync(request)).StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], path);
        }

        var made = await IdentityApiTests.AdminAsync(Client, HttpMethod.Post, "/api/v1/scim-tokens", new { name = "Okta" });
        var token = made.GetProperty("token").GetString()!;
        token.Should().StartWith("scim_");
        (await ScimAsync(HttpMethod.Get, "/scim/v2/ServiceProviderConfig", token: token)).Status.Should().Be(HttpStatusCode.OK);
        var listed = await IdentityApiTests.AdminAsync(Client, HttpMethod.Get, "/api/v1/scim-tokens");
        listed.ToString().Should().NotContain(token).And.NotContain("tokenHash");

        await IdentityApiTests.AdminAsync(Client, HttpMethod.Delete, $"/api/v1/scim-tokens/{made.GetProperty("listing").GetProperty("id").GetString()}");
        (await ScimAsync(HttpMethod.Get, "/scim/v2/ServiceProviderConfig", token: token)).Status.Should().Be(HttpStatusCode.Unauthorized, "revoked at once on this instance");
    }
}
