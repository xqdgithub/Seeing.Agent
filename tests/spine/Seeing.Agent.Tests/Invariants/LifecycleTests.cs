using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Spec §8：停用对称、硬依赖拒启、同 id 未 Replace 拒启。
/// </summary>
public class LifecycleTests
{
    [Fact]
    public async Task Deactivate_Removes_Tools_And_Sections_Activate_Restores()
    {
        var toolBag = new InMemoryToolBag();
        var section = new MutableSectionContributor();
        var module = new ToolAndSectionModule(toolBag, section);

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["demo"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [module], new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);

        await lifecycle.ActivateAsync();
        toolBag.Has("demo_tool").Should().BeTrue();
        (await BuildPromptAsync(section)).Should().Contain("demo-section-body");

        await lifecycle.DeactivateAsync(["demo"]);
        toolBag.Has("demo_tool").Should().BeFalse();
        (await BuildPromptAsync(section)).Should().NotContain("demo-section-body");

        await lifecycle.ActivateAsync();
        toolBag.Has("demo_tool").Should().BeTrue();
        (await BuildPromptAsync(section)).Should().Contain("demo-section-body");
    }

    [Fact]
    public async Task HardDependency_EnableA_WithoutB_Should_Refuse()
    {
        var engine = new SettlementEngine(new ModuleCatalog(), NullLogger<SettlementEngine>.Instance);
        var input = new SettlementInput
        {
            Available =
            [
                new ModuleDescriptor("io.local", [], [], []),
                new ModuleDescriptor("filesystem", [], [], ["io.local"]),
            ],
            ConfiguredBoot = "broken",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = new("broken", ["filesystem"], Array.Empty<string>()),
            },
        };

        var act = async () => await engine.SettleAsync(input);
        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*filesystem*io.local*");
    }

    [Fact]
    public void DuplicateModuleId_WithoutReplace_Should_Refuse()
    {
        var a = new StubModule("filesystem", typeof(OldFs));
        var b = new StubModule("filesystem", typeof(NewFs));
        var catalog = new ModuleCatalog();

        var act = () => new ModuleLifecycleManager(catalog, [a, b], new ServiceCollection().BuildServiceProvider());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filesystem*");
    }

    [Fact]
    public void AddSeeingModule_DuplicateId_WithoutReplace_Should_Refuse_At_Registration()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSeeingModule<StubFsModule>(registry, replace: false);
        var act = () => services.AddSeeingModule<StubFsModule2>(registry, replace: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filesystem*");
    }

    [Fact]
    public void AfterReplace_Only_New_Provider_Is_Resolvable()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSeeingModule<StubOldWorldModule>(registry, replace: false);
        services.ReplaceModule<StubNewWorldModule>(registry);

        using var sp = services.BuildServiceProvider();
        var modules = sp.GetServices<ISeeingModule>().Where(m => m.Id == "io.local").ToList();
        modules.Should().HaveCount(1);
        modules[0].Should().BeOfType<StubNewWorldModule>();
    }

    private class StubFsModule : ISeeingModule
    {
        public string Id => "filesystem";
        public IReadOnlyList<string> ProvidedTools => [];
        public IReadOnlyList<string> ProvidedSeams => [];
        public IReadOnlyList<string> DependsOn => [];
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubFsModule2 : StubFsModule;

    private class StubOldWorldModule : ISeeingModule
    {
        public string Id => "io.local";
        public IReadOnlyList<string> ProvidedTools => [];
        public IReadOnlyList<string> ProvidedSeams => ["executionWorld"];
        public IReadOnlyList<string> DependsOn => [];
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubNewWorldModule : StubOldWorldModule;


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

    private sealed class StubModule(string id, Type providerType) : ISeeingModule
    {
        public string Id { get; } = id;
        public Type ProviderType { get; } = providerType;
        public IReadOnlyList<string> ProvidedTools { get; } = [];
        public IReadOnlyList<string> ProvidedSeams { get; } = ["executionWorld"];
        public IReadOnlyList<string> DependsOn { get; } = [];
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OldFs;
    private sealed class NewFs;

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
