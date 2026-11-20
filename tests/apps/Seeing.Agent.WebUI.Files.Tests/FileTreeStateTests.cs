using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileTreeStateTests
{
    private static FileResourceId Id(string rootId, string path) => new(rootId, path);

    [Fact]
    public void SetSelected_NewId_ShouldUpdateAndRaise()
    {
        var s = new FileTreeState();
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected(Id("r1", "src/app/main.js"));

        s.SelectedId.Should().Be(Id("r1", "src/app/main.js"));
        raised.Should().Be(1);
        s.SelectionVersion.Should().Be(1);
    }

    [Fact]
    public void SetSelected_BackslashPath_ShouldNormalize()
    {
        var s = new FileTreeState();

        s.SetSelected(Id("r1", "src\\app\\main.js"));

        s.SelectedId!.Value.RelativePath.Should().Be("src/app/main.js");
    }

    [Fact]
    public void SetSelected_SameValue_ShouldNotRaise()
    {
        var s = new FileTreeState();
        s.SetSelected(Id("r1", "a.txt"));
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected(Id("r1", "a.txt"));

        raised.Should().Be(0);
    }

    [Fact]
    public void SetSelected_SameValueDifferentCase_ShouldNotRaise()
    {
        var s = new FileTreeState();
        s.SetSelected(Id("r1", "A.txt"));
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected(Id("r1", "a.txt"));

        raised.Should().Be(0);
    }

    [Fact]
    public void SetSelected_Null_ShouldClearSelection()
    {
        var s = new FileTreeState();
        s.SetSelected(Id("r1", "a.txt"));

        s.SetSelected(null);

        s.SelectedId.Should().BeNull();
        s.SelectionVersion.Should().Be(2);
    }

    [Fact]
    public void SetSelectedDirectory_ShouldRaiseSelectionChanged()
    {
        var s = new FileTreeState();
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelectedDirectory(Id("r1", "src"));

        s.SelectedDirectoryId.Should().Be(Id("r1", "src"));
        raised.Should().Be(1);

        s.SetSelectedDirectory(Id("r1", "src"));
        raised.Should().Be(1, "同一目录重复点击不应重复触发");
    }

    [Fact]
    public void IsDirectChildOf_SameRelativePathDifferentRoot_ShouldNotMatch()
    {
        var changed = Id("r2", "src/a.txt");
        var dir = Id("r1", "src");

        FileTreeState.IsDirectChildOf(changed, dir).Should().BeFalse();
    }

    [Fact]
    public void IsDirectChildOf_SameRoot_ShouldMatchOnlyDirectChildren()
    {
        var dir = Id("r1", "src");

        FileTreeState.IsDirectChildOf(Id("r1", "src/a.txt"), dir).Should().BeTrue();
        FileTreeState.IsDirectChildOf(Id("r1", "src/deep/a.txt"), dir).Should().BeFalse();
        FileTreeState.IsDirectChildOf(Id("r1", "other/a.txt"), dir).Should().BeFalse();
    }

    [Fact]
    public void IsDirectChildOf_EmptyDir_ShouldMatchRootChildren()
    {
        FileTreeState.IsDirectChildOf(Id("r1", "a.txt"), Id("r1", "")).Should().BeTrue();
        FileTreeState.IsDirectChildOf(Id("r1", "src/a.txt"), Id("r1", "")).Should().BeFalse();
    }
}
