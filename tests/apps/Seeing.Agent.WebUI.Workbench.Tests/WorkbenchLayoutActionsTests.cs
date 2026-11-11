using FluentAssertions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 布局级操作：工作台换边、聊天区显隐（工作区全屏）。
/// <para>
/// 这两条最容易出的错是「把界面搞成白屏且无法恢复」，因此每条防白屏的分支都要有用例：
/// 关掉最后一个工作台、模块停用清空实例、恢复快照时无实例。
/// </para>
/// </summary>
public class WorkbenchLayoutActionsTests
{
    private static WorkbenchContribution Wb(
        string id, WorkbenchDock dock = WorkbenchDock.Right, double width = WorkbenchSizing.DefaultPercentValue) => new()
    {
        Point = WorkbenchPoints.DockId,
        Id = id,
        Title = id,
        ViewKey = WorkbenchPoints.ViewKeyFor(id),
        DefaultDock = dock,
        Sizing = new WorkbenchSizing(DefaultPercent: width)
    };

    // ── 换边 ──

    [Fact]
    public void MoveToOtherDock_ShouldMoveAndActivateInTarget()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        var moved = state.MoveToOtherDock("s1", key);

        moved.Should().NotBeNull();
        moved!.Value.Dock.Should().Be(WorkbenchDock.Left);

