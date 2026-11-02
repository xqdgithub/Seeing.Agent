using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>复盘限流器的窗口语义：满额拒绝、过期驱逐。</summary>
public sealed class ReflectionRateLimiterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_Should_Allow_Up_To_Max()
    {
        var limiter = new ReflectionRateLimiter();

        limiter.TryAcquire(3, s_now).Should().BeTrue();
        limiter.TryAcquire(3, s_now.AddMinutes(1)).Should().BeTrue();
        limiter.TryAcquire(3, s_now.AddMinutes(2)).Should().BeTrue();

        limiter.CountInWindow(s_now.AddMinutes(3)).Should().Be(3);
    }

    [Fact]
    public void TryAcquire_Should_Reject_When_Full()
    {
        var limiter = new ReflectionRateLimiter();
        limiter.TryAcquire(1, s_now).Should().BeTrue();

        limiter.TryAcquire(1, s_now.AddMinutes(1)).Should().BeFalse();

        limiter.CountInWindow(s_now.AddMinutes(1)).Should().Be(1);
    }

    [Fact]
    public void TryAcquire_Should_Evict_Entries_Outside_Window()
    {
        var limiter = new ReflectionRateLimiter();
        limiter.TryAcquire(1, s_now).Should().BeTrue();

        // 窗口边界外（恰好 1 小时）应被驱逐，额度重新可用。
        var later = s_now.AddHours(1);

        limiter.CountInWindow(later).Should().Be(0);
        limiter.TryAcquire(1, later).Should().BeTrue();
        limiter.CountInWindow(later).Should().Be(1);
    }

    [Fact]
    public void TryAcquire_Should_Reject_When_Max_Is_Not_Positive()
    {
        var limiter = new ReflectionRateLimiter();

        limiter.TryAcquire(0, s_now).Should().BeFalse();
        limiter.CountInWindow(s_now).Should().Be(0);
    }
}
