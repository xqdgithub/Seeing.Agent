using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 侧栏可见性状态机：手动隐藏、新增成员自动展开、复用执行中的成员也展开、按组记忆。
/// <para>
/// 抽出为纯逻辑是为了让「何时该自动展开」这条规则可单测 —— 它直接决定用户
/// 手动收起后侧栏会不会无缘无故跳出来。
/// </para>
/// </summary>
public class SessionSidebarVisibilityTests
{
    private const string GroupA = "group-a";
    private const string GroupB = "group-b";

    private static string[] Members(params string[] ids) => ids;

    [Fact]
    public void Initially_ShouldBeVisible()
    {
        var v = new SessionSidebarVisibility();

        v.ShouldShow(GroupA).Should().BeTrue("首次加载应自动显示侧栏");
    }

    [Fact]
    public void AfterHide_ShouldStayHidden()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1", "s2"));

        v.Hide(GroupA);

        v.ShouldShow(GroupA).Should().BeFalse();
    }

    [Fact]
    public void ReObservingSameMembers_ShouldKeepHidden()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1", "s2"));
        v.Hide(GroupA);

        // 组事件重放同一批成员：不应把侧栏弹出来
        v.Observe(GroupA, Members("s1", "s2"));

        v.ShouldShow(GroupA).Should().BeFalse();
    }

    /// <summary>用户隐藏后新增了子 Agent —— 自动展开。</summary>
    [Fact]
    public void NewMemberAdded_ShouldAutoShow()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1", "s2"));
        v.Hide(GroupA);

        v.Observe(GroupA, Members("s1", "s2", "s3"));

        v.ShouldShow(GroupA).Should().BeTrue();
    }

    /// <summary>用户隐藏后已有成员重新开始执行 —— 也自动展开（2B 的约定）。</summary>
    [Fact]
    public void ExistingMemberStartedExecuting_ShouldAutoShow()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1", "s2"));
        v.Hide(GroupA);

        v.NotifyExecuting("s1", isExecuting: true);

        v.ShouldShow(GroupA).Should().BeTrue();
    }

    /// <summary>执行结束不应触发展开，否则每次子任务收尾都会弹侧栏。</summary>
    [Fact]
    public void ExistingMemberStoppedExecuting_ShouldNotShow()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1", "s2"));
        v.Hide(GroupA);
        v.NotifyExecuting("s1", isExecuting: true);
        v.Hide(GroupA); // 用户再次收起

        v.NotifyExecuting("s1", isExecuting: false);

        v.ShouldShow(GroupA).Should().BeFalse();
    }

    /// <summary>隐藏状态按组记忆：切换组再切回来仍保持隐藏。</summary>
    [Fact]
    public void HiddenState_ShouldBeRememberedPerGroup()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1"));
        v.Hide(GroupA);

        // 切到 B 组：B 从未隐藏过，应显示
        v.ShouldShow(GroupB).Should().BeTrue();

        // 切回 A：仍隐藏
        v.ShouldShow(GroupA).Should().BeFalse();
    }

    /// <summary>隐藏 A 不影响 B 的显示状态。</summary>
    [Fact]
    public void HidingOneGroup_ShouldNotAffectAnother()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1"));
        v.Observe(GroupB, Members("s9"));

        v.Hide(GroupA);

        v.ShouldShow(GroupB).Should().BeTrue();
        v.ShouldShow(GroupA).Should().BeFalse();
    }

    /// <summary>不同组各自记录成员基线，新增判定互不干扰。</summary>
    [Fact]
    public void NewMemberDetection_ShouldBePerGroup()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1"));
        v.Observe(GroupB, Members("s9"));
        v.Hide(GroupA);
        v.Hide(GroupB);

        // B 组新增成员，A 组不变
        v.Observe(GroupB, Members("s9", "s10"));

        v.ShouldShow(GroupB).Should().BeTrue();
        v.ShouldShow(GroupA).Should().BeFalse();
    }

    [Fact]
    public void Show_ShouldExplicitlyRestore()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1"));
        v.Hide(GroupA);

        v.Show(GroupA);

        v.ShouldShow(GroupA).Should().BeTrue();
    }

    [Fact]
    public void Toggle_ShouldFlipState()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members("s1"));

        v.Toggle(GroupA);
        v.ShouldShow(GroupA).Should().BeFalse();

        v.Toggle(GroupA);
        v.ShouldShow(GroupA).Should().BeTrue();
    }

    /// <summary>无组成员时不显示（与原「侧栏无窗口则隐藏」行为一致）。</summary>
    [Fact]
    public void NoMembers_ShouldNotShow()
    {
        var v = new SessionSidebarVisibility();

        v.ShouldShow(GroupA, memberCount: 0).Should().BeFalse();
    }

    /// <summary>空组不建立「已隐藏」记忆，避免后续有成员时误判为隐藏。</summary>
    [Fact]
    public void EmptyGroupThenMembers_ShouldShow()
    {
        var v = new SessionSidebarVisibility();
        v.Observe(GroupA, Members());

        v.Observe(GroupA, Members("s1"));

        v.ShouldShow(GroupA).Should().BeTrue();
    }

    /// <summary>空 groupId 一律当作「无组」，不显示也不记忆。</summary>
    [Fact]
    public void EmptyGroupId_ShouldNotShow()
    {
        var v = new SessionSidebarVisibility();

        v.ShouldShow(string.Empty).Should().BeFalse();
    }
}
