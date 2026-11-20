using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// <see cref="IActivityOutputStore"/> 的唯一实现：把活动输出追加进
/// <c>{活动根}/{activityId}.output.log</c>。
/// <para>
/// <b>为什么用追加写而不是原子替换</b>：输出是持续到达的字节流，逐块「读—改—写」会把
/// 5 MiB 文件重写数千次，且块间窗口会让读者看到半截内容。这里取 <see cref="IPathWriteLock"/>
/// 串行同一活动的写入，用 <see cref="FileMode.Append"/> 落盘，读者以 <see cref="FileShare.ReadWrite"/>
/// 并发读（读到的是已刷新的前缀，合法的近似快照）。
/// </para>
/// <para>
/// <b>内存只保留尾部 64KB 与统计</b>：本类永不缓存全文；尾部只用于诊断（<see cref="TryGetStats"/>）。
/// </para>
/// <para>
/// <b>截断</b>：达到 <see cref="BackgroundActivityOptions.DefaultOutputLimitBytes"/> 后停止写入原始输出，
/// 并在文件末尾追加一次 <c>[truncated]</c>；<see cref="BackgroundActivityOutputPage.Truncated"/>
/// 由文件尾部标记还原，冷启动（内存状态为空）也能正确上报。
/// </para>
/// </summary>
internal sealed class BackgroundActivityOutputStore : IActivityOutputStore
{
    /// <summary>输出文件后缀。</summary>
    internal const string FileSuffix = ".output.log";

    /// <summary>活动根目录名（位于项目 <c>.seeing</c> 或用户 <c>~/.seeing</c> 下）。</summary>
    internal const string DirectoryName = "background-activities";

    /// <summary>单页读取硬上限（32KB；<c>ToolOutputLimiter</c> 阈值 50KB，留出余量）。</summary>
    internal const int MaxPageBytes = 32 * 1024;

    /// <summary>内存尾部容量。</summary>
    internal const int TailCapacity = 64 * 1024;

    private const int RestoreBufferSize = 64 * 1024;

    /// <summary>截断标记：追加在文件末尾，只写一次。</summary>
    private static readonly byte[] s_truncationMarker = Encoding.UTF8.GetBytes("\n[truncated]\n");

    /// <summary>标记自带的换行数（还原统计时扣除，避免把标记算成输出行）。</summary>
    private const int TruncationMarkerNewlines = 2;

    private readonly IPathWriteLock _writeLock;
    private readonly IWorkspaceProvider _workspace;
    private readonly IOptionsMonitor<BackgroundActivityOptions> _options;
    private readonly ILogger<BackgroundActivityOutputStore>? _logger;

    /// <summary>每活动内存状态（尾部 64KB + 总字节/行数 + 截断标记）。进程级、按活动 id 隔离。</summary>
    private readonly ConcurrentDictionary<string, ActivityOutputState> _states = new(StringComparer.Ordinal);

    public BackgroundActivityOutputStore(
        IPathWriteLock writeLock,
        IWorkspaceProvider workspace,
        IOptionsMonitor<BackgroundActivityOptions> options,
        ILogger<BackgroundActivityOutputStore>? logger = null)
    {
        _writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AppendAsync(string activityId, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        var path = ResolveOutputPath(activityId);
        if (chunk.IsEmpty)
            return;

        using var lease = await _writeLock.AcquireAsync(path, ct).ConfigureAwait(false);

        var state = GetOrCreateState(activityId);
        await EnsureInitializedAsync(state, path, ct).ConfigureAwait(false);

        // 已截断：后续输出直接丢弃（标记只写一次，绝不重复追加）。
        if (state.Truncated)
            return;

        var limit = ResolveOutputLimit();
        var remaining = limit - state.TotalBytes;
        var writable = remaining <= 0 ? 0 : (int)Math.Min(chunk.Length, remaining);

        // 本次写不满 → 本次就是越限点：写完可写部分后追加一次标记。
        var appendMarker = writable < chunk.Length;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = new FileStream(
                         path,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.ReadWrite,
                         bufferSize: 4096,
                         useAsync: true))
        {
            if (writable > 0)
                await stream.WriteAsync(chunk[..writable], ct).ConfigureAwait(false);

            if (appendMarker)
                await stream.WriteAsync(s_truncationMarker, ct).ConfigureAwait(false);

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        if (writable > 0)
            state.Append(chunk.Span[..writable]);

        if (appendMarker)
            state.MarkTruncated();
    }

    /// <inheritdoc />
    public async Task<BackgroundActivityOutputPage> ReadAsync(
        string activityId,
        long offset,
        int maxBytes,
        CancellationToken ct)
    {
        var path = ResolveOutputPath(activityId);
        var start = Math.Max(0, offset);
        var pageBytes = Math.Clamp(maxBytes, 1, MaxPageBytes);

        // 与写入共用同一把路径锁：保证读到的文件长度与内存截断标记是同一次写入后的快照。
        using var lease = await _writeLock.AcquireAsync(path, ct).ConfigureAwait(false);

        var state = GetOrCreateState(activityId);
        await EnsureInitializedAsync(state, path, ct).ConfigureAwait(false);

        if (!File.Exists(path))
            return new BackgroundActivityOutputPage(string.Empty, start, EndOfStream: true, state.Truncated);

        long length;
        byte[] buffer;
        int read;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         bufferSize: 4096,
                         useAsync: true))
        {
            length = stream.Length;
            if (start >= length)
                return new BackgroundActivityOutputPage(string.Empty, start, EndOfStream: true, state.Truncated);

            var bytesToRead = (int)Math.Min(pageBytes, length - start);
            buffer = new byte[bytesToRead];
            stream.Seek(start, SeekOrigin.Begin);
            read = await ReadExactlyAsync(stream, buffer, ct).ConfigureAwait(false);
        }

