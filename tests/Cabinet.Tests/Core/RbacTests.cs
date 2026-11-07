using Cabinet.Core.Models;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Core;

public class RbacTests
{
    [Fact]
    public void IndexPermission_WildcardStar_MatchesAllIndexes()
    {
        var perm = new IndexPermission { IndexPattern = "*", Capabilities = Capability.Search };

        perm.MatchesIndex("main").Should().BeTrue();
        perm.MatchesIndex("gameserver-prod").Should().BeTrue();
    }

    [Fact]
    public void IndexPermission_PrefixWildcard_MatchesPrefix()
    {
        var perm = new IndexPermission { IndexPattern = "gameserver-*", Capabilities = Capability.Search };

        perm.MatchesIndex("gameserver-prod").Should().BeTrue();
        perm.MatchesIndex("gameserver-dev").Should().BeTrue();
        perm.MatchesIndex("webserver-prod").Should().BeFalse();
    }

    [Fact]
    public void IndexPermission_ExactMatch_CaseInsensitive()
    {
        var perm = new IndexPermission { IndexPattern = "main", Capabilities = Capability.Search };

        perm.MatchesIndex("main").Should().BeTrue();
        perm.MatchesIndex("MAIN").Should().BeTrue();
        perm.MatchesIndex("other").Should().BeFalse();
    }

    [Fact]
    public void RbacPrincipal_SuperAdmin_HasAllCapabilities()
    {
        var principal = new RbacPrincipal { Id = "test", Role = SystemRole.SuperAdmin };

        principal.HasCapability(Capability.Search, "any-index").Should().BeTrue();
        principal.HasCapability(Capability.UserManagement, "any-index").Should().BeTrue();
        principal.HasCapability(Capability.All, "any-index").Should().BeTrue();
    }

    [Fact]
    public void RbacPrincipal_Viewer_HasOnlySearchAndHealth()
    {
        var principal = new RbacPrincipal { Id = "test", Role = SystemRole.Viewer };

        principal.HasCapability(Capability.Search).Should().BeTrue();
        principal.HasCapability(Capability.SystemHealth).Should().BeTrue();
        principal.HasCapability(Capability.Ingest).Should().BeFalse();
        principal.HasCapability(Capability.UserManagement).Should().BeFalse();
    }

    [Fact]
    public void RbacPrincipal_WithIndexPermissions_GrantsAdditionalCapabilities()
    {
        var principal = new RbacPrincipal
        {
            Id = "test",
            Role = SystemRole.Viewer,
            IndexPermissions = new List<IndexPermission>
            {
                new() { IndexPattern = "special-*", Capabilities = Capability.Ingest | Capability.Export }
            }
        };

        // Default capabilities for viewer
        principal.HasCapability(Capability.Search, "main").Should().BeTrue();
        principal.HasCapability(Capability.Ingest, "main").Should().BeFalse();

        // Additional capabilities for special indexes
        principal.HasCapability(Capability.Ingest, "special-prod").Should().BeTrue();
        principal.HasCapability(Capability.Export, "special-prod").Should().BeTrue();
    }

    [Theory]
    [InlineData(SystemRole.Viewer, Capability.Search | Capability.SystemHealth)]
    [InlineData(SystemRole.Analyst, Capability.Search | Capability.Ingest | Capability.SavedQueries | Capability.Detection | Capability.SystemHealth)]
    [InlineData(SystemRole.SuperAdmin, Capability.All)]
    public void GetRoleCapabilities_ReturnsExpectedForRole(SystemRole role, Capability expected)
    {
        var result = RbacPrincipal.GetRoleCapabilities(role);
        result.Should().Be(expected);
    }

    // ─── SavedQuery serialization ───────────────────────────

    [Fact]
    public void SavedQuery_UsageCount_SerializesAsProperty()
    {
        var sq = new SavedQuery { Name = "test", Query = "find *" };
        sq.IncrementUsageCount();
        sq.IncrementUsageCount();
        sq.UsageCount.Should().Be(2);

        // Verify it serializes correctly via System.Text.Json
        var json = System.Text.Json.JsonSerializer.Serialize(sq);
        json.Should().Contain("\"UsageCount\":2");
    }

    [Fact]
    public void SavedQuery_IncrementUsageCount_IsThreadSafe()
    {
        var sq = new SavedQuery { Name = "test", Query = "find *" };
        const int iterations = 1000;

        Parallel.For(0, iterations, _ => sq.IncrementUsageCount());

        sq.UsageCount.Should().Be(iterations);
    }
}
