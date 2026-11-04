using FluentAssertions;
using Seeing.Agent.Core.Configuration;
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
        names.Should().Contain("DefaultModels");
        names.Should().NotContain("DefaultModel", "旧单值默认模型已删除（决议 11/22）");
        names.Should().Contain("AgentModels");
    }

    /// <summary>DefaultModels 配置形状：能力键（小驼峰）→ 带 provider/ 前缀的模型引用。</summary>
    [Fact]
    public void DefaultModels_ShouldBeCapabilityKeyedStringDictionary()
    {
        var property = typeof(SeeingAgentOptions).GetProperty("DefaultModels");

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(Dictionary<string, string>));

        var options = new SeeingAgentOptions();
        options.DefaultModels.Should().NotBeNull("未配置时为空字典而非 null，读端据此返回 null");
        options.DefaultModels["Chat"] = "openai/gpt-4o";
        options.DefaultModels.TryGetValue("chat", out var value).Should().BeTrue("能力键大小写不敏感");
        value.Should().Be("openai/gpt-4o");
    }
}
