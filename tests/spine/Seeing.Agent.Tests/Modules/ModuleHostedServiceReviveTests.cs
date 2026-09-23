using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class ModuleHostedServiceReviveTests
{
    [Fact]
    public async Task Activate_Deactivate_Activate_ShouldRestartModuleHostedService()
    {
        var hosted = new FakeModuleHostedService("demo");
        var module = new TrackingModule("demo");

        var services = new ServiceCollection();
        services.AddSingleton<IModuleHostedService>(hosted);
        var sp = services.BuildServiceProvider();

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["demo"]);

        var lifecycle = new ModuleLifecycleManager(
            catalog, [module], sp, NullLogger<ModuleLifecycleManager>.Instance);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        hosted.StartCount.Should().Be(1);
        hosted.IsRunning.Should().BeTrue();
        module.ActivateCount.Should().Be(1);

        await lifecycle.DeactivateAsync(["demo"], TestContext.Current.CancellationToken);
        hosted.StopCount.Should().Be(1);
        hosted.IsRunning.Should().BeFalse();
        module.DeactivateCount.Should().Be(1);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        hosted.StartCount.Should().Be(2);
        hosted.IsRunning.Should().BeTrue();
        module.ActivateCount.Should().Be(2);
    }

    [Fact]
    public async Task Activate_WhenAlreadyRunning_ShouldNotDoubleStart()
    {
        var hosted = new FakeModuleHostedService("demo");
        var module = new TrackingModule("demo");

        var services = new ServiceCollection();
        services.AddSingleton<IModuleHostedService>(hosted);
        var sp = services.BuildServiceProvider();

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["demo"]);

        var lifecycle = new ModuleLifecycleManager(
            catalog, [module], sp, NullLogger<ModuleLifecycleManager>.Instance);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        hosted.StartCount.Should().Be(1);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        hosted.StartCount.Should().Be(1);
        hosted.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_ShouldStopOnlyMatchingModuleHostedServices()
    {
        var demo = new FakeModuleHostedService("demo");
        var other = new FakeModuleHostedService("other");
        var module = new TrackingModule("demo");

        var services = new ServiceCollection();
        services.AddSingleton<IModuleHostedService>(demo);
        services.AddSingleton<IModuleHostedService>(other);
        var sp = services.BuildServiceProvider();

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([module]));
        catalog.ReplaceEnabled(["demo"]);

        var lifecycle = new ModuleLifecycleManager(
            catalog, [module], sp, NullLogger<ModuleLifecycleManager>.Instance);

        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);
        await other.StartAsync(TestContext.Current.CancellationToken);

        await lifecycle.DeactivateAsync(["demo"], TestContext.Current.CancellationToken);

        demo.IsRunning.Should().BeFalse();
        demo.StopCount.Should().Be(1);
        other.IsRunning.Should().BeTrue();
        other.StopCount.Should().Be(0);
    }

    private sealed class TrackingModule : ISeeingModule
    {
        public TrackingModule(string id) => Id = id;

        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools { get; } = [];
        public IReadOnlyList<string> ProvidedSeams { get; } = [];
        public IReadOnlyList<string> DependsOn { get; } = [];
        public int ActivateCount { get; private set; }
        public int DeactivateCount { get; private set; }

        public void ConfigureServices(IServiceCollection services) { }

        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ActivateCount++;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            DeactivateCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModuleHostedService : IModuleHostedService
    {
        private readonly ModuleHostedRunGate _run = new();

        public FakeModuleHostedService(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }
        public bool IsRunning => _run.IsRunning;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_run.IsRunning)
                return Task.CompletedTask;
            if (!_run.TryBegin())
                return Task.CompletedTask;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_run.IsRunning)
                StopCount++;
            _run.End();
            return Task.CompletedTask;
        }
    }
}
