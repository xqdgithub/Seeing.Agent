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
    public async Task DeliverAndAwait_Concurrent_Deliveries_Should_Each_Observe_Own_Execution()
    {
        // 并发投递同一会话：单飞消费下可能被合并成少数几轮，但每个等待者都必须拿到
        // 自己那条消息所在轮次的 outcome，且 executionId 必须真实存在。
        var runtime = BuildRuntime(out _, out _);

        var pending = Enumerable.Range(0, 5)
            .Select(i => runtime.DeliverAndAwaitAsync(MakeMessage("ses_g", $"m{i}"), CancellationToken.None))
            .ToArray();

        var outcomes = await Task.WhenAll(pending);

        outcomes.Should().OnlyContain(o => o.SessionId == "ses_g");
        outcomes.Should().OnlyContain(o => !string.IsNullOrEmpty(o.ExecutionId));
        outcomes.Should().OnlyContain(o => o.Status == ExecutionStatus.Completed);
    }

    // ===== 测试脚手架 =====

    /// <summary>假执行体：可注入阻塞逻辑；默认立刻完成并把记录置为 Completed。</summary>
    private sealed class FakeRunner : IExecutionRunner
    {
        /// <summary>阻塞钩子（参数为本次执行的取消令牌）。</summary>
        public Func<CancellationToken, Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
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
        Func<CancellationToken, Task>? runHook,
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
