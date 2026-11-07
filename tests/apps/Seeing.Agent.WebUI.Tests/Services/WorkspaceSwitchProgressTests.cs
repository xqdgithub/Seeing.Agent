using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WorkspaceSwitchProgressTests
{
    [Fact]
    public void Begin_ShouldTurnOn_AndRaiseChanged()
    {
        var progress = new WorkspaceSwitchProgress();
        var changes = 0;
        progress.Changed += () => changes++;

        progress.Begin("正在切换");

        progress.IsRunning.Should().BeTrue();
        progress.Message.Should().Be("正在切换");
        changes.Should().Be(1);
    }

    [Fact]
    public void Report_ShouldUpdateMessage_OnlyWhileRunning()
    {
        var progress = new WorkspaceSwitchProgress();

        progress.Report("忽略");
        progress.Message.Should().BeNull("未开始时的上报应被丢弃");

        progress.Begin("开始");
        progress.Report("正在加载配置");

        progress.Message.Should().Be("正在加载配置");
    }

    [Fact]
    public void End_ShouldTurnOff_AndBeIdempotent()
    {
        var progress = new WorkspaceSwitchProgress();
        var changes = 0;
        progress.Changed += () => changes++;

        progress.Begin("开始");
        progress.End();
        progress.End();

        progress.IsRunning.Should().BeFalse();
        progress.Message.Should().BeNull();
        changes.Should().Be(2, "重复 End 不应再触发一次重绘");
    }

    [Fact]
    public void Hub_ShouldRouteToScopeOwnedProgress_AndDropOutsideScope()
    {
        var hub = new WorkspaceSwitchProgressHub();
        var scoped = new WorkspaceSwitchProgress();

        using (WorkspaceSwitchProgressHub.Use(scoped))
        {
            hub.Begin("开始");
            scoped.IsRunning.Should().BeTrue("作用域内的上报必须落到该 circuit 的进度实例");

            hub.End();
            scoped.IsRunning.Should().BeFalse();
        }

        // 作用域外上报：不抛出，也不影响任何实例
        hub.Begin("无作用域");
        scoped.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Hub_ShouldDeliverMessages_InsideScope()
    {
        var hub = new WorkspaceSwitchProgressHub();
        var scoped = new WorkspaceSwitchProgress();
        var seen = new List<string?>();

        scoped.Changed += () => seen.Add(scoped.Message);

        using (WorkspaceSwitchProgressHub.Use(scoped))
        {
            hub.Begin("开始");
            hub.Report("加载中");
            hub.End();
        }

        seen.Should().Equal("开始", "加载中", null);
    }
}
