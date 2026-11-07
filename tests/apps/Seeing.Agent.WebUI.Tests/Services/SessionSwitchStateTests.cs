using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.State;

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

    /// <summary>
    /// 守门：执行锁释放必须幂等。
    /// <para>
    /// <see cref="SessionState.CancelExecution"/> / <see cref="SessionState.CompleteExecution"/> /
    /// <see cref="SessionState.ResetExecutionState"/> 分别来自 UI 点击、事件流后台线程、会话切换，
    /// 三者可能并发观测到 <c>_executionLock.CurrentCount == 0</c> 而双释放，
    /// 未兜底会抛 <see cref="SemaphoreFullException"/> 打崩 circuit。
    /// </para>
    /// </summary>
    [Fact]
    public void ReleasePaths_CalledRepeatedly_ShouldNotThrow()
    {
        var state = new SessionState();
        state.StartExecution();

        var act = () =>
        {
            state.CompleteExecution();
            state.CancelExecution();
            state.ResetExecutionState();
            state.CompleteExecution();
            state.CancelExecution();
        };

        act.Should().NotThrow();
    }

    /// <summary>并发释放（cancel / complete / reset 同时触发）不得抛 <see cref="SemaphoreFullException"/>。</summary>
    [Fact]
    public async Task ReleasePaths_Concurrent_ShouldNotThrow()
    {
        var state = new SessionState();
        state.StartExecution();

        var failures = new List<Exception>();
        var tasks = new[]
        {
            Task.Run(() => { try { state.CancelExecution(); } catch (Exception ex) { lock (failures) failures.Add(ex); } }),
            Task.Run(() => { try { state.CompleteExecution(); } catch (Exception ex) { lock (failures) failures.Add(ex); } }),
            Task.Run(() => { try { state.ResetExecutionState(); } catch (Exception ex) { lock (failures) failures.Add(ex); } }),
        };

        await Task.WhenAll(tasks);

        failures.Should().BeEmpty();
    }

    /// <summary>
    /// 守门：TOCTOU 压力测试。<c>check CurrentCount == 0</c> 与 <c>Release()</c> 之间的窗口
    /// 在多线程同时进入时会导致双释放 → <see cref="SemaphoreFullException"/>。
    /// 用大量重复轮次提高复现概率；该测试是并发契约的守门，不是稳定复现用例。
    /// </summary>
    [Fact]
    public async Task ReleasePaths_HighContention_ShouldNotThrowSemaphoreFull()
    {
        var failures = new List<Exception>();

        for (var round = 0; round < 300; round++)
        {
            var state = new SessionState();
            state.StartExecution();

            using var barrier = new Barrier(3);
            var tasks = new[]
            {
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { state.CancelExecution(); }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                }),
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { state.CompleteExecution(); }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                }),
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { state.ResetExecutionState(); }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                }),
            };

            await Task.WhenAll(tasks);
            state.Dispose();
        }

        failures.Should().BeEmpty();
    }
}
