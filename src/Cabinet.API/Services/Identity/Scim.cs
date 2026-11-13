using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cabinet.Core.Models;

namespace Cabinet.API.Services.Identity;

/// <summary>A SCIM error (RFC 7644 section 3.12), with its HTTP status and scimType.</summary>
public sealed class ScimException : Exception
{
    public ScimException(int status, string? scimType, string detail)
        : base(detail)
    {
        Status = status;
        ScimType = scimType;
    }

    public int Status { get; }

    public string? ScimType { get; }
}

/// <summary>SCIM schema URNs.</summary>
public static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string EnterpriseUser = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

/// <summary>One comparison in a SCIM filter, such as <c>userName eq "alice"</c>.</summary>
public sealed record ScimComparison(string Attribute, string Operator, string? Value);

/// <summary>
/// The SCIM filters identity providers send: comparisons with eq, ne, co, sw, ew or pr, joined by
/// and. Entra and Okta only ever send <c>attribute eq "value"</c>.
/// </summary>
public static class ScimFilter
{
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "co", "sw", "ew", "pr" };

    /// <summary>Parses a filter. Throws <see cref="ScimException"/> with invalidFilter for anything else.</summary>
    public static IReadOnlyList<ScimComparison> Parse(string filter)
    {
        var comparisons = new List<ScimComparison>();
        var position = 0;
        while (true)
        {
            SkipSpaces(filter, ref position);
            var attribute = ReadWord(filter, ref position);
            SkipSpaces(filter, ref position);
            var op = ReadWord(filter, ref position);
            if (attribute.Length == 0 || !Operators.Contains(op))
            {
                throw new ScimException(400, "invalidFilter", $"Filters are comparisons such as userName eq \"alice\", joined by and. Couldn't read '{filter}'.");
            }

            string? value = null;
            if (!op.Equals("pr", StringComparison.OrdinalIgnoreCase))
            {
                SkipSpaces(filter, ref position);
                value = ReadValue(filter, ref position);
            }

            comparisons.Add(new ScimComparison(attribute, op.ToLowerInvariant(), value));
            SkipSpaces(filter, ref position);
            if (position >= filter.Length)
            {
                return comparisons;
            }

            if (!ReadWord(filter, ref position).Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                throw new ScimException(400, "invalidFilter", "Only and can join comparisons in a filter.");
            }
        }
    }

    /// <summary>Whether a value matches a comparison. Case-insensitive unless <paramref name="caseExact"/>.</summary>
    public static bool Matches(ScimComparison comparison, string? actual, bool caseExact = false)
    {
        var comparisonType = caseExact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wanted = comparison.Value ?? string.Empty;
        return comparison.Operator switch
        {
            "pr" => !string.IsNullOrEmpty(actual),
            "eq" => actual != null && string.Equals(actual, wanted, comparisonType),
            "ne" => !string.Equals(actual, wanted, comparisonType),
            "co" => actual != null && actual.Contains(wanted, comparisonType),
            "sw" => actual != null && actual.StartsWith(wanted, comparisonType),
            "ew" => actual != null && actual.EndsWith(wanted, comparisonType),
            _ => false,
        };
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static string ReadWord(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '"')
        {
            position++;
        }

        return text[start..position];
    }

    private static string? ReadValue(string text, ref int position)
    {
        if (position < text.Length && text[position] == '"')
        {
            position++;
            var value = new StringBuilder();
            while (position < text.Length && text[position] != '"')
            {
                if (text[position] == '\\' && position + 1 < text.Length)
                {
                    position++;
                }

                value.Append(text[position]);
                position++;
            }

            if (position >= text.Length)
            {
                throw new ScimException(400, "invalidFilter", "A quoted value in the filter isn't closed.");
            }

            position++;
            return value.ToString();
        }

        var word = ReadWord(text, ref position);
        if (word.Length == 0)
        {
            throw new ScimException(400, "invalidFilter", "A comparison in the filter has no value.");
        }

        return word.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : word;
    }
}