        var layout = state.GetLayout("s1");
        layout.Right.Should().BeEmpty("源 dock 不再保留");
        layout.Left.Should().ContainSingle().Which.Key.WorkbenchId.Should().Be("files");
        layout.ActiveLeft.Should().Be(moved.Value.ToString());
    }

    [Fact]
    public void MoveToOtherDock_ShouldPreserveTitleViewKeyAndState()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;
        state.ApplyViewReport("s1", key, new WorkbenchViewReport
        {
            ViewState = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "a.md" }
        });

        var moved = state.MoveToOtherDock("s1", key)!.Value;

        var instance = state.GetLayout("s1").Left.Single();
        instance.Key.Should().Be(moved);
        instance.Title.Should().Be("files");
        instance.ViewKey.Should().Be(WorkbenchPoints.ViewKeyFor("files"));
        instance.ViewState!["path"].Should().Be("a.md", "换边不应丢失可恢复状态");
    }

    [Fact]
    public void MoveToOtherDock_TargetAlreadyHasSameWorkbench_ShouldMergeNotDuplicate()
    {
        var state = new WorkbenchDockState();
        var left = state.Open("s1", WorkbenchDock.Left, Wb("files", WorkbenchDock.Left))!.Value;
        var right = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        var moved = state.MoveToOtherDock("s1", right);

        // 目标（左）已有同一个工作台：合并到它，源关闭
        moved!.Value.Should().Be(left);

        var layout = state.GetLayout("s1");
        layout.Right.Should().BeEmpty();
        layout.Left.Should().ContainSingle("同一工作台不会在两侧各留一份");
        layout.ActiveLeft.Should().Be(left.ToString());
    }

    [Fact]
    public void MoveToOtherDock_UnknownInstance_ShouldBeNoOp()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        var moved = state.MoveToOtherDock("s1", new WorkbenchInstanceKey(WorkbenchDock.Right, "nope"));

        moved.Should().BeNull();
        state.GetLayout("s1").Right.Should().ContainSingle("原有实例不受影响");
    }

    [Fact]
    public void MoveToOtherDock_ShouldRaiseChanged()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;
        var changed = 0;
        state.Changed += _ => changed++;

        state.MoveToOtherDock("s1", key);

        changed.Should().Be(1, "换边必须触发重渲染与持久化");
    }

    // ── 聊天区显隐 ──

    [Fact]
    public void SetChatHidden_WithWorkbench_ShouldApply()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.SetChatHidden("s1", true).Should().BeTrue();
        state.IsChatHidden("s1").Should().BeTrue();
    }

    [Fact]
    public void SetChatHidden_WithoutWorkbench_ShouldRefuse()
    {
        // 没有工作台时隐藏聊天 = 白屏且无恢复入口
        var state = new WorkbenchDockState();

        state.SetChatHidden("s1", true).Should().BeFalse();
        state.IsChatHidden("s1").Should().BeFalse();
    }

    [Fact]
    public void SetChatHidden_SameValue_ShouldNotRaiseChanged()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.SetChatHidden("s1", true);
        var changed = 0;
        state.Changed += _ => changed++;

        state.SetChatHidden("s1", true).Should().BeFalse();

        changed.Should().Be(0);
    }

    [Fact]
    public void ClosingLastWorkbench_ShouldRestoreChat()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;
        state.SetChatHidden("s1", true);

        state.Close("s1", key);

        state.IsChatHidden("s1").Should().BeFalse("关掉最后一个工作台必须把聊天区放回来，否则白屏");
    }

    [Fact]
    public void Prune_RemovingAll_ShouldRestoreChat()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("video"));
        state.SetChatHidden("s1", true);

        state.Prune("s1", _ => false);

        state.IsChatHidden("s1").Should().BeFalse("模块停用清空实例后同样不能留白屏");
    }

    [Fact]
    public void IsChatHidden_UnknownSession_ShouldBeFalse()
        => new WorkbenchDockState().IsChatHidden("nope").Should().BeFalse();

    [Fact]
    public void ChatHidden_ShouldBePerSession()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.Open("s2", WorkbenchDock.Right, Wb("files"));

        state.SetChatHidden("s1", true);

        state.IsChatHidden("s1").Should().BeTrue();
        state.IsChatHidden("s2").Should().BeFalse("切会话不得串场");
    }

    // ── 宽度预算 ──

    [Fact]
    public void DefaultSizing_ShouldBeHalfOfContentWidth()
    {
        // 迁移前的文件面板就是 50%：单开一个工作台时它应当占半个屏，
        // 而不是被「两个 dock 的总预算」提前牺牲掉
        WorkbenchSizing.Default.DefaultPercent.Should().Be(50);
        WorkbenchSizing.DefaultPercentValue.Should().Be(50);
    }

    [Fact]
    public void OpenSecondWorkbenchInSameDock_ShouldNotResetWidth()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files", WorkbenchDock.Right, width: 50));
        state.SetWidth("s1", WorkbenchDock.Right, 65);

        // 同侧再开一个工作台（新标签）：宽度是用户拖出来的，不该被声明值覆盖
        state.Open("s1", WorkbenchDock.Right, Wb("video", WorkbenchDock.Right, width: 50));

        state.GetLayout("s1").RightWidthPercent.Should().Be(65,
            "加标签不应把用户拖好的宽度重置回默认值");
    }

    [Fact]
    public void OpenBothDocks_ShouldKeepNewlyOpenedSideWide()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.Open("s1", WorkbenchDock.Left, Wb("video", WorkbenchDock.Left));

        var layout = state.GetLayout("s1");

        // 新开的是左侧：它保持 50%，右侧被让到预算内
        layout.LeftWidthPercent.Should().Be(50, "刚打开的一侧应当按声明宽度呈现");
        (layout.LeftWidthPercent + layout.RightWidthPercent)
            .Should().BeLessThanOrEqualTo(WorkbenchDockState.MaxCombinedPercent + 0.01);
    }

    [Fact]
    public void SingleDock_ShouldNotBeConstrainedByCombinedBudget()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.GetLayout("s1").RightWidthPercent.Should().Be(50,
            "只有一侧时没有预算冲突，应保持声明的 50%");
    }

    [Fact]
    public void Open_BothDocksAtHalf_ShouldLeaveRoomForChat()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Left, Wb("files", WorkbenchDock.Left));
        state.Open("s1", WorkbenchDock.Right, Wb("video"));

        var layout = state.GetLayout("s1");
        (layout.LeftWidthPercent + layout.RightWidthPercent)
            .Should().BeLessThanOrEqualTo(WorkbenchDockState.MaxCombinedPercent + 0.01,
                "两侧之和必须有上限，否则主列被压成 0 宽（表现为聊天区凭空消失）");
    }

    [Fact]
    public void SetWidth_ShouldShrinkOtherSideToStayWithinBudget()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Left, Wb("files", WorkbenchDock.Left, width: 30));
        state.Open("s1", WorkbenchDock.Right, Wb("video", WorkbenchDock.Right, width: 30));

        // 想把右侧拖到 70%：左侧已在下限 20%，因此右侧最大只能到 75-20=55%
        state.SetWidth("s1", WorkbenchDock.Right, 70);

        var layout = state.GetLayout("s1");
        layout.LeftWidthPercent.Should().Be(WorkbenchDockState.MinWidthPercent,
            "对侧被压到下限即止");
        layout.RightWidthPercent.Should().Be(
            WorkbenchDockState.MaxCombinedPercent - WorkbenchDockState.MinWidthPercent,
            "被操作的一侧让到预算允许的最大值");
        (layout.LeftWidthPercent + layout.RightWidthPercent)
            .Should().BeLessThanOrEqualTo(WorkbenchDockState.MaxCombinedPercent + 0.01);
    }

    [Fact]
    public void SetWidth_OtherSideAtMin_ShouldStillKeepBudget()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Left, Wb("files", WorkbenchDock.Left, width: 20));
        state.Open("s1", WorkbenchDock.Right, Wb("video", WorkbenchDock.Right, width: 20));

        // 一侧已经在下限，另一侧不能一路拖到 75
        state.SetWidth("s1", WorkbenchDock.Right, 75);

        var layout = state.GetLayout("s1");
        (layout.LeftWidthPercent + layout.RightWidthPercent)
            .Should().BeLessThanOrEqualTo(WorkbenchDockState.MaxCombinedPercent + 0.01);
    }

    [Fact]
    public void SetWidth_SingleDock_ShouldAllowUpToMax()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.SetWidth("s1", WorkbenchDock.Right, WorkbenchDockState.MaxWidthPercent);

        state.GetLayout("s1").RightWidthPercent.Should().Be(WorkbenchDockState.MaxWidthPercent,
            "只有一侧时不存在预算冲突");
    }

    // ── 快照往返 ──

    [Fact]
    public void Snapshot_ShouldRoundTripChatHidden()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.SetChatHidden("s1", true);

        var snapshot = WorkbenchLayoutSnapshot.From("s1", state.GetLayout("s1"));
        snapshot.ChatHidden.Should().BeTrue();

        var restored = new WorkbenchDockState();
        restored.Restore("s1", new WorkbenchSessionLayout(
            snapshot.Left.Select(i => new WorkbenchInstance(
                new WorkbenchInstanceKey(WorkbenchDock.Left, i.WorkbenchId), i.WorkbenchId,
                WorkbenchPoints.ViewKeyFor(i.WorkbenchId), null, i.ViewState)).ToArray(),
            snapshot.Right.Select(i => new WorkbenchInstance(
                new WorkbenchInstanceKey(WorkbenchDock.Right, i.WorkbenchId), i.WorkbenchId,
                WorkbenchPoints.ViewKeyFor(i.WorkbenchId), null, i.ViewState)).ToArray(),
            snapshot.ActiveLeft,
            snapshot.ActiveRight,
            snapshot.LeftWidthPercent,
            snapshot.RightWidthPercent)
        {
            ChatHidden = snapshot.ChatHidden
        });

        restored.IsChatHidden("s1").Should().BeTrue("刷新后全屏状态应当恢复");
    }

    [Fact]
    public void Restore_EmptyLayout_ShouldNotCarryChatHidden()
    {
        var state = new WorkbenchDockState();

        state.Restore("s1", WorkbenchSessionLayout.Empty with { ChatHidden = true });

        state.IsChatHidden("s1").Should().BeFalse("没有实例就不存在「全屏」这回事");
    }

    [Fact]
    public void Restore_WithInstances_ShouldCarryChatHidden()
    {
        var state = new WorkbenchDockState();

        state.Restore("s1", new WorkbenchSessionLayout(
            [new WorkbenchInstance(
                new WorkbenchInstanceKey(WorkbenchDock.Left, "files"), "文件",
                WorkbenchPoints.ViewKeyFor("files"), null, null)],
            [],
            "Left:files#0",
            null,
            45,
            45)
        {
            ChatHidden = true
        });

        state.IsChatHidden("s1").Should().BeTrue();
    }
}
