using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileTabsStateTests
{
    private static FileReadResult File(string rel, string content, bool editable = true) =>
        new(new FileResourceId("root", rel), null, content, FileContentKind.Text, "text/plain",
            "utf-8", editable, content.Length, 1, false, false, FileReadMode.Full);

    [Fact]
    public void Open_Then_Close_ShouldUpdateActive()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.Open(File("b.txt", "B"));
        s.ActiveKey.Should().Be("root:b.txt");
        s.Close(new FileResourceId("root", "b.txt"));
        s.ActiveKey.Should().Be("root:a.txt");
    }

    [Fact]
    public void ApplyDiskUpdate_DirtyTab_ShouldNotOverwrite()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.MarkDirty(new FileResourceId("root", "a.txt"), "A-edited");
        var applied = s.ApplyDiskUpdate(File("a.txt", "A-disk"));
        applied.Should().BeFalse();
        s.OpenFiles[0].Content.Should().Be("A-edited");
    }

    [Fact]
    public void ApplyRename_Directory_ShouldRewritePrefix()
    {
        var s = new FileTabsState();
        s.Open(File("dir/a.txt", "A"));
        s.ApplyRename(new FileResourceId("root", "dir"), new FileResourceId("root", "renamed"));
        s.OpenFiles[0].Id.RelativePath.Should().Be("renamed/a.txt");
    }

    [Fact]
    public void Open_Then_Activate_ShouldSetActiveKey()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.Open(File("b.txt", "B"));
        s.Activate(new FileResourceId("root", "a.txt"));
        s.ActiveKey.Should().Be("root:a.txt");
    }

    [Fact]
    public void MarkDeleted_ShouldFlagTab()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.MarkDeleted(new FileResourceId("root", "a.txt"));
        s.OpenFiles[0].IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void SetPendingDiff_ShouldStoreDiff()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.SetPendingDiff(new FileResourceId("root", "a.txt"), new FileDiffData("old", "new"));
        s.OpenFiles[0].PendingDiff.Should().Be(new FileDiffData("old", "new"));
    }

    [Fact]
    public void ClearPendingDiff_ShouldReset()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.SetPendingDiff(new FileResourceId("root", "a.txt"), new FileDiffData("old", "new"));

        s.ClearPendingDiff(new FileResourceId("root", "a.txt"));

        s.OpenFiles[0].PendingDiff.Should().BeNull();
    }

    [Fact]
    public void Close_LastTab_ShouldClearActiveKey()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.Close(new FileResourceId("root", "a.txt"));
        s.ActiveKey.Should().BeNull();
        s.OpenFiles.Should().BeEmpty();
    }

    [Fact]
    public void Open_ReadOnlyFile_ShouldMarkReadOnly()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A", editable: false));
        s.OpenFiles[0].IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Changed_ShouldFireOnMutations()
    {
        var s = new FileTabsState();
        var count = 0;
        s.Changed += () => count++;
        s.Open(File("a.txt", "A"));
        s.MarkDirty(new FileResourceId("root", "a.txt"), "B");
        s.Activate(new FileResourceId("root", "a.txt"));
        count.Should().Be(3);
    }
}
