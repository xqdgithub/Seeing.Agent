using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.E2E.Tests;

/// <summary>
/// 会话入站运行时（SessionRuntime + ISessionDirectory）端到端场景，
/// 覆盖 spec §13 关键时序（A–F）：接管 / 完成上报 / 合并消费 / 中断 / 停止。
/// <para>
/// 采用真实 <see cref="SessionRuntime"/> + 真实 <see cref="SessionManager"/> /
/// <see cref="SessionGroupManager"/>，仅以 fake <see cref="IExecutionRunner"/>
/// 替换执行体，验证运行时层时序（不拉起完整 LLM/工具栈）。
/// </para>
/// </summary>
public class SessionInboxE2ETests
{
    /// <summary>假执行体：记录调用次数、会话与批次，并可阻塞以制造在途窗口。</summary>
    private sealed class FakeRunner : IExecutionRunner
    {
        private int _runCount;
        private int _active;
        private int _maxActive;

        public int RunCount => Volatile.Read(ref _runCount);
        public int MaxActive => Volatile.Read(ref _maxActive);

        public readonly List<InboxBatch> Batches = new();
        public readonly List<string> SessionIds = new();

        public Func<Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            Interlocked.Increment(ref _runCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);

            lock (Batches)
            {
                Batches.Add(batch);
                SessionIds.Add(record.SessionId);
            }

            try
            {
                if (OnRunAsync is not null)
                    await OnRunAsync();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            record.Status = ExecutionStatus.Completed;
            return record;
        }

