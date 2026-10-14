using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileViewerCoordinatorTests
{
    private static FileReadResult File(FileResourceId id, string content, FileReadMode mode = FileReadMode.Display) =>
        new(id, null, content, FileContentKind.Text, "text/plain", "utf-8", true, content.Length, 1, false, false, mode);

    [Fact]
    public async Task ApplyAsync_DirtyTab_ShouldSkip()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(File(id, "A", FileReadMode.Full));
        tabs.MarkDirty(id, "edited");

        var ws = new Mock<IFileWorkspace>();
        ws.Setup(w => w.GetAbsolutePath(It.IsAny<FileResourceId>())).Returns("x");
        ws.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<FileReadMode>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(File(id, "disk"));

        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var outcome = await coord.ApplyAsync(new FileChangeEvent(id, FileChangeKind.Modified, null, null, DateTime.UtcNow));
        outcome.Should().Be(ApplyOutcome.ConflictSkipped);
        tabs.OpenFiles[0].Content.Should().Be("edited");
        tabs.OpenFiles[0].HasExternalConflict.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_CleanTab_ShouldRefresh()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(File(id, "A", FileReadMode.Full));

        var ws = new Mock<IFileWorkspace>();
        ws.Setup(w => w.GetAbsolutePath(It.IsAny<FileResourceId>())).Returns("x");
        ws.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<FileReadMode>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(File(id, "disk"));

        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var outcome = await coord.ApplyAsync(new FileChangeEvent(id, FileChangeKind.Modified, null, null, DateTime.UtcNow));
        outcome.Should().Be(ApplyOutcome.Refreshed);
        tabs.OpenFiles[0].Content.Should().Be("disk");
    }

    [Fact]
    public async Task ApplyAsync_Deleted_ShouldMarkDeleted()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(File(id, "A", FileReadMode.Full));

        var ws = new Mock<IFileWorkspace>();
        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var outcome = await coord.ApplyAsync(new FileChangeEvent(id, FileChangeKind.Deleted, null, null, DateTime.UtcNow));
        outcome.Should().Be(ApplyOutcome.Deleted);
        tabs.OpenFiles[0].IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_Renamed_ShouldRewriteTab()
    {
        var oldId = new FileResourceId("root", "dir/a.txt");
        var newDirId = new FileResourceId("root", "renamed");
        var tabs = new FileTabsState();
        tabs.Open(File(oldId, "A", FileReadMode.Full));

        var ws = new Mock<IFileWorkspace>();
        ws.Setup(w => w.GetResourceId("old")).Returns(new FileResourceId("root", "dir"));

        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var outcome = await coord.ApplyAsync(new FileChangeEvent(newDirId, FileChangeKind.Renamed, null, "old", DateTime.UtcNow));
        outcome.Should().Be(ApplyOutcome.Renamed);
        tabs.OpenFiles[0].Id.RelativePath.Should().Be("renamed/a.txt");
    }

    [Fact]
    public void Dispose_ShouldBeIdempotentSafe()
    {
        var tabs = new FileTabsState();
        var ws = new Mock<IFileWorkspace>();
        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var act = () => coord.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveConflictReload_ShouldDiscardLocalChanges()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(File(id, "A", FileReadMode.Full));
        tabs.MarkDirty(id, "local");
        tabs.MarkExternalConflict(id);

        tabs.ResolveConflictReload(id, File(id, "disk", FileReadMode.Full));

        tabs.OpenFiles[0].Content.Should().Be("disk");
        tabs.OpenFiles[0].HasUnsavedChanges.Should().BeFalse();
        tabs.OpenFiles[0].HasExternalConflict.Should().BeFalse();
    }

    [Fact]
    public void ResolveConflictKeepLocal_ShouldClearConflictFlagOnly()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(File(id, "A", FileReadMode.Full));
        tabs.MarkDirty(id, "local");
        tabs.MarkExternalConflict(id);

        tabs.ResolveConflictKeepLocal(id, null);

        tabs.OpenFiles[0].Content.Should().Be("local");
        tabs.OpenFiles[0].HasUnsavedChanges.Should().BeTrue();
        tabs.OpenFiles[0].HasExternalConflict.Should().BeFalse();
    }
}
