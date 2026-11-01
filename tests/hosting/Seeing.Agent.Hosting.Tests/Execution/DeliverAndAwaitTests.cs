using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

/// <summary>
/// <c>DeliverAndAwaitAsync</c>：把「投递」与「等待本次投递产生的那次执行的终态」合成一个契约。
/// <para>
/// 取代既有的「投递 + 25ms 轮询 + 猜 LastExecution」做法：等待必须绑定 executionId，
/// 连续两次投递不得互相串台，取消与投递失败都必须以异常收口而不是静默返回。
/// </para>
/// </summary>
public class DeliverAndAwaitTests
{
    [Fact]
    public async Task DeliverAndAwait_Should_Return_Outcome_Of_That_Execution()
    {
        var runtime = BuildRuntime(out _, out _);

        var outcome = await runtime.DeliverAndAwaitAsync(
            MakeMessage("ses_a", "hello"), CancellationToken.None);

        outcome.SessionId.Should().Be("ses_a");
        outcome.ExecutionId.Should().NotBeNullOrEmpty();
        outcome.Status.Should().Be(ExecutionStatus.Completed);
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Not_Be_Ambiguous_Across_Two_Deliveries()
    {
        var runtime = BuildRuntime(out _, out _);

        var first = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_b", "one"), CancellationToken.None);
        var second = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_b", "two"), CancellationToken.None);

        first.ExecutionId.Should().NotBe(second.ExecutionId);
        first.SessionId.Should().Be("ses_b");
        second.SessionId.Should().Be("ses_b");
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Throw_On_Cancellation()
    {
        // 执行体阻塞 5s；调用方 200ms 后取消 —— 必须抛 OCE，而不是等安全超时后静默返回。
        var runtime = BuildSlowRuntime(out _, out _);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await runtime.DeliverAndAwaitAsync(
            MakeMessage("ses_c", "slow"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // 收尾：取消在途执行体，避免测试进程残留后台循环。
        await runtime.StopAsync("ses_c");
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Throw_When_Delivery_Fails()
    {
        var runtime = BuildRuntime(out _, out _);

        var act = async () => await runtime.DeliverAndAwaitAsync(
            new InboxMessage { TargetSessionId = "", Sender = MakeSender(), Body = "x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Reject_NonWake_Delivery()
    {
        // Inject/Notify 不产生执行，等待必然落空：显式拒绝，避免调用方永久挂起。
        var runtime = BuildRuntime(out _, out _);
        var message = MakeMessage("ses_d", "no-run") with { Delivery = InboxDelivery.Inject };

        var act = async () => await runtime.DeliverAndAwaitAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Throw_When_Session_Stopped_While_Waiting()
    {
        // 会话被 Stop：未完成的等待必须被取消/失败，而不是永久挂起。
        var runtime = BuildSlowRuntime(out _, out _);

        var waiting = runtime.DeliverAndAwaitAsync(MakeMessage("ses_e", "slow"), CancellationToken.None);
        await Task.Delay(100);
        await runtime.StopAsync("ses_e");

        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Report_SubAgent_Session_As_Not_TopLevel()
    {
        // 顶层判定真值来源为 SessionData.Kind；子 Agent 会话必须报 false。
        var runtime = BuildRuntime(out var sessionManager, out _);
        var session = await sessionManager.EnsureSessionAsync("ses_sub");
        session.Kind = SessionKind.SubAgent;

        var outcome = await runtime.DeliverAndAwaitAsync(
            MakeMessage("ses_sub", "work"), CancellationToken.None);

        outcome.IsTopLevel.Should().BeFalse();
        outcome.SessionId.Should().Be("ses_sub");
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Report_Root_Session_As_TopLevel()
    {
        // 反向断言：根会话必须报 true。缺这条时，一个恒返回 false 的实现能通过全部测试。
        var runtime = BuildRuntime(out var sessionManager, out _);
        var session = await sessionManager.EnsureSessionAsync("ses_root");
        session.Kind.Should().Be(SessionKind.Root);

        var outcome = await runtime.DeliverAndAwaitAsync(
            MakeMessage("ses_root", "hi"), CancellationToken.None);

        outcome.IsTopLevel.Should().BeTrue();
        outcome.ParentSessionId.Should().BeNull();
    }

    [Theory]
    [InlineData(InboxPresentation.Assistant)]
    [InlineData(InboxPresentation.None)]
    public async Task DeliverAndAwait_Should_Reject_Born_Consumed_Presentation_Not_Hang(
        InboxPresentation presentation)
    {
        // Critical 1 回归：这两种 presentation 落盘即 consumed=true，永不进入未读集合，
        // 消费循环不会为它开一轮。入口必须拒绝，否则 await 永久挂起。
        var runtime = BuildRuntime(out _, out var runner);
        var message = MakeMessage("ses_born", "payload") with { Presentation = presentation };

        var act = async () => await runtime
            .DeliverAndAwaitAsync(message, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().ThrowAsync<ArgumentException>();
        runner.RunCount.Should().Be(0, "被拒绝的投递不应启动任何执行");
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Report_Failed_Status_When_Runner_Throws()
    {
        // Critical 2 回归：_runner 抛异常时 record 仍是 Running。
        // 若不落定状态，outcome 会把「非终态」当终态上报。
        var runtime = CreateRuntime(
            runHook: _ => throw new InvalidOperationException("runner boom"),
            out _, out _);

        var outcome = await runtime
            .DeliverAndAwaitAsync(MakeMessage("ses_fail", "x"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Status.Should().Be(ExecutionStatus.Failed);
        outcome.Status.Should().NotBe(ExecutionStatus.Running);
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Return_Cancelled_Status_When_Interrupted()
    {
        // Interrupt 走 OCE 路径：状态必须落定为 Cancelled，而不是残留 Running。
        var runtime = BuildRuntime(out _, out var runner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = async token =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        };

        var waiting = runtime.DeliverAndAwaitAsync(MakeMessage("ses_int", "x"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.InterruptAsync("ses_int");

        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        outcome.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task DeliverAndAwait_After_Stop_Should_Restart_Execution_Not_Hang()
    {
        // 既有语义：Wake 投递会在 gate 内清除停止态并重新拉起消费循环。
        // 因此 Stop 之后再投递应当正常执行并返回 outcome——关键是**不能挂起**。
        var runtime = BuildRuntime(out _, out var runner);
        await runtime.StopAsync("ses_stopped");

        var outcome = await runtime
            .DeliverAndAwaitAsync(MakeMessage("ses_stopped", "after-stop"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Status.Should().Be(ExecutionStatus.Completed);
        outcome.SessionId.Should().Be("ses_stopped");
        runner.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task DeliverAndAwait_Pending_Wait_Should_Be_Cancelled_On_Dispose()
    {
        // Important 3 回归：运行时释放时在途等待必须被取消，不能永久挂起。
        var runtime = CreateRuntime(runHook: null, out _, out var runner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = async token =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        };

        var waiting = runtime.DeliverAndAwaitAsync(MakeMessage("ses_dispose", "x"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync();

        var act = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeliverAndAwait_Sequential_Deliveries_Should_Run_Separate_Executions()
    {
        // Important 6：不只断言「都返回 Completed」，还要证明两次投递真的各跑了一轮
        // （否则「5 次投递全返回同一 outcome」的实现也能骗过测试）。
        var runtime = BuildRuntime(out _, out var runner);

        var first = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_seq", "one"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var second = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_seq", "two"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        first.ExecutionId.Should().NotBe(second.ExecutionId);
        runner.RunCount.Should().Be(2, "两次顺序投递必须各自触发一轮执行，不得合并成一次");
    }

    [Fact]
    public async Task DeliverAndAwait_Concurrent_Deliveries_Should_Run_As_Many_Runs_As_Observed()
    {
        // Important 6：并发投递可能被合并成少数几轮，但执行的轮次数必须与
        // 「实际返回的不同 executionId 数量」一致，且每个等待者拿到的 id 都必须真实存在。
        var runtime = BuildRuntime(out _, out var runner);

        var pending = Enumerable.Range(0, 5)
            .Select(i => runtime.DeliverAndAwaitAsync(MakeMessage("ses_g", $"m{i}"), CancellationToken.None))
            .ToArray();

        var outcomes = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));

        outcomes.Should().OnlyContain(o => o.SessionId == "ses_g");
        outcomes.Should().OnlyContain(o => o.Status == ExecutionStatus.Completed);
        outcomes.Should().OnlyContain(o => !string.IsNullOrEmpty(o.ExecutionId));

        // 每个 outcome 对应的 executionId 都必须由一次真实执行产生。
        var distinctRuns = outcomes.Select(o => o.ExecutionId).Distinct().Count();
        distinctRuns.Should().Be(runner.RunCount);
        runner.RunCount.Should().BeInRange(1, 5);
    }

    // ===== 测试脚手架 =====

    /// <summary>假执行体：可注入阻塞逻辑；默认立刻完成并把记录置为 Completed。</summary>
    private sealed class FakeRunner : IExecutionRunner
    {
        private int _runCount;

        /// <summary>本执行体被调用（跑轮次）的次数。</summary>
        public int RunCount => Volatile.Read(ref _runCount);

        /// <summary>阻塞钩子（参数为本次执行的取消令牌）。</summary>
        public Func<CancellationToken, Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            Interlocked.Increment(ref _runCount);

            if (OnRunAsync is not null)
                await OnRunAsync(executionToken).ConfigureAwait(false);

            record.Status = ExecutionStatus.Completed;
            record.CompletedAt = DateTime.UtcNow;
            return record;
        }
    }

    private static SessionRuntime BuildRuntime(
        out SessionManager sessionManager, out FakeRunner runner) =>
        CreateRuntime(runHook: null, out sessionManager, out runner);

    private static SessionRuntime BuildSlowRuntime(
        out SessionManager sessionManager, out FakeRunner runner) =>
        CreateRuntime(
            runHook: token => Task.Delay(TimeSpan.FromSeconds(5), token),
            out sessionManager,
            out runner);

    private static SessionRuntime CreateRuntime(
        Func<CancellationToken, Task> runHook,
        out SessionManager sessionManager,
        out FakeRunner runner)
    {
        sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        runner = new FakeRunner { OnRunAsync = runHook };

        var publisher = new Mock<IExecutionEventPublisher>();
        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        var provider = services.BuildServiceProvider();

        return new SessionRuntime(
            runner,
            publisher.Object,
            provider,
            new ExecutionOptions { MaxConcurrentExecutions = -1 },
            NullLogger<SessionRuntime>.Instance);
    }

    private static InboxSender MakeSender() =>
        new() { Kind = InboxSenderKind.System, Id = "test" };

    private static InboxMessage MakeMessage(string sessionId, string body) => new()
    {
        TargetSessionId = sessionId,
        Sender = MakeSender(),
        Body = body,
        Delivery = InboxDelivery.Wake,
        Presentation = InboxPresentation.User
    };
}
