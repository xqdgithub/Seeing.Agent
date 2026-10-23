using Seeing.Agent.Abstractions.Execution;
using Seeing.IO.Local;

namespace Seeing.Agent.Files.Tests;

/// <summary>测试用：委托给本地文件系统，但在枚举时注入一个无法读取元数据的“幽灵”路径。</summary>
internal sealed class GhostEntryFileSystem : IFileSystem
{
    private readonly LocalFileSystem _inner = new();
    private readonly string _ghostPath;

    /// <summary>构造并指定要注入的幽灵路径。</summary>
    public GhostEntryFileSystem(string ghostPath) => _ghostPath = ghostPath;

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
    {
        foreach (var f in _inner.EnumerateFiles(directory, pattern, recursive))
            yield return f;
        yield return _ghostPath;
    }

    /// <inheritdoc />
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadAllTextAsync(path, cancellationToken);
    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadAllBytesAsync(path, cancellationToken);
    /// <inheritdoc />
    public Stream OpenRead(string path) => _inner.OpenRead(path);
    /// <inheritdoc />
    public IAsyncEnumerable<string> ReadLinesAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadLinesAsync(path, cancellationToken);
    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) => _inner.WriteAllTextAsync(path, contents, cancellationToken);
    /// <inheritdoc />
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    /// <inheritdoc />
    public bool Exists(string path) => _inner.Exists(path);
    /// <inheritdoc />
    public void Delete(string path) => _inner.Delete(path);
    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);
    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory, string pattern, bool recursive) => _inner.EnumerateDirectories(directory, pattern, recursive);
    /// <inheritdoc />
    public long GetFileLength(string path) => _inner.GetFileLength(path);
    /// <inheritdoc />
    public bool IsDirectory(string path) => _inner.IsDirectory(path);
    /// <inheritdoc />
    public string GetFullPath(string path) => _inner.GetFullPath(path);
}
