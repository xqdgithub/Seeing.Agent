using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

public class TuiInputEditorStateTests
{
    private static TuiInputEditorState WithText(string text)
    {
        var state = new TuiInputEditorState();
        state.SetText(text);
        return state;
    }

    [Fact]
    public void InsertText_ShouldAppendAndAdvanceCursor()
    {
        var state = new TuiInputEditorState();

        state.Apply(new TuiKeyInput(TuiInputAction.InsertText, "abc"));

        state.Text.Should().Be("abc");
        state.Cursor.Should().Be(3);
    }

    [Fact]
    public void InsertText_InMiddle_ShouldInsertAtCursor()
    {
        var state = WithText("ac");
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));

        state.Apply(new TuiKeyInput(TuiInputAction.InsertText, "b"));

        state.Text.Should().Be("abc");
        state.Cursor.Should().Be(2);
    }

    [Fact]
    public void Backspace_ShouldRemovePreviousChar()
    {
        var state = WithText("abc");

        state.Apply(new TuiKeyInput(TuiInputAction.Backspace));

        state.Text.Should().Be("ab");
        state.Cursor.Should().Be(2);
    }

    [Fact]
    public void Backspace_AtStart_ShouldNoOp()
    {
        var state = WithText("abc");
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));

        state.Apply(new TuiKeyInput(TuiInputAction.Backspace));

        state.Text.Should().Be("abc");
        state.Cursor.Should().Be(0);
    }

    [Fact]
    public void DeleteForward_ShouldRemoveCharAtCursor()
    {
        var state = WithText("abc");
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));

        state.Apply(new TuiKeyInput(TuiInputAction.DeleteForward));

        state.Text.Should().Be("ab");
        state.Cursor.Should().Be(2);
    }

    [Fact]
    public void MoveLeftAndRight_ShouldClampAtBounds()
    {
        var state = WithText("ab");

        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));
        state.Cursor.Should().Be(0);

        state.Apply(new TuiKeyInput(TuiInputAction.MoveRight));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveRight));
        state.Apply(new TuiKeyInput(TuiInputAction.MoveRight));
        state.Cursor.Should().Be(2);
    }

    [Fact]
    public void InsertNewline_ShouldCreateMultilineText()
    {
        var state = WithText("ab");

        state.Apply(new TuiKeyInput(TuiInputAction.InsertNewline));
        state.Apply(new TuiKeyInput(TuiInputAction.InsertText, "cd"));

        state.Text.Should().Be("ab\ncd");
        state.Cursor.Should().Be(5);
    }

    [Fact]
    public void HistoryPrev_ShouldRecallLastSubmitted()
    {
        var state = new TuiInputEditorState();
        state.SetText("first");
        state.Apply(new TuiKeyInput(TuiInputAction.Submit));
        state.Clear();

        state.Apply(new TuiKeyInput(TuiInputAction.HistoryPrev));

        state.Text.Should().Be("first");
        state.Cursor.Should().Be(5);
    }

    [Fact]
    public void HistoryPrevThenNext_ShouldReturnToDraft()
    {
        var state = new TuiInputEditorState();
        state.SetText("first");
        state.Apply(new TuiKeyInput(TuiInputAction.Submit));
        state.Clear();

        state.Apply(new TuiKeyInput(TuiInputAction.HistoryPrev));
        state.Apply(new TuiKeyInput(TuiInputAction.HistoryNext));

        state.Text.Should().BeEmpty();
        state.Cursor.Should().Be(0);
    }

    [Fact]
    public void HistoryPrev_WithNoHistory_ShouldNoOp()
    {
        var state = new TuiInputEditorState();

        state.Apply(new TuiKeyInput(TuiInputAction.HistoryPrev));

        state.Text.Should().BeEmpty();
        state.Cursor.Should().Be(0);
    }

    [Fact]
    public void Clear_ShouldResetTextCursorAndHistoryPointer()
    {
        var state = WithText("abc");
        state.Apply(new TuiKeyInput(TuiInputAction.MoveLeft));

        state.Clear();

        state.Text.Should().BeEmpty();
        state.Cursor.Should().Be(0);
    }

    [Fact]
    public void Cancel_WithText_ShouldClearInput()
    {
        var state = WithText("abc");

        state.Apply(new TuiKeyInput(TuiInputAction.Cancel));

        state.Text.Should().BeEmpty();
    }

    [Fact]
    public void Cancel_WithEmptyText_ShouldNoOp()
    {
        var state = new TuiInputEditorState();

        state.Apply(new TuiKeyInput(TuiInputAction.Cancel));

        state.Text.Should().BeEmpty();
        state.Cursor.Should().Be(0);
    }

    [Fact]
    public void Attachments_ShouldAddAndRemove()
    {
        var state = new TuiInputEditorState();

        state.AddAttachment("a.png");
        state.AddAttachment("b.txt");
        state.Attachments.Should().Equal("a.png", "b.txt");

        state.RemoveAttachment(0).Should().BeTrue();
        state.Attachments.Should().Equal("b.txt");

        state.RemoveAttachment(5).Should().BeFalse();
        state.Attachments.Should().Equal("b.txt");

        state.ClearAttachments();
        state.Attachments.Should().BeEmpty();
    }
}
