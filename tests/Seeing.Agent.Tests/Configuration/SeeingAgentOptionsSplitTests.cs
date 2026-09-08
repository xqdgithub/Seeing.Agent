using FluentAssertions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// P5-T5：主库程序集不得再承载已迁出的子系统 Options 类型。
/// </summary>
public class SeeingAgentOptionsSplitTests
{
    private static readonly string[] ForbiddenTypeNames =
    [
        "GatewayOptions",
        "GatewayClientsOptions",
        "AcpOptions",
        "AcpBackendConfig",
        "TokenBudgetOptions",
        "ThresholdOptions",
        "SkillsConfig",
        "SkillsOptions"
    ];

    [Fact]
    public void SeeingAgentAssembly_ShouldNotContainMovedSubsystemOptionsTypes()
    {
        var assembly = typeof(SeeingAgentOptions).Assembly;
        var found = assembly.GetTypes()
            .Where(t => ForbiddenTypeNames.Contains(t.Name))
            .Select(t => t.FullName)
            .OrderBy(n => n)
            .ToList();

        found.Should().BeEmpty(
            "subsystem Options must live in owning packages, not Seeing.Agent. Found: {0}",
            string.Join(", ", found));
    }

    [Fact]
    public void SeeingAgentOptions_ShouldNotExposeMovedNestedProperties()
    {
        var names = typeof(SeeingAgentOptions).GetProperties().Select(p => p.Name).ToHashSet();

        names.Should().NotContain("Gateway");
        names.Should().NotContain("GatewayClients");
        names.Should().NotContain("Acp");
        names.Should().NotContain("TokenBudget");
        names.Should().NotContain("Skills");
        names.Should().NotContain("Shell");

        // spine / core remains
        names.Should().Contain("Permission");
        names.Should().Contain("Workspace");
        names.Should().Contain("DefaultAgent");
        names.Should().Contain("DefaultModel");
        names.Should().Contain("AgentModels");
    }
}