/// <summary>
/// Users and groups in SCIM's shape, and SCIM's changes applied to them, including how Entra ID
/// and Okta write them: PATCH op names in any case, booleans as strings, a PATCH without a path
/// whose value holds attributes (dotted, or under the enterprise extension's URN), emails
/// addressed by type, and members removed by filter or by value.
/// </summary>
public static class ScimMapper
{
    private const string CorePrefix = ScimSchemas.User + ":";
    private const string GroupPrefix = ScimSchemas.Group + ":";

    // ---------- Users ----------

    /// <summary>A user as SCIM shows them.</summary>
    public static JsonObject UserJson(DirectoryUser user, IEnumerable<DirectoryGroup> groups, string baseUrl)
    {
        var json = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimSchemas.User, ScimSchemas.EnterpriseUser),
            ["id"] = user.Id,
            ["userName"] = user.UserName,
            ["active"] = user.Active,
        };
        if (user.ExternalId != null)
        {
            json["externalId"] = user.ExternalId;
        }

        if (user.GivenName != null || user.FamilyName != null)
        {
            var name = new JsonObject();
            if (user.GivenName != null)
            {
                name["givenName"] = user.GivenName;
            }

            if (user.FamilyName != null)
            {
                name["familyName"] = user.FamilyName;
            }

            name["formatted"] = string.Join(' ', new[] { user.GivenName, user.FamilyName }.Where(part => !string.IsNullOrEmpty(part)));
            json["name"] = name;
        }

        if (user.DisplayName != null)
        {
            json["displayName"] = user.DisplayName;
        }

        if (user.Title != null)
        {
            json["title"] = user.Title;
        }

        if (user.Emails.Count > 0)
        {
            json["emails"] = new JsonArray(user.Emails.Select(email =>
            {
                var item = new JsonObject { ["value"] = email.Value, ["primary"] = email.Primary };
                if (email.Type != null)
                {
                    item["type"] = email.Type;
                }

                return (JsonNode)item;
            }).ToArray());
        }

        var memberOf = groups.Where(group => group.Members.Contains(user.Id)).ToList();
        if (memberOf.Count > 0)
        {
            json["groups"] = new JsonArray(memberOf.Select(group => (JsonNode)new JsonObject
            {
                ["value"] = group.Id,
                ["display"] = group.DisplayName,
                ["$ref"] = $"{baseUrl}/Groups/{group.Id}",
            }).ToArray());
        }

        if (user.Department != null)
        {
            json[ScimSchemas.EnterpriseUser] = new JsonObject { ["department"] = user.Department };
        }

        json["meta"] = Meta("User", user.CreatedAt, user.UpdatedAt, $"{baseUrl}/Users/{user.Id}");
        return json;
    }

    /// <summary>
    /// Replaces a user's SCIM attributes with those in a POST or PUT body. What Cabinet keeps that
    /// SCIM doesn't describe, such as the role, password and MFA, stays.
    /// </summary>
    public static void ReplaceUser(DirectoryUser user, JsonObject body)
    {
        var userName = Text(Property(body, "userName"));
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ScimException(400, "invalidValue", "userName is required");
        }

        user.UserName = userName;
        user.DisplayName = null;
        user.GivenName = null;
        user.FamilyName = null;
        user.Title = null;
        user.Department = null;
        user.Emails = new List<DirectoryEmail>();
        user.ExternalId = null;
        foreach (var (key, value) in body)
        {
            if (key.Equals("schemas", StringComparison.OrdinalIgnoreCase) || key.Equals("id", StringComparison.OrdinalIgnoreCase)
                || key.Equals("meta", StringComparison.OrdinalIgnoreCase) || key.Equals("groups", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyUser(user, key, value, "replace");
        }
    }

    /// <summary>Applies a PATCH request's operations to a user.</summary>
    public static void PatchUser(DirectoryUser user, JsonObject body)
    {
        foreach (var (op, path, value) in Operations(body))
        {
            if (path == null)
            {
                if (value is not JsonObject attributes)
                {
                    throw new ScimException(400, "invalidValue", "A PATCH operation without a path needs an object of attributes as its value");
                }

                foreach (var (key, attributeValue) in attributes)
                {
                    ApplyUser(user, key, attributeValue, op);
                }
            }
            else
            {
                ApplyUser(user, path, value, op);
            }
        }
    }

    private static void ApplyUser(DirectoryUser user, string path, JsonNode? value, string op)
    {
        path = path.Trim();
        if (path.StartsWith(CorePrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[CorePrefix.Length..];
        }

        if (path.StartsWith(ScimSchemas.EnterpriseUser, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[ScimSchemas.EnterpriseUser.Length..].TrimStart(':');
            if (rest.Length == 0)
            {
                if (value is JsonObject extension)
                {
                    foreach (var (key, extensionValue) in extension)
                    {
                        ApplyEnterprise(user, key, extensionValue, op);
                    }
                }

                return;
            }

            ApplyEnterprise(user, rest, value, op);
            return;
        }

        var remove = op == "remove";
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("emails", StringComparison.Ordinal))
        {
            ApplyEmails(user, path, value, op);
            return;
        }

        switch (lower)
        {
            case "username":
                if (remove || string.IsNullOrWhiteSpace(Text(value)))
                {
                    throw new ScimException(400, "mutability", "userName can't be removed");
                }

                user.UserName = Text(value)!;
                break;
            case "displayname":
                user.DisplayName = remove ? null : Text(value);
                break;
            case "title":
                user.Title = remove ? null : Text(value);
                break;
            case "externalid":
                user.ExternalId = remove ? null : Text(value);
                break;
            case "active":
                user.Active = !remove && Bool(value, "active");
                break;
            case "name":
                if (remove)
                {
                    user.GivenName = user.FamilyName = null;
                }
                else if (value is JsonObject name)
                {
                    if (op == "replace")
                    {
                        user.GivenName = user.FamilyName = null;
                    }

                    foreach (var (key, part) in name)
                    {
                        ApplyUser(user, $"name.{key}", part, op);
                    }
                }

                break;
            case "name.givenname":
                user.GivenName = remove ? null : Text(value);
                break;
            case "name.familyname":
                user.FamilyName = remove ? null : Text(value);
                break;
            default:
                // Attributes Cabinet doesn't keep, such as phone numbers, addresses and the
                // password, are accepted and ignored, as RFC 7644 allows
                break;
        }
    }

    private static void ApplyEnterprise(DirectoryUser user, string attribute, JsonNode? value, string op)
    {
        if (attribute.Equals("department", StringComparison.OrdinalIgnoreCase))
        {
            user.Department = op == "remove" ? null : Text(value);
        }
    }

    private static void ApplyEmails(DirectoryUser user, string path, JsonNode? value, string op)
    {
        var (filter, subAttribute) = SplitFilteredPath(path, "emails");
        if (filter == null)
        {
            if (op == "remove")
            {
                user.Emails = new List<DirectoryEmail>();
                return;
            }

            var given = (value is JsonArray array ? array.ToList() : [value])
                .OfType<JsonObject>()
                .Select(item => new DirectoryEmail
                {
                    Value = Text(Property(item, "value")) ?? string.Empty,
                    Type = Text(Property(item, "type")),
                    Primary = Property(item, "primary") is { } primary && Bool(primary, "primary"),
                })
                .Where(email => email.Value.Length > 0)
                .ToList();
            if (op == "replace")
            {
                user.Emails = given;
            }
            else
            {
                foreach (var email in given)
                {
                    user.Emails.RemoveAll(existing => existing.Value.Equals(email.Value, StringComparison.OrdinalIgnoreCase)
                        || (email.Type != null && string.Equals(existing.Type, email.Type, StringComparison.OrdinalIgnoreCase)));
                    user.Emails.Add(email);
                }
            }

            NormalizePrimary(user.Emails);
            return;
        }

        // emails[type eq "work"].value, or emails[primary eq true]
        var matches = user.Emails.Where(email => filter.Attribute.ToLowerInvariant() switch
        {
            "type" => ScimFilter.Matches(filter, email.Type),
            "value" => ScimFilter.Matches(filter, email.Value),
            "primary" => ScimFilter.Matches(filter, email.Primary ? "true" : "false"),
            _ => false,
        }).ToList();
        if (op == "remove")
        {
            if (subAttribute == null || subAttribute.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                user.Emails.RemoveAll(matches.Contains);
            }

            NormalizePrimary(user.Emails);
            return;
        }

        var text = value is JsonObject item ? Text(Property(item, "value")) : Text(value);
        if (subAttribute != null && subAttribute.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var email in matches)
            {
                email.Primary = Bool(value, "primary");
            }

            NormalizePrimary(user.Emails);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (matches.Count > 0)
        {
            matches[0].Value = text;
        }
        else
        {
            user.Emails.Add(new DirectoryEmail
            {
                Value = text,
                Type = filter.Attribute.Equals("type", StringComparison.OrdinalIgnoreCase) ? filter.Value : null,
                Primary = filter.Attribute.Equals("primary", StringComparison.OrdinalIgnoreCase) || user.Emails.Count == 0,
            });
        }

        NormalizePrimary(user.Emails);
    }

    private static void NormalizePrimary(List<DirectoryEmail> emails)
    {
        var primary = emails.FirstOrDefault(email => email.Primary);
        foreach (var email in emails)
        {
            email.Primary = email == primary;
        }
    }

    // ---------- Groups ----------

    /// <summary>A group as SCIM shows it.</summary>
    public static JsonObject GroupJson(DirectoryGroup group, IReadOnlyDictionary<string, string> userNames, string baseUrl, bool withMembers = true)
    {
        var json = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimSchemas.Group),
            ["id"] = group.Id,
            ["displayName"] = group.DisplayName,
        };
        if (group.ExternalId != null)
        {
            json["externalId"] = group.ExternalId;
        }

        if (withMembers)
        {
            json["members"] = new JsonArray(group.Members.Select(member => (JsonNode)new JsonObject
            {
                ["value"] = member,
                ["display"] = userNames.TryGetValue(member, out var name) ? name : null,
                ["$ref"] = $"{baseUrl}/Users/{member}",
                ["type"] = "User",
            }).ToArray());
        }

        json["meta"] = Meta("Group", group.CreatedAt, group.UpdatedAt, $"{baseUrl}/Groups/{group.Id}");
        return json;
    }

    /// <summary>Replaces a group's SCIM attributes with those in a POST or PUT body. Its role mapping stays.</summary>
    public static void ReplaceGroup(DirectoryGroup group, JsonObject body)
    {
        var displayName = Text(Property(body, "displayName"));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ScimException(400, "invalidValue", "displayName is required");
        }

        group.DisplayName = displayName;
        group.ExternalId = Text(Property(body, "externalId"));
        group.Members = MemberIds(Property(body, "members")).ToList();
    }

    /// <summary>Applies a PATCH request's operations to a group.</summary>
    public static void PatchGroup(DirectoryGroup group, JsonObject body)
    {
        foreach (var (op, path, value) in Operations(body))
        {
            if (path == null)
            {
                if (value is not JsonObject attributes)
                {
                    throw new ScimException(400, "invalidValue", "A PATCH operation without a path needs an object of attributes as its value");
                }

                foreach (var (key, attributeValue) in attributes)
                {
                    ApplyGroup(group, key, attributeValue, op);
                }
            }
            else
            {
                ApplyGroup(group, path, value, op);
            }
        }
    }

    private static void ApplyGroup(DirectoryGroup group, string path, JsonNode? value, string op)
    {
        path = path.Trim();
        if (path.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[GroupPrefix.Length..];
        }

        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("members", StringComparison.Ordinal))
        {
            var (filter, _) = SplitFilteredPath(path, "members");
            if (filter != null)
            {
                // members[value eq "id"], as Entra and Okta remove one member
                if (op == "remove")
                {
                    group.Members.RemoveAll(member => filter.Attribute.Equals("value", StringComparison.OrdinalIgnoreCase) && ScimFilter.Matches(filter, member, caseExact: true));
                    return;
                }

                throw new ScimException(400, "invalidPath", "Only remove can use a filter on members");
            }

            var given = MemberIds(value).ToList();
            switch (op)
            {
                case "add":
                    group.Members.AddRange(given.Where(member => !group.Members.Contains(member)));
                    break;
                case "replace":
                    group.Members = given;
                    break;
                case "remove":
                    // Entra removes members with the path "members" and the members as the value
                    if (value == null)
                    {
                        group.Members.Clear();
                    }
                    else
                    {
                        group.Members.RemoveAll(given.Contains);
                    }

                    break;
            }

            return;
        }

        switch (lower)
        {
            case "displayname":
                if (op == "remove" || string.IsNullOrWhiteSpace(Text(value)))
                {
                    throw new ScimException(400, "mutability", "displayName can't be removed");
                }

                group.DisplayName = Text(value)!;
                break;
            case "externalid":
                group.ExternalId = op == "remove" ? null : Text(value);
                break;
            default:
                // id, meta and attributes Cabinet doesn't keep
                break;
        }
    }

    private static IEnumerable<string> MemberIds(JsonNode? value) =>
        (value is JsonArray array ? array.ToList() : value == null ? [] : [value])
            .Select(item => item is JsonObject member ? Text(Property(member, "value")) : Text(item))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal);

    // ---------- Shared ----------

    /// <summary>A PATCH request's operations, with op names in lower case whatever case they came in.</summary>
    private static IEnumerable<(string Op, string? Path, JsonNode? Value)> Operations(JsonObject body)
    {
        if (Property(body, "Operations") is not JsonArray operations || operations.Count == 0)
        {
            throw new ScimException(400, "invalidSyntax", "A PATCH request needs Operations");
        }

        if (operations.Count > 1000)
        {
            throw new ScimException(400, "tooMany", "At most 1,000 operations in one PATCH request");
        }

        foreach (var node in operations)
        {
            if (node is not JsonObject operation)
            {
                throw new ScimException(400, "invalidSyntax", "Each operation is an object");
            }

            var op = Text(Property(operation, "op"))?.ToLowerInvariant();
            if (op is not ("add" or "replace" or "remove"))
            {
                throw new ScimException(400, "invalidSyntax", "op must be add, replace or remove");
            }

            var path = Text(Property(operation, "path"));
            if (op == "remove" && string.IsNullOrWhiteSpace(path))
            {
                throw new ScimException(400, "noTarget", "A remove operation needs a path");
            }

            yield return (op, string.IsNullOrWhiteSpace(path) ? null : path, Property(operation, "value"));
        }
    }

    /// <summary>Splits <c>emails[type eq "work"].value</c> into its filter and sub-attribute.</summary>
    private static (ScimComparison? Filter, string? SubAttribute) SplitFilteredPath(string path, string attribute)
    {
        var rest = path[attribute.Length..];
        if (rest.Length == 0)
        {
            return (null, null);
        }

        if (rest.StartsWith('.'))
        {
            return (null, rest[1..]);
        }

        var close = rest.LastIndexOf(']');
        if (!rest.StartsWith('[') || close < 0)
        {
            throw new ScimException(400, "invalidPath", $"Couldn't read the path '{path}'");
        }

        var comparisons = ScimFilter.Parse(rest[1..close]);
        if (comparisons.Count != 1)
        {
            throw new ScimException(400, "invalidPath", "Paths can filter on one comparison");
        }

        var after = rest[(close + 1)..];
        return (comparisons[0], after.StartsWith('.') ? after[1..] : null);
    }

    /// <summary>A property, found without regard to case as SCIM attribute names are.</summary>
    public static JsonNode? Property(JsonObject json, string name)
    {
        foreach (var (key, value) in json)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Text from a string, number or boolean; null for anything else.</summary>
    public static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? "true" : "false";
        }

        return value.ToJsonString();
    }

    /// <summary>A boolean, written as true or false, or as Entra writes it, "True" or "False".</summary>
    public static bool Bool(JsonNode? node, string attribute)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<string>(out var text) && bool.TryParse(text.Trim(), out var parsed))
            {
                return parsed;
            }
        }

        throw new ScimException(400, "invalidValue", $"{attribute} must be true or false");
    }

    private static JsonObject Meta(string resourceType, DateTime created, DateTime updated, string location) => new()
    {
        ["resourceType"] = resourceType,
        ["created"] = DateTime.SpecifyKind(created, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        ["lastModified"] = DateTime.SpecifyKind(updated, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        ["location"] = location,
        ["version"] = $"W/\"{updated.Ticks.ToString(CultureInfo.InvariantCulture)}\"",
    };
}
