using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// SCIM 2.0 (RFC 7643 and 7644), so an identity provider such as Entra ID or Okta can create,
/// update, deactivate and remove users and groups in Cabinet's directory. Only a SCIM token can
/// call it. Deactivating a user ends their sessions; every change is audited.
/// </summary>
[ApiController]
[Route("scim/v2")]
[Authorize(AuthenticationSchemes = ScimAuthenticationHandler.SchemeName)]
[RequireScimToken]
[ConfiguredRequestSizeLimit(RequestSizeLimitKind.Scim)]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ScimController : ControllerBase
{
    private const string ContentType = "application/scim+json";
    private const int MaxBodyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly UserDirectory _directory;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ScimController> _logger;

    public ScimController(UserDirectory directory, IAuditLogger audit, ILogger<ScimController> logger)
    {
        _directory = directory;
        _audit = audit;
        _logger = logger;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/scim/v2";

    private string Caller => User.FindFirst("ClientId")?.Value ?? "scim";

    // ---------- Discovery ----------

    /// <summary>What this SCIM server supports.</summary>
    [HttpGet("ServiceProviderConfig")]
    public IActionResult ServiceProviderConfig() => Scim(200, new JsonObject
    {
        ["schemas"] = new JsonArray(ScimSchemas.ServiceProviderConfig),
        ["documentationUri"] = "https://github.com/bogware/cabinet/blob/main/docs/DEPLOYMENT.md",
        ["patch"] = new JsonObject { ["supported"] = true },
        ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
        ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = _directory.Options.ScimMaxResults },
        ["changePassword"] = new JsonObject { ["supported"] = false },
        ["sort"] = new JsonObject { ["supported"] = false },
        ["etag"] = new JsonObject { ["supported"] = false },
        ["authenticationSchemes"] = new JsonArray(new JsonObject
        {
            ["type"] = "oauthbearertoken",
            ["name"] = "OAuth Bearer Token",
            ["description"] = "A SCIM token made on Cabinet's Users page",
            ["primary"] = true,
        }),
        ["meta"] = new JsonObject { ["resourceType"] = "ServiceProviderConfig", ["location"] = $"{BaseUrl}/ServiceProviderConfig" },
    });

    /// <summary>The resource types: users and groups.</summary>
    [HttpGet("ResourceTypes")]
    public IActionResult ResourceTypes() => Scim(200, ListOf([UserType(), GroupType()], 1));

    /// <summary>One resource type.</summary>
    [HttpGet("ResourceTypes/{name}")]
    public IActionResult ResourceType(string name) => name.ToLowerInvariant() switch
    {
        "user" => Scim(200, UserType()),
        "group" => Scim(200, GroupType()),
        _ => Error(404, null, $"No resource type {name}"),
    };

    /// <summary>The schemas of the attributes Cabinet keeps.</summary>
    [HttpGet("Schemas")]
    public IActionResult Schemas() => Scim(200, ListOf([UserSchema(), EnterpriseSchema(), GroupSchema()], 1));

    /// <summary>One schema.</summary>
    [HttpGet("Schemas/{id}")]
    public IActionResult Schema(string id) => id switch
    {
        ScimSchemas.User => Scim(200, UserSchema()),
        ScimSchemas.EnterpriseUser => Scim(200, EnterpriseSchema()),
        ScimSchemas.Group => Scim(200, GroupSchema()),
        _ => Error(404, null, $"No schema {id}"),
    };

    // ---------- Users ----------

    /// <summary>Users, filtered and a page at a time. A filter nothing matches gives an empty list, never 404.</summary>
    [HttpGet("Users")]
    public async Task<IActionResult> ListUsers([FromQuery] string? filter, [FromQuery] int? startIndex, [FromQuery] int? count, CancellationToken cancellationToken)
    {
        return await HandleAsync(async () =>
        {
            IEnumerable<DirectoryUser> users;
            var comparisons = string.IsNullOrWhiteSpace(filter) ? [] : ScimFilter.Parse(filter);
            if (comparisons is [{ Operator: "eq", Value: { } name } single] && single.Attribute.Equals("userName", StringComparison.OrdinalIgnoreCase))
            {
                // What Entra and Okta ask before creating someone: a lookup, not a scan
                users = await _directory.FindByUserNameAsync(name, cancellationToken) is { } found ? [found] : [];
            }
            else if (comparisons is [{ Operator: "eq", Value: { } externalId } byExternal] && byExternal.Attribute.Equals("externalId", StringComparison.OrdinalIgnoreCase))
            {
                users = await _directory.FindByExternalIdAsync(externalId, cancellationToken) is { } found ? [found] : [];
            }
            else
            {
                users = (await _directory.ListAsync(cancellationToken)).Where(user => comparisons.All(comparison => UserMatches(user, comparison)));
            }

            var groups = await _directory.ListGroupsAsync(cancellationToken);
            var all = users.ToList();
            var (start, page) = Page(all, startIndex, count);
            return Scim(200, ListOf(page.Select(user => (JsonNode)ScimMapper.UserJson(user, groups, BaseUrl)).ToList(), start, all.Count));
        });
    }

    /// <summary>One user.</summary>
    [HttpGet("Users/{id}")]
    public Task<IActionResult> GetUser(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var user = await _directory.GetFreshAsync(id, cancellationToken) ?? throw new ScimException(404, null, $"No user {id}");
        return Scim(200, ScimMapper.UserJson(user, await _directory.ListGroupsAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Provisions a user. A user name that's taken is a 409 uniqueness error.</summary>
    [HttpPost("Users")]
    public Task<IActionResult> CreateUser(CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var user = new DirectoryUser { Id = string.Empty, UserName = string.Empty, Source = UserSource.Scim };
        ScimMapper.ReplaceUser(user, body);
        if (ScimMapper.Property(body, "active") is { } active)
        {
            user.Active = ScimMapper.Bool(active, "active");
        }

        user = await SaveNewUserAsync(user, cancellationToken);
        await AuditAsync(AuditAction.Create, "ScimUser", user.Id, $"SCIM provisioned user '{AuditName.Sanitize(user.UserName)}' (active {user.Active})", cancellationToken);
        Response.Headers.Location = $"{BaseUrl}/Users/{user.Id}";
        return Scim(201, ScimMapper.UserJson(user, await _directory.ListGroupsAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Replaces a user's attributes, as Okta updates users. <c>active: false</c> deactivates them.</summary>
    [HttpPut("Users/{id}")]
    public Task<IActionResult> ReplaceUser(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var user = await UpdateUserAsync(id, user =>
        {
            ScimMapper.ReplaceUser(user, body);
            if (ScimMapper.Property(body, "active") is { } active)
            {
                user.Active = ScimMapper.Bool(active, "active");
            }
        }, cancellationToken);
        await AuditAsync(AuditAction.Update, "ScimUser", id, $"SCIM replaced user '{AuditName.Sanitize(user.UserName)}' (active {user.Active})", cancellationToken);
        return Scim(200, ScimMapper.UserJson(user, await _directory.ListGroupsAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Changes some of a user's attributes, as Entra ID updates and deactivates users.</summary>
    [HttpPatch("Users/{id}")]
    public Task<IActionResult> PatchUser(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var user = await UpdateUserAsync(id, user => ScimMapper.PatchUser(user, body), cancellationToken);
        await AuditAsync(AuditAction.Update, "ScimUser", id, $"SCIM changed user '{AuditName.Sanitize(user.UserName)}' (active {user.Active})", cancellationToken);
        return Scim(200, ScimMapper.UserJson(user, await _directory.ListGroupsAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Removes a user. Deactivating (active false) keeps them instead.</summary>
    [HttpDelete("Users/{id}")]
    public Task<IActionResult> DeleteUser(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var user = await _directory.GetFreshAsync(id, cancellationToken);
        if (user == null || !await _directory.DeleteAsync(id, cancellationToken))
        {
            throw new ScimException(404, null, $"No user {id}");
        }

        await AuditAsync(AuditAction.Delete, "ScimUser", id, $"SCIM removed user '{AuditName.Sanitize(user.UserName)}'", cancellationToken);
        return NoContent();
    });

    private async Task<DirectoryUser> SaveNewUserAsync(DirectoryUser user, CancellationToken cancellationToken)
    {
        try
        {
            return await _directory.CreateAsync(user, cancellationToken);
        }
        catch (DirectoryConflictException ex)
        {
            throw new ScimException(409, "uniqueness", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new ScimException(400, "invalidValue", ex.Message);
        }
    }

    private async Task<DirectoryUser> UpdateUserAsync(string id, Action<DirectoryUser> change, CancellationToken cancellationToken)
    {
        try
        {
            return await _directory.UpdateAsync(id, change, cancellationToken) ?? throw new ScimException(404, null, $"No user {id}");
        }
        catch (DirectoryConflictException ex)
        {
            throw new ScimException(409, "uniqueness", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new ScimException(400, "invalidValue", ex.Message);
        }
    }

    private static bool UserMatches(DirectoryUser user, ScimComparison comparison) => comparison.Attribute.ToLowerInvariant() switch
    {
        "username" => ScimFilter.Matches(comparison, user.UserName),
        "externalid" => ScimFilter.Matches(comparison, user.ExternalId, caseExact: true),
        "id" => ScimFilter.Matches(comparison, user.Id, caseExact: true),
        "displayname" => ScimFilter.Matches(comparison, user.DisplayName),
        "active" => ScimFilter.Matches(comparison, user.Active ? "true" : "false"),
        "emails" or "emails.value" => comparison.Operator == "pr" ? user.Emails.Count > 0 : user.Emails.Any(email => ScimFilter.Matches(comparison, email.Value)),
        "name.givenname" => ScimFilter.Matches(comparison, user.GivenName),
        "name.familyname" => ScimFilter.Matches(comparison, user.FamilyName),
        _ => throw new ScimException(400, "invalidFilter", $"Can't filter on {comparison.Attribute}"),
    };

    // ---------- Groups ----------

    /// <summary>Groups, filtered and a page at a time. <c>excludedAttributes=members</c> leaves members out.</summary>
    [HttpGet("Groups")]
    public Task<IActionResult> ListGroups([FromQuery] string? filter, [FromQuery] int? startIndex, [FromQuery] int? count, [FromQuery] string? excludedAttributes, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var comparisons = string.IsNullOrWhiteSpace(filter) ? [] : ScimFilter.Parse(filter);
        var all = (await _directory.ListGroupsAsync(cancellationToken)).Where(group => comparisons.All(comparison => GroupMatches(group, comparison))).ToList();
        var (start, page) = Page(all, startIndex, count);
        var names = await UserNamesAsync(cancellationToken);
        var withMembers = !Excludes(excludedAttributes, "members");
        return Scim(200, ListOf(page.Select(group => (JsonNode)ScimMapper.GroupJson(group, names, BaseUrl, withMembers)).ToList(), start, all.Count));
    });

    /// <summary>One group.</summary>
    [HttpGet("Groups/{id}")]
    public Task<IActionResult> GetGroup(string id, [FromQuery] string? excludedAttributes, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var group = await _directory.GetGroupAsync(id, cancellationToken) ?? throw new ScimException(404, null, $"No group {id}");
        return Scim(200, ScimMapper.GroupJson(group, await UserNamesAsync(cancellationToken), BaseUrl, !Excludes(excludedAttributes, "members")));
    });

    /// <summary>Provisions a group. Its role is given in Cabinet, on the Users page.</summary>
    [HttpPost("Groups")]
    public Task<IActionResult> CreateGroup(CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var group = new DirectoryGroup { Id = string.Empty, DisplayName = string.Empty, Source = UserSource.Scim };
        ScimMapper.ReplaceGroup(group, body);
        try
        {
            group = await _directory.CreateGroupAsync(group, cancellationToken);
        }
        catch (DirectoryConflictException ex)
        {
            throw new ScimException(409, "uniqueness", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new ScimException(400, "invalidValue", ex.Message);
        }

        await AuditAsync(AuditAction.Create, "ScimGroup", group.Id, $"SCIM provisioned group '{AuditName.Sanitize(group.DisplayName)}' with {group.Members.Count} members", cancellationToken);
        Response.Headers.Location = $"{BaseUrl}/Groups/{group.Id}";
        return Scim(201, ScimMapper.GroupJson(group, await UserNamesAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Replaces a group's name and members. The role it gives stays.</summary>
    [HttpPut("Groups/{id}")]
    public Task<IActionResult> ReplaceGroup(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var group = await UpdateGroupAsync(id, group => ScimMapper.ReplaceGroup(group, body), cancellationToken);
        await AuditAsync(AuditAction.Update, "ScimGroup", id, $"SCIM replaced group '{AuditName.Sanitize(group.DisplayName)}': {group.Members.Count} members", cancellationToken);
        return Scim(200, ScimMapper.GroupJson(group, await UserNamesAsync(cancellationToken), BaseUrl));
    });

    /// <summary>Changes a group: its name, or members added and removed.</summary>
    [HttpPatch("Groups/{id}")]
    public Task<IActionResult> PatchGroup(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var body = await ReadBodyAsync(cancellationToken);
        var group = await UpdateGroupAsync(id, group => ScimMapper.PatchGroup(group, body), cancellationToken);
        await AuditAsync(AuditAction.Update, "ScimGroup", id, $"SCIM changed group '{AuditName.Sanitize(group.DisplayName)}': {group.Members.Count} members", cancellationToken);

        // Entra and Okta accept 204 for a PATCH; the group's full member list could be long
        return NoContent();
    });

    /// <summary>Removes a group.</summary>
    [HttpDelete("Groups/{id}")]
    public Task<IActionResult> DeleteGroup(string id, CancellationToken cancellationToken) => HandleAsync(async () =>
    {
        var group = await _directory.GetGroupAsync(id, cancellationToken);
        if (group == null || !await _directory.DeleteGroupAsync(id, cancellationToken))
        {
            throw new ScimException(404, null, $"No group {id}");
        }

        await AuditAsync(AuditAction.Delete, "ScimGroup", id, $"SCIM removed group '{AuditName.Sanitize(group.DisplayName)}'", cancellationToken);
        return NoContent();
    });

    private async Task<DirectoryGroup> UpdateGroupAsync(string id, Action<DirectoryGroup> change, CancellationToken cancellationToken)
    {
        try
        {
            return await _directory.UpdateGroupAsync(id, change, cancellationToken) ?? throw new ScimException(404, null, $"No group {id}");
        }
        catch (DirectoryConflictException ex)
        {
            throw new ScimException(409, "uniqueness", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new ScimException(400, "invalidValue", ex.Message);
        }
    }

    private static bool GroupMatches(DirectoryGroup group, ScimComparison comparison) => comparison.Attribute.ToLowerInvariant() switch
    {
        "displayname" => ScimFilter.Matches(comparison, group.DisplayName),
        "externalid" => ScimFilter.Matches(comparison, group.ExternalId, caseExact: true),
        "id" => ScimFilter.Matches(comparison, group.Id, caseExact: true),
        "members" or "members.value" => group.Members.Any(member => ScimFilter.Matches(comparison, member, caseExact: true)),
        _ => throw new ScimException(400, "invalidFilter", $"Can't filter on {comparison.Attribute}"),
    };

    private async Task<IReadOnlyDictionary<string, string>> UserNamesAsync(CancellationToken cancellationToken) =>
        (await _directory.ListAsync(cancellationToken)).ToDictionary(user => user.Id, user => user.UserName, StringComparer.Ordinal);

    private static bool Excludes(string? excludedAttributes, string attribute) =>
        (excludedAttributes ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(excluded => excluded.Equals(attribute, StringComparison.OrdinalIgnoreCase) || excluded.Equals($"{ScimSchemas.Group}:{attribute}", StringComparison.OrdinalIgnoreCase));

    // ---------- Plumbing ----------

    private (int Start, List<T> Page) Page<T>(List<T> all, int? startIndex, int? count)
    {
        var start = Math.Max(1, startIndex ?? 1);
        var take = Math.Clamp(count ?? _directory.Options.ScimMaxResults, 0, _directory.Options.ScimMaxResults);
        return (start, all.Skip(start - 1).Take(take).ToList());
    }

    private static JsonObject ListOf(List<JsonNode> resources, int startIndex, int? total = null) => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.ListResponse),
        ["totalResults"] = total ?? resources.Count,
        ["startIndex"] = startIndex,
        ["itemsPerPage"] = resources.Count,
        ["Resources"] = new JsonArray(resources.ToArray()),
    };

    private async Task<JsonObject> ReadBodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (limited.Length + read > MaxBodyBytes)
                {
                    throw new ScimException(413, "tooMany", "The request is too large");
                }

                limited.Write(buffer, 0, read);
            }

            limited.Position = 0;
            return JsonNode.Parse(limited, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                ?? throw new ScimException(400, "invalidSyntax", "The body must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new ScimException(400, "invalidSyntax", $"The body isn't valid JSON: {ex.Message}");
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new ScimException(413, "tooMany", "The request is too large");
        }
    }

    private async Task<IActionResult> HandleAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ScimException ex)
        {
            if (ex.Status >= 500)
            {
                _logger.LogWarning(ex, "SCIM request failed");
            }

            return Error(ex.Status, ex.ScimType, ex.Message);
        }
    }

    private ContentResult Scim(int status, JsonNode body) => new()
    {
        StatusCode = status,
        ContentType = ContentType,
        Content = body.ToJsonString(Compact),
    };

    private ContentResult Error(int status, string? scimType, string detail)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimSchemas.Error),
            ["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["detail"] = detail,
        };
        if (scimType != null)
        {
            body["scimType"] = scimType;
        }

        return Scim(status, body);
    }

    private Task AuditAsync(AuditAction action, string resourceType, string id, string description, CancellationToken cancellationToken) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = Caller,
            Action = action,
            ResourceType = resourceType,
            ResourceId = id,
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, cancellationToken);

    // ---------- Schema documents ----------

    private JsonObject UserType() => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.ResourceType),
        ["id"] = "User",
        ["name"] = "User",
        ["endpoint"] = "/Users",
        ["schema"] = ScimSchemas.User,
        ["schemaExtensions"] = new JsonArray(new JsonObject { ["schema"] = ScimSchemas.EnterpriseUser, ["required"] = false }),
        ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = $"{BaseUrl}/ResourceTypes/User" },
    };

    private JsonObject GroupType() => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.ResourceType),
        ["id"] = "Group",
        ["name"] = "Group",
        ["endpoint"] = "/Groups",
        ["schema"] = ScimSchemas.Group,
        ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = $"{BaseUrl}/ResourceTypes/Group" },
    };

    private static JsonObject Attribute(string name, string type = "string", bool required = false, bool caseExact = false, string uniqueness = "none", bool multiValued = false, string mutability = "readWrite", JsonArray? subAttributes = null)
    {
        var attribute = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["multiValued"] = multiValued,
            ["required"] = required,
            ["caseExact"] = caseExact,
            ["mutability"] = mutability,
            ["returned"] = "default",
            ["uniqueness"] = uniqueness,
        };
        if (subAttributes != null)
        {
            attribute["subAttributes"] = subAttributes;
        }

        return attribute;
    }

    private JsonObject UserSchema() => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.Schema),
        ["id"] = ScimSchemas.User,
        ["name"] = "User",
        ["description"] = "A user in Cabinet's directory",
        ["attributes"] = new JsonArray(
            Attribute("userName", required: true, uniqueness: "server"),
            Attribute("name", "complex", subAttributes: new JsonArray(Attribute("givenName"), Attribute("familyName"), Attribute("formatted", mutability: "readOnly"))),
            Attribute("displayName"),
            Attribute("title"),
            Attribute("active", "boolean"),
            Attribute("emails", "complex", multiValued: true, subAttributes: new JsonArray(Attribute("value"), Attribute("type"), Attribute("primary", "boolean"))),
            Attribute("groups", "complex", multiValued: true, mutability: "readOnly", subAttributes: new JsonArray(Attribute("value", mutability: "readOnly"), Attribute("display", mutability: "readOnly")))),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUrl}/Schemas/{ScimSchemas.User}" },
    };

    private JsonObject EnterpriseSchema() => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.Schema),
        ["id"] = ScimSchemas.EnterpriseUser,
        ["name"] = "EnterpriseUser",
        ["description"] = "The enterprise extension's attributes Cabinet keeps",
        ["attributes"] = new JsonArray(Attribute("department")),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUrl}/Schemas/{ScimSchemas.EnterpriseUser}" },
    };

    private JsonObject GroupSchema() => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.Schema),
        ["id"] = ScimSchemas.Group,
        ["name"] = "Group",
        ["description"] = "A group in Cabinet's directory. The role it gives is set in Cabinet.",
        ["attributes"] = new JsonArray(
            Attribute("displayName", required: true),
            Attribute("members", "complex", multiValued: true, subAttributes: new JsonArray(Attribute("value", caseExact: true, mutability: "immutable"), Attribute("display", mutability: "readOnly")))),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{BaseUrl}/Schemas/{ScimSchemas.Group}" },
    };
}
