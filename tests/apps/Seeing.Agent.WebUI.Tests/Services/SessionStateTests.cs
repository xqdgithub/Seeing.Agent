using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionStateTests
{
    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var state = new SessionState();
        state.Dispose();
        state.Dispose(); // 防重入：不抛异常
    }

    [Fact]
    public void Dispose_AfterStartExecution_ShouldReleaseResourcesWithoutThrow()
    {
        var state = new SessionState();
        state.StartExecution(); // 占用执行锁并创建取消令牌
        state.Dispose();        // 释放锁 + cts，不抛
    }

    [Fact]
    public void SyncExecutionStarted_WhenIdle_ShouldMarkActiveAndExposeCancelToken()
    {
        var state = new SessionState();

        state.SyncExecutionStarted("exec-1");

        state.HasActiveExecution.Should().BeTrue();
        state.IsExecuting.Should().BeTrue();
        state.ExecutionStatus.Should().Be(ExecutionStatus.Running);
        state.CurrentExecutionId.Should().Be("exec-1");
        state.CancellationTokenSource.Should().NotBeNull();
        state.CancellationTokenSource!.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void SyncExecutionStarted_WhenAlreadyExecuting_ShouldReuseTokenAndBeIdempotent()
    {
        var state = new SessionState();
        state.StartExecution();
        var cts = state.CancellationTokenSource;

        state.SyncExecutionStarted("exec-2");

        state.HasActiveExecution.Should().BeTrue();
        state.CurrentExecutionId.Should().Be("exec-2");
        state.CancellationTokenSource.Should().BeSameAs(cts);
    }

    [Fact]
    public void SyncExecutionStarted_AfterCancel_ShouldReenableExecution()
    {
        var state = new SessionState();
        state.StartExecution();
        state.CancelExecution();
        state.CompleteExecution();

        state.SyncExecutionStarted("exec-resumed");

        state.HasActiveExecution.Should().BeTrue();
        state.IsExecuting.Should().BeTrue();
        state.ExecutionStatus.Should().Be(ExecutionStatus.Running);
    }
}
