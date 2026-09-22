using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 事件源按需重建与失败退避的纯逻辑契约（不启动泵/终端）：
/// 泵已完成 → 需要重建；重建失败 → 保持无事件模式并退避（不立即重试）；重建成功后清零恢复。
/// </summary>
public sealed class TuiChatEngineEventRecoveryTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RequiresEventSourceRebuild_ShouldFollowPumpCompletion(bool completed, bool expected)
        => TuiChatEngine.RequiresEventSourceRebuild(completed).Should().Be(expected);

    [Fact]
    public void ShouldAttemptEventSourceRebuild_WithoutFailure_ShouldAllowImmediately()
        => TuiChatEngine
            .ShouldAttemptEventSourceRebuild(DateTime.MinValue, DateTime.UtcNow, failureCount: 0)
            .Should().BeTrue();

    [Fact]
    public void ShouldAttemptEventSourceRebuild_JustFailed_ShouldBackoff()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        TuiChatEngine
            .ShouldAttemptEventSourceRebuild(now, now, failureCount: 1)
            .Should().BeFalse("重建失败后必须退避，不得立即重试");
    }

    [Fact]
    public void ShouldAttemptEventSourceRebuild_AfterBackoffElapsed_ShouldAllowRetry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastFailure = now - TimeSpan.FromSeconds(30);

        TuiChatEngine
            .ShouldAttemptEventSourceRebuild(lastFailure, now, failureCount: 1)
            .Should().BeTrue();
    }

    [Fact]
    public void UpdateRebuildBackoff_OnSuccess_ShouldResetAndRestoreEventSource()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var state = TuiChatEngine.UpdateRebuildBackoff(
            failureCount: 3, lastFailureAt: DateTime.MinValue, success: true, now);

        state.FailureCount.Should().Be(0);
        state.LastFailureAt.Should().Be(DateTime.MinValue);
        // 事件源恢复：退避已清零，可立即读取新泵事件。
        TuiChatEngine
            .ShouldAttemptEventSourceRebuild(state.LastFailureAt, now, state.FailureCount)
            .Should().BeTrue();
    }

    [Fact]
    public void UpdateRebuildBackoff_OnFailure_ShouldAccumulateAndRecordTime()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var state = TuiChatEngine.UpdateRebuildBackoff(
            failureCount: 0, lastFailureAt: DateTime.MinValue, success: false, now);

        state.FailureCount.Should().Be(1);
        state.LastFailureAt.Should().Be(now);
        TuiChatEngine
            .ShouldAttemptEventSourceRebuild(state.LastFailureAt, now, state.FailureCount)
            .Should().BeFalse();
    }
}
