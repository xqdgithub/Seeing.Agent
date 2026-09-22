using FluentAssertions;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 审批模式文案与生效判定测试（对齐 WebUI「默认 / 自动 / 确认」与 EffectivePermissionPolicy 优先级）。
/// </summary>
public sealed class AutoApproveTextTests
{
    [Theory]
    [InlineData(SessionAutoApprove.FollowGlobal, "默认")]
    [InlineData(SessionAutoApprove.Enabled, "自动")]
    [InlineData(SessionAutoApprove.Disabled, "确认")]
    public void Mode_ShouldMapTriState(SessionAutoApprove mode, string expected)
        => AutoApproveText.Mode(mode).Should().Be(expected);

    [Theory]
    [InlineData(SessionAutoApprove.Enabled, false, true)]
    [InlineData(SessionAutoApprove.Disabled, true, false)]
    [InlineData(SessionAutoApprove.FollowGlobal, true, true)]
    [InlineData(SessionAutoApprove.FollowGlobal, false, false)]
    public void IsAuto_ShouldFollowSessionThenGlobal(SessionAutoApprove mode, bool globalAuto, bool expected)
        => AutoApproveText.IsAuto(mode, globalAuto).Should().Be(expected);

    [Theory]
    [InlineData(SessionAutoApprove.FollowGlobal, false, "确认(全局)")]
    [InlineData(SessionAutoApprove.FollowGlobal, true, "自动(全局)")]
    [InlineData(SessionAutoApprove.Enabled, false, "自动")]
    [InlineData(SessionAutoApprove.Disabled, true, "确认")]
    public void Label_ShouldDescribeEffectAndSource(SessionAutoApprove mode, bool globalAuto, string expected)
        => AutoApproveText.Label(mode, globalAuto).Should().Be(expected);

    [Theory]
    [InlineData(SessionAutoApprove.FollowGlobal, SessionAutoApprove.Enabled)]
    [InlineData(SessionAutoApprove.Enabled, SessionAutoApprove.Disabled)]
    [InlineData(SessionAutoApprove.Disabled, SessionAutoApprove.FollowGlobal)]
    public void Next_ShouldCycleTriState(SessionAutoApprove current, SessionAutoApprove expected)
        => AutoApproveText.Next(current).Should().Be(expected);
}
