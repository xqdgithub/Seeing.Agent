using FluentAssertions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 停靠状态：会话分桶、开/激活/关、宽度夹取、ViewState 保留、Prune 收敛、恢复。
/// </summary>
public class WorkbenchDockStateTests
{
    private static WorkbenchContribution Wb(
        string id, WorkbenchDock dock = WorkbenchDock.Right, double width = 45, int order = 0) => new()
        {
            Point = WorkbenchPoints.DockId,
            Id = id,
            Title = id,
            ViewKey = WorkbenchPoints.ViewKeyFor(id),
            Order = order,
            DefaultDock = dock,
            Sizing = new WorkbenchSizing(DefaultPercent: width)
        };

    [Fact]
    public void GetLayout_UnknownSession_ShouldBeEmptyWithDefaultWidth()
    {
        var layout = new WorkbenchDockState(55).GetLayout("nope");

        layout.Left.Should().BeEmpty();
        layout.Right.Should().BeEmpty();
        layout.RightWidthPercent.Should().Be(55);
    }

    [Fact]
    public void Open_ShouldAddAndActivate()
    {
        var state = new WorkbenchDockState();

        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"));

        key.Should().NotBeNull();
        var layout = state.GetLayout("s1");
        layout.Right.Should().ContainSingle().Which.Key.WorkbenchId.Should().Be("files");
        layout.ActiveRight.Should().Be(key!.Value.ToString());
        layout.Right[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public void Open_SameWorkbenchAgain_ShouldNotDuplicate()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.GetLayout("s1").Right.Should().HaveCount(1);
    }

    [Fact]
    public void Open_SameWorkbenchInBothDocks_ShouldCoexist()
    {
        var state = new WorkbenchDockState();

        state.Open("s1", WorkbenchDock.Left, Wb("files"));
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        var layout = state.GetLayout("s1");
        layout.Left.Should().HaveCount(1);
        layout.Right.Should().HaveCount(1);
    }

    [Fact]
    public void Open_ShouldApplyDeclaredWidth()
    {
        var state = new WorkbenchDockState();

        state.Open("s1", WorkbenchDock.Right, Wb("files", width: 60));

        state.GetLayout("s1").RightWidthPercent.Should().Be(60);
    }

    [Fact]
    public void Open_ShouldPreserveExistingViewState()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        state.ApplyViewReport("s1", key, new WorkbenchViewReport
        {
            ViewState = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "a.md" }
        });

        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.GetLayout("s1").Right[0].ViewState!["path"].Should().Be("a.md",
            "重开同一工作台不应丢掉已上报的状态");
    }

    [Fact]
    public void Activate_Nonexistent_ShouldBeNoOp()
    {
        var state = new WorkbenchDockState();
        var changed = 0;
        state.Changed += _ => changed++;

        state.Activate("s1", new WorkbenchInstanceKey(WorkbenchDock.Right, "nope"));

        changed.Should().Be(0);
    }

    [Fact]
    public void Close_Active_ShouldMoveActivationToNextInstance()
    {
        var state = new WorkbenchDockState();
        var a = state.Open("s1", WorkbenchDock.Right, Wb("a"))!.Value;
        state.Open("s1", WorkbenchDock.Right, Wb("b"));
        state.Activate("s1", a);

        state.Close("s1", a);

        var layout = state.GetLayout("s1");
        layout.Right.Should().HaveCount(1);
        layout.ActiveRight.Should().Contain("b");
    }

    [Fact]
    public void Close_LastInstance_ShouldDropSession()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        state.Close("s1", key);

        state.GetLayout("s1").Right.Should().BeEmpty();
        state.HasAny("s1").Should().BeFalse();
    }

    [Fact]
    public void SetWidth_ShouldClampToRange()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.SetWidth("s1", WorkbenchDock.Right, 5);
        state.GetLayout("s1").RightWidthPercent.Should().Be(WorkbenchDockState.MinWidthPercent);

        state.SetWidth("s1", WorkbenchDock.Right, 99);
        state.GetLayout("s1").RightWidthPercent.Should().Be(WorkbenchDockState.MaxWidthPercent);
    }

    [Fact]
    public void Sessions_ShouldBeIsolated()
    {
        var state = new WorkbenchDockState();

        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.Open("s2", WorkbenchDock.Right, Wb("video"));

        state.GetLayout("s1").Right.Single().Key.WorkbenchId.Should().Be("files");
        state.GetLayout("s2").Right.Single().Key.WorkbenchId.Should().Be("video");
    }

    [Fact]
    public void Prune_ShouldRemoveUnknownWorkbenches()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        state.Open("s1", WorkbenchDock.Right, Wb("video"));
        var changed = 0;
        state.Changed += _ => changed++;

        state.Prune("s1", id => id == "files");

        state.GetLayout("s1").Right.Should().ContainSingle()
            .Which.Key.WorkbenchId.Should().Be("files");
        changed.Should().Be(1);
    }

    [Fact]
    public void Prune_AllRemoved_ShouldDropSession()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("video"));

        state.Prune("s1", _ => false);

        state.HasAny("s1").Should().BeFalse();
    }

    [Fact]
    public void Prune_NothingRemoved_ShouldNotNotify()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        var changed = 0;
        state.Changed += _ => changed++;

        state.Prune("s1", _ => true);

        changed.Should().Be(0, "没有变化就不该触发持久化与快照上报");
    }

    [Fact]
    public void ApplyViewReport_PreservesPreviousStateWhenNull()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        state.ApplyViewReport("s1", key, new WorkbenchViewReport
        {
            ViewState = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "a.md" }
        });
        state.ApplyViewReport("s1", key, new WorkbenchViewReport { ContextSummary = "在看 a.md" });

        state.GetLayout("s1").Right[0].ViewState!["path"].Should().Be("a.md",
            "ViewState 为 null 表示「不覆盖已存值」");
    }

    [Fact]
    public void Clear_ShouldDropSession()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.Clear("s1");

        state.HasAny("s1").Should().BeFalse();
    }

    [Fact]
    public void Restore_ShouldRebuildMembershipAndWidths()
    {
        var state = new WorkbenchDockState();
        var payload = JsonSerializer.SerializeToElement(new { path = "a.md" });

        state.Restore("s1", new WorkbenchSessionLayout(
            [new WorkbenchInstance(new WorkbenchInstanceKey(WorkbenchDock.Left, "files"), "文件", "workbench.files", payload, null)],
            [new WorkbenchInstance(new WorkbenchInstanceKey(WorkbenchDock.Right, "video"), "MD", "workbench.video", null, null)],
            "Left:files#0",
            "Right:video#0",
            40,
            30));

        var layout = state.GetLayout("s1");
        layout.Left.Should().ContainSingle();
        layout.Right.Should().ContainSingle();
        layout.LeftWidthPercent.Should().Be(40);
        layout.RightWidthPercent.Should().Be(30);
        layout.ActiveLeft.Should().Be("Left:files#0");
    }

    [Fact]
    public void Restore_WidthsOverBudget_ShouldScaleProportionally()
    {
        // 旧快照可能来自「没有预算」的版本（两侧加起来 100%），恢复时必须等比压回预算内，
        // 否则主列被压成 0 宽（表现为聊天区凭空消失）
        var state = new WorkbenchDockState();

        state.Restore("s1", new WorkbenchSessionLayout(
            [new WorkbenchInstance(new WorkbenchInstanceKey(WorkbenchDock.Left, "files"), "文件", "workbench.files", null, null)],
            [new WorkbenchInstance(new WorkbenchInstanceKey(WorkbenchDock.Right, "video"), "MD", "workbench.video", null, null)],
            null,
            null,
            62,
            38));

        var layout = state.GetLayout("s1");
        (layout.LeftWidthPercent + layout.RightWidthPercent)
            .Should().BeLessThanOrEqualTo(WorkbenchDockState.MaxCombinedPercent + 0.01);
        layout.LeftWidthPercent.Should().BeApproximately(62 * 0.75, 0.5, "等比缩放保留相对关系");
        layout.RightWidthPercent.Should().BeApproximately(38 * 0.75, 0.5);
    }

    [Fact]
    public void Restore_EmptyLayout_ShouldClearSession()
    {
        var state = new WorkbenchDockState();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        state.Restore("s1", WorkbenchSessionLayout.Empty);

        state.HasAny("s1").Should().BeFalse();
    }

    [Fact]
    public void Restore_ActiveKeyNotInList_ShouldFallBackToFirst()
    {
        var state = new WorkbenchDockState();

        state.Restore("s1", new WorkbenchSessionLayout(
            [],
            [new WorkbenchInstance(new WorkbenchInstanceKey(WorkbenchDock.Right, "files"), "文件", "workbench.files", null, null)],
            null,
            "Right:gone#0",
            45,
            45));

        state.GetLayout("s1").ActiveRight.Should().Be("Right:files#0");
    }

    [Fact]
    public void SubscriberThrows_ShouldNotBreakOtherSubscribers()
    {
        var state = new WorkbenchDockState();
        var secondCalled = false;
        state.Changed += _ => throw new InvalidOperationException("boom");
        state.Changed += _ => secondCalled = true;

        var act = () => state.Open("s1", WorkbenchDock.Right, Wb("files"));

        act.Should().NotThrow();
        secondCalled.Should().BeTrue();
    }
}
