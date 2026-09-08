using Seeing.Agent.Abstractions.Execution;

namespace Seeing.IO.Local;

/// <summary>
/// 基于 <see cref="File"/> / <see cref="Directory"/> 的本地文件系统实现。
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    /// <inheritdoc />
    public Stream OpenRead(string path) => File.OpenRead(path);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ReadLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            yield return line;
        }
    }

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <inheritdoc />
    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, pattern, option);
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory, string pattern, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateDirectories(directory, pattern, option);
    }

    /// <inheritdoc />
    public string GetFullPath(string path) => Path.GetFullPath(path);
}
