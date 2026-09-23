using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class ModuleLifecycleManagerTests
{
    [Fact]
    public async Task ActivateAsync_仅激活启用集_且按依赖序()
    {
        var io = new TrackingModule("io.local");
        var fs = new TrackingModule("filesystem", dependsOn: ["io.local"]);
        var unused = new TrackingModule("unused");

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([io, fs, unused]));
        catalog.ReplaceEnabled(["filesystem", "io.local"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [io, fs, unused], new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        lifecycle.Activated.Should().BeEquivalentTo(["filesystem", "io.local"]);
        unused.ActivateCount.Should().Be(0);
        io.ActivateCount.Should().Be(1);
        fs.ActivateCount.Should().Be(1);
        // 依赖先于依赖方
        io.LastActivateOrder.Should().BeLessThan(fs.LastActivateOrder);
    }

    [Fact]
    public async Task Deactivate_后工具与分节消失_再Activate恢复()
    {
        var toolBag = new InMemoryToolBag();
        var section = new MutableSectionContributor();
        var module = new ToolAndSectionModule(toolBag, section);

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["demo"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [module], new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        toolBag.Has("demo_tool").Should().BeTrue();
        var promptAfterActivate = await BuildPromptAsync(section);
        promptAfterActivate.Should().Contain("demo-section-body");

        await lifecycle.DeactivateAsync(["demo"], TestContext.Current.CancellationToken);

        toolBag.Has("demo_tool").Should().BeFalse();
        var promptAfterDeactivate = await BuildPromptAsync(section);
        promptAfterDeactivate.Should().NotContain("demo-section-body");
        lifecycle.IsActivated("demo").Should().BeFalse();

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        toolBag.Has("demo_tool").Should().BeTrue();
        var promptAfterReactivate = await BuildPromptAsync(section);
        promptAfterReactivate.Should().Contain("demo-section-body");
        lifecycle.IsActivated("demo").Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_失败时标记Unhealthy并抛出()
    {
        var module = new FailingModule();
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["boom"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [module], new ServiceCollection().BuildServiceProvider());

        var act = async () => await lifecycle.ActivateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*boom*");
        catalog.Unhealthy.Should().ContainKey("boom");
        lifecycle.IsActivated("boom").Should().BeFalse();
    }

    private static async Task<string> BuildPromptAsync(IPromptSectionContributor section)
    {
        var builder = new PromptBuilder([section]);
        return await builder.BuildAsync(new PromptContext
        {
            Agent = new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                SystemPrompt = "## Tools\n\n## Skills\n",
            },
        });
    }

    private sealed class TrackingModule : ISeeingModule
    {
        private static int s_order;

        public TrackingModule(string id, string[]? dependsOn = null)
        {
            Id = id;
            DependsOn = dependsOn ?? [];
        }

        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools { get; } = [];
        public IReadOnlyList<string> ProvidedSeams { get; } = [];
        public IReadOnlyList<string> DependsOn { get; }
        public int ActivateCount { get; private set; }
        public int LastActivateOrder { get; private set; }

        public void ConfigureServices(IServiceCollection services) { }

        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ActivateCount++;
            LastActivateOrder = Interlocked.Increment(ref s_order);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingModule : ISeeingModule
    {
        public string Id => "boom";
        public IReadOnlyList<string> ProvidedTools { get; } = [];
        public IReadOnlyList<string> ProvidedSeams { get; } = [];
        public IReadOnlyList<string> DependsOn { get; } = [];
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom failed");
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ToolAndSectionModule : ISeeingModule
    {
        private readonly InMemoryToolBag _tools;
        private readonly MutableSectionContributor _section;

        public ToolAndSectionModule(InMemoryToolBag tools, MutableSectionContributor section)
        {
            _tools = tools;
            _section = section;
        }

        public string Id => "demo";
        public IReadOnlyList<string> ProvidedTools { get; } = ["demo_tool"];
        public IReadOnlyList<string> ProvidedSeams { get; } = [];
        public IReadOnlyList<string> DependsOn { get; } = [];
        public void ConfigureServices(IServiceCollection services) { }

        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            _tools.Register("demo_tool");
            _section.Active = true;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            _tools.Unregister("demo_tool");
            _section.Active = false;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryToolBag
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        public void Register(string id) => _ids.Add(id);
        public void Unregister(string id) => _ids.Remove(id);
        public bool Has(string id) => _ids.Contains(id);
    }

    private sealed class MutableSectionContributor : IPromptSectionContributor
    {
        public bool Active { get; set; }
        public string SectionName => PromptSectionNames.Tools;
        public int Order => 0;

        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(Active ? "demo-section-body" : null);
    }
}
