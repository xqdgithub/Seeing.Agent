using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 配置迁移：Modules.Enabled 警告忽略；扁平 ToolsDisabled → Tools.Disabled（OpenSpec 5.3）。
/// </summary>
public class ConfigMigrationBootCeilingTests
{
    private static ModuleDescriptor Desc(string id, params string[] dependsOn)
        => new(id, Array.Empty<string>(), Array.Empty<string>(), dependsOn);

    [Fact]
    public async Task SettlementEngine_ModulesEnabled_ShouldWarnAndIgnore_BootStarUsesAvailable()
    {
        var catalog = new ModuleCatalog();
        var engine = new SettlementEngine(catalog, NullLogger<SettlementEngine>.Instance);
        var available = new[]
        {
            Desc("io.local"),
            Desc("filesystem", "io.local"),
            Desc("basic"),
        };

        var result = await engine.SettleAsync(new SettlementInput
        {
            Available = available,
            ConfiguredBoot = "*",
            // 旧 Modules.Enabled：应警告并忽略，不得收窄 boot
            UserEnabled = ["io.local"],
            UserDisabled = null,
        });

        result.Boot.Should().Be("*");
        result.Enabled.Should().BeEquivalentTo(["basic", "filesystem", "io.local"]);
        result.Warnings.Should().Contain(w =>
            w.Contains("Modules.Enabled", StringComparison.Ordinal) &&
            w.Contains("CapabilitySets", StringComparison.Ordinal));
        catalog.IsEnabled("filesystem").Should().BeTrue();
    }

    [Fact]
    public void ScenarioConfig_FlatToolsDisabled_ShouldMergeIntoToolsDisabled_OnToDefinition()
    {
#pragma warning disable CS0618 // ToolsDisabled 迁移兼容
        var cfg = new ScenarioConfig
        {
            Modules = ["io.local", "shell"],
            ToolsDisabled = ["bash", "write"],
            Tools = new ScenarioToolsConfig
            {
                Disabled = ["write", "edit"],
            },
        };
#pragma warning restore CS0618

        var def = cfg.ToDefinition("custom");

        // 定义层：扁平 ∪ 嵌套
        def.ToolsDisabled.Should().BeEquivalentTo(["bash", "write", "edit"]);

        // 配置对象：已合并进 Tools.Disabled（加载路径规范化）
        cfg.Tools.Should().NotBeNull();
        cfg.Tools!.Disabled.Should().BeEquivalentTo(["write", "edit", "bash"]);
    }

    [Fact]
    public void ScenarioConfig_MergeFlatToolsDisabledIntoNested_Idempotent()
    {
#pragma warning disable CS0618
        var cfg = new ScenarioConfig
        {
            ToolsDisabled = ["bash"],
        };
#pragma warning restore CS0618

        cfg.MergeFlatToolsDisabledIntoNested();
        cfg.MergeFlatToolsDisabledIntoNested();

        cfg.Tools!.Disabled.Should().Equal("bash");
        cfg.Tools.Disabled.Should().HaveCount(1);
    }

    [Fact]
    public void ScenarioConfig_FromDefinition_ShouldWriteNestedToolsDisabledOnly()
    {
        var def = new ScenarioDefinition(
            "x",
            ["io.local"],
            "general",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ["bash"]);

        var cfg = ScenarioConfig.FromDefinition(def);

#pragma warning disable CS0618
        cfg.ToolsDisabled.Should().BeEmpty();
#pragma warning restore CS0618
        cfg.Tools!.Disabled.Should().Equal("bash");
    }
}
