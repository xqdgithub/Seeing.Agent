using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Tools.Basic;
using Seeing.Agent.Core.Tools.FileSystem;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Modules;

/// <summary>
/// W2 不变量：工具仅经模块 Activate 挂载；Deactivate/Activate 对称。
/// </summary>
public class ToolActivationInvariantTests
{
    private static readonly string[] FileSystemModuleIds =
    [
        "read", "write", "edit", "glob", "grep", "delete", "add_workspace_path",
    ];

    [Fact]
    public void AddSeeingCore_Only_GetTools_Should_Be_Empty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        using var sp = services.BuildServiceProvider();
        var tools = sp.GetRequiredService<IToolManager>();

        tools.GetTools().Should().BeEmpty();
        services.Where(d => d.ServiceType == typeof(ITool)).Should().BeEmpty();
    }

    [Fact]
    public async Task Activate_BasicModule_Registers_CurrentTime_Only()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingModule<BasicModule>(registry);
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();
        var catalog = sp.GetRequiredService<ModuleCatalog>();
        var modules = sp.GetServices<ISeeingModule>().ToList();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors(modules));
        catalog.ReplaceEnabled(["basic"]);

        var lifecycle = sp.GetRequiredService<ModuleLifecycleManager>();
        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        var tm = sp.GetRequiredService<IToolManager>();
        tm.GetTools().Select(t => t.Id).Should().BeEquivalentTo(["current_time"]);

        var basic = modules.Single(m => m.Id == "basic");
        basic.ProvidedTools.Should().BeEquivalentTo(tm.GetTools().Select(t => t.Id));
    }

    [Fact]
    public async Task Deactivate_FileSystem_Removes_Read_Reactivate_Restores()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingModule<LocalExecutionWorldModule>(registry);
        services.AddSeeingModule<FileSystemModule>(registry);
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();
        var catalog = sp.GetRequiredService<ModuleCatalog>();
        var modules = sp.GetServices<ISeeingModule>().ToList();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors(modules));
        catalog.ReplaceEnabled(["io.local", "filesystem"]);

        var lifecycle = sp.GetRequiredService<ModuleLifecycleManager>();
        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        var tm = sp.GetRequiredService<IToolManager>();
        tm.GetTool("read").Should().NotBeNull();

        await lifecycle.DeactivateAsync(["filesystem"], TestContext.Current.CancellationToken);
        tm.GetTool("read").Should().BeNull();
        tm.GetTools().Select(t => t.Id).Should().NotContain(FileSystemModuleIds);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        tm.GetTool("read").Should().NotBeNull();
        tm.GetTools().Select(t => t.Id).Should().Contain(FileSystemModuleIds);
    }

    [Fact]
    public async Task Deactivate_With_ForceCancelOption_Still_Unregisters_Tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingModule<BasicModule>(registry);
        services.AddSeeingCore(registry);
        services.AddSingleton(new ModuleReloadOptions { ForceCancelInFlight = true });

        await using var sp = services.BuildServiceProvider();
        var catalog = sp.GetRequiredService<ModuleCatalog>();
        var modules = sp.GetServices<ISeeingModule>().ToList();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors(modules));
        catalog.ReplaceEnabled(["basic"]);

        var lifecycle = sp.GetRequiredService<ModuleLifecycleManager>();
        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        sp.GetRequiredService<IToolManager>().GetTool("current_time").Should().NotBeNull();

        await lifecycle.DeactivateAsync(["basic"], TestContext.Current.CancellationToken);
        sp.GetRequiredService<IToolManager>().GetTool("current_time").Should().BeNull();
    }
}
