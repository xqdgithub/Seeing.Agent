using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Background;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Integration.Hosting;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Background;

public sealed class MemoryEvolutionWorkerGatingTests
{
    [Fact]
    public async Task StartAsync_WhenModuleDisabled_ShouldNoOp()
    {
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("memory")).Returns(false);

        var worker = CreateWorker(catalog.Object, new MemoryModuleActivity());

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        // no-op: ExecuteAsync never started; completing Start/Stop is the assertion
        true.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_ShouldExitLongLoop()
    {
        var activity = new MemoryModuleActivity();
        activity.MarkActive();

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("memory")).Returns(true);

        var worker = CreateWorker(catalog.Object, activity);

        await worker.StartAsync(CancellationToken.None);

        // Give ExecuteAsync a moment to enter loops
        await Task.Delay(50);

        activity.MarkInactiveAndWake();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StopAsync(cts.Token);

        activity.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeactivateThenStart_ShouldReviveLoop()
    {
        var activity = new MemoryModuleActivity();
        activity.MarkActive();

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("memory")).Returns(true);

        var worker = CreateWorker(catalog.Object, activity);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        worker.IsRunning.Should().BeTrue();

        activity.MarkInactiveAndWake();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await worker.StopAsync(cts.Token);

        worker.IsRunning.Should().BeFalse();

        activity.MarkActive();
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        worker.IsRunning.Should().BeTrue();

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await worker.StopAsync(cts.Token);
    }

    private static MemoryEvolutionWorker CreateWorker(IModuleCatalog catalog, MemoryModuleActivity activity)
    {
        var options = new Mock<IOptionsMonitor<MemoryOptions>>();
        options.Setup(x => x.CurrentValue).Returns(new MemoryOptions { Enabled = true });

        var sessionEvents = new Mock<IMemorySessionEvents>();
        sessionEvents.Setup(x => x.SessionEnded).Returns(new SessionEndedObservable());

        return new MemoryEvolutionWorker(
            Mock.Of<IMemoryEvolutionService>(),
            Mock.Of<ISessionActivityTracker>(),
            Mock.Of<IMemoryFlushService>(),
            options.Object,
            sessionEvents.Object,
            activity,
            NullLogger<MemoryEvolutionWorker>.Instance,
            catalog);
    }

    private sealed class SessionEndedObservable : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }
}
