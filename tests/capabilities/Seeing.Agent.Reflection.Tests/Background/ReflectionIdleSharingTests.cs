using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Background;

/// <summary>
/// 空闲触发与其它后台消费方共享同一份活动事实时的行为。
/// <para>
/// <b>这是一个已发生的功能级故障的回归</b>：记忆演化默认 15 分钟处理空闲会话并
/// （曾经）调用 <c>ISessionActivityTracker.Clear</c>，复盘默认等 30 分钟。
/// 时间戳被删掉后，复盘的 <c>GetIdleSessions(30min)</c> 永远返回空——空闲触发永久失效，
/// 且没有任何报错。修复后两侧各自持有去重登记，事实不可被消费方删除。
/// </para>
/// </summary>
public sealed class ReflectionIdleSharingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>扫描时刻固定在 <see cref="Start"/> 之后，保证活动时刻与扫描时刻同源。</summary>
    private static readonly DateTimeOffset Now = Start.AddHours(1);

    [Fact]
    public void Scanner_Should_See_Session_After_Other_Consumer_Processed_Same_Idle_Period()
    {
        var (tracker, clock) = NewTracker();
        tracker.Touch("ses-1");
        clock.Advance(TimeSpan.FromHours(2));

        // 另一个消费方（记忆演化）处理了同一段空闲期——它只登记自己的去重状态。
        var memoryClaims = new IdlePeriodClaimStore();
        memoryClaims.Claim("ses-1", tracker.TryGetLastActivity("ses-1", out var t) ? t : default);

        var scanner = BuildScanner(tracker);

        scanner.Scan(clock.GetUtcNow()).Should().Be(1,
            "别的消费方处理过不代表复盘也处理过；共享事实被删掉才会导致永久看不到");
    }

    [Fact]
    public void Scanner_Should_Not_Delete_Shared_Activity_Timestamp()
    {
        var (tracker, clock) = NewTracker();
        tracker.Touch("ses-1");
        clock.Advance(TimeSpan.FromHours(2));

        BuildScanner(tracker).Scan(clock.GetUtcNow()).Should().Be(1);

        tracker.TryGetLastActivity("ses-1", out _)
            .Should().BeTrue("扫描器不得清理共享活动事实，否则其它消费方（记忆演化）也失去这个会话");
    }

    [Fact]
    public void Scanner_Should_Keep_Its_Own_Cooling_Across_Scans()
    {
        var (tracker, clock) = NewTracker();
        tracker.Touch("ses-1");
        clock.Advance(TimeSpan.FromHours(2));

        var scanner = BuildScanner(tracker);

        scanner.Scan(clock.GetUtcNow()).Should().Be(1);
        clock.Advance(TimeSpan.FromMinutes(1));
        scanner.Scan(clock.GetUtcNow()).Should().Be(0,
            "同一段空闲期不得重复投递：每次扫描都烧一次预算是不可接受的");
    }

    /// <summary>活动追踪器与扫描时刻共用同一个可控时钟（否则「是否空闲」判定没有可比性）。</summary>
    private static (SessionActivityTracker Tracker, FakeClock Clock) NewTracker()
    {
        var clock = new FakeClock(Start);
        return (new SessionActivityTracker(clock), clock);
    }

    private static ReflectionIdleScanner BuildScanner(SessionActivityTracker tracker)
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(m => m.Get(It.IsAny<string>()))
            .Returns<string>(id => new SessionData
            {
                Id = id,
                Kind = SessionKind.Root,
                Origin = SessionOrigin.User,
                Title = "t"
            });

        var options = new Mock<IOptionsMonitor<ReflectionOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new ReflectionOptions
        {
            Enabled = true,
            Mode = ReflectionMode.SuggestOnly,
            // 扫描器把阈值下限夹到 1 分钟，因此上面统一推进 2 小时以满足任意阈值。
            IdleMinutes = 30
        });

        return new ReflectionIdleScanner(
            new ReflectionQueue(capacity: 8),
            tracker,
            options.Object,
            sessions.Object);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
