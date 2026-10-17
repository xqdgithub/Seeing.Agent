using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// SessionRuntime 控制面（spec §7）：Interrupt / Stop / 查询 / 订阅 / 清理 / 热重载边界。
/// </summary>
public class SessionRuntimeControlTests
{
    /// <summary>假执行体：记录调用次数、可阻塞以制造在途窗口。</summary>
    private sealed class FakeRunner : IExecutionRunner
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);
        public readonly List<string> SessionIds = new();
        public Func<Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            Interlocked.Increment(ref _runCount);
            lock (SessionIds)
                SessionIds.Add(record.SessionId);

            if (OnRunAsync is not null)
                await OnRunAsync();

            record.Status = ExecutionStatus.Completed;
            return record;
        }
    }

    private sealed class RecordingPublisher : IExecutionEventPublisher
    {
        public readonly List<IMessageEvent> Events = new();
        public readonly List<IMessageEvent> Buffered = new();
        public IMessageEvent? SubscribeSentinel { get; set; }

        public void Publish(string sessionId, IMessageEvent evt)
        {
            lock (Events)
                Events.Add(evt);
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Stream();

        private async IAsyncEnumerable<IMessageEvent> Stream()
        {
            await Task.CompletedTask;
            if (SubscribeSentinel is not null)
                yield return SubscribeSentinel;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
        {
            lock (Buffered)
                return Buffered.ToArray();
        }

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }

        public Seeing.Agent.Abstractions.Events.ILiveEventSubscription SubscribeLive(string sessionId)
            => Seeing.Agent.Core.Execution.LiveEventSubscription.Completed;
    }

    private static SessionRuntime CreateRuntime(
        out SessionManager sessionManager,
        out FakeRunner runner,
        out RecordingPublisher publisher,
        ISessionGroupManager? groupManager = null,
        TimeSpan? sessionIdleTimeout = null,
        SessionManager? existingSessionManager = null,
        int maxConcurrent = -1)
    {
        sessionManager = existingSessionManager ?? new SessionManager(logger: NullLogger<SessionManager>.Instance);
        runner = new FakeRunner();
        publisher = new RecordingPublisher();

        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton<IExecutionEventPublisher>(publisher);
        if (groupManager is not null)
            services.AddSingleton(groupManager);
        var provider = services.BuildServiceProvider();

        var options = new ExecutionOptions
        {
            MaxConcurrentExecutions = maxConcurrent,
            SessionIdleTimeout = sessionIdleTimeout ?? TimeSpan.FromMinutes(30),
            // 拉长清理间隔，避免定时器在测试期间自动触发
            CleanupInterval = TimeSpan.FromHours(1)
        };

        return new SessionRuntime(
            runner, publisher, provider, options, NullLogger<SessionRuntime>.Instance);
    }

    private static SessionGroupManager CreateGroupManager(SessionManager sessionManager) =>
        new(
            sessionManager,
            new FileSessionGroupStore(
                Path.Combine(Path.GetTempPath(), "seeing-grp-ctrl-tests", Guid.NewGuid().ToString("N"))),
            new SessionForker(NullLogger<SessionForker>.Instance, sessionManager),
            NullLogger<SessionGroupManager>.Instance);

    private static InboxMessage Msg(
        string sessionId,
        InboxDelivery delivery,
        InboxPresentation presentation = InboxPresentation.User,
        string body = "hello") => new()
        {
            TargetSessionId = sessionId,
            Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "test" },
            Body = body,
            Delivery = delivery,
            Presentation = presentation
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("WaitUntil condition not met");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Interrupt_ShouldCancelCurrentExecution_AndLoopContinues()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        // 执行中到达的第二条输入，Interrupt 后应被下一轮消费
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "queued"));

        var interrupted = await runtime.InterruptAsync("s1");
        interrupted.Should().BeTrue();

        // 释放首轮执行体，循环应继续消费剩余未读（runner 再被调一次）
        release.SetResult();
        await WaitUntilAsync(() => runner.RunCount >= 2);
    }

    [Fact]
    public async Task Interrupt_NoActiveExecution_ShouldReturnFalse()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var interrupted = await runtime.InterruptAsync("s1");

        interrupted.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_ShouldHaltLoop_AndLeaveUnreadUnconsumed()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        // 未读的 Inject，Stop 后应保持 consumed=false（不消费）
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "leftover"));

        var cancelled = await runtime.StopAsync("s1");
        cancelled.Should().Be(1);

        release.SetResult();
        await Task.Delay(300);

        // 循环已终止：runner 不再被调
        runner.RunCount.Should().Be(1);

        var leftover = sessionManager.Get("s1")!.Messages
            .Single(m => m.Content == "leftover");
        leftover.Metadata![InboxMetadataKeys.Consumed].Should().Be(false);
    }

    [Fact]
    public async Task StopThenDeliver_ShouldReactivate()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.StopAsync("s1");

        // 停止不是永久冻结：新投递应清除停止态并重新启动循环
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));

        await WaitUntilAsync(() => runner.RunCount == 1);
        sessionManager.Get("s1")!.Metadata.Should().NotContainKey(SessionControlKeys.StopReason);
    }

    [Fact]
    public async Task Stop_DuringGlobalConcurrencyWait_ShouldPreventThatRound()
    {
        // 全局并发=1：s1 独占并发槽并阻塞执行体，s2 的循环将在全局并发等待窗口内排队。
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _, maxConcurrent: 1);
        await sessionManager.EnsureSessionAsync("s1");
        await sessionManager.EnsureSessionAsync("s2");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        // s2 唤醒：其循环已完成 gate 段（标记 consumed）并阻塞在全局并发等待。
        await runtime.DeliverAsync(Msg("s2", InboxDelivery.Wake, body: "s2"));
        await WaitUntilAsync(() =>
            sessionManager.Get("s2")!.Messages.Any(m =>
                m.Metadata?.ContainsKey(InboxMetadataKeys.Presentation) == true
                && Equals(m.Metadata![InboxMetadataKeys.Consumed], true)));

        // 在全局并发等待窗口内 Stop s2：此时 s2 尚无 record，取消 Cts 无从生效。
        await runtime.StopAsync("s2");

        // 放行 s1 → 全局并发放行 s2，但 Stop 校验应使其不执行本轮。
        release.SetResult();
        await Task.Delay(400);

        runner.SessionIds.Should().ContainSingle().Which.Should().Be("s1");
        runtime.GetOverview("s2").CurrentExecution.Should().BeNull();
    }

    [Fact]
    public async Task Stop_ShouldCascadeToChildren_WithStopReason()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var groupManager = CreateGroupManager(sessionManager);
        var runtime = CreateRuntime(
            out _, out var runner, out _, groupManager,
            existingSessionManager: sessionManager);
        var parent = await sessionManager.EnsureSessionAsync("parent");
        var child = await groupManager.CreateChildAsync(
            "parent", "general", "child", Array.Empty<SessionPermissionRule>(), null);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("parent", InboxDelivery.Wake));
        await runtime.DeliverAsync(Msg(child.Id, InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("parent").CurrentExecution is not null);
        await WaitUntilAsync(() => runtime.GetOverview(child.Id).CurrentExecution is not null);

        var cancelled = await runtime.StopAsync("parent");
        cancelled.Should().Be(2);

        // 子会话先写标记并落盘
        sessionManager.Get(child.Id)!.Metadata[SessionControlKeys.StopReason]
            .Should().Be(SessionControlKeys.StopReasonCascadedFromParent);

        release.SetResult();
        await Task.Delay(200);

        runner.RunCount.Should().Be(2);
        parent.Should().NotBeNull();
    }

    [Fact]
    public async Task WaitForIdle_StoppedSession_ShouldReturnImmediately()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out _);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.StopAsync("s1");

        var task = runtime.WaitForIdleAsync("s1");
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetOverview_ShouldExposeCurrentAndLastExecution()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        var running = runtime.GetOverview("s1");
        running.CurrentExecution.Should().NotBeNull();
        running.CurrentExecution!.Status.Should().Be(ExecutionStatus.Running);
        running.HasActiveExecution.Should().BeTrue();

        release.SetResult();
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is null);

        var idle = runtime.GetOverview("s1");
        idle.CurrentExecution.Should().BeNull();
        idle.LastExecution.Should().NotBeNull();
        idle.LastExecution!.Status.Should().Be(ExecutionStatus.Completed);
    }

    [Fact]
    public void GetOverview_NoState_ShouldReturnEmpty()
    {
        var runtime = CreateRuntime(out _, out _, out _);

        var overview = runtime.GetOverview("missing");

        overview.CurrentExecution.Should().BeNull();
        overview.LastExecution.Should().BeNull();
        overview.PendingInputCount.Should().Be(0);
        overview.HasActiveExecution.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAllInFlight_ShouldStopAllActiveSessions()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");
        await sessionManager.EnsureSessionAsync("s2");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await runtime.DeliverAsync(Msg("s2", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);
        await WaitUntilAsync(() => runtime.GetOverview("s2").CurrentExecution is not null);

        var cancelled = await runtime.CancelAllInFlightAsync();
        cancelled.Should().Be(2);

        release.SetResult();
        await Task.Delay(200);

        runner.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task InFlightBoundary_ShouldReflectActiveExecutions()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        runtime.HasInFlight().Should().BeFalse();
        runtime.ListInFlightExecutionIds().Should().BeEmpty();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.HasInFlight());

        runtime.HasAnyActiveExecution().Should().BeTrue();
        runtime.ListInFlightExecutionIds().Should().ContainSingle();

        release.SetResult();
        await WaitUntilAsync(() => !runtime.HasInFlight());
    }

    [Fact]
    public async Task ReconcileIncompleteTasks_ShouldMarkRunningTaskCancelled()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out _);
        var session = await sessionManager.EnsureSessionAsync("s1");

        var msg = SessionMessage.AssistantMessage("thinking");
        msg.ToolCalls = ImmutableList.Create(new SessionToolCall
        {
            Id = "t1",
            Name = "task",
            Status = "running",
            TaskId = "child-orphan"
        });
        session.AddMessage(msg);

        var count = await runtime.ReconcileIncompleteTasksAsync("s1");

        count.Should().Be(1);
        var tc = sessionManager.Get("s1")!.Messages[0].ToolCalls![0];
        tc.Status.Should().Be("cancelled");
        tc.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubscribeAndBufferedEvents_ShouldDelegateToPublisher()
    {
        var runtime = CreateRuntime(out _, out _, out var publisher);
        var sentinel = new InboxNoticeEvent { SessionId = "s1", SenderKind = "system" };
        publisher.SubscribeSentinel = sentinel;
        publisher.Buffered.Add(sentinel);

        runtime.GetBufferedEvents("s1").Should().ContainSingle().Which.Should().BeSameAs(sentinel);

        var collected = new List<IMessageEvent>();
        await foreach (var evt in runtime.SubscribeEvents("s1"))
            collected.Add(evt);

        collected.Should().ContainSingle().Which.Should().BeSameAs(sentinel);
    }

    [Fact]
    public async Task CleanupIdleSessions_ShouldReclaimStaleState()
    {
        var runtime = CreateRuntime(
            out var sessionManager, out var runner, out _,
            sessionIdleTimeout: TimeSpan.Zero);
        await sessionManager.EnsureSessionAsync("s1");

        // 跑一轮以建立运行态（LastExecution 保留）
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").LastExecution is not null);
        runtime.GetOverview("s1").LastExecution.Should().NotBeNull();

        // 等循环完全退出（从 _activeLoops 移除自身）
        await Task.Delay(300);
        runtime.CleanupIdleSessions();

        // 超时空闲 → 运行态被回收（无 state → 空 Overview）
        var overview = runtime.GetOverview("s1");
        overview.CurrentExecution.Should().BeNull();
        overview.LastExecution.Should().BeNull();
        overview.PendingInputCount.Should().Be(0);
        runner.RunCount.Should().Be(1);
    }
}
