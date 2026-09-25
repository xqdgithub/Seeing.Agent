using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Hosting.Execution;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Modules;

/// <summary>
/// 动态工具结算契约（P1-26 回归）与 ComponentManager 模块门控（P0-2）。
/// </summary>
public class DynamicToolSettlementTests
{
    [Fact]
    public void Registry_ReturnsDynamicIds_OnlyForEnabledModules()
    {
        var registry = new DynamicToolContributorRegistry();
        registry.Register(new FakeContributor("mcp", ["demo_echo", "demo_ping"]));

        registry.GetDynamicToolIds(["mcp"]).Should().BeEquivalentTo(["demo_echo", "demo_ping"]);
        registry.GetDynamicToolIds(["other"]).Should().BeEmpty();

        registry.Unregister("mcp");
        registry.GetDynamicToolIds(["mcp"]).Should().BeEmpty();
    }

    [Fact]
    public void MergeDynamicToolIds_AddsDistinctAndSorted()
    {
        var merged = ExecutionJobService.MergeDynamicToolIds(["read", "write"], ["demo_echo", "read", "  "]);

        merged.Should().Equal("demo_echo", "read", "write");
    }

    [Fact]
    public async Task DynamicTool_FlowsInto_SettledSchema()
    {
        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        await manager.RegisterToolAsync(new NamedTool("demo_echo"));
        await manager.RegisterToolAsync(new NamedTool("read"));

        var registry = new DynamicToolContributorRegistry();
        registry.Register(new FakeContributor("mcp", ["demo_echo"]));

        // 模拟 ExecutionJobService 结算合并段：静态 settled ∪ enabled 模块动态贡献
        var merged = ExecutionJobService.MergeDynamicToolIds(
            ["read"],
            registry.GetDynamicToolIds(["mcp"]));

        var schemas = await manager.GetToolSchemasAsync(
            merged,
            new AgentDefinition { Name = "t" },
            TestContext.Current.CancellationToken);

        schemas.Select(s => s.Function!.Name).Should().Contain("demo_echo");
    }

    [Fact]
    public async Task ComponentManager_SkipsLoader_WhenOwningModuleDisabled()
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable([new ModuleDescriptor("mcp", [], [], [])]);
        catalog.ReplaceEnabled([]);

        var services = new ServiceCollection();
        services.AddSingleton<IModuleCatalog>(catalog);
        using var sp = services.BuildServiceProvider();

        var manager = new ComponentManager(sp, NullLogger<ComponentManager>.Instance);
        var loader = new CountingLoader("Mcp", "mcp");
        manager.RegisterLoader(loader);

        await manager.LoadAsync("Mcp", "root", TestContext.Current.CancellationToken);
        loader.LoadCalls.Should().Be(0);

        // 启用后同一次加载应重新放行
        catalog.ReplaceEnabled(["mcp"]);
        await manager.LoadAsync("Mcp", "root", TestContext.Current.CancellationToken);
        loader.LoadCalls.Should().Be(1);
    }

    [Fact]
    public async Task ComponentManager_LoadsLoader_WhenModuleIdIsEmpty()
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable([new ModuleDescriptor("mcp", [], [], [])]);
        catalog.ReplaceEnabled([]);

        var services = new ServiceCollection();
        services.AddSingleton<IModuleCatalog>(catalog);
        using var sp = services.BuildServiceProvider();

        var manager = new ComponentManager(sp, NullLogger<ComponentManager>.Instance);
        var loader = new CountingLoader("Plugin", moduleId: string.Empty);
        manager.RegisterLoader(loader);

        await manager.LoadAsync("Plugin", "root", TestContext.Current.CancellationToken);
        loader.LoadCalls.Should().Be(1);
    }

    private sealed class FakeContributor(string moduleId, string[] ids) : IDynamicToolContributor
    {
        public string ModuleId { get; } = moduleId;

        public IReadOnlyCollection<string> GetDynamicToolIds() => ids;
    }

    private sealed class CountingLoader(string type, string moduleId) : IComponentLoader
    {
        public string Type { get; } = type;

        public string ModuleId { get; } = moduleId;

        public int LoadCalls { get; private set; }

        public Task<ComponentLoadResult> LoadAsync(
            IServiceProvider services,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(new ComponentLoadResult { Type = Type, Success = true, Count = 1 });
        }
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;

        public string Description => Id;

        public IReadOnlyList<string> Tags => Array.Empty<string>();

        public ToolCategory Category => ToolCategory.General;

        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(ToolResult.Succeeded("ok"));
    }
}
