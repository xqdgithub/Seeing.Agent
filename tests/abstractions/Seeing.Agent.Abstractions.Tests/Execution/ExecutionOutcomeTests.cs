using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Abstractions.Tests.Execution;

/// <summary>
/// 一轮执行终态事实契约（<see cref="ExecutionOutcome"/>）与终态观察者端口（<see cref="IExecutionOutcomeObserver"/>）的行为锁定。
/// </summary>
public class ExecutionOutcomeTests
{
    [Fact]
    public void ExecutionOutcome_Should_Expose_Usage_And_Steps()
    {
        var outcome = new ExecutionOutcome
        {
            SessionId = "s1", ExecutionId = "e1",
            Status = ExecutionStatus.Completed,
            AgentName = "build", AgentExecuted = true, IsTopLevel = true,
            TotalSteps = 5,
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };
        outcome.Usage!.TotalTokens.Should().Be(150);
        outcome.TotalSteps.Should().Be(5);
    }

    [Fact]
    public async Task Observer_Default_ShouldObserve_Should_Return_True()
    {
        var fake = new AllObservingObserver();
        // 默认接口实现只能经接口类型访问（C# 语义限制），故此处显式以接口类型调用。
        IExecutionOutcomeObserver observer = fake;
        observer.ShouldObserve(new ExecutionOutcome
        {
            SessionId = "s", ExecutionId = "e", Status = ExecutionStatus.Completed
        }).Should().BeTrue();
        await observer.OnExecutionCompletedAsync(new ExecutionOutcome
        {
            SessionId = "s", ExecutionId = "e", Status = ExecutionStatus.Completed
        });
        fake.Called.Should().BeTrue();
    }

    /// <summary>只实现必需成员的观察者，用于验证 <c>ShouldObserve</c> 的接口默认实现。</summary>
    private sealed class AllObservingObserver : IExecutionOutcomeObserver
    {
        /// <summary>是否被调用过。</summary>
        public bool Called { get; private set; }

        /// <summary>记录回调发生。</summary>
        public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        { Called = true; return Task.CompletedTask; }
    }
}
