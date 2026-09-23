using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

/// <summary>
/// <see cref="SelectableList"/> 契约回归：游标/页窗/勾选/复位/确认语义（spec §5 冻结契约）。
/// </summary>
public sealed class SelectableListTests
{
    [Fact]
    public void Ctor_DefaultCursor_ShouldBeZero()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 10);

        list.Count.Should().Be(3);
        list.SelectedIndex.Should().Be(0);
        list.SelectedLabel.Should().Be("a");
        list.MultiSelect.Should().BeFalse();
    }

    [Fact]
    public void PageIndices_AllowPagingFalse_ShouldReturnAllEvenWhenPageSizeSmaller()
    {
        var list = new SelectableList(["a", "b", "c", "d", "e"], pageSize: 2, allowPaging: false);

        list.PageIndices().Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void MoveDown_AllowPagingFalse_AtEnd_ShouldStayAndNotScroll()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 10, allowPaging: false);

        list.MoveDown();
        list.MoveDown();
        list.MoveDown();

        list.SelectedIndex.Should().Be(2);
        list.PageIndices().Should().Equal(0, 1, 2);
    }

    [Fact]
    public void MoveUp_AllowPagingFalse_AtStart_ShouldStay()
    {
        var list = new SelectableList(["a", "b"], pageSize: 10, allowPaging: false);

        list.MoveUp();

        list.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void MoveDown_AllowPagingTrue_PastPageEnd_ShouldScrollWindow()
    {
        var list = new SelectableList(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], pageSize: 3);

        list.MoveDown();
        list.MoveDown();
        list.MoveDown();

        list.SelectedIndex.Should().Be(3);
        list.PageFirstIndex.Should().Be(1);
        list.PageIndices().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void MoveUp_AllowPagingTrue_BeforePageStart_ShouldScrollWindowUp()
    {
        var list = new SelectableList(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], pageSize: 3);

        for (var i = 0; i < 9; i++)
            list.MoveDown();

        list.PageIndices().Should().Equal(7, 8, 9);

        list.MoveUp();
        list.MoveUp();
        list.MoveUp();

        // 8、7 仍在窗 [7,8,9] 内；6 越出窗顶 → 窗滚动使 6 可见（selected 置顶）。
        list.SelectedIndex.Should().Be(6);
        list.PageIndices().Should().Equal(6, 7, 8);
    }

    [Fact]
    public void SetByHit_Valid_ShouldMoveCursorAndScrollWindowAndReturnTrue()
    {
        var list = new SelectableList(["0", "1", "2", "3", "4", "5"], pageSize: 3);

        list.SetByHit(5).Should().BeTrue();

        list.SelectedIndex.Should().Be(5);
        list.PageIndices().Should().Equal(3, 4, 5);
    }

    [Fact]
    public void SetByHit_SameAsCurrent_ShouldReturnFalse()
    {
        var list = new SelectableList(["0", "1", "2"], pageSize: 3);

        list.SetByHit(1).Should().BeTrue();
        list.SetByHit(1).Should().BeFalse();

        list.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SetByHit_OutOfRange_ShouldReturnFalseAndKeepState()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 3);

        list.SetByHit(3).Should().BeFalse();
        list.SetByHit(-1).Should().BeFalse();

        list.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Toggle_OnAndOff_ShouldTrackChecked()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 3, multiSelect: true);

        list.Toggle(1);

        list.IsChecked(1).Should().BeTrue();
        list.CheckedIndices().Should().Equal(1);

        list.Toggle(1);

        list.IsChecked(1).Should().BeFalse();
        list.CheckedIndices().Should().BeEmpty();
    }

    [Fact]
    public void Toggle_OutOfRange_ShouldBeIgnored()
    {
        var list = new SelectableList(["a"], pageSize: 3, multiSelect: true);

        list.Toggle(1);
        list.Toggle(-1);

        list.CheckedIndices().Should().BeEmpty();
        list.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void CheckedIndices_MultipleToggles_ShouldBeAscending()
    {
        var list = new SelectableList(["a", "b", "c", "d"], pageSize: 4, multiSelect: true);

        list.Toggle(3);
        list.Toggle(0);
        list.Toggle(2);

        list.CheckedIndices().Should().Equal(0, 2, 3);
        list.ConfirmChecked().Should().Equal(0, 2, 3);
    }

    [Fact]
    public void UpdateItems_Changed_ShouldResetCursorToZero()
    {
        var list = new SelectableList(["a", "b", "c", "d", "e"], pageSize: 10);

        list.MoveDown();
        list.MoveDown();
        list.SelectedIndex.Should().Be(2);

        list.UpdateItems(["a", "b", "c", "d", "e", "f"]);

        list.SelectedIndex.Should().Be(0);
        list.PageFirstIndex.Should().Be(0);
    }

    [Fact]
    public void UpdateItems_Shrunk_ShouldDropOutOfRangeChecked()
    {
        var list = new SelectableList(["a", "b", "c", "d", "e"], pageSize: 10, multiSelect: true);

        list.Toggle(1);
        list.Toggle(4);

        list.UpdateItems(["x", "y", "z"]);

        list.Count.Should().Be(3);
        list.CheckedIndices().Should().Equal(1);
        list.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Reset_WithIndex_ShouldMoveCursor()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 3);

        list.MoveDown();
        list.Reset();

        list.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Reset_OutOfRangeIndex_ShouldClampIntoBounds()
    {
        var list = new SelectableList(["a", "b"], pageSize: 2);

        list.Reset(7);

        list.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void Reset_ShouldKeepChecked()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 3, multiSelect: true);

        list.Toggle(2);
        list.Reset();

        list.SelectedIndex.Should().Be(0);
        list.CheckedIndices().Should().Equal(2);
    }

    [Fact]
    public void ConfirmIndex_EmptyList_ShouldReturnMinusOne()
    {
        var list = new SelectableList([], pageSize: 3);

        list.ConfirmIndex().Should().Be(-1);
    }

    [Fact]
    public void ConfirmIndex_ShouldReturnCurrentCursor()
    {
        var list = new SelectableList(["a", "b", "c"], pageSize: 3);

        list.MoveDown();

        list.ConfirmIndex().Should().Be(1);
    }

    [Fact]
    public void MoveUpAndDown_EmptyList_ShouldNotThrow()
    {
        var list = new SelectableList([], pageSize: 3);

        list.MoveUp();
        list.MoveDown();

        list.SelectedIndex.Should().Be(0);
        list.PageIndices().Should().BeEmpty();
        list.SelectedLabel.Should().BeNull();
    }
}
