namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 文件系统 seam — 工具与宿主通过此接口访问路径与 I/O，禁止直接调用 <c>File.*</c>。
/// </summary>
public interface IFileSystem
{
    /// <summary>读取文件全部文本。</summary>
    string ReadAllText(string path);

    /// <summary>异步读取文件全部文本。</summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>读取文件全部字节。</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>异步读取文件全部字节。</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>打开文件只读流（调用方负责 Dispose）。</summary>
    Stream OpenRead(string path);

    /// <summary>按行异步枚举文件内容。</summary>
    IAsyncEnumerable<string> ReadLinesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>写入文件全部文本。</summary>
    void WriteAllText(string path, string contents);

    /// <summary>异步写入文件全部文本。</summary>
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>创建目录（含中间路径）。</summary>
    void CreateDirectory(string path);

    /// <summary>路径是否存在（文件或目录）。</summary>
    bool Exists(string path);

    /// <summary>删除文件或目录。</summary>
    void Delete(string path);

    /// <summary>文件最后写入时间（UTC）。</summary>
    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>枚举目录下匹配模式的文件。</summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern, bool recursive);

    /// <summary>枚举目录下匹配模式的子目录。</summary>
    IEnumerable<string> EnumerateDirectories(string directory, string pattern, bool recursive);

    /// <summary>解析为绝对路径。</summary>
    string GetFullPath(string path);
}
