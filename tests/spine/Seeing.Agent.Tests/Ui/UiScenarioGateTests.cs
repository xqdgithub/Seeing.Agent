using FluentAssertions;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

/// <summary>
/// 会话工作模式 → 模块集解析的五个分支，逐条对照旧实现
/// <c>SessionShellVisibility.cs:115-127</c>。
/// </summary>
public class UiScenarioGateTests
{
    private sealed class FakeScenarioCatalog : IScenarioCatalog
    {
        private readonly Dictionary<string, ScenarioDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static FakeScenarioCatalog With(string name, string[] modules)
        {
            var catalog = new FakeScenarioCatalog();
            catalog._byName[name] = new ScenarioDefinition(
                Name: name,
                Modules: modules,
                DefaultAgent: "build",
                Seams: new Dictionary<string, string>(),
                ToolsDisabled: []);
            return catalog;
        }

        public ScenarioDefinition? Get(string name)
            => _byName.GetValueOrDefault(name);

        public IReadOnlyList<ScenarioDefinition> ListAll() => _byName.Values.ToArray();

        public bool IsBuiltInName(string name) => false;

        public bool HasConfiguredOverride(string name) => _byName.ContainsKey(name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ScenarioGate_Blank_ShouldNotNarrow(string? scenario)
        => UiScenarioGate.Resolve(scenario, catalog: null).Should().BeNull();

    [Fact]
    public void ScenarioGate_Unknown_ShouldNotNarrow()
        => UiScenarioGate.Resolve("no-such-scenario", catalog: null).Should().BeNull();

    [Fact]
    public void ScenarioGate_Wildcard_ShouldNotNarrow()
    {
        // 内置 full 的 Modules = ["*"]
        UiScenarioGate.Resolve("full", catalog: null).Should().BeNull();
    }

    [Fact]
    public void ScenarioGate_ExplicitModules_ShouldBeCaseInsensitive()
    {
        var gate = UiScenarioGate.Resolve("custom", FakeScenarioCatalog.With("custom", ["memory", "Skills"]));

        gate.Should().NotBeNull();
        gate!.Contains("skills").Should().BeTrue("门集必须 OrdinalIgnoreCase");
        gate.Contains("MEMORY").Should().BeTrue();
    }

    [Fact]
    public void ScenarioGate_EmptyModules_ShouldReturnEmptySetNot_Null()
    {
        // 空集 = 全部隐藏；null = 不收敛。两者语义相反。
        var gate = UiScenarioGate.Resolve("empty", FakeScenarioCatalog.With("empty", []));

        gate.Should().NotBeNull();
        gate!.Should().BeEmpty();
    }

    [Fact]
    public void ResolveEffective_SessionWins_ElseProcess()
    {
        UiScenarioGate.ResolveEffective("work", "full").Should().Be("work");
        UiScenarioGate.ResolveEffective(null, "full").Should().Be("full");
        UiScenarioGate.ResolveEffective("  ", " full ").Should().Be("full");
        UiScenarioGate.ResolveEffective(null, null).Should().BeNull();
    }
}
