using Cabinet.API.Services;
using Cabinet.Core.Models;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public class FieldMaskingTests
{
    [Fact]
    public void MaskFields_RedactStrategy_ReplacesValue()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule { FieldPattern = "password", Strategy = MaskingStrategy.Redact });

        var fields = new Dictionary<string, string> { ["password"] = "secret123", ["username"] = "admin" };
        var result = service.MaskFields(fields, SystemRole.Viewer);

        result["password"].Should().Be("***REDACTED***");
        result["username"].Should().Be("admin");
    }

    [Fact]
    public void MaskFields_HashStrategy_ProducesConsistentHash()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule { FieldPattern = "email", Strategy = MaskingStrategy.Hash });

        var fields = new Dictionary<string, string> { ["email"] = "test@example.com" };
        var result1 = service.MaskFields(fields, SystemRole.Viewer);
        var result2 = service.MaskFields(new Dictionary<string, string> { ["email"] = "test@example.com" }, SystemRole.Viewer);

        result1["email"].Should().StartWith("sha256:");
        result1["email"].Should().Be(result2["email"]);
    }

    [Fact]
    public void MaskFields_ExemptRole_SkipsMasking()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule
        {
            FieldPattern = "ssn",
            Strategy = MaskingStrategy.Redact,
            ExemptRoles = new List<SystemRole> { SystemRole.Admin }
        });

        var fields = new Dictionary<string, string> { ["ssn"] = "123-45-6789" };
        var result = service.MaskFields(fields, SystemRole.Admin);

        result["ssn"].Should().Be("123-45-6789");
    }

    [Fact]
    public void MaskFields_WildcardPattern_MatchesMultipleFields()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule { FieldPattern = "secret_*", Strategy = MaskingStrategy.Redact });

        var fields = new Dictionary<string, string>
        {
            ["secret_key"] = "abc", ["secret_token"] = "xyz", ["public_data"] = "hello"
        };
        var result = service.MaskFields(fields, SystemRole.Viewer);

        result["secret_key"].Should().Be("***REDACTED***");
        result["secret_token"].Should().Be("***REDACTED***");
        result["public_data"].Should().Be("hello");
    }

    [Fact]
    public void MaskFields_RemoveStrategy_RemovesField()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule { FieldPattern = "internal_id", Strategy = MaskingStrategy.Remove });

        var fields = new Dictionary<string, string> { ["internal_id"] = "12345", ["name"] = "test" };
        var result = service.MaskFields(fields, SystemRole.Viewer);

        result.Should().NotContainKey("internal_id");
        result["name"].Should().Be("test");
    }

    [Fact]
    public void MaskFields_NoRules_ReturnsOriginal()
    {
        var service = new FieldMaskingService();

        var fields = new Dictionary<string, string> { ["data"] = "value" };
        var result = service.MaskFields(fields, SystemRole.Viewer);

        result.Should().BeEquivalentTo(fields);
    }

    [Fact]
    public void MaskFields_PartialStrategy_MasksMiddle()
    {
        var service = new FieldMaskingService();
        service.AddRule(new FieldMaskingRule { FieldPattern = "card", Strategy = MaskingStrategy.Partial });

        var fields = new Dictionary<string, string> { ["card"] = "4111111111111111" };
        var result = service.MaskFields(fields, SystemRole.Viewer);

        result["card"].Should().StartWith("41");
        result["card"].Should().EndWith("11");
        result["card"].Should().Contain("*");
    }
}
