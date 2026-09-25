using System.Reflection;
using FluentAssertions;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Scenarios;
using Xunit;

namespace Seeing.Agent.Tests.Scenarios;

public class BuiltInScenariosTests
{
    [Fact]
    public void Code_Modules_Should_Contain_Git()
    {
        BuiltInScenarios.Code.Modules.Should().Contain("git");
        BuiltInScenarios.Code.DefaultAgent.Should().Be("build");
        BuiltInScenarios.Code.Name.Should().Be("code");
    }

    [Fact]
    public void Minimal_Work_Research_Full_Should_Match_Brief()
    {
        BuiltInScenarios.Minimal.Modules.Should().Equal(
            "io.local", "agents.builtin", "llm.openai", "basic",
            "provider.deepseek", "provider.opencodezen");
        BuiltInScenarios.Minimal.DefaultAgent.Should().Be("general");

        BuiltInScenarios.Code.Modules.Should().Contain(
            "io.local", "agents.builtin", "llm.openai", "basic",
            "filesystem", "shell", "git", "subagent");

        BuiltInScenarios.Work.Modules.Should().Contain(
            "filesystem", "web", "memory", "scheduler");
        BuiltInScenarios.Work.DefaultAgent.Should().Be("general");

        BuiltInScenarios.Research.Modules.Should().Contain("web", "memory");
        BuiltInScenarios.Research.DefaultAgent.Should().Be("explore");

        BuiltInScenarios.Full.Modules.Should().Contain(
            "git", "memory", "mcp", "skills", "acp", "gateway",
            "provider.deepseek", "provider.opencodezen");
        BuiltInScenarios.Full.DefaultAgent.Should().Be("build");
    }

    [Fact]
    public void Full_Should_Enable_All_Implemented_Module_Ids()
    {
        // 反射发现 src 内全部 ISeeingModule 实现 id，与 Full 求差：消除硬编码双写漂移。
        // 语义：Full 覆盖全部已实现模块，仅显式豁免 FullExcludedModules（可选在线外部源）。
        var implemented = SrcModuleScanner.DiscoverImplementedModuleIds();

        implemented.Should().NotBeEmpty("扫描器应能从 src 构建产物反射发现内置模块");

        var missing = implemented
            .Except(BuiltInScenarios.Full.Modules, StringComparer.Ordinal)
            .Except(BuiltInCapabilitySets.FullExcludedModules, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "全部已实现的 ISeeingModule 都必须登记进 BuiltInCapabilitySets.Full（或在 FullExcludedModules 显式豁免）；缺失: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void FullExcludedModules_Should_Be_Implemented_And_Absent_From_Full()
    {
        // 豁免清单自检：豁免项必须真实存在且确实不在 Full，避免陈旧/错误豁免削弱守门语义
        var implemented = SrcModuleScanner.DiscoverImplementedModuleIds();

        foreach (var excluded in BuiltInCapabilitySets.FullExcludedModules)
        {
            implemented.Should().Contain(excluded,
                "FullExcludedModules 中的 '{0}' 必须是真实存在的已实现模块", excluded);
            BuiltInScenarios.Full.Modules.Should().NotContain(excluded,
                "FullExcludedModules 中的 '{0}' 不应出现在 Full 中", excluded);
        }
    }

    [Fact]
    public void All_BuiltIn_Scenarios_With_OpenAi_Should_Include_Provider_Plugs()
    {
        foreach (var scenario in BuiltInScenarios.All)
        {
            if (!scenario.Modules.Contains("llm.openai", StringComparer.Ordinal))
                continue;

            scenario.Modules.Should().Contain(
                ["provider.deepseek", "provider.opencodezen"],
                because: "OpenAI 兼容网关插件须随 llm.openai 进入结算启用集，否则不会 Activate/拉模型");
        }
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
