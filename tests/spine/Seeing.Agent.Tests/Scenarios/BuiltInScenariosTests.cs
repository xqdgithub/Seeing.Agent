using FluentAssertions;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Scenarios;
using Xunit;

namespace Seeing.Agent.Tests.Scenarios;

public class BuiltInScenariosTests
{
    [Fact]
    public void Only_Full_Remains_And_It_Selects_Every_Module()
    {
        BuiltInScenarios.All.Should().ContainSingle();
        BuiltInScenarios.All[0].Should().BeSameAs(BuiltInScenarios.Full);
        BuiltInScenarios.Full.Name.Should().Be("full");
        BuiltInScenarios.Full.DefaultAgent.Should().Be("build");
        BuiltInScenarios.Full.Modules.Should().Equal(BuiltInScenarios.AllModules);
        BuiltInScenarios.IncludesAllModules(BuiltInScenarios.Full.Modules).Should().BeTrue();

        BuiltInScenarios.TryGet("full").Should().BeSameAs(BuiltInScenarios.Full);
        BuiltInScenarios.TryGet("FULL").Should().BeSameAs(BuiltInScenarios.Full);
        BuiltInScenarios.TryGet("code").Should().BeNull();
        BuiltInScenarios.TryGet("work").Should().BeNull();
        BuiltInScenarios.TryGet("minimal").Should().BeNull();
        BuiltInScenarios.TryGet("research").Should().BeNull();
    }

    [Fact]
    public void Full_Should_Enable_All_Implemented_Module_Ids()
    {
        // 反射发现 src 内全部 ISeeingModule 实现 id，与 Boot 能力集 Full 求差。
        // 内置工作模式 full 是 "*"，不再持有这份清单。
        var implemented = SrcModuleScanner.DiscoverImplementedModuleIds();

        implemented.Should().NotBeEmpty("扫描器应能从 src 构建产物反射发现内置模块");

        var missing = implemented
            .Except(BuiltInCapabilitySets.Full, StringComparer.Ordinal)
            .Except(BuiltInCapabilitySets.FullExcludedModules, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "全部已实现的 ISeeingModule 都必须登记进 BuiltInCapabilitySets.Full（或在 FullExcludedModules 显式豁免）；缺失: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void FullExcludedModules_Should_Be_Implemented_And_Absent_From_Full()
    {
        var implemented = SrcModuleScanner.DiscoverImplementedModuleIds();

        foreach (var excluded in BuiltInCapabilitySets.FullExcludedModules)
        {
            implemented.Should().Contain(excluded,
                "FullExcludedModules 中的 '{0}' 必须是真实存在的已实现模块", excluded);
            BuiltInCapabilitySets.Full.Should().NotContain(excluded,
                "FullExcludedModules 中的 '{0}' 不应出现在 Full 中", excluded);
        }
    }

    [Fact]
    public void CapabilitySet_Full_With_OpenAi_Should_Include_Provider_Plugs()
    {
        BuiltInCapabilitySets.Full.Should().Contain("llm.openai");
        BuiltInCapabilitySets.Full.Should().Contain(
            ["provider.deepseek", "provider.mimo", "provider.opencodezen", "provider.siliconflow"],
            because: "OpenAI 兼容网关插件须随 llm.openai 进入 Boot=full 的启用集，否则不会 Activate/拉模型");
    }

    [Fact]
    public void Core_Assembly_Should_Not_Reference_Tools_Git()
    {
        var core = typeof(BuiltInScenarios).Assembly;

        core.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should()
            .NotContain("Seeing.Agent.Tools.Git",
                "BuiltInScenarios 只用字符串模块 id，Core 不得编译引用 Tools.Git");

        core.GetTypes()
            .Select(t => t.FullName)
            .Should()
            .NotContain(n => n != null && n.Contains("Tools.Git", StringComparison.Ordinal),
                "Core 程序集内不得出现 Tools.Git 类型");
    }

    [Fact]
    public void ScenarioDefinition_Is_Sealed_Record_With_Expected_Shape()
    {
        typeof(ScenarioDefinition).IsSealed.Should().BeTrue();

        var def = new ScenarioDefinition(
            "custom",
            ["io.local"],
            "general",
            new Dictionary<string, string> { ["executionWorld"] = "io.local" },
            ["bash"]);

        def.Name.Should().Be("custom");
        def.Modules.Should().Equal("io.local");
        def.DefaultAgent.Should().Be("general");
        def.Seams.Should().ContainKey("executionWorld");
        def.ToolsDisabled.Should().Equal("bash");
    }
}
