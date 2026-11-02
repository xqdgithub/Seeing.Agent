using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Background;

/// <summary>
/// 空闲扫描的门禁与冷却。
/// <para>
/// 这条路径<b>不经过</b> <c>ReflectionObserver</c>，所以来源/结构门禁必须自带一份。
/// 漏掉它的后果不是「少复盘一次」，而是对子代理会话甚至复盘会话自身做复盘——
/// 破坏「只复盘顶层会话」与「防递归」两条硬约束，并烧掉每日预算。
/// </para>
/// </summary>
public sealed class ReflectionIdleScannerTests
{
    private const string RootId = "root-1";
    private const string ChildId = "child-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Scan_Should_Enqueue_TopLevel_User_Session()
    {
        var f = new Fixture();
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(1);

        f.Queue.TryDequeue(out var request).Should().BeTrue();
        request.SessionId.Should().Be(RootId);
        request.Kind.Should().Be(ReflectionTriggerKind.Idle);
        request.At.Should().Be(Now, "扫描器不读时钟，时刻必须来自注入的 now");
    }

    [Theory]
    [InlineData(SessionKind.SubAgent, SessionOrigin.SubAgentTask, "子代理会话")]
    [InlineData(SessionKind.SubAgent, SessionOrigin.Reflection, "复盘会话自身")]
    [InlineData(SessionKind.Root, SessionOrigin.Scheduler, "定时任务会话")]
    [InlineData(SessionKind.Root, SessionOrigin.Heartbeat, "心跳会话")]
    public void Scan_Should_Skip_Non_User_Visible_Sessions(
        SessionKind kind,
        SessionOrigin origin,
        string reason)
    {
        var f = new Fixture();
        f.AddSession(ChildId, kind, origin);
        f.Backdate(ChildId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(0, $"{reason}不应被空闲触发复盘");
        f.Queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Scan_Should_Skip_Session_Not_In_Memory()
    {
        // 已淘汰的会话不做磁盘加载：后台扫描不应为此付 IO。
        var f = new Fixture();
        f.Backdate("evicted-1", TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(0);
    }

    [Fact]
    public void Scan_Should_Skip_Session_That_Became_Active_Again()
    {
        // 活动追踪器给出的是快照；从扫描到入队之间用户可能已经回来。
        var f = new Fixture();
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromMinutes(5));   // 未达 30 分钟阈值

        f.Scanner.Scan(Now).Should().Be(0, "二次确认发现会话仍然活跃，不得复盘");
    }

    [Fact]
    public void Scan_Should_Not_Reenqueue_Same_Idle_Period()
    {
        var f = new Fixture();
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(1);
        f.Queue.TryDequeue(out _).Should().BeTrue();

        // 同一段空闲期再扫一次：不得重复投递（否则每次扫描都烧一次预算）。
        f.Scanner.Scan(Now.AddMinutes(1)).Should().Be(0,
            "冷却期内不得重复投递同一个空闲会话");
        f.Queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Scan_Should_Reenqueue_After_Session_Becomes_Active_Then_Idle_Again()
    {
        var f = new Fixture();
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(1);
        f.Queue.TryDequeue(out _).Should().BeTrue();

        // 用户回到会话（Touch 刷新时刻）后又闲置：这是新的一段空闲期，必须允许再触发。
        f.Backdate(RootId, TimeSpan.FromHours(2));
        f.Scanner.Scan(Now).Should().Be(1,
            "用户产生新活动后再次空闲，属于新的空闲期");
    }

    [Fact]
    public void Scan_Should_Skip_When_Disabled()
    {
        var f = new Fixture(options => options.Enabled = false);
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(0);
    }

    [Fact]
    public void Scan_Should_Skip_When_Mode_Is_Off()
    {
        var f = new Fixture(options => options.Mode = ReflectionMode.Off);
        f.AddSession(RootId, SessionKind.Root, SessionOrigin.User);
        f.Backdate(RootId, TimeSpan.FromHours(1));

        f.Scanner.Scan(Now).Should().Be(0, "Mode=Off 等价于关闭");
    }

    [Fact]
    public void Scan_Should_Not_Throw_When_SessionManager_Missing()
    {
        // 能力包可独立装配：没有会话管理器时空闲触发整体降级，但不抛异常。
        var queue = new ReflectionQueue(capacity: 8);
        var tracker = new Seeing.Session.Management.SessionActivityTracker();
        tracker.Touch(RootId);
        var monitor = new Mock<IOptionsMonitor<ReflectionOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(DefaultOptions());

        var scanner = new ReflectionIdleScanner(queue, tracker, monitor.Object, sessionManager: null);

        var act = () => scanner.Scan(DateTimeOffset.UtcNow);

        act.Should().NotThrow();
    }

    private static ReflectionOptions DefaultOptions() => new() { Enabled = true, IdleMinutes = 30 };

    private sealed class Fixture
    {
        private readonly Dictionary<string, SessionData> _sessions = new();
        private readonly Seeing.Session.Management.SessionActivityTracker _tracker = new();
        private readonly Mock<ISessionManager> _sessionManager = new();

        public ReflectionQueue Queue { get; } = new(capacity: 16);
        public ReflectionIdleScanner Scanner { get; }

        public Fixture(Action<ReflectionOptions>? configure = null)
        {
            var options = DefaultOptions();
            configure?.Invoke(options);

            var monitor = new Mock<IOptionsMonitor<ReflectionOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(options);

            _sessionManager
                .Setup(m => m.Get(It.IsAny<string>()))
                .Returns<string>(id => _sessions.TryGetValue(id, out var s) ? s : null);

            Scanner = new ReflectionIdleScanner(
                Queue,
                _tracker,
                monitor.Object,
                _sessionManager.Object);
        }

        public void AddSession(string id, SessionKind kind, SessionOrigin origin)
        {
            _sessions[id] = new SessionData
            {
                Id = id,
                Kind = kind,
                Origin = origin,
                Title = "t",
                SelectedAgent = "build"
            };
        }

        /// <summary>
        /// 把会话时间戳设为「相对 <paramref name="Now"/> 的 <paramref name="ago"/> 之前」。
        /// <para>
        /// 必须用测试基准时刻而不是 <c>DateTimeOffset.UtcNow</c>：扫描器读的是注入的 now，
        /// 两者若不同源，「是否空闲」的判定会得出与真实时钟无关的结果。
        /// </para>
        /// <para>
        /// <see cref="Seeing.Session.Management.SessionActivityTracker"/> 只能写「现在」，
        /// 没有公开的「设置历史时刻」API。这里用反射写私有字段——生产代码不该为测试开后门，
        /// 反射是侵入最小的替代。
        /// </para>
        /// </summary>
        public void Backdate(string sessionId, TimeSpan ago)
        {
            _tracker.Touch(sessionId);

            var field = typeof(Seeing.Session.Management.SessionActivityTracker)
                .GetField("_last", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)
                field!.GetValue(_tracker)!;
            dict[sessionId] = Now - ago;
        }
    }
}