        private void UpdateMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maxActive)))
            {
                if (Interlocked.CompareExchange(ref _maxActive, value, current) == current)
                    break;
            }
        }
    }

    private sealed class RecordingPublisher : IExecutionEventPublisher
    {
        public readonly List<IMessageEvent> Events = new();

        public void Publish(string sessionId, IMessageEvent evt)
        {
            lock (Events)
                Events.Add(evt);
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Empty();

        private static async IAsyncEnumerable<IMessageEvent> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
            => Array.Empty<IMessageEvent>();

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }
    }

    private static SessionRuntime CreateRuntime(
        out SessionManager sessionManager,
        out SessionGroupManager groupManager,
        out FakeRunner runner,
        out RecordingPublisher publisher)
    {
        sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        groupManager = new SessionGroupManager(
            sessionManager,
            new FileSessionGroupStore(
                Path.Combine(Path.GetTempPath(), "seeing-e2e-tests", Guid.NewGuid().ToString("N"))),
            new SessionForker(NullLogger<SessionForker>.Instance, sessionManager),
            NullLogger<SessionGroupManager>.Instance);
        runner = new FakeRunner();
        publisher = new RecordingPublisher();

        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton<ISessionGroupManager>(groupManager);
        services.AddSingleton<IExecutionEventPublisher>(publisher);
        var provider = services.BuildServiceProvider();

        var options = new ExecutionOptions
        {
            MaxConcurrentExecutions = -1,
            CleanupInterval = TimeSpan.FromHours(1)
        };

        return new SessionRuntime(
            runner, publisher, provider, options, NullLogger<SessionRuntime>.Instance);
    }

    private static InboxMessage Msg(
        string sessionId,
        InboxDelivery delivery,
        InboxPresentation presentation = InboxPresentation.User,
        InboxSenderKind senderKind = InboxSenderKind.System,
        string? senderId = "test",
        string body = "hello") => new()
    {
        TargetSessionId = sessionId,
        Sender = new InboxSender { Kind = senderKind, Id = senderId },
        Body = body,
        Delivery = delivery,
        Presentation = presentation
    };

    /// <summary>复刻 TaskTool 后台完成上报：按子会话 Owner 决定 Wake/Notify。</summary>
    private static Task<InboxResult> ReportCompletionAsync(
        ISessionRuntime runtime, string parentId, string childId, SessionOwner owner) =>
        runtime.DeliverAsync(new InboxMessage
        {
            TargetSessionId = parentId,
            Sender = new InboxSender { Kind = InboxSenderKind.Session, Id = childId },
            Body = "子任务已完成",
            Delivery = owner == SessionOwner.Human ? InboxDelivery.Notify : InboxDelivery.Wake,
            Presentation = owner == SessionOwner.Human ? InboxPresentation.None : InboxPresentation.System,
            SourceTag = SystemReminder.Sources.Task,
            Kind = SystemReminder.Kinds.Completed
        });

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

    // ============================ A. 人介入并接管 ============================

    [Fact]
    public async Task HumanIntervenesChild_ShouldTakeOverOwnership_AndDriveChild()
    {
        var runtime = CreateRuntime(out var sessionManager, out var groupManager, out var runner, out _);
        var parent = await sessionManager.EnsureSessionAsync("parent");
        var child = await groupManager.CreateChildAsync(
            "parent", "general", "child", Array.Empty<SessionPermissionRule>(), null);

        child.GetControl().Owner.Should().Be(SessionOwner.Parent);

        var result = await runtime.DeliverAsync(
            Msg(child.Id, InboxDelivery.Wake, senderKind: InboxSenderKind.Human, senderId: "user-1", body: "让我来处理"));

        result.Success.Should().BeTrue();
        await WaitUntilAsync(() => runner.RunCount == 1);

        // 接管：Owner → Human，记录接管人
        var control = sessionManager.Get(child.Id)!.GetControl();
        control.Owner.Should().Be(SessionOwner.Human);
        control.TakenOverBy.Should().Be("user-1");

        runner.SessionIds.Should().ContainSingle().Which.Should().Be(child.Id);
        parent.Should().NotBeNull();
    }

    // ============================ B. 子完成（未接管）→ 唤醒父 ============================

    [Fact]
    public async Task ChildCompleted_ParentOwned_ShouldWakeParentWithUnreadInput()
    {
        var runtime = CreateRuntime(out var sessionManager, out var groupManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("parent");
        var child = await groupManager.CreateChildAsync(
            "parent", "general", "child", Array.Empty<SessionPermissionRule>(), null);
        child.GetControl().Owner.Should().Be(SessionOwner.Parent);

        var result = await ReportCompletionAsync(runtime, "parent", child.Id, SessionOwner.Parent);

        // 投递时（gate 内计算）父会话存在 1 条未读 Wake 输入
        result.Success.Should().BeTrue();
        result.PendingInputCount.Should().Be(1);

        await WaitUntilAsync(() => runner.SessionIds.Contains("parent"));

        var parentMsg = sessionManager.Get("parent")!.Messages.Should().ContainSingle().Subject;
        parentMsg.Metadata![InboxMetadataKeys.Delivery].Should().Be("wake");
        parentMsg.Metadata![InboxMetadataKeys.Presentation].Should().Be("system");
        parentMsg.Metadata![InboxMetadataKeys.SenderKind].Should().Be("session");
        parentMsg.Metadata![InboxMetadataKeys.SenderId].Should().Be(child.Id);
    }

    // ============================ C. 子完成（已接管）→ 仅 UI 通知 ============================

    [Fact]
    public async Task ChildCompleted_HumanOwned_ShouldOnlyNotifyParent_WithoutWaking()
    {
        var runtime = CreateRuntime(out var sessionManager, out var groupManager, out var runner, out var publisher);
        await sessionManager.EnsureSessionAsync("parent");
        var child = await groupManager.CreateChildAsync(
            "parent", "general", "child", Array.Empty<SessionPermissionRule>(), null);

        // 人先接管子会话
        await runtime.DeliverAsync(
            Msg(child.Id, InboxDelivery.Wake, senderKind: InboxSenderKind.Human, senderId: "user-1"));
        await WaitUntilAsync(() => runner.RunCount == 1);
        sessionManager.Get(child.Id)!.GetControl().Owner.Should().Be(SessionOwner.Human);

        var runsBefore = runner.RunCount;
        var result = await ReportCompletionAsync(runtime, "parent", child.Id, SessionOwner.Human);

        result.Success.Should().BeTrue();
        result.PendingInputCount.Should().Be(0);

        await Task.Delay(200);

        // 父会话无落盘、无唤醒；只发布 InboxNoticeEvent
        sessionManager.Get("parent")!.Messages.Should().BeEmpty();
        runner.RunCount.Should().Be(runsBefore);
        lock (publisher.Events)
            publisher.Events.Should().ContainSingle(e => e is InboxNoticeEvent);
    }

    // ============================ D. 先插三条再唤醒 ============================

    [Fact]
    public async Task InjectThreeThenWake_ShouldRunOnce_ConsumingAllFour()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "a"));
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "b"));
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "c"));

        // 三次 Inject 不触发循环
        await Task.Delay(200);
        runner.RunCount.Should().Be(0);

        var wake = await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake, body: "wake"));
        wake.PendingInputCount.Should().Be(4);

        await WaitUntilAsync(() => runner.RunCount >= 1);
        await Task.Delay(200);

        runner.RunCount.Should().Be(1);
        InboxBatch batch;
        lock (runner.Batches)
            batch = runner.Batches[0];
        batch.Inputs.Should().HaveCount(4);
        batch.HasUserInput.Should().BeTrue();
        batch.FirstUserText.Should().Be("a");

        // 单一清除点：本批全部置 consumed=true
        sessionManager.Get("s1")!.Messages
            .Where(m => m.Metadata?.ContainsKey(InboxMetadataKeys.Presentation) == true)
            .Should().OnlyContain(m => Equals(m.Metadata![InboxMetadataKeys.Consumed], true));
    }

    // ============================ E. 中断后继续 ============================

    [Fact]
    public async Task Interrupt_ShouldCancelCurrentRound_AndLoopContinuesConsumingUnread()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        // 执行中到达的第二条输入，Interrupt 后应被下一轮消费
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "queued"));

        var interrupted = await runtime.InterruptAsync("s1");
        interrupted.Should().BeTrue();

        release.SetResult();
        await WaitUntilAsync(() => runner.RunCount >= 2);

        // 循环继续消费剩余未读
        runner.RunCount.Should().Be(2);
    }

    // ============================ F. 停止与重新激活 ============================

    [Fact]
    public async Task Stop_ShouldHaltLoop_LeaveUnreadUnconsumed_ThenRedeliverReactivates()
    {
        var runtime = CreateRuntime(out var sessionManager, out _, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        // Stop 后应保持 consumed=false 的剩余未读
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "leftover"));

        var cancelled = await runtime.StopAsync("s1");
        cancelled.Should().Be(1);

        release.SetResult();
        await Task.Delay(300);

        // 循环已终止：runner 不再被调；剩余未读保持未消费
        runner.RunCount.Should().Be(1);
        var leftover = sessionManager.Get("s1")!.Messages.Single(m => m.Content == "leftover");
        leftover.Metadata![InboxMetadataKeys.Consumed].Should().Be(false);

        // 再次 Deliver 重新激活（停止不是永久冻结）
        runner.OnRunAsync = null;
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake, body: "again"));
        await WaitUntilAsync(() => runner.RunCount >= 2);

        sessionManager.Get("s1")!.Metadata.Should().NotContainKey(SessionControlKeys.StopReason);
    }
}
