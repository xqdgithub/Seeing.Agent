using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

/// <summary>
/// 文件工作台的「内部」状态。
/// <para>
/// 开合 / 停靠侧 / 宽度已移交工作台平台（<c>WorkbenchDockState</c>，测试见
/// <c>Seeing.Agent.WebUI.Workbench.Tests</c>），因此这里的用例只剩三件事：
/// 目录树可见性、agent 打开的待处理请求、按会话隔离的标签集合。
/// </para>
/// </summary>
public class FilePanelStateTests
{
    private static FileResourceId Id(string rel) => new("root", rel);

    [Fact]
    public void RequestOpen_ShouldHideTreeAndRecordPending()
    {
        var s = new FilePanelState();

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.TreeVisible.Should().BeFalse("agent 驱动打开时默认收起目录树，把宽度留给内容");
        s.PendingOpen.Should().Be(new FileOpenRequest(Id("a.txt")));
    }

    [Fact]
    public void RequestOpen_WithPosition_ShouldCarryLineColumn()
    {
        var s = new FilePanelState();

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt"), 10, 5));

        s.PendingOpen.Should().Be(new FileOpenRequest(Id("a.txt"), 10, 5));
    }

    [Fact]
    public void RequestOpen_WithHideTreeFalse_ShouldPreserveTreeVisibility()
    {
        var s = new FilePanelState();
        s.SetTreeVisible(true);

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")), hideTree: false);

        s.TreeVisible.Should().BeTrue("宿主侧打开（用户点击）不应改变用户的目录树偏好");
    }

    [Fact]
    public void RequestOpen_ShouldRaiseChanged()
    {
        var s = new FilePanelState();
        var count = 0;
        s.Changed += () => count++;

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        count.Should().Be(1);
    }

    [Fact]
    public void ConsumePendingOpen_ShouldClearOnce()
    {
        var s = new FilePanelState();
        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.ConsumePendingOpen().Should().Be(new FileOpenRequest(Id("a.txt")));
        s.ConsumePendingOpen().Should().BeNull();
    }

    [Fact]
    public void GetTabs_SameSession_ShouldReturnSameInstance()
    {
        var s = new FilePanelState();

        var a = s.GetTabs("s1");
        var b = s.GetTabs("s1");

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void GetTabs_DifferentSessions_ShouldIsolate()
    {
        var s = new FilePanelState();

        var a = s.GetTabs("s1");
        var b = s.GetTabs("s2");

        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void SetTreeVisible_Changed_ShouldRaiseEventOnce()
    {
        var s = new FilePanelState();
        var count = 0;
        s.Changed += () => count++;

        s.SetTreeVisible(false);
        s.SetTreeVisible(false);

        count.Should().Be(1, "相同值不触发事件，否则每次渲染都会连锁重绘");
    }
}
