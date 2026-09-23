using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Interactions;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 在途审批管理器测试（spec §5.2 / §11.2）：
/// Begin/Wait/TryResolve 幂等、取消/超时、策略重评估、回调异常隔离、上限 fail-closed、GetPending、Dispose 退订。
/// </summary>
public class PermissionRequestManagerTests
{
    [Fact]
    public async Task BeginAsync_ShouldAssignRequestId_AndPublishRequestEvent()
    {
        using var harness = new ManagerHarness();

        var ticket = await harness.Manager.BeginAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            CallId = "call-1",
            LoopId = "loop-1"
        }, TestContext.Current.CancellationToken);

        ticket.RequestId.Should().NotBeNullOrEmpty();
        ticket.SessionId.Should().Be("s1");
        harness.Manager.PendingCount.Should().Be(1);

        var evt = harness.PublishedEvents.OfType<PermissionRequestEvent>().Single();
        evt.RequestId.Should().Be(ticket.RequestId);
        evt.SessionId.Should().Be("s1");
        evt.CallId.Should().Be("call-1");
        evt.LoopId.Should().Be("loop-1");
        evt.TimeoutSeconds.Should().Be(300);
        evt.AllowedScopes.Should().Contain(PermissionGrantScope.Once);
    }

    [Fact]
    public async Task BeginAsync_ShouldPreserveCallerProvidedRequestId()
    {
        using var harness = new ManagerHarness();

        var ticket = await harness.Manager.BeginAsync(new PermissionRequest
        {
            RequestId = "fixed-id",
            SessionId = "s1",
            PermissionKind = "tool.execute"
        }, TestContext.Current.CancellationToken);

        ticket.RequestId.Should().Be("fixed-id");
    }

    [Fact]
    public async Task WaitAsync_TryResolve_ShouldReturnResolution_AndBeIdempotent()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User, "ok", "s1").Should().BeTrue();
        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Deny,
            PermissionGrantScope.Once, PermissionResolvedBy.User, "late", "s1").Should().BeFalse();

        var resolution = await waiter;
        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        resolution.Reason.Should().Be("ok");
        resolution.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task TryResolve_WrongExpectedSession_ShouldReturnFalse()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User, null, "other-session").Should().BeFalse();

        harness.Manager.PendingCount.Should().Be(1);
    }

    [Fact]
    public void TryResolve_UnknownRequest_ShouldReturnFalse()
    {
        using var harness = new ManagerHarness();

        harness.Manager.TryResolve("missing", PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeFalse();
    }

    [Fact]
    public async Task TryResolve_ShouldPublishResolvedEvent()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Deny,
            PermissionGrantScope.Session, PermissionResolvedBy.Policy, "blocked").Should().BeTrue();

        var evt = harness.PublishedEvents.OfType<PermissionResolvedEvent>().Single();
        evt.RequestId.Should().Be(ticket.RequestId);
        evt.SessionId.Should().Be("s1");
        evt.CallId.Should().Be("call-1");
        evt.Decision.Should().Be(PermissionEffect.Deny);
        evt.Scope.Should().Be(PermissionGrantScope.Session);
        evt.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        evt.Reason.Should().Be("blocked");
    }

    [Fact]
    public async Task TryResolve_AfterWaitConsumed_ShouldReturnFalse()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
        await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_Cancelled_ShouldDenyWithCancellation()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var waiter = harness.Manager.WaitAsync(ticket, cts.Token);
        cts.Cancel();

        var resolution = await waiter;
        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Cancellation);
    }

    [Fact]
    public async Task WaitAsync_Timeout_ShouldDenyWithTimeout()
    {
        using var harness = new ManagerHarness(TimeSpan.FromMilliseconds(80));
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var resolution = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Timeout);
    }

    [Fact]
    public async Task WaitAsync_UnknownTicket_ShouldReturnNoChannelDeny()
    {
        using var harness = new ManagerHarness();

        var resolution = await harness.Manager.WaitAsync(new RequestTicket("missing", "s1"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
    }

    [Fact]
    public async Task BeginAsync_QueueFull_ShouldFailClosedWithNoChannelDeny()
    {
        using var harness = new ManagerHarness();

        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"), TestContext.Current.CancellationToken);

        harness.Manager.PendingCount.Should().Be(32);

        var overflow = await harness.Manager.BeginAsync(NewRequest("overflow"), TestContext.Current.CancellationToken);
        var resolution = await harness.Manager.WaitAsync(overflow, TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        resolution.Reason.Should().Be("队列已满");
        harness.Manager.PendingCount.Should().Be(32);
    }

    // F4：溢出拒绝须补发 PermissionResolvedEvent，使终态可观测。
    [Fact]
    public async Task BeginAsync_QueueFull_ShouldPublishResolvedEvent()
    {
        using var harness = new ManagerHarness();

        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"), TestContext.Current.CancellationToken);

        var overflow = await harness.Manager.BeginAsync(NewRequest("overflow"), TestContext.Current.CancellationToken);

        var evt = harness.PublishedEvents.OfType<PermissionResolvedEvent>()
            .Single(e => e.RequestId == overflow.RequestId);
        evt.Decision.Should().Be(PermissionEffect.Deny);
        evt.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        evt.Reason.Should().Be("队列已满");

        var resolution = await harness.Manager.WaitAsync(overflow, TestContext.Current.CancellationToken);
        resolution.Decision.Should().Be(PermissionEffect.Deny);
    }

    [Fact]
    public async Task ReEvaluate_SessionUpdatedToEnabled_ShouldResolveAllowWithPolicy()
    {
        using var harness = new ManagerHarness();
        harness.Sessions.Setup(s => s.Get("s1"))
            .Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.FollowGlobal });

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Sessions.Setup(s => s.Get("s1"))
            .Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.Enabled });
        harness.SessionEvents.Publish(new SessionEvent { SessionId = "s1", Type = SessionEventType.Updated });

        var resolution = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
    }

    [Fact]
    public async Task ReEvaluate_ParentSessionEnabled_ShouldResolveChildPendingRequest()
    {
        var groups = new Mock<ISessionGroupManager>();
        groups.Setup(g => g.TryGetParent("child", out It.Ref<string?>.IsAny))
            .Returns((string _, out string? parent) =>
            {
                parent = "parent";
                return true;
            });

        using var harness = new ManagerHarness(groups: groups.Object);
        harness.Sessions.Setup(s => s.Get("child"))
            .Returns(new SessionData { Id = "child", Kind = SessionKind.SubAgent, AutoApprove = SessionAutoApprove.FollowGlobal });
        harness.Sessions.Setup(s => s.Get("parent"))
            .Returns(new SessionData { Id = "parent", Kind = SessionKind.Root, AutoApprove = SessionAutoApprove.FollowGlobal });

        var ticket = await harness.Manager.BeginAsync(NewRequest("child"), TestContext.Current.CancellationToken);

        // 父会话执行中切换为「自动」→ 子代理在途请求应即时放行
        harness.Sessions.Setup(s => s.Get("parent"))
            .Returns(new SessionData { Id = "parent", Kind = SessionKind.Root, AutoApprove = SessionAutoApprove.Enabled });
        harness.SessionEvents.Publish(new SessionEvent { SessionId = "parent", Type = SessionEventType.Updated });

        var resolution = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
    }

    [Fact]
    public async Task ReEvaluate_OverrideDisabled_ShouldNeverAutoApprove()
    {
        using var harness = new ManagerHarness();
        harness.Sessions.Setup(s => s.Get("s1"))
            .Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.Enabled });

        var ticket = await harness.Manager.BeginAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Override = SessionAutoApprove.Disabled
        }, TestContext.Current.CancellationToken);

        harness.SessionEvents.Publish(new SessionEvent { SessionId = "s1", Type = SessionEventType.Updated });
        harness.Options.Set(new SeeingAgentOptions
        {
            Permission = new PermissionOptions { AutoApproveAll = true }
        });

        harness.Manager.PendingCount.Should().Be(1);
        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Deny,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
    }

    [Fact]
    public async Task ReEvaluate_GlobalAutoApproveChanged_ShouldResolveAllow()
    {
        using var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Options.Set(new SeeingAgentOptions
        {
            Permission = new PermissionOptions { AutoApproveAll = true }
        });

        var resolution = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
    }

    [Fact]
    public async Task OnSessionEvent_PolicyThrows_ShouldBeIsolated()
    {
        using var harness = new ManagerHarness();
        await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        harness.Sessions.Setup(s => s.Get("s1")).Throws(new InvalidOperationException("boom"));

        var act = () => harness.SessionEvents.Publish(
            new SessionEvent { SessionId = "s1", Type = SessionEventType.Updated });

        act.Should().NotThrow();
    }

    [Fact]
    public async Task OnOptionsChanged_PolicyThrows_ShouldBeIsolated()
    {
        using var harness = new ManagerHarness();
        await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        harness.Sessions.Setup(s => s.Get("s1")).Throws(new InvalidOperationException("boom"));

        var act = () => harness.Options.Set(new SeeingAgentOptions
        {
            Permission = new PermissionOptions { AutoApproveAll = true }
        });

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetPending_ShouldReturnOnlyUnresolvedRequests()
    {
        using var harness = new ManagerHarness();
        var first = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        await harness.Manager.BeginAsync(NewRequest("s2"), TestContext.Current.CancellationToken);

        harness.Manager.GetPending("s1").Should().HaveCount(2);
        harness.Manager.GetPending("s2").Should().HaveCount(1);
        harness.Manager.GetPending("missing").Should().BeEmpty();
        harness.Manager.GetPending(string.Empty).Should().BeEmpty();

        harness.Manager.TryResolve(first.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();

        harness.Manager.GetPending("s1").Should().HaveCount(1);
        harness.Manager.PendingCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_ShouldUnsubscribe_AndDenyPending()
    {
        var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        harness.SessionEvents.SubscriptionCount.Should().Be(1);
        harness.Options.ListenerCount.Should().Be(1);

        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        harness.Manager.Dispose();

        var resolution = await waiter;
        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Cancellation);

        harness.SessionEvents.SubscriptionCount.Should().Be(0);
        harness.Options.ListenerCount.Should().Be(0);
    }

    // F4：Dispose 残留请求统一经 TryResolve，须发布 PermissionResolvedEvent（与溢出路径一致）。
    [Fact]
    public async Task Dispose_ShouldPublishResolvedEventForPending()
    {
        var harness = new ManagerHarness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Manager.Dispose();

        var evt = harness.PublishedEvents.OfType<PermissionResolvedEvent>()
            .Single(e => e.RequestId == ticket.RequestId);
        evt.Decision.Should().Be(PermissionEffect.Deny);
        evt.ResolvedBy.Should().Be(PermissionResolvedBy.Cancellation);
        evt.Reason.Should().Be("管理器已释放");
    }

    [Fact]
    public async Task Dispose_Twice_ShouldBeSafe()
    {
        var harness = new ManagerHarness();
        await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var act = () =>
        {
            harness.Manager.Dispose();
            harness.Manager.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task TryResolve_ShouldBroadcastDismissToChannels()
    {
        var channel = new RecordingChannel();
        using var harness = new ManagerHarness(channels: new IPermissionChannel[] { channel });
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();

        await WaitUntilAsync(() => channel.Dismissed.Count == 1);
        channel.Dismissed.Single().RequestId.Should().Be(ticket.RequestId);
    }

    [Fact]
    public async Task GetAllPending_ShouldReturnUnresolvedAcrossSessions()
    {
        using var harness = new ManagerHarness();
        var first = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        await harness.Manager.BeginAsync(NewRequest("s2"), TestContext.Current.CancellationToken);
        await harness.Manager.BeginAsync(NewRequest("s2"), TestContext.Current.CancellationToken);

        harness.Manager.GetAllPending().Should().HaveCount(3);
        harness.Manager.GetAllPending().Select(r => r.SessionId).Should().Contain(new[] { "s1", "s2" });

        harness.Manager.TryResolve(first.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();

        harness.Manager.GetAllPending().Should().HaveCount(2);
        harness.Manager.GetAllPending().Select(r => r.SessionId).Should().NotContain("s1");
    }

    [Fact]
    public async Task GetAllPending_QueueFull_ShouldExcludeOverflowEntry()
    {
        using var harness = new ManagerHarness();
        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"), TestContext.Current.CancellationToken);

        await harness.Manager.BeginAsync(NewRequest("overflow"), TestContext.Current.CancellationToken);

        harness.Manager.GetAllPending().Should().HaveCount(32);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnBeginResolveAndDispose()
    {
        var harness = new ManagerHarness();
        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        count.Should().Be(1);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
        count.Should().Be(2);

        harness.Manager.Dispose();
        count.Should().Be(3);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnOverflow()
    {
        using var harness = new ManagerHarness();
        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"), TestContext.Current.CancellationToken);

        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        await harness.Manager.BeginAsync(NewRequest("overflow"), TestContext.Current.CancellationToken);

        count.Should().Be(1);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnWaitTimeout()
    {
        using var harness = new ManagerHarness(TimeSpan.FromMilliseconds(50));
        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        count.Should().Be(1);

        await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        count.Should().Be(2);
    }

    [Fact]
    public async Task PendingChanged_SubscriberThrows_ShouldBeIsolated()
    {
        using var harness = new ManagerHarness();
        harness.Manager.PendingChanged += () => throw new InvalidOperationException("boom");

        var act = async () => await harness.Manager.BeginAsync(NewRequest("s1"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PresenterUnregistered_ShouldConvergePendingToDeny()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        harness.Presentation.Unregister(presenter);

        var resolution = await waiter;
        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        resolution.Reason.Should().Be("呈现端已移除");
    }

    [Fact]
    public async Task SurfaceShrink_ShouldNotConverge()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        presenter.SetSurface(); // 同一 presenter 集合收缩：仅 SurfacedChanged，不 Unregister

        await Task.Delay(200, TestContext.Current.CancellationToken);

        harness.Manager.PendingCount.Should().Be(1);
        harness.Manager.GetAllPending().Should().ContainSingle();

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
    }

    [Fact]
    public async Task PresenterUnregisteredThenReRegisteredWithinGrace_ShouldNotConverge()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(300));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        harness.Presentation.Unregister(presenter);
        harness.Presentation.Register(new TestPresenter("s1"));

        await Task.Delay(600, TestContext.Current.CancellationToken);

        harness.Manager.PendingCount.Should().Be(1);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
    }

    private static PermissionRequest NewRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        PermissionKind = "tool.execute",
        CallId = "call-1"
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(10);
        condition().Should().BeTrue("条件应在超时前满足");
    }

    private sealed class ManagerHarness : IDisposable
    {
        public List<IMessageEvent> PublishedEvents { get; } = new();
        public Mock<ISessionManager> Sessions { get; } = new();
        public ManualSessionEventPublisher SessionEvents { get; } = new();
        public TestOptionsMonitor Options { get; }
        public SurfaceRegistry Presentation { get; } = new();
        public EffectivePermissionPolicy Policy { get; }
        public PermissionRequestManager Manager { get; }

        public ManagerHarness(
            TimeSpan? timeout = null,
            IEnumerable<IPermissionChannel>? channels = null,
            TimeSpan? convergenceGrace = null,
            ISessionGroupManager? groups = null)
        {
            Options = new TestOptionsMonitor(new SeeingAgentOptions());
            Policy = new EffectivePermissionPolicy(Sessions.Object, Options, groups);

            var publisher = new Mock<IExecutionEventPublisher>();
            publisher
                .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
                .Callback<string, IMessageEvent>((_, evt) => PublishedEvents.Add(evt));

            var channelList = (channels ?? Array.Empty<IPermissionChannel>()).ToList();
            Manager = new PermissionRequestManager(
                publisher.Object, Presentation, Policy, channelList, SessionEvents, Options,
                NullLogger<PermissionRequestManager>.Instance,
                timeout ?? TimeSpan.FromMinutes(5),
                convergenceGrace);
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class TestPresenter : ISurfaceProvider
    {
        private IReadOnlyCollection<string> _surface;

        public TestPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<SeeingAgentOptions>
    {
        private readonly List<Action<SeeingAgentOptions, string?>> _listeners = new();

        public TestOptionsMonitor(SeeingAgentOptions value) => CurrentValue = value;

        public SeeingAgentOptions CurrentValue { get; private set; }

        public int ListenerCount
        {
            get
            {
                lock (_listeners)
                    return _listeners.Count;
            }
        }

        public SeeingAgentOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<SeeingAgentOptions, string?> listener)
        {
            lock (_listeners)
                _listeners.Add(listener);
            return new ActionDisposable(() =>
            {
                lock (_listeners)
                    _listeners.Remove(listener);
            });
        }

        public void Set(SeeingAgentOptions value)
        {
            CurrentValue = value;
            Action<SeeingAgentOptions, string?>[] snapshot;
            lock (_listeners)
                snapshot = _listeners.ToArray();
            foreach (var listener in snapshot)
                listener(value, null);
        }
    }

    private sealed class ManualSessionEventPublisher : ISessionEventPublisher
    {
        private readonly List<IObserver<SessionEvent>> _observers = new();

        public IObservable<SessionEvent> Events => new Observable(this);

        public int SubscriptionCount
        {
            get
            {
                lock (_observers)
                    return _observers.Count;
            }
        }

        public void Publish(SessionEvent sessionEvent)
        {
            IObserver<SessionEvent>[] snapshot;
            lock (_observers)
                snapshot = _observers.ToArray();
            foreach (var observer in snapshot)
                observer.OnNext(sessionEvent);
        }

        private sealed class Observable : IObservable<SessionEvent>
        {
            private readonly ManualSessionEventPublisher _owner;

            public Observable(ManualSessionEventPublisher owner) => _owner = owner;

            public IDisposable Subscribe(IObserver<SessionEvent> observer)
            {
                lock (_owner._observers)
                    _owner._observers.Add(observer);
                return new ActionDisposable(() =>
                {
                    lock (_owner._observers)
                        _owner._observers.Remove(observer);
                });
            }
        }
    }

    private sealed class RecordingChannel : IPermissionChannel
    {
        public List<PermissionResolution> Dismissed { get; } = new();

        public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;

        public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default)
        {
            lock (Dismissed)
                Dismissed.Add(resolution);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;

        public ActionDisposable(Action action) => _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>
/// 执行级授权器 / 工厂 / 默认通道窄端口测试（spec §4.2/§4.4、§5.1 按 kind 注入 AllowedScopes）。
/// </summary>
public class ExecutionContextPermissionAuthorizerTests
{
    [Fact]
    public async Task AuthorizeAsync_FilesystemKind_ShouldInjectFilesystemScopes_AndBackfillSession()
    {
        PermissionRequest? captured = null;
        var service = new Mock<IPermissionService>();
        service
            .Setup(s => s.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "r1",
                SessionId = "session-x",
                Decision = PermissionEffect.Allow
            });

        var authorizer = new ExecutionContextPermissionAuthorizer(
            service.Object, "session-x", SessionAutoApprove.Enabled);

        await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = string.Empty,
            PermissionKind = "filesystem.write"
        }, TestContext.Current.CancellationToken);

        authorizer.SessionId.Should().Be("session-x");
        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("session-x");
        captured.Override.Should().Be(SessionAutoApprove.Enabled);
        captured.AllowedScopes.Should().BeEquivalentTo(new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        });
    }

    [Fact]
    public async Task AuthorizeAsync_NonFilesystemKind_ShouldUseDefaultScopes()
    {
        PermissionRequest? captured = null;
        var service = new Mock<IPermissionService>();
        service
            .Setup(s => s.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "r1",
                SessionId = "s1",
                Decision = PermissionEffect.Allow
            });

        var authorizer = new ExecutionContextPermissionAuthorizer(service.Object, "s1");

        await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "shell.execute"
        }, TestContext.Current.CancellationToken);

        captured!.AllowedScopes.Should().BeEquivalentTo(new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session
        });
    }

    [Fact]
    public async Task AuthorizeAsync_RequestOverride_ShouldWinOverBoundOverride()
    {
        PermissionRequest? captured = null;
        var service = new Mock<IPermissionService>();
        service
            .Setup(s => s.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "r1",
                SessionId = "s1",
                Decision = PermissionEffect.Allow
            });

        var authorizer = new ExecutionContextPermissionAuthorizer(
            service.Object, "s1", SessionAutoApprove.Enabled);

        await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Override = SessionAutoApprove.Disabled
        }, TestContext.Current.CancellationToken);

        captured!.Override.Should().Be(SessionAutoApprove.Disabled);
    }

    [Fact]
    public void Create_ShouldBindSessionAndOverride()
    {
        var factory = new DefaultPermissionAuthorizerFactory(Mock.Of<IPermissionService>());

        var authorizer = factory.Create("session-y", SessionAutoApprove.Disabled);

        authorizer.SessionId.Should().Be("session-y");
    }
}

/// <summary>无交互宿主默认通道测试（spec §4.4）。</summary>
public class DenyAllPermissionChannelTests
{
    [Fact]
    public async Task Channel_ShouldNeverAutoApprove_AndNoOpPresentDismiss()
    {
        var channel = new DenyAllPermissionChannel();
        var request = new PermissionRequest { SessionId = "s1", PermissionKind = "tool.execute" };

        channel.TryAutoApprove(request).Should().BeNull();

        await channel.PresentAsync(request, TestContext.Current.CancellationToken);
        await channel.DismissAsync(new PermissionResolution
        {
            RequestId = "r1",
            SessionId = "s1",
            Decision = PermissionEffect.Allow
        }, TestContext.Current.CancellationToken);
    }
}
