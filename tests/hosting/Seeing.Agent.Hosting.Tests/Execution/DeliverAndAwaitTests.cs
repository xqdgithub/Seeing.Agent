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
        var executionTokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                executionTokenCancelled.TrySetResult();
                throw;
            }
        };

        var waiting = runtime.DeliverAndAwaitAsync(MakeMessage("ses_dispose", "x"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var releasedAt = System.Diagnostics.Stopwatch.StartNew();
        await runtime.DisposeAsync();

        var act = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        releasedAt.Stop();

        // 时效断言：等待必须在 Dispose 后立刻收口，而不是等执行体的 5s 自然跑完。
        // 少了这条，「遍历 _states 取消 Cts」那半边修复即使被删掉，测试也只会变慢、不会变红。
        releasedAt.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1),
            "Dispose 必须主动取消在途执行令牌，而非等执行体自然结束");

        // 令牌断言：直接钉住「执行体收到的 token 确实被取消」这一事实。
        await executionTokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TeardownLoop_Should_Not_Clear_Another_Loops_Record()
    {
        // 回归 1 的**接线**单测（直接调用真实收尾入口 TeardownLoop，而不是孤立 helper）：
        // 旧循环收尾时，若 CurrentExecution 已被**新循环**写入，旧循环必须放手（身份不匹配）。
        // 无条件置 null 会抹掉新循环的在途记录，使 Stop/Interrupt/Dispose 读不到 Cts、
        // 取消对该轮彻底失效，CleanupIdleSessions 也会误判空闲清掉运行态。
        var runtime = BuildRuntime(out _, out _);

        var oldLoopRecord = Record("exec_old");
        var newLoopRecord = Record("exec_new");
        var oldLoopHandle = NewLoopHandle();

        // 场景：旧循环退出时，状态里已是新循环的记录。
        runtime.SetCurrentExecutionForTest("ses_race", newLoopRecord);
        runtime.TeardownLoop("ses_race", oldLoopHandle, oldLoopRecord);

        runtime.GetCurrentExecutionForTest("ses_race").Should().BeSameAs(
            newLoopRecord, "旧循环不得清掉新循环仍在途的记录");

        // 自己的记录仍然要能清掉（否则会永久残留，让 Stop 误以为有在途执行）。
        runtime.TeardownLoop("ses_race", oldLoopHandle, newLoopRecord);
        runtime.GetCurrentExecutionForTest("ses_race").Should().BeNull(
            "本循环自己的在途记录必须被清理，否则 Stop 会误以为仍有在途执行");
    }

    [Fact]
    public void TeardownLoop_Without_Owned_Record_Must_Leave_CurrentExecution_Intact()
    {
        // 变异 2 的护栏：若 loopOwnedRecord 从未赋值（等价于 owned 恒为 null），
        // 收尾就成了「永不清理」——本循环自己的记录会永久残留，Stop 之后仍报「有在途执行」。
        // 这条断言钉住「正常的单循环退出必须清掉自己的记录」。
        var runtime = BuildRuntime(out _, out _);
        var handle = NewLoopHandle();

        var owned = Record("exec_owned");
        runtime.SetCurrentExecutionForTest("ses_own", owned);

        // 正常路径：传入本循环的记录 → 必须清掉。
        runtime.TeardownLoop("ses_own", handle, owned);
        runtime.GetCurrentExecutionForTest("ses_own").Should().BeNull();

        // 反向：传 null（等价 record 未赋值）时不应清别人 —— 但此时也没有「自己的记录」可留。
        var foreign = Record("exec_foreign");
        runtime.SetCurrentExecutionForTest("ses_own2", foreign);
        runtime.TeardownLoop("ses_own2", handle, owned: null);
        runtime.GetCurrentExecutionForTest("ses_own2").Should().BeSameAs(
            foreign, "owned 为 null 时不得清空任何记录");
    }

    [Fact]
    public async Task RunLoop_Should_Hand_Its_Own_Record_To_Teardown()
    {
        // 变异 2 的护栏：RunLoopAsync 必须把本循环最后创建的记录交给 TeardownLoop。
        // 若 loopOwnedRecord 永不赋值（等价恒传 null），计数量恒为 0，本用例失败。
        // 注意不能只靠「CurrentExecution 最终为 null」——逐轮 finally 已经清过，
        // 外层的 null 赋值缺失会被完全掩盖。
        var runtime = BuildRuntime(out _, out _);
        runtime.TeardownWithOwnedCount.Should().Be(0, "初始应无收尾记录");

        await runtime.DeliverAndAwaitAsync(MakeMessage("ses_wire", "hi"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // 直接等计数量本身，而不是等 HasAnyActiveExecution。
        // 二者不是同一原子事实：CurrentExecution 在逐轮 finally 就已被清空，
        // 而计数递增发生在其后的 TeardownLoop —— 等前者会在收尾尚未执行时提前返回（实测 3.3% 假红）。
        await WaitUntilAsync(() => runtime.TeardownWithOwnedCount > 0, TimeSpan.FromSeconds(10));

        runtime.TeardownWithOwnedCount.Should().BeGreaterThan(
            0, "RunLoopAsync 必须把本轮记录交给收尾，而不是恒传 null");
    }

    [Fact]
    public void TeardownLoop_Should_Remove_Own_Loop_Entry_But_Not_Another()
    {
        // 变异 6 的护栏：收尾必须按**句柄值匹配**摘除 _activeLoops。
        // 传错句柄时不能误删别人条目，同时自己的条目必须被摘掉——否则条目泄漏，
        // 该会话会被永久认为「有活跃循环」，投递不再启动新循环。
        var runtime = BuildRuntime(out _, out _);

        var ownHandle = NewLoopHandle();
        var otherHandle = NewLoopHandle();
        runtime.RegisterActiveLoopForTest("ses_leak", ownHandle);
        runtime.RegisterActiveLoopForTest("ses_leak_other", otherHandle);

        // 用自己的句柄收尾：自己的条目必须被摘除。
        runtime.TeardownLoop("ses_leak", ownHandle, owned: null);
        runtime.HasActiveLoopForTest("ses_leak").Should().BeFalse(
            "收尾必须摘除本循环的活跃登记，否则条目泄漏、会话被永久判定为繁忙");

        // 用错句柄收尾：不得误删他人条目（值匹配语义）。
        runtime.TeardownLoop("ses_leak_other", NewLoopHandle(), owned: null);
        runtime.HasActiveLoopForTest("ses_leak_other").Should().BeTrue(
            "传错句柄不得误删另一个循环的登记");
    }

    [Fact]
    public void ClearRoundCurrentExecution_Should_Not_Clear_Another_Record()
    {
        // 变异 9 的护栏：轮内 finally 与顶层同型——必须按身份清空。
        // 去掉轮内守卫后，一次「执行中写入的记录」会被并发的收尾抹掉，
        // 状态断言可能因后续重新赋值而看不出来，故直接钉住该方法本身。
        var runtime = BuildRuntime(out _, out _);

        var roundRecord = Record("exec_round");
        var foreignRecord = Record("exec_foreign");

        // 场景：轮内收尾时，状态里已是另一条路径写入的记录。
        runtime.SetCurrentExecutionForTest("ses_tick", foreignRecord);
        runtime.ClearRoundCurrentExecution("ses_tick", roundRecord);

        runtime.GetCurrentExecutionForTest("ses_tick").Should().BeSameAs(
            foreignRecord, "轮内收尾不得清掉他人记录");

        // 自己的记录必须能清掉（否则轮内残留会让 Stop 误以为仍有在途执行）。
        runtime.SetCurrentExecutionForTest("ses_tick", roundRecord);
        runtime.ClearRoundCurrentExecution("ses_tick", roundRecord);
        runtime.GetCurrentExecutionForTest("ses_tick").Should().BeNull();
    }

    [Fact]
    public async Task RunLoop_Should_Clear_Round_Record_Via_Round_Guard()
    {
        // 变异 9 的接线护栏：轮内 finally 必须真的带着本轮记录调用守卫（而非恒传 null）。
        var runtime = BuildRuntime(out _, out _);
        runtime.RoundClearWithRecordCount.Should().Be(0, "初始应无轮内收尾记录");

        await runtime.DeliverAndAwaitAsync(MakeMessage("ses_tickwire", "hi"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => runtime.RoundClearWithRecordCount > 0, TimeSpan.FromSeconds(10));

        runtime.RoundClearWithRecordCount.Should().BeGreaterThan(
            0, "逐轮 finally 必须带着本轮记录调用守卫，而不是恒传 null");
    }

    [Fact]
    public async Task DeliverAndAwait_Should_Expose_InFlight_Execution_So_Stop_Can_Cancel_It()
    {
        // 覆盖「在途执行对 Stop 可见且可取消」——注意：本用例**不**覆盖跨循环收尾清零回归
        // （那条由 TeardownLoop_Should_Not_Clear_Another_Loops_Record 直接钉住接线）。
        var runtime = BuildRuntime(out _, out var runner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runner.OnRunAsync = async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };

        var waiting = runtime.DeliverAndAwaitAsync(MakeMessage("ses_inflight", "x"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 关键断言：执行中 CurrentExecution 必须可见（不被任何收尾逻辑清掉）。
        var overview = runtime.GetOverview("ses_inflight");
        overview.CurrentExecution.Should().NotBeNull("在途执行必须对 Stop/Interrupt/Dispose 可见");
        overview.HasActiveExecution.Should().BeTrue();

        // 端到端证明：可见性成立时 Stop 才能真的取消它。
        var stopped = await runtime.StopAsync("ses_inflight");
        stopped.Should().BeGreaterThan(0, "Stop 必须能取消在途执行，说明 Cts 可读");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

    /// <summary>轮询等待条件成立（测试内自用）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("WaitUntil 条件超时");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// 测试用：构造一个未被任何循环登记的句柄。
    /// <para>
    /// 特意赋予**唯一且永不完成**的 Loop Task：<c>RemoveLoop</c> 是按 (sessionId, handle.Loop) 值匹配的，
    /// 若所有句柄的 Loop 都是默认的同一个 <c>Task.CompletedTask</c>，值匹配将退化为只比 sessionId，
    /// 测不出「传错句柄」这类缺陷。
    /// </para>
    /// </summary>
    private static SessionRuntime.LoopHandle NewLoopHandle()
    {
        var handle = new SessionRuntime.LoopHandle();
        handle.Loop = new TaskCompletionSource().Task; // 永不完成，且每个句柄各不相同
        return handle;
    }

    /// <summary>测试用：构造一个指定 Id 的执行记录。</summary>
    private static ExecutionRecord Record(string executionId, string sessionId = "") => new()
    {
        ExecutionId = executionId,
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        StartedAt = DateTime.UtcNow
    };

    private static InboxMessage MakeMessage(string sessionId, string body) => new()
    {
        TargetSessionId = sessionId,
        Sender = MakeSender(),
        Body = body,
        Delivery = InboxDelivery.Wake,
        Presentation = InboxPresentation.User
    };
}