        var (content, consumed) = DecodeUtf8Prefix(buffer, read);
        var next = start + consumed;

        return new BackgroundActivityOutputPage(content, next, next >= length, state.Truncated);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string activityId)
    {
        var path = ResolveOutputPath(activityId);

        using var lease = await _writeLock.AcquireAsync(path, CancellationToken.None).ConfigureAwait(false);

        _states.TryRemove(activityId, out _);

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败不外抛：活动记录已不再引用该文件，残留只占磁盘。
            _logger?.LogWarning(ex, "删除后台活动输出失败 ActivityId={ActivityId} Path={Path}", activityId, path);
        }
    }

    /// <summary>取某活动的输出统计（诊断/测试用；接口契约不暴露）。尚未见过该活动时返回 <see langword="null"/>。</summary>
    internal ActivityOutputStats? TryGetStats(string activityId)
    {
        if (string.IsNullOrEmpty(activityId) || !_states.TryGetValue(activityId, out var state) || !state.Initialized)
            return null;

        return new ActivityOutputStats(state.TotalBytes, state.LineCount, state.Truncated, state.TailText());
    }

    // ─── 路径 ─────────────────────────────────────────────────────────────────

    /// <summary>活动根：工作区已决议 → <c>{项目根}/.seeing/background-activities</c>；否则 <c>~/.seeing/background-activities</c>。</summary>
    private string ResolveActivityRoot()
    {
        try
        {
            var projectRoot = _workspace.GetProjectRoot();
            if (!string.IsNullOrWhiteSpace(projectRoot))
                return Path.Combine(projectRoot, ".seeing", DirectoryName);
        }
        catch (InvalidOperationException)
        {
            // 工作区尚未决议：占位目录绝不能当工作区，回落到用户级目录（spec §11.1）。
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".seeing", DirectoryName);
    }

    /// <summary>解析并校验输出文件路径；非法活动 id 与越界路径一律抛异常。</summary>
    private string ResolveOutputPath(string activityId)
    {
        if (!IsValidActivityId(activityId))
        {
            throw new ArgumentException(
                $"非法后台活动 id（要求 ^act_[0-9a-f]{{32}}$）：'{activityId}'。",
                nameof(activityId));
        }

        var root = ResolveActivityRoot();
        var path = Path.Combine(root, activityId + FileSuffix);

        // 纵深防御：id 已由正则约束，这里再确认拼出的路径确实落在活动根内（符号链接等）。
        if (!PathBoundary.IsStrictlyWithin(path, root))
            throw new UnauthorizedAccessException($"后台活动输出路径越界：'{path}'（根：'{root}'）。");

        return path;
    }

    /// <summary>活动 id 形如 <c>act_</c> + 32 位小写十六进制（与 manager 的 <c>NewActivityId</c> 对齐）。</summary>
    private static bool IsValidActivityId(string? activityId)
    {
        const string prefix = "act_";
        const int hexLength = 32;

        if (activityId is null || activityId.Length != prefix.Length + hexLength)
            return false;

        if (!activityId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        for (var i = prefix.Length; i < activityId.Length; i++)
        {
            var c = activityId[i];
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
                return false;
        }

        return true;
    }

    private long ResolveOutputLimit()
    {
        var configured = _options.CurrentValue.DefaultOutputLimitBytes;

        // 非正值按 0 处理（立即截断），不静默回落到 5 MiB——配置写错时行为必须可预测。
        return configured > 0 ? configured : 0;
    }

    // ─── 内存状态 ─────────────────────────────────────────────────────────────

    private ActivityOutputState GetOrCreateState(string activityId) =>
        _states.GetOrAdd(activityId, static _ => new ActivityOutputState());

    private static async Task EnsureInitializedAsync(
        ActivityOutputState state,
        string path,
        CancellationToken ct)
    {
        if (state.Initialized)
            return;

        await state.InitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.Initialized)
                return;

            await RestoreAsync(state, path, ct).ConfigureAwait(false);
            state.MarkInitialized();
        }
        finally
        {
            state.InitGate.Release();
        }
    }

    /// <summary>冷启动还原：扫一遍文件得到总字节/行数，保留尾部 64KB，并按尾部标记判断是否已截断。</summary>
    private static async Task RestoreAsync(ActivityOutputState state, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return;

        var buffer = new byte[RestoreBufferSize];
        long totalBytes = 0;
        var lineCount = 0;
        var tail = new byte[TailCapacity];
        var tailLength = 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: RestoreBufferSize,
            useAsync: true);

        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            totalBytes += read;
            lineCount += CountNewlines(buffer.AsSpan(0, read));
            tailLength = AppendToTail(tail, tailLength, buffer.AsSpan(0, read));
        }

        var truncated = tail.AsSpan(0, tailLength).EndsWith(s_truncationMarker);
        if (truncated)
        {
            totalBytes = Math.Max(0, totalBytes - s_truncationMarker.Length);
            lineCount = Math.Max(0, lineCount - TruncationMarkerNewlines);
        }

        state.Restore(tail, tailLength, totalBytes, lineCount, truncated);
    }

    private static int CountNewlines(ReadOnlySpan<byte> data)
    {
        var count = 0;
        foreach (var b in data)
        {
            if (b == (byte)'\n')
                count++;
        }

        return count;
    }

    /// <summary>把 <paramref name="data"/> 追加进尾部环形缓冲，返回新的有效长度（只保留最后 <see cref="TailCapacity"/> 字节）。</summary>
    private static int AppendToTail(byte[] tail, int tailLength, ReadOnlySpan<byte> data)
    {
        if (data.Length >= tail.Length)
        {
            data[^tail.Length..].CopyTo(tail);
            return tail.Length;
        }

        var overflow = tailLength + data.Length - tail.Length;
        if (overflow > 0)
        {
            tail.AsSpan(overflow, tailLength - overflow).CopyTo(tail);
            tailLength -= overflow;
        }

        data.CopyTo(tail.AsSpan(tailLength));
        return tailLength + data.Length;
    }

    // ─── 读取辅助 ─────────────────────────────────────────────────────────────

    private static async Task<int> ReadExactlyAsync(FileStream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    /// <summary>
    /// 解码 UTF-8 前缀：末尾若是不完整的多字节序列，则留到下一页，避免把字符劈成替换符。
    /// 返回实际消费的字节数（调用方以此为 <c>NextOffset</c>）。
    /// </summary>
    private static (string Content, int Consumed) DecodeUtf8Prefix(byte[] buffer, int length)
    {
        if (length == 0)
            return (string.Empty, 0);

        var complete = TrimIncompleteTrailingSequence(buffer, length);

        // 整页只有一个残缺起始字节：用替换符消费掉它，保证游标一定前进（不产生死循环分页）。
        if (complete == 0)
            return (Encoding.UTF8.GetString(buffer, 0, 1), 1);

        return (Encoding.UTF8.GetString(buffer, 0, complete), complete);
    }

    private static int TrimIncompleteTrailingSequence(byte[] buffer, int length)
    {
        var maxBack = Math.Min(3, length - 1);
        for (var back = 0; back <= maxBack; back++)
        {
            var index = length - 1 - back;
            var value = buffer[index];

            // 续字节（10xxxxxx）继续向前找起始字节。
            if ((value & 0xC0) == 0x80)
                continue;

            var expected = value switch
            {
                < 0x80 => 1,
                >= 0xF0 => 4,
                >= 0xE0 => 3,
                >= 0xC0 => 2,
                _ => 1
            };

            var available = length - index;
            return available < expected ? index : length;
        }

        // 全是续字节（非法输入）：整段交给解码器做替换。
        return length;
    }

    // ─── 状态容器 ─────────────────────────────────────────────────────────────

    /// <summary>单活动输出状态：只保留尾部 64KB 与总量统计，永不缓存全文。</summary>
    private sealed class ActivityOutputState
    {
        /// <summary>固定容量（容量为 0 时追加逻辑无法保留任何字节，因此必须预分配）。</summary>
        private byte[] _tail = new byte[TailCapacity];
        private int _tailLength;

        public SemaphoreSlim InitGate { get; } = new(1, 1);

        public bool Initialized { get; private set; }

        public long TotalBytes { get; private set; }

        public int LineCount { get; private set; }

        public bool Truncated { get; private set; }

        public void MarkInitialized() => Initialized = true;

        /// <summary>追加一段已落盘的输出字节（只更新统计与尾部）。</summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            TotalBytes += data.Length;
            LineCount += CountNewlines(data);
            _tailLength = AppendToTail(_tail, _tailLength, data);
        }

        public void MarkTruncated() => Truncated = true;

        public void Restore(byte[] tail, int tailLength, long totalBytes, int lineCount, bool truncated)
        {
            _tail = tail;
            _tailLength = tailLength;
            TotalBytes = totalBytes;
            LineCount = lineCount;
            Truncated = truncated;
        }

        public string TailText() => Encoding.UTF8.GetString(_tail, 0, _tailLength);
    }
}

/// <summary>活动输出统计（诊断/测试用，不属于契约）。</summary>
internal readonly record struct ActivityOutputStats(
    long TotalBytes,
    int LineCount,
    bool Truncated,
    string Tail);
