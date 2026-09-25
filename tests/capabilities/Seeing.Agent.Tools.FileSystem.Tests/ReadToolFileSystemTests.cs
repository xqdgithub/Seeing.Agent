using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.FileSystem;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class ReadToolFileSystemTests
{
    [Fact]
    public async Task ExecuteAsync_TextFile_UsesInjectedFileSystemWithoutOsFile()
    {
        const string virtualPath = "/workspace/docs/readme.txt";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(virtualPath, "line one\nline two\nline three");

        var tool = new ReadTool(NullLogger<ReadTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = virtualPath }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("line one");
        result.Output.Should().Contain("line two");
        result.Output.Should().Contain("line three");
        result.Metadata.Should().ContainKey("type").WhoseValue.Should().Be("file");
        result.Metadata.Should().ContainKey("totalLines").WhoseValue.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_Directory_UsesInjectedFileSystemWithoutOsFile()
    {
        const string dirPath = "/workspace/docs";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddDirectory(dirPath);
        fileSystem.AddFile("/workspace/docs/a.txt", "a");
        fileSystem.AddFile("/workspace/docs/b.txt", "b");
        fileSystem.AddDirectory("/workspace/docs/sub");
        fileSystem.AddFile("/workspace/docs/sub/c.txt", "c");

        var tool = new ReadTool(NullLogger<ReadTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = dirPath }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("类型: directory");
        result.Output.Should().Contain("a.txt");
        result.Output.Should().Contain("b.txt");
        result.Output.Should().Contain("sub/");
        result.Metadata.Should().ContainKey("type").WhoseValue.Should().Be("directory");
        result.Metadata.Should().ContainKey("count").WhoseValue.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPath_ReturnsFailureWithoutTouchingOs()
    {
        var fileSystem = new InMemoryFileSystem();
        var tool = new ReadTool(NullLogger<ReadTool>.Instance, fileSystem, AllowAllPathGate.Instance);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = "/workspace/missing.txt" }),
            new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("文件不存在");
    }

    [Fact]
    public async Task ExecuteAsync_OffsetAndLimit_AppliesPagination()
    {
        const string virtualPath = "/workspace/log.txt";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(virtualPath, "one\ntwo\nthree\nfour");

        var tool = new ReadTool(NullLogger<ReadTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = virtualPath, offset = 2, limit = 2 }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("2: two");
        result.Output.Should().Contain("3: three");
        result.Output.Should().NotContain("1: one");
        result.Output.Should().NotContain("4: four");
    }

    [Fact]
    public async Task ExecuteAsync_PngBinary_StreamsFromInjectedFileSystem()
    {
        const string virtualPath = "/workspace/logo.png";
        var pngBytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D
        };
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddBinaryFile(virtualPath, pngBytes);

        var tool = new ReadTool(NullLogger<ReadTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = virtualPath }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("type").WhoseValue.Should().Be("binary");
        result.Metadata.Should().ContainKey("mime").WhoseValue.Should().Be("image/png");
        result.Metadata.Should().ContainKey("size").WhoseValue.Should().Be(pngBytes.Length);
        result.Attachments.Should().ContainSingle(a => a.Path == virtualPath);
    }

    [Fact]
    public async Task WriteTool_ExecuteAsync_UsesInjectedFileSystem()
    {
        const string virtualPath = "/workspace/out.txt";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddDirectory("/workspace");

        var tool = new WriteTool(NullLogger<WriteTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { filePath = virtualPath, content = "hello seam" }),
            new ToolContext());

        result.Success.Should().BeTrue();
        fileSystem.ReadAllText(virtualPath).Should().Be("hello seam");
    }

    [Fact]
    public async Task DeleteTool_ExecuteAsync_UsesInjectedFileSystem()
    {
        const string virtualPath = "/workspace/gone.txt";
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(virtualPath, "x");

        var tool = new DeleteTool(NullLogger<DeleteTool>.Instance, fileSystem, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = virtualPath }),
            new ToolContext());

        result.Success.Should().BeTrue();
        fileSystem.Exists(virtualPath).Should().BeFalse();
    }

    internal sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _timestamps = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, string contents) =>
            AddBinaryFile(path, Encoding.UTF8.GetBytes(contents));

        public void AddBinaryFile(string path, byte[] contents)
        {
            var normalized = Normalize(path);
            _files[normalized] = contents;
            _timestamps[normalized] = DateTime.UtcNow;
            EnsureParentDirectories(normalized);
        }

        public void AddDirectory(string path) => _directories.Add(Normalize(path));

        public string ReadAllText(string path) =>
            Encoding.UTF8.GetString(ReadAllBytes(path));

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadAllText(path));

        public byte[] ReadAllBytes(string path) =>
            _files.TryGetValue(Normalize(path), out var contents)
                ? contents
                : throw new FileNotFoundException(path);

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadAllBytes(path));

        public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

        public async IAsyncEnumerable<string> ReadLinesAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var reader = new StringReader(ReadAllText(path));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    yield break;
                }

                yield return line;
            }
        }

        public void WriteAllText(string path, string contents)
        {
            var normalized = Normalize(path);
            _files[normalized] = Encoding.UTF8.GetBytes(contents);
            _timestamps[normalized] = DateTime.UtcNow;
            EnsureParentDirectories(normalized);
        }

        public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            WriteAllText(path, contents);
            return Task.CompletedTask;
        }

        public void CreateDirectory(string path) => _directories.Add(Normalize(path));

        public bool Exists(string path)
        {
            var normalized = Normalize(path);
            return _files.ContainsKey(normalized) || _directories.Contains(normalized);
        }

        public void Delete(string path)
        {
            var normalized = Normalize(path);
            _files.Remove(normalized);
            _directories.Remove(normalized);
            _timestamps.Remove(normalized);
        }

        public DateTime GetLastWriteTimeUtc(string path) =>
            _timestamps.TryGetValue(Normalize(path), out var ts) ? ts : DateTime.UtcNow;

        public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
        {
            var normalizedDir = Normalize(directory);
            if (!_directories.Contains(normalizedDir))
            {
                throw new DirectoryNotFoundException(directory);
            }

            var prefix = normalizedDir.TrimEnd('/') + "/";
            var depth = recursive ? int.MaxValue : 1;

            return _files.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(path => CountSegments(prefix, path) <= depth)
                .Where(path => MatchesPattern(Path.GetFileName(path), pattern));
        }

        public IEnumerable<string> EnumerateDirectories(string directory, string pattern, bool recursive)
        {
            var normalizedDir = Normalize(directory);
            if (!_directories.Contains(normalizedDir))
            {
                throw new DirectoryNotFoundException(directory);
            }

            var prefix = normalizedDir.TrimEnd('/') + "/";
            var depth = recursive ? int.MaxValue : 1;

            return _directories
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.Equals(path, normalizedDir, StringComparison.OrdinalIgnoreCase))
                .Where(path => CountSegments(prefix, path) <= depth)
                .Where(path => MatchesPattern(Path.GetFileName(path.TrimEnd('/')), pattern));
        }

        public string GetFullPath(string path) => Normalize(path);

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        private void EnsureParentDirectories(string filePath)
        {
            var parent = Path.GetDirectoryName(filePath)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(parent))
            {
                _directories.Add(parent);
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/');
            }
        }

        private static int CountSegments(string prefix, string path)
        {
            var remainder = path[prefix.Length..];
            return remainder.Count(c => c == '/') + 1;
        }

        private static bool MatchesPattern(string name, string pattern) =>
            pattern is "*" or "*.*" || string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
