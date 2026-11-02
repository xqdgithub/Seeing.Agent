using FluentAssertions;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// <see cref="SessionActivityTracker"/> 属 primitives 归属（记忆抽取与复盘触发共用）。
/// 这里只守护「空闲判定」这一被两个消费方共同依赖的语义。
/// </summary>
public class SessionActivityTrackerTests
{
    [Fact]
    public void GetIdleSessions_WhenTouchedRecently_ShouldBeEmpty()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        tracker.GetIdleSessions(TimeSpan.FromMinutes(15)).Should().BeEmpty();
    }

    [Fact]
    public void GetIdleSessions_Should_Return_Only_Sessions_Past_Threshold()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("old");
        Thread.Sleep(30);
        tracker.Touch("fresh");

        var idle = tracker.GetIdleSessions(TimeSpan.FromMilliseconds(20));

        idle.Should().Contain("old").And.NotContain("fresh");
    }

    [Fact]
    public void Clear_ShouldRemoveSession()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        tracker.Clear("s1");

        tracker.GetIdleSessions(TimeSpan.Zero).Should().NotContain("s1",
            "已清理的会话不应再被后台扫描命中");
    }

    [Fact]
    public void Touch_Should_Ignore_Blank_SessionId()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("");
        tracker.Touch("   ");

        tracker.GetIdleSessions(TimeSpan.Zero).Should().BeEmpty(
            "空白会话 ID 不产生记录，否则后台会扫描到无法解析的会话");
    }

    [Fact]
    public void Touch_Should_Refresh_Existing_Entry()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        Thread.Sleep(30);
        tracker.Touch("s1");

        tracker.GetIdleSessions(TimeSpan.FromMilliseconds(20)).Should().NotContain("s1",
            "重复 Touch 必须刷新时间戳，否则仍在活动的会话会被误判为空闲");
    }
}
