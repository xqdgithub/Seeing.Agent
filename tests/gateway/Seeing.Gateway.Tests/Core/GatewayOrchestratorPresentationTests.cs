using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

/// <summary>
/// Gateway 订阅呈现端：每订阅注册一实例，注册/注销与订阅同生命周期；同会话多订阅互不影响。
/// </summary>
public class GatewayOrchestratorPresentationTests
{
    [Fact]
    public async Task SubscribeExecutionEvents_ShouldRegisterAndUnregisterPresenter()
    {
        var store = new FakePermissionPresentationStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = CreateOrchestrator(BuildProvider(store, _ => GatedEventsAsync(gate.Task)));

        var enumerator = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var moveTask = enumerator.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => store.CanSurface("ses_1"), "presenter 未登记");

        gate.SetResult();
        (await moveTask).Should().BeFalse();
        store.CanSurface("ses_1").Should().BeFalse("订阅结束后应注销 presenter");

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeExecutionEvents_WhenStreamFaults_ShouldStillUnregisterPresenter()
    {
        var store = new FakePermissionPresentationStore();
        var orchestrator = CreateOrchestrator(
            BuildProvider(store, _ => ThrowingEventsAsync(TestContext.Current.CancellationToken)));

        var events = new List<GatewayEvent>();
        await foreach (var e in orchestrator.SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().Contain(e => e.Object == GatewayEventObject.Error);
        store.CanSurface("ses_1").Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentSubscriptions_SameSession_UnregisteringOneShouldNotAffectOther()
    {
        var store = new FakePermissionPresentationStore();
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new[] { gateA.Task, gateB.Task };
        var orchestrator = CreateOrchestrator(BuildProvider(store, i => GatedEventsAsync(gates[i])));

        var enumA = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_a", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var enumB = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_b", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var moveA = enumA.MoveNextAsync().AsTask();
        var moveB = enumB.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => store.PresenterCount == 2, "两个订阅未同时登记 presenter");
        store.CanSurface("ses_1").Should().BeTrue();

        gateA.SetResult();
        (await moveA).Should().BeFalse();
        store.PresenterCount.Should().Be(1, "注销一个订阅不应影响另一个");
        store.CanSurface("ses_1").Should().BeTrue();

        gateB.SetResult();
        (await moveB).Should().BeFalse();
        store.PresenterCount.Should().Be(0);
        store.CanSurface("ses_1").Should().BeFalse();

        await enumA.DisposeAsync();
        await enumB.DisposeAsync();
    }

    private static ServiceProvider BuildProvider(
        FakePermissionPresentationStore store,
        Func<int, IAsyncEnumerable<IMessageEvent>> eventsFactory)
    {
        var call = 0;
        var chatOrchestrator = new Mock<IChatOrchestrator>();
        chatOrchestrator
            .Setup(c => c.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => eventsFactory(Interlocked.Increment(ref call) - 1));
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IPermissionPresentationStore>(store);
        services.AddSingleton(chatOrchestrator.Object);
        services.AddSingleton(sessionManager.Object);
        return services.BuildServiceProvider();
    }

    private static GatewayOrchestratorV2 CreateOrchestrator(IServiceProvider provider) =>
        new(
            provider,
            new GatewayOptions(),
            new GatewayRunTracker(),
            new SessionExecutionQueue(),
            NullLogger<GatewayOrchestratorV2>.Instance);

    private static async IAsyncEnumerable<IMessageEvent> GatedEventsAsync(
        Task gate, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<IMessageEvent> ThrowingEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("stream boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException(because);
            await Task.Delay(10);
        }
    }

    private sealed class FakePermissionPresentationStore : IPermissionPresentationStore
    {
        private readonly ConcurrentDictionary<IPermissionPresenter, byte> _presenters = new();

        public int PresenterCount => _presenters.Count;

        public event Action? Changed;

        public event Action? PresenterUnregistered
        {
            add { }
            remove { }
        }

        public void Register(IPermissionPresenter presenter)
        {
            _presenters[presenter] = 0;
            Changed?.Invoke();
        }

        public void Unregister(IPermissionPresenter presenter)
        {
            _presenters.TryRemove(presenter, out _);
            Changed?.Invoke();
        }

        public bool CanSurface(string sessionId) =>
            _presenters.Keys.Any(p => p.SurfaceSessionIds.Contains(sessionId));
    }
}
