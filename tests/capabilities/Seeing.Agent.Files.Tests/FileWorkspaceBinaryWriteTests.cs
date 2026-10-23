using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Files.Watching;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceBinaryWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "files-bin-" + Guid.NewGuid().ToString("N"));
    private readonly FileWorkspace _ws;

    public FileWorkspaceBinaryWriteTests()
    {
        Directory.CreateDirectory(_root);
        _ws = new FileWorkspace(
            "s1",
            _root,
            new LocalFileSystem(),
            new AllowAllGate(),
            new FileWatcherPool(),
            new ProcessPathWriteLock());
    }

    [Fact]
    public async Task WriteBytesAsync_ShouldCreateParentAndRoundtrip()
    {
        var file = Path.Combine(_root, ".seeing", "uploads", "s1", "a.bin");

        await _ws.WriteBytesAsync(file, new byte[] { 9, 8, 7 });

        Directory.Exists(Path.GetDirectoryName(file)).Should().BeTrue();
        (await _ws.ReadBytesAsync(file)).Should().Equal(9, 8, 7);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
