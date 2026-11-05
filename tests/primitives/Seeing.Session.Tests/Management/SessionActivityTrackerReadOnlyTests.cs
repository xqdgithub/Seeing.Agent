using FluentAssertions;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// <see cref="SessionActivityTracker.TryGetLastActivity"/> 的语义守门。
/// <para>
/// 后台工作者靠它做「扫描到执行之间用户是否已回来」的二次确认。
/// 它必须是<b>只读</b>的——若顺手刷新了时间戳，空闲会话会被永久续命，永远触发不了。
/// </para>
/// </summary>
public class SessionActivityTrackerReadOnlyTests
{
    [Fact]
    public void TryGetLastActivity_Should_Return_False_For_Unknown_Session()
    {
        var tracker = new SessionActivityTracker();

        tracker.TryGetLastActivity("missing", out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetLastActivity_Should_Not_Refresh_Timestamp()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new SessionActivityTracker(time);
        tracker.Touch("s1");
        tracker.TryGetLastActivity("s1", out var first).Should().BeTrue();

        // 推进虚拟时间 30ms：只读查询不得推进时间戳。
        time.Advance(TimeSpan.FromMilliseconds(30));

        // 关键：只读查询不得推进时间戳，否则空闲判定永远不成立。
        tracker.TryGetLastActivity("s1", out var second).Should().BeTrue();
        second.Should().Be(first, "只读查询必须返回同一个时刻");

        tracker.GetIdleSessions(TimeSpan.FromMilliseconds(20))
            .Should().Contain("s1",
                "查询本身不应续命：会话仍然处于空闲状态");
    }

    [Fact]
    public void TryGetLastActivity_Should_Return_False_After_Clear()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        tracker.Clear("s1");

        tracker.TryGetLastActivity("s1", out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetLastActivity_Should_Tolerate_Blank_SessionId()
    {
        var tracker = new SessionActivityTracker();

        tracker.TryGetLastActivity("", out _).Should().BeFalse();
        tracker.TryGetLastActivity("  ", out _).Should().BeFalse();
    }

    /// <summary>可手动推进的时钟。</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
