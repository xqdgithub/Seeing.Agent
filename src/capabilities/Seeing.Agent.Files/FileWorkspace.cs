using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Files.Watching;
using Seeing.Agent.Paths;
using Seeing.Agent.Tools.Support;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seeing.Agent.Files;

/// <summary>会话级文件工作区 facade。</summary>
internal sealed class FileWorkspace : IFileWorkspace
{
    private const long MaxReadBytes = 20 * 1024 * 1024;
    private const long MaxEditableSize = 5 * 1024 * 1024;
    private const long MaxHashableSize = 10 * 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly IWorkspacePathGate _gate;
    private readonly FileWatcherPool _watchers;
    private readonly IPathWriteLock _locks;
    private FileWorkspaceRoot _root;

    /// <summary>构造会话工作区。</summary>
    public FileWorkspace(
        string sessionId,
        string rootPath,
        IFileSystem fs,
        IWorkspacePathGate gate,
        FileWatcherPool watchers,
        IPathWriteLock locks,
        FileWorkspaceRootKind kind = FileWorkspaceRootKind.Project,
        string? label = null)
    {
        SessionId = sessionId;
        var fullRoot = Path.GetFullPath(rootPath);
        var rootId = ComputeRootId(fullRoot);
        _root = new FileWorkspaceRoot(
            rootId,
            fullRoot,
            string.IsNullOrWhiteSpace(label) ? Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar)) : label!,
            kind,
            Directory.Exists(fullRoot));
        _fs = fs;
        _gate = gate;
        _watchers = watchers;
        _locks = locks;
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public FileWorkspaceRoot Root => _root;

    /// <inheritdoc />
    public string RootId => _root.RootId;

    /// <inheritdoc />
    public string RootPath => _root.RootPath;

    /// <summary>
    /// 用最新投影刷新根元信息（标签/类别/存在性）；根身份（RootId/RootPath）不变。
    /// <para>缓存命中时也必须调用：目录被删除/恢复后「不可用」标记才会更新。</para>
    /// </summary>
    internal void SyncRoot(FileWorkspaceRootKind kind, string? label, bool exists)
    {
        var name = string.IsNullOrWhiteSpace(label)
            ? Path.GetFileName(_root.RootPath.TrimEnd(Path.DirectorySeparatorChar))
            : label!;

        _root = _root with { Label = name, Kind = kind, Exists = exists };
    }

    private static string ComputeRootId(string root)
    {
        var normalized = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
    }

    /// <summary>相对路径以本根为基准（绝不用进程 CWD）。</summary>
    private string ToFull(string path)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, RootPath);

    private void EnsureAllowed(string path)
    {
        var reason = _gate.EnsureAllowed(SessionId, path);
        if (reason is not null)
            throw new UnauthorizedAccessException(reason);
    }

    /// <inheritdoc />
    public FileResourceId GetResourceId(string path)
    {
        // 相对路径按本根解析（不能用进程 CWD），越界直接拒绝——否则会产出 "../.." 的跨根标识。
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, RootPath);
        if (!PathBoundary.IsWithin(full, RootPath))
            throw new ArgumentException($"路径不在根内: {full}", nameof(path));

        var rel = Path.GetRelativePath(RootPath, full).Replace('\\', '/');
        return new FileResourceId(RootId, rel);
    }

    /// <inheritdoc />
    public string GetAbsolutePath(FileResourceId id)
    {
        if (!string.Equals(id.RootId, RootId, StringComparison.Ordinal))
            throw new ArgumentException($"资源属于其它根: {id.RootId}", nameof(id));

        var full = Path.GetFullPath(
            Path.Combine(RootPath, id.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!PathBoundary.IsWithin(full, RootPath))
            throw new ArgumentException($"资源路径越界: {id.RelativePath}", nameof(id));

        return full;
    }

    /// <inheritdoc />
    public Task<FileEntry> StatAsync(string path, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full))
            throw new FileNotFoundException("文件不存在", full);
        var size = isDir ? (long?)null : new FileInfo(full).Length;
        var lw = isDir ? (DateTime?)null : _fs.GetLastWriteTimeUtc(full);
        return Task.FromResult(new FileEntry(GetResourceId(full), Path.GetFileName(full), isDir, size, lw));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        if (!Directory.Exists(full))
            throw new FileNotFoundException("目录不存在", full);
        var list = new List<FileEntry>();
        foreach (var d in _fs.EnumerateDirectories(full, "*", false))
            list.Add(new FileEntry(GetResourceId(d), Path.GetFileName(d), true, null, null));
        foreach (var f in _fs.EnumerateFiles(full, "*", false))
        {
            // 枚举与取元数据之间存在竞态（文件被删除/替换），或路径落入 Windows 设备名
            // （如 "nul" → \\.\nul）时 FileInfo.Length 会抛错；列举应为尽力而为，跳过不可读条目。
            try
            {
                list.Add(new FileEntry(GetResourceId(f), Path.GetFileName(f), false, new FileInfo(f).Length, _fs.GetLastWriteTimeUtc(f)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.FromResult<IReadOnlyList<FileEntry>>(list);
    }

    /// <inheritdoc />
    public async Task<FileReadResult> ReadAsync(string path, FileReadMode mode = FileReadMode.Display, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("文件不存在", full);

        var mime = FileContentSupport.GetMimeType(full);
        var kind = FileContentClassifier.Classify(full, mime);

        var sampled = info.Length > MaxReadBytes;
        byte[] bytes;
        if (sampled)
        {
            // 超大文件：只读前 MaxReadBytes 字节，避免整文件入内存（revision 置 null）
            bytes = await ReadPrefixAsync(full, MaxReadBytes, ct).ConfigureAwait(false);
        }
        else
        {
            bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        }
        var (encoding, editable) = FileContentClassifier.DetectEditable(bytes);
        FileRevision? revision = (!sampled && info.Length <= MaxHashableSize)
            ? RevisionCalculator.FromBytes(bytes, info.LastWriteTimeUtc)
            : null;

        if (kind is FileContentKind.Binary or FileContentKind.Image)
        {
            return new FileReadResult(GetResourceId(full), revision, null, kind, mime, encoding,
                IsEditable: false, info.Length, 0, IsTruncated: false, IsSampled: sampled, mode);
        }

        var text = encoding == "utf-16le" ? Encoding.Unicode.GetString(bytes)
                 : encoding == "utf-16be" ? Encoding.BigEndianUnicode.GetString(bytes)
                 : new UTF8Encoding(false).GetString(bytes);

        var total = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Length;

        if (mode == FileReadMode.Full && (info.Length > MaxEditableSize || sampled))
            throw new FileTooLargeException(full, info.Length, MaxEditableSize);

        var content = text;
        var truncated = sampled;
        if (mode == FileReadMode.Display && text.Length > FileContentSupport.MaxBytes)
        {
            content = text[..FileContentSupport.MaxBytes];
            truncated = true;
        }

        return new FileReadResult(GetResourceId(full), revision, content, kind, mime, encoding,
            editable && !truncated && info.Length <= MaxEditableSize, info.Length, total, truncated, sampled, mode);
    }

    private async Task<byte[]> ReadPrefixAsync(string full, long maxBytes, CancellationToken ct)
    {
        using var stream = _fs.OpenRead(full);
        long available;
        try { available = stream.Length; }
        catch (NotSupportedException) { available = maxBytes; }
        var length = (int)Math.Min(maxBytes, available);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return offset == length ? buffer : buffer[..offset];
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        if (!File.Exists(full)) throw new FileNotFoundException("文件不存在", full);
        var info = new FileInfo(full);
        // 渲染用二进制有上限保护，避免大文件撑爆 circuit
        if (info.Length > MaxReadBytes)
            return await ReadPrefixAsync(full, MaxReadBytes, ct).ConfigureAwait(false);
        return await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FileWriteResult> WriteAsync(string path, string content, FileRevision? expectedRevision = null, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);

        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);
        return await WriteCoreAsync(full, content, expectedRevision, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            _fs.CreateDirectory(directory);

        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);
        await _fs.WriteAllBytesAsync(full, content, ct).ConfigureAwait(false);
    }

    /// <summary>持锁前提下的写入核心（调用方已持有该路径写锁）。</summary>
    private async Task<FileWriteResult> WriteCoreAsync(string full, string content, FileRevision? expectedRevision, CancellationToken ct)
    {
        var exists = File.Exists(full);
        var oldBytes = exists ? await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
        var oldRev = exists ? RevisionCalculator.FromBytes(oldBytes!, _fs.GetLastWriteTimeUtc(full)) : (FileRevision?)null;

        if (expectedRevision is { } exp && !exp.Matches(oldRev))
            return new FileWriteResult(GetResourceId(full), null, Conflict: true, oldRev, null);

        if (exists)
        {
            var (_, editable) = FileContentClassifier.DetectEditable(oldBytes!);
            if (!editable)
                throw new FileNotEditableException(full, "非 UTF-8 编码");
        }

        await _fs.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        var newBytes = Encoding.UTF8.GetBytes(content);
        var newRev = RevisionCalculator.FromBytes(newBytes, _fs.GetLastWriteTimeUtc(full));
        var oldText = oldBytes is null ? "" : new UTF8Encoding(false).GetString(oldBytes);
        var diff = LineDiff.ComputeUnified(oldText, content);
        return new FileWriteResult(GetResourceId(full), newRev, Conflict: false, null, diff);
    }

    /// <inheritdoc />
    public async Task<FileWriteResult> EditAsync(string path, string oldString, string newString, bool replaceAll = false, FileRevision? expectedRevision = null, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);

        // 读-改-写全程持锁（消除与工具/UI 并发写的 lost-update）
        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);
        if (!File.Exists(full)) throw new FileNotFoundException("文件不存在", full);
        var bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var text = new UTF8Encoding(false).GetString(bytes);

        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("无法找到 oldString（需完全匹配）");
        string updated;
        if (replaceAll) updated = text.Replace(oldString, newString);
        else if (text.IndexOf(oldString, index + oldString.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("存在多个匹配，请提供更多上下文或 replaceAll=true");
        else updated = text[..index] + newString + text[(index + oldString.Length)..];

        return await WriteCoreAsync(full, updated, expectedRevision, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);
        _fs.Delete(full);
    }

    /// <inheritdoc />
    public async Task<string> GetDiffAsync(string path, string baselineContent, CancellationToken ct = default)
    {
        var full = ToFull(path);
        EnsureAllowed(full);
        var bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var current = new UTF8Encoding(false).GetString(bytes);
        return LineDiff.ComputeUnified(baselineContent, current);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FileChangeEvent> SubscribeChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var sub = _watchers.Subscribe(RootPath);
        await foreach (var raw in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var full = ToFull(raw.Path);
            // 边界判定只用 PathBoundary（避免 D:\a 命中 D:\ab）
            if (!PathBoundary.IsWithin(full, RootPath))
                continue;
            FileRevision? rev = null;
            if (raw.Kind is FileChangeKind.Modified or FileChangeKind.Created && File.Exists(full))
            {
                try { rev = RevisionCalculator.FromBytes(_fs.ReadAllBytes(full), _fs.GetLastWriteTimeUtc(full)); }
                catch { rev = null; }
            }
            yield return new FileChangeEvent(GetResourceId(full), raw.Kind, rev, raw.OldPath, DateTime.UtcNow);
        }
    }
}
