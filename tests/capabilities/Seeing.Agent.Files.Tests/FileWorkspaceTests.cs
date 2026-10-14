using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Files;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "files-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileWorkspace _ws;

    public FileWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        var fs = new LocalFileSystem();
        _ws = new FileWorkspace("s1", _root, fs, new AllowAllGate(), new Watching.FileWatcherPool(), new ProcessPathWriteLock());
    }

    [Fact]
    public async Task Write_Then_Read_Roundtrip()
    {
        var file = Path.Combine(_root, "a.txt");
        var r = await _ws.WriteAsync(file, "hello");

        r.Conflict.Should().BeFalse();
        var read = await _ws.ReadAsync(file, FileReadMode.Full);
        read.Content.Should().Be("hello");
        read.IsEditable.Should().BeTrue();
    }

    [Fact]
    public async Task Write_StaleRevision_ShouldConflict()
    {
        var file = Path.Combine(_root, "a.txt");
        await _ws.WriteAsync(file, "v1");
        var stale = new FileRevision("deadbeef", 2, DateTime.UtcNow);

        var r = await _ws.WriteAsync(file, "v2", stale);

        r.Conflict.Should().BeTrue();
        (await _ws.ReadAsync(file, FileReadMode.Full)).Content.Should().Be("v1");
    }

    [Fact]
    public async Task Write_MatchingRevision_ShouldSucceed()
    {
        var file = Path.Combine(_root, "a.txt");
        var first = await _ws.WriteAsync(file, "v1");

        var second = await _ws.WriteAsync(file, "v2", first.Revision);

        second.Conflict.Should().BeFalse();
        (await _ws.ReadAsync(file, FileReadMode.Full)).Content.Should().Be("v2");
    }

    [Fact]
    public async Task Read_NonUtf8_ShouldNotBeEditable()
    {
        var file = Path.Combine(_root, "b.bin");
        await File.WriteAllBytesAsync(file, [0xFF, 0xFE, 0x41, 0x00]);

        var read = await _ws.ReadAsync(file);

        read.IsEditable.Should().BeFalse();
    }

    [Fact]
    public async Task Read_DisplayTruncates_LargeText()
    {
        var big = new string('x', Seeing.Agent.Tools.Support.FileContentSupport.MaxBytes + 100);
        var file = Path.Combine(_root, "big.txt");
        await File.WriteAllTextAsync(file, big);

        var read = await _ws.ReadAsync(file, FileReadMode.Display);

        read.IsTruncated.Should().BeTrue();
        read.IsEditable.Should().BeFalse();
    }

    [Fact]
    public async Task Edit_MissingOldString_ShouldThrow()
    {
        var file = Path.Combine(_root, "a.txt");
        await _ws.WriteAsync(file, "hello");

        var act = async () => await _ws.EditAsync(file, "nope", "x");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Edit_ShouldApply_AndAdvanceRevision()
    {
        var file = Path.Combine(_root, "edit.txt");
        var w = await _ws.WriteAsync(file, "hello world");

        var r = await _ws.EditAsync(file, "world", "there", expectedRevision: w.Revision);

        r.Conflict.Should().BeFalse();
        (await _ws.ReadAsync(file, FileReadMode.Full)).Content.Should().Be("hello there");
    }

    [Fact]
    public void RootId_ShouldBeStableAndLowercaseHex()
    {
        var again = new FileWorkspace("s2", _root, new LocalFileSystem(), new AllowAllGate(), new Watching.FileWatcherPool(), new ProcessPathWriteLock());

        again.RootId.Should().Be(_ws.RootId);
        again.RootId.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void GetResourceId_UsesForwardSlashes()
    {
        var sub = Path.Combine(_root, "a", "b.txt");

        var id = _ws.GetResourceId(sub);

        id.RelativePath.Should().Be("a/b.txt");
        _ws.GetAbsolutePath(id).Should().Be(Path.GetFullPath(sub));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
