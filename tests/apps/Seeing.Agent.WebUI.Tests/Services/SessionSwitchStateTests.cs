using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.State;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>会话切换时的本地执行态重置：新会话不得继承上一会话的执行态与取消令牌。</summary>
public class SessionSwitchStateTests
{
    [Fact]
    public void Reset_AfterExecutingSession_ShouldClearExecutionState()
    {
        var state = new SessionState();
        state.SyncExecutionStarted("exec-1");
        state.ExecutionStatus = ExecutionStatus.Running;
        state.PendingInputCount = 3;

        state.ResetExecutionState();

        state.IsExecuting.Should().BeFalse();
        state.ExecutionStatus.Should().BeNull();
        state.CurrentExecutionId.Should().BeNull();
        state.PendingInputCount.Should().Be(0);
        state.HasActiveExecution.Should().BeFalse();
    }

    [Fact]
    public void Reset_ShouldReplaceCancellationTokenSource()
    {
        var state = new SessionState();
        state.StartExecution();
        var before = state.CancellationTokenSource;

        state.ResetExecutionState();
        state.StartExecution();

        state.CancellationTokenSource.Should().NotBeSameAs(before);
        before!.IsCancellationRequested.Should().BeTrue();
    }
}
