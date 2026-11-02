using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Reflection;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Integration;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Trigger;

/// <summary>终态观察者的门禁与轮次触发语义。</summary>
public sealed class ReflectionObserverTests
{
    private static ReflectionObserver CreateObserver(
        ReflectionOptions? options = null,
        IReflectionQueue? queue = null,
        ReflectionTurnCounter? turns = null,
        bool active = true)
    {
        var monitor = new Mock<IOptionsMonitor<ReflectionOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options ?? new ReflectionOptions { Enabled = true });

        var activity = new ReflectionModuleActivity { IsActive = active };

        return new ReflectionObserver(
            monitor.Object,
            turns ?? new ReflectionTurnCounter(),
            queue ?? new ReflectionQueue(capacity: 8),
            activity,
            NullLogger<ReflectionObserver>.Instance);
    }

    private static ExecutionOutcome CreateOutcome(
        string agentName = "build",
        SessionOrigin origin = SessionOrigin.User,
        ExecutionStatus status = ExecutionStatus.Completed,
        bool agentExecuted = true,
        bool isTopLevel = true,
        string sessionId = "session-1",
        string executionId = "exec-1")
        => new()
        {
            SessionId = sessionId,
            ExecutionId = executionId,
            Status = status,
            AgentName = agentName,
            AgentExecuted = agentExecuted,
            IsTopLevel = isTopLevel,
            Origin = origin
        };

    [Theory]
    // enabled, executed, topLevel, status, agentName, origin, expected
    [InlineData(true, true, true, ExecutionStatus.Completed, "build", SessionOrigin.User, true)]
    [InlineData(false, true, true, ExecutionStatus.Completed, "build", SessionOrigin.User, false)]
    [InlineData(true, false, true, ExecutionStatus.Completed, "build", SessionOrigin.User, false)]
    [InlineData(true, true, false, ExecutionStatus.Completed, "explore", SessionOrigin.User, false)]
    [InlineData(true, true, true, ExecutionStatus.Failed, "build", SessionOrigin.User, false)]
    [InlineData(true, true, true, ExecutionStatus.Completed, "reflection", SessionOrigin.User, false)]
    [InlineData(true, true, true, ExecutionStatus.Completed, "build", SessionOrigin.Reflection, false)]
    public void ShouldObserve_Should_Gate_Correctly(
        bool enabled,
        bool agentExecuted,
        bool isTopLevel,
        ExecutionStatus status,
        string agentName,
        SessionOrigin origin,
        bool expected)
    {
        var observer = CreateObserver(new ReflectionOptions
        {
            Enabled = enabled,
            Mode = ReflectionMode.SuggestOnly
        });
        var outcome = CreateOutcome(agentName, origin, status, agentExecuted, isTopLevel);

        observer.ShouldObserve(outcome).Should().Be(expected);
    }

    [Fact]
    public void ShouldObserve_Should_Return_False_When_Mode_Is_Off()
    {
        // Enabled 与 Mode 是两道独立开关：只关 Mode 也必须停。
        // 单靠 Enabled 短路会让这个分支永远不被覆盖（删掉它测试仍全绿）。
        var observer = CreateObserver(new ReflectionOptions
        {
            Enabled = true,
            Mode = ReflectionMode.Off
        });

        observer.ShouldObserve(CreateOutcome()).Should().BeFalse(
            "Mode=Off 等价于关闭，不能因为 Enabled=true 就跑");
    }

    [Fact]
    public void ShouldObserve_Should_Return_False_When_SessionId_Is_Blank()
    {
        var observer = CreateObserver(new ReflectionOptions { Enabled = true });

        observer.ShouldObserve(CreateOutcome(sessionId: "   ")).Should().BeFalse(
            "没有会话 ID 就无法定位复盘目标");
    }

    [Fact]
    public async Task OnExecutionCompleted_Should_Not_Enqueue_When_Inactive()
    {
        var queue = new ReflectionQueue(capacity: 8);
        var observer = CreateObserver(
            new ReflectionOptions { Enabled = true, TurnNudgeInterval = 1 },
            queue,
            active: false);

        await observer.OnExecutionCompletedAsync(CreateOutcome());

        queue.PendingCount.Should().Be(0, "模块未激活时观察者不得工作");
    }

    [Fact]
    public async Task OnExecutionCompleted_Should_Not_Enqueue_Before_Interval()
    {
        var queue = new ReflectionQueue(capacity: 8);
        var options = new ReflectionOptions { Enabled = true, TurnNudgeInterval = 3 };
        var observer = CreateObserver(options, queue);

        await observer.OnExecutionCompletedAsync(CreateOutcome());
        await observer.OnExecutionCompletedAsync(CreateOutcome());

        queue.PendingCount.Should().Be(0);

        await observer.OnExecutionCompletedAsync(CreateOutcome());

        queue.PendingCount.Should().Be(1);
        queue.TryDequeue(out var request).Should().BeTrue();
        request.Kind.Should().Be(ReflectionTriggerKind.TurnNudge);
        request.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task OnExecutionCompleted_Should_Not_Throw_When_Queue_Is_Full()
    {
        // 容量 1 且已占用另一会话，本次入队必然失败；观察者应静默返回。
        var queue = new ReflectionQueue(capacity: 1);
        queue.TryEnqueue(new ReflectionRequest(
                "other-session",
                "exec-0",
                ReflectionTriggerKind.TurnNudge,
                DateTimeOffset.UtcNow))
            .Should().BeTrue();

        var observer = CreateObserver(
            new ReflectionOptions { Enabled = true, TurnNudgeInterval = 1 },
            queue);

        var act = async () => await observer.OnExecutionCompletedAsync(CreateOutcome());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnExecutionCompleted_Should_Swallow_Queue_Exception()
    {
        var queue = new Mock<IReflectionQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ReflectionRequest>()))
            .Throws(new InvalidOperationException("队列异常"));

        var observer = CreateObserver(
            new ReflectionOptions { Enabled = true, TurnNudgeInterval = 1 },
            queue.Object);

        var act = async () => await observer.OnExecutionCompletedAsync(CreateOutcome());

        await act.Should().NotThrowAsync();
    }
}
