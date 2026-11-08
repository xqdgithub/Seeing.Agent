using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 首页控制台的文案与色调投影（调度器状态、MCP 状态、Agent 运行时）。
/// <para>
/// 这些是纯函数：只决定「用什么词、什么颜色」，不碰 DOM。
/// 它们覆盖了首页不会出错的边界——状态为 null、模块未启用、状态枚举新增。
/// </para>
/// </summary>
public class HomeConsoleProjectionTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 15, 0, 0);

    // ===== 调度器状态 =====

    [Fact]
    public void SchedulerStateText_Null_ShouldBeUnknownInsteadOfThrowing()
    {
        // 首页在调度模块未启用时会拿到 null，不能让整页渲染失败
        HomeDashboardView.SchedulerStateText(null).Should().Be("未知");
    }

    [Theory]
    [InlineData(true, false, true, "运行中")]
    [InlineData(false, true, true, "待机")]
    [InlineData(false, false, true, "已暂停")]
    [InlineData(false, false, false, "已停止")]
    public void SchedulerStateText_GivenRunningStandbyStarted_ShouldFollowPriority(
        bool isRunning, bool isStandby, bool isStarted, string expected)
    {
        var status = new SchedulerStatus
        {
            IsRunning = isRunning,
            IsStandby = isStandby,
            IsStarted = isStarted
        };

        HomeDashboardView.SchedulerStateText(status).Should().Be(expected);
    }

    [Fact]
    public void SchedulerStateTone_ShouldBeOkOnlyWhenRunning()
    {
        HomeDashboardView.SchedulerStateTone(new SchedulerStatus { IsRunning = true }).Should().Be("tone-ok");
        HomeDashboardView.SchedulerStateTone(new SchedulerStatus { IsRunning = false }).Should().Be("tone-idle");
        HomeDashboardView.SchedulerStateTone(null).Should().Be("tone-idle");
    }

    // ===== MCP 状态 =====

    [Theory]
    [InlineData(McpConnectionState.Connected, "已连接")]
    [InlineData(McpConnectionState.Connecting, "连接中")]
    [InlineData(McpConnectionState.Reconnecting, "重连中")]
    [InlineData(McpConnectionState.Paused, "已暂停")]
    [InlineData(McpConnectionState.Error, "异常")]
    [InlineData(McpConnectionState.Pending, "等待中")]
    [InlineData(McpConnectionState.Removed, "已移除")]
    [InlineData(McpConnectionState.Disabled, "已禁用")]
    public void McpStateText_ForAnyConnectionState_ShouldMapToChineseLabel(McpConnectionState state, string expected)
    {
        var status = new McpServerStatus(
            "srv", state, config: null, McpServerPriority.Normal,
            toolCount: 0, toolNames: Array.Empty<string>(),
            createdAt: DateTimeOffset.Now, lastConnectedAt: null, lastErrorAt: null,
            lastError: null, reconnectAttempts: 0, activePolicy: null);

        HomeDashboardView.McpStateText(status).Should().Be(expected);
    }

    [Fact]
    public void McpStateText_DisabledConfig_ShouldWinOverConnectionState()
    {
        // Config.Disabled 为 true 时即便 State 停留在 Connected，也应显示「已禁用」：
        // 用户关掉的服务不该在首页看起来还在跑
        var status = new McpServerStatus(
            "srv", McpConnectionState.Connected,
            config: new McpServerConfig { Disabled = true },
            McpServerPriority.Normal,
            toolCount: 3, toolNames: Array.Empty<string>(),
            createdAt: DateTimeOffset.Now, lastConnectedAt: null, lastErrorAt: null,
            lastError: null, reconnectAttempts: 0, activePolicy: null);

        HomeDashboardView.McpStateText(status).Should().Be("已禁用");
        HomeDashboardView.McpStateTone(status).Should().Be("tone-idle");
    }

    [Theory]
    [InlineData(McpConnectionState.Connected, "tone-ok")]
    [InlineData(McpConnectionState.Error, "tone-err")]
    [InlineData(McpConnectionState.Paused, "tone-warn")]
    [InlineData(McpConnectionState.Reconnecting, "tone-warn")]
    [InlineData(McpConnectionState.Pending, "tone-idle")]
    [InlineData(McpConnectionState.Connecting, "tone-idle")]
    public void McpStateTone_ForAnyConnectionState_ShouldMapToTone(McpConnectionState state, string expected)
    {
        var status = new McpServerStatus(
            "srv", state, config: null, McpServerPriority.Normal,
            toolCount: 0, toolNames: Array.Empty<string>(),
            createdAt: DateTimeOffset.Now, lastConnectedAt: null, lastErrorAt: null,
            lastError: null, reconnectAttempts: 0, activePolicy: null);

        HomeDashboardView.McpStateTone(status).Should().Be(expected);
    }

    // ===== Agent 运行时 =====

    [Fact]
    public void AgentRuntimeText_AcpPassthrough_ShouldBeAcp()
    {
        var agent = new AgentDefinition { Name = "general", Runtime = AgentRuntime.AcpPassthrough };

        HomeDashboardView.AgentRuntimeText(agent).Should().Be("ACP");
    }

    [Fact]
    public void AgentRuntimeText_Native_ShouldBeNative()
    {
        // 默认值即 Native：内置 Agent 不显式设置 Runtime。
        // Native / ACP 是运行时分类名，按 §10 U1 保留原文，不译成中文。
        var agent = new AgentDefinition { Name = "build" };

        HomeDashboardView.AgentRuntimeText(agent).Should().Be("Native");
    }

    // ===== 会话活跃窗口 =====

    [Fact]
    public void IsActiveWithin_ExactlyOnWindowEdge_ShouldBeActive()
    {
        // 边界取「含」：正好 24h 前的那条会话仍算活跃，避免统计在整点抖动
        HomeDashboardView.IsActiveWithin(
            Now.AddHours(-24), Now, TimeSpan.FromHours(24)).Should().BeTrue();
    }

    [Fact]
    public void IsActiveWithin_JustOutsideWindow_ShouldBeIdle()
    {
        HomeDashboardView.IsActiveWithin(
            Now.AddHours(-24).AddSeconds(-1), Now, TimeSpan.FromHours(24)).Should().BeFalse();
    }

    [Fact]
    public void IsActiveWithin_FutureTimestamp_ShouldCountAsActive()
    {
        // 时钟漂移或写入路径时区混用会产生「未来」时间：
        // 那是刚写入的会话，不能被算成空闲。
        HomeDashboardView.IsActiveWithin(
            Now.AddHours(1), Now, TimeSpan.FromHours(24)).Should().BeTrue();
    }

    // ===== 会话分布 =====

    [Fact]
    public void SessionStatusBuckets_ShouldOrderBySeverityAndDropEmptyBuckets()
    {
        var sessions = new List<SessionSummary>
        {
            new() { Id = "a", Status = SessionStatus.Active, LastActiveAt = Now.AddHours(-1) },
            new() { Id = "b", Status = SessionStatus.Idle, LastActiveAt = Now.AddDays(-3) },
            new() { Id = "c", Status = SessionStatus.Error, LastActiveAt = Now.AddHours(-2) }
        };

        var buckets = HomeDashboardView.SessionStatusBuckets(
            sessions, Now, TimeSpan.FromHours(24));

        buckets.Select(b => b.Label).Should().Equal("出错", "活跃 · 24h", "空闲");
        buckets.Select(b => b.Count).Should().Equal(1, 1, 1);
    }

    [Fact]
    public void SessionStatusBuckets_EmptyInput_ShouldReturnNoBuckets()
    {
        // 无会话时右栏不出现「会话分布」整块，而不是三条 0
        HomeDashboardView.SessionStatusBuckets(
            Array.Empty<SessionSummary>(), Now, TimeSpan.FromHours(24)).Should().BeEmpty();
    }

    [Fact]
    public void SessionStatusBuckets_ErrorStatus_ShouldNotAlsoCountAsActive()
    {
        // 出错的会话必须只落在「出错」一档，不能被活跃/空闲重复统计
        var sessions = new List<SessionSummary>
        {
            new() { Id = "err", Status = SessionStatus.Error, LastActiveAt = Now.AddMinutes(-5) }
        };

        var buckets = HomeDashboardView.SessionStatusBuckets(
            sessions, Now, TimeSpan.FromHours(24));

        buckets.Should().ContainSingle();
        buckets[0].Label.Should().Be("出错");
    }

    [Fact]
    public void SessionStatusBuckets_ShouldUseLastActiveAtNotUpdatedAt()
    {
        // 首页口径是「最近有没有在聊」，不是「元数据最近被改过」：
        // 元数据刚更新但很久没活动的会话应算空闲。
        var sessions = new List<SessionSummary>
        {
            new()
            {
                Id = "meta-only",
                Status = SessionStatus.Idle,
                LastActiveAt = Now.AddDays(-5),
                UpdatedAt = Now.AddMinutes(-1)
            }
        };

        var buckets = HomeDashboardView.SessionStatusBuckets(
            sessions, Now, TimeSpan.FromHours(24));

        buckets.Should().ContainSingle();
        buckets[0].Label.Should().Be("空闲");
    }
}
