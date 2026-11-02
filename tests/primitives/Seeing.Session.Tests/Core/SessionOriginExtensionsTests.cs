using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Core;

/// <summary>
/// <see cref="SessionOriginExtensions.IsUserVisibleTaskOrigin"/> 的语义守门。
/// <para>
/// 这个谓词决定「哪些会话出现在任务视图 / 可被 task 复用」。三处消费方
/// （<c>TaskStatusTool</c> / <c>TaskTool</c> / <c>TuiSurfaceProvider</c>）共用它，
/// 一旦口径改回白名单，存量会话会在升级后集体从视图消失。
/// </para>
/// </summary>
public class SessionOriginExtensionsTests
{
    [Theory]
    [InlineData(SessionOrigin.User)]
    [InlineData(SessionOrigin.SubAgentTask)]
    public void IsUserVisible_When_User_Or_SubAgentTask_Should_Return_True(SessionOrigin origin)
    {
        // SubAgentTask 是正常委派；User 必须也算——存量子会话落盘时没有 Origin 字段，
        // 反序列化后就是 User。若排除它，升级前的所有 task 会话都会从任务视图消失。
        origin.IsUserVisibleTaskOrigin().Should().BeTrue();
    }

    [Theory]
    [InlineData(SessionOrigin.Reflection)]
    [InlineData(SessionOrigin.Scheduler)]
    [InlineData(SessionOrigin.Heartbeat)]
    public void IsUserVisible_When_Platform_Internal_Should_Return_False(SessionOrigin origin)
    {
        origin.IsUserVisibleTaskOrigin().Should().BeFalse(
            "平台内部会话（复盘 / 定时 / 心跳）不是用户可见的任务");
    }

    [Fact]
    public void IsUserVisible_Should_Not_Be_Whitelist()
    {
        // 反向锁：若有人把实现改成 `origin == SubAgentTask`，上面 User 用例会红。
        // 这里额外锁住「除 SubAgentTask 外至少还有一个可见来源」，防止未来为省事改成白名单。
        Enum.GetValues<SessionOrigin>()
            .Where(origin => origin.IsUserVisibleTaskOrigin())
            .Should().HaveCountGreaterThan(1,
                "可见来源必须多于一个：存量会话缺 Origin 字段时默认为 User，不能只认 SubAgentTask");
    }
}
