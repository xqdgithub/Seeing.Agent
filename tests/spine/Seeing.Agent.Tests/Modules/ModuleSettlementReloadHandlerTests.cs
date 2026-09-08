using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class ModuleSettlementReloadHandlerTests
{
    [Fact]
    public async Task Reload_在途执行时_Deactivate推迟_空闲后完成()
    {
        var a = new TrackingModule("a");
        var b = new TrackingModule("b");
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([a, b]));
        catalog.ReplaceEnabled(["a", "b"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [a, b], new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);
        await lifecycle.ActivateAsync();

        var options = new MutableOptions(new SeeingAgentOptions
        {
            Modules = new ModulesOptions { Enabled = ["a"] }, // drop b
        });

        var inFlight = new Mock<IExecutionInFlightBoundary>();
        var stillInFlight = true;
        inFlight.Setup(x => x.HasInFlight()).Returns(() => stillInFlight);
        inFlight.Setup(x => x.ListInFlightExecutionIds()).Returns([]);
        inFlight.Setup(x => x.CancelAllInFlightAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var reloadOptions = new ModuleReloadOptions { ForceCancelInFlight = false };
        var handler = new ModuleSettlementReloadHandler(
            new SettlementEngine(catalog),
            lifecycle,
            catalog,
            options,
            [a, b],
            reloadOptions: reloadOptions,
            settlementOptions: null,
            inFlight: inFlight.Object);

        await handler.ReloadAsync(new ConfigChange { ChangedSections = ["Modules"] });

        lifecycle.IsActivated("b").Should().BeTrue("在途时应推迟 Deactivate");
        handler.PendingDeactivate.Should().Contain("b");
        b.DeactivateCount.Should().Be(0);

        stillInFlight = false;
        await WaitUntilAsync(() => b.DeactivateCount > 0, TimeSpan.FromSeconds(2));

        lifecycle.IsActivated("b").Should().BeFalse();
        b.DeactivateCount.Should().Be(1);
        lifecycle.IsActivated("a").Should().BeTrue();
    }

    [Fact]
    public async Task Reload_强制切换_取消在途并报错_且完成Deactivate()
    {
        var a = new TrackingModule("a");
        var b = new TrackingModule("b");
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([a, b]));
        catalog.ReplaceEnabled(["a", "b"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [a, b], new ServiceCollection().BuildServiceProvider());
        await lifecycle.ActivateAsync();

        var options = new MutableOptions(new SeeingAgentOptions
        {
            Modules = new ModulesOptions { Enabled = ["a"] },
        });

        var inFlight = new Mock<IExecutionInFlightBoundary>();
        inFlight.Setup(x => x.HasInFlight()).Returns(true);
        inFlight.Setup(x => x.ListInFlightExecutionIds()).Returns(["exec_1"]);
        inFlight.Setup(x => x.CancelAllInFlightAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new ModuleSettlementReloadHandler(
            new SettlementEngine(catalog),
            lifecycle,
            catalog,
            options,
            [a, b],
            reloadOptions: new ModuleReloadOptions { ForceCancelInFlight = true },
            inFlight: inFlight.Object);

        var act = async () => await handler.ReloadAsync(new ConfigChange { ChangedSections = ["Modules"] });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*强制*取消*");

        inFlight.Verify(x => x.CancelAllInFlightAsync(It.IsAny<CancellationToken>()), Times.Once);
        b.DeactivateCount.Should().Be(1);
        lifecycle.IsActivated("b").Should().BeFalse();
    }

    [Fact]
    public async Task Reload_不能启用从未引用的包()
    {
        var a = new TrackingModule("a");
        var catalog = new ModuleCatalog();
        var engine = new SettlementEngine(catalog);
        var lifecycle = new ModuleLifecycleManager(catalog, [a], new ServiceCollection().BuildServiceProvider());
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([a]));
        catalog.ReplaceEnabled(["a"]);
        await lifecycle.ActivateAsync();

        var options = new MutableOptions(new SeeingAgentOptions
        {
            Modules = new ModulesOptions { Enabled = ["a", "never-referenced-pkg"] },
        });

        var handler = new ModuleSettlementReloadHandler(
            engine,
            lifecycle,
            catalog,
            options,
            [a],
            reloadOptions: new ModuleReloadOptions());

        await handler.ReloadAsync(new ConfigChange { ChangedSections = ["Modules"] });

        catalog.Enabled.Should().BeEquivalentTo(["a"]);
        catalog.IsAvailable("never-referenced-pkg").Should().BeFalse();
        lifecycle.Activated.Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task Reload_新增模块立即Activate()
    {
        var a = new TrackingModule("a");
        var b = new TrackingModule("b");
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([a, b]));
        catalog.ReplaceEnabled(["a"]);

        var lifecycle = new ModuleLifecycleManager(catalog, [a, b], new ServiceCollection().BuildServiceProvider());
        await lifecycle.ActivateAsync();
        b.ActivateCount.Should().Be(0);

        var options = new MutableOptions(new SeeingAgentOptions
        {
            Modules = new ModulesOptions { Enabled = ["a", "b"] },
        });

        var handler = new ModuleSettlementReloadHandler(
            new SettlementEngine(catalog),
            lifecycle,
            catalog,
            options,
            [a, b]);

        await handler.ReloadAsync(new ConfigChange { ChangedSections = ["Modules"] });

        lifecycle.IsActivated("b").Should().BeTrue();
        b.ActivateCount.Should().Be(1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }

        predicate().Should().BeTrue("timed out waiting for condition");
    }

    private sealed class MutableOptions : IOptionsMonitor<SeeingAgentOptions>
    {
        public MutableOptions(SeeingAgentOptions current) => CurrentValue = current;
        public SeeingAgentOptions CurrentValue { get; set; }
        public SeeingAgentOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SeeingAgentOptions, string?> listener) => null;
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
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
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
}
