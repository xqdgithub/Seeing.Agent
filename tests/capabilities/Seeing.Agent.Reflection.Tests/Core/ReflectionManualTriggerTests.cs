using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>
/// 手动触发入口的校验与入队语义。
/// <para>
/// 这条路径与自动触发共用同一个队列与执行器，但门禁不同：
/// 它是用户的显式动作，因此<b>不听 <c>Enabled</c></b>（否则用户为了验证功能
/// 还得先去改配置再回来点，功能感知上等于不存在），但模式、
/// 「只复盘顶层会话」与防递归三条硬约束必须照常生效。
/// </para>
/// </summary>
public sealed class ReflectionManualTriggerTests
{
    private const string RootId = "ses_root";
    private const string ChildId = "ses_child";

    private sealed class Fixture
    {
        private readonly Dictionary<string, SessionData> _sessions = new();

        public ReflectionQueue Queue { get; } = new(capacity: 4);
        public ReflectionModuleActivity Activity { get; } = new() { IsActive = true };
        public ReflectionOptions Options { get; } = new() { Enabled = true, Mode = ReflectionMode.SuggestOnly };
        public ReflectionWakeSignal Wake { get; } = new();

        public Fixture AddSession(string id, SessionKind kind = SessionKind.Root, SessionOrigin origin = SessionOrigin.User)
        {
            _sessions[id] = new SessionData { Id = id, Kind = kind, Origin = origin, Title = "t" };
            return this;
        }

        public ReflectionManualTrigger Build()
        {
            var sessions = new Mock<ISessionManager>();
            sessions.Setup(m => m.Get(It.IsAny<string>()))
                .Returns<string>(id => _sessions.TryGetValue(id, out var s) ? s : null);

            var monitor = new Mock<IOptionsMonitor<ReflectionOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(Options);

            return new ReflectionManualTrigger(
                Queue, sessions.Object, Activity, monitor.Object, Wake);
        }
    }

    [Fact]
    public void Trigger_Should_Enqueue_Manual_Request_For_Root_Session()
    {
        var fixture = new Fixture().AddSession(RootId);

        var result = fixture.Build().Trigger(RootId);

        result.Status.Should().Be(ReflectionManualTriggerStatus.Enqueued);
        result.Accepted.Should().BeTrue();

        fixture.Queue.TryDequeue(out var request).Should().BeTrue();
        request.SessionId.Should().Be(RootId);
        request.Kind.Should().Be(ReflectionTriggerKind.Manual,
            "手动触发必须留下可区分的来源，否则记录页无法解释这次复盘是谁发起的");
    }

    [Fact]
    public void Trigger_Should_Work_When_Auto_Enabled_Is_Off()
    {
        // Enabled 只表达「要不要自动跑」。手动触发是显式动作，不受它限制，
        // 否则「关闭自动复盘的用户」永远无法手动验证这条链路。
        var fixture = new Fixture().AddSession(RootId);
        fixture.Options.Enabled = false;

        fixture.Build().Trigger(RootId).Status.Should().Be(ReflectionManualTriggerStatus.Enqueued);
    }

    [Fact]
    public void Trigger_Should_Reject_When_Mode_Is_Off()
    {
        // 模式 Off 是硬停：连手动也不得绕过（用户明确表达了「不要复盘」）。
        var fixture = new Fixture().AddSession(RootId);
        fixture.Options.Mode = ReflectionMode.Off;

        var result = fixture.Build().Trigger(RootId);

        result.Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
        fixture.Queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Trigger_Should_Reject_When_Module_Inactive()
    {
        var fixture = new Fixture().AddSession(RootId);
        fixture.Activity.IsActive = false;

        fixture.Build().Trigger(RootId).Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
        fixture.Queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Trigger_Should_Reject_SubAgent_Session()
    {
        var fixture = new Fixture().AddSession(ChildId, SessionKind.SubAgent, SessionOrigin.SubAgentTask);

        var result = fixture.Build().Trigger(ChildId);

        result.Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
        result.Message.Should().Contain("顶层");
    }

    [Fact]
    public void Trigger_Should_Reject_Reflection_Session()
    {
        // 防递归：给复盘子会话手动触发等于让它复盘自己。
        var fixture = new Fixture().AddSession(ChildId, SessionKind.Root, SessionOrigin.Reflection);

        fixture.Build().Trigger(ChildId).Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
    }

    [Fact]
    public void Trigger_Should_Reject_Session_Not_In_Memory()
    {
        // 不在内存说明会话已被回收或 ID 有误：直接给出可读原因，
        // 比静默入队后由 Worker 报 no-source 更有用。
        var fixture = new Fixture();

        var result = fixture.Build().Trigger("ses_missing");

        result.Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
        result.Message.Should().Contain("内存");
    }

    [Fact]
    public void Trigger_Should_Reject_Empty_SessionId()
    {
        var result = new Fixture().Build().Trigger("  ");

        result.Status.Should().Be(ReflectionManualTriggerStatus.Invalid);
    }

    [Fact]
    public void Trigger_Should_Report_Updated_When_Same_Session_Queued_Twice()
    {
        var fixture = new Fixture().AddSession(RootId);
        var trigger = fixture.Build();

        trigger.Trigger(RootId).Status.Should().Be(ReflectionManualTriggerStatus.Enqueued);
        trigger.Trigger(RootId).Status.Should().Be(ReflectionManualTriggerStatus.Updated,
            "同会话重复点击应合并请求，而不是排两次队");
        fixture.Queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Trigger_Should_Report_Dropped_When_Queue_Full()
    {
        // 队列容量 4：先塞满，再触发一次必须回报「丢弃」而不是假装成功。
        var fixture = new Fixture().AddSession(RootId);
        var trigger = fixture.Build();

        for (var i = 0; i < 4; i++)
        {
            var id = $"ses_fill_{i}";
            var sessions = new Mock<ISessionManager>();
            sessions.Setup(m => m.Get(id)).Returns(new SessionData { Id = id, Kind = SessionKind.Root });
            new ReflectionManualTrigger(
                    fixture.Queue, sessions.Object, fixture.Activity,
                    MonitorFor(fixture.Options), fixture.Wake)
                .Trigger(id);
        }

        trigger.Trigger(RootId).Status.Should().Be(ReflectionManualTriggerStatus.Dropped);
    }

    private static IOptionsMonitor<ReflectionOptions> MonitorFor(ReflectionOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<ReflectionOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return monitor.Object;
    }
}
