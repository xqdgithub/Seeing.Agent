using Seeing.Agent.Abstractions.Files;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Seeing.Agent.Files.Watching;

/// <summary>单根 FileSystemWatcher 包装 + 防抖 + 稳定性双采样。</summary>
internal sealed class RootWatcher : IDisposable
{
    /// <summary>归一化后的原始事件。</summary>
    public readonly record struct RawChange(string Path, FileChangeKind Kind, string? OldPath);

    private static readonly string[] Ignored = [".git", "bin", "obj", "node_modules", ".vs"];

    private const int DebounceMs = 200;
    private const int SampleGapMs = 70;
    private const int MaxStabilityWaitMs = 2000;

    /// <summary>防抖字典键：避免拼接字符串（路径可能含分隔符）。</summary>
    private readonly record struct ChangeKey(string Path, FileChangeKind Kind, string? OldPath);

    /// <summary>待处理事件：首次入队时间用于界定稳定性重试上限。</summary>
    private readonly record struct PendingChange(
        string Path, FileChangeKind Kind, string? OldPath, DateTime FirstSeenUtc, DateTime LastSeenUtc);

    /// <summary>文件状态快照（长度 + 最后写入时间）。</summary>
    private readonly record struct FileSample(bool Exists, long Length, DateTime LastWriteUtc);

    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<ChangeKey, PendingChange> _pending = new();
    private readonly Timer _debounce;
    private readonly List<Channel<RawChange>> _sinks = [];
    private readonly object _sinkLock = new();
    private int _flushing;

    /// <summary>对指定根目录启动递归监听。</summary>
    public RootWatcher(string root)
    {
        _fsw = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => Queue(e.FullPath, FileChangeKind.Created, null);
        _fsw.Changed += (_, e) => Queue(e.FullPath, FileChangeKind.Modified, null);
        _fsw.Deleted += (_, e) => Queue(e.FullPath, FileChangeKind.Deleted, null);
        _fsw.Renamed += (_, e) => Queue(e.FullPath, FileChangeKind.Renamed, e.OldFullPath);
        _debounce = new Timer(_ => Flush(), null, DebounceMs, DebounceMs);
    }

    /// <summary>登记一个订阅者，返回其读取端。</summary>
    public ChannelReader<RawChange> Register()
    {
        var ch = Channel.CreateUnbounded<RawChange>();
        lock (_sinkLock) { _sinks.Add(ch); }
        return ch.Reader;
    }

    private static bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => Ignored.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private void Queue(string path, FileChangeKind kind, string? oldPath)
    {
        if (IsIgnored(path)) return;
        var now = DateTime.UtcNow;
        var key = new ChangeKey(path, kind, oldPath);
        // 保留 FirstSeenUtc，避免持续写入时无限延长稳定性重试上限。
        _pending.AddOrUpdate(
            key,
            _ => new PendingChange(path, kind, oldPath, now, now),
            (_, existing) => existing with { LastSeenUtc = now });
    }

    private static FileSample Sample(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists
                ? new FileSample(true, fi.Length, fi.LastWriteTimeUtc)
                : new FileSample(false, -1, default);
        }
        catch
        {
            return new FileSample(false, -1, default);
        }
    }

    private void Flush()
    {
        // 防止 Timer 回调重入；同一时刻只允许一个采样批次。
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            var direct = new List<PendingChange>();
            var sampleable = new List<PendingChange>();

            foreach (var (key, pending) in _pending.ToArray())
            {
                if ((now - pending.LastSeenUtc).TotalMilliseconds < DebounceMs) continue;
                if (key.Kind is FileChangeKind.Deleted or FileChangeKind.Renamed) direct.Add(pending);
                else sampleable.Add(pending);
            }

            // Deleted/Renamed 无文件可采样，直接发出。
            foreach (var pending in direct)
                if (_pending.TryRemove(new ChangeKey(pending.Path, pending.Kind, pending.OldPath), out _))
                    Emit(pending);

            if (sampleable.Count == 0) return;

            // 双采样：先取一遍快照，间隔 ~70ms 再取一遍；一致才认为写入完成。
            var first = new FileSample[sampleable.Count];
            for (var i = 0; i < sampleable.Count; i++) first[i] = Sample(sampleable[i].Path);
            Thread.Sleep(SampleGapMs);

            var emittedAt = DateTime.UtcNow;
            for (var i = 0; i < sampleable.Count; i++)
            {
                var pending = sampleable[i];
                var second = Sample(pending.Path);
                var stable = first[i] == second;
                var createdReady = pending.Kind != FileChangeKind.Created || second.Length > 0;
                var exceeded = (emittedAt - pending.FirstSeenUtc).TotalMilliseconds >= MaxStabilityWaitMs;

                // 稳定且满足 Created 非空要求才按原 kind 发出；超时兜底降级为 Modified，
                // 保证 UI 至少刷新一次（FileWorkspace 会以不稳定读取跳过 revision）。
                if (stable && createdReady || exceeded)
                {
                    if (!_pending.TryRemove(new ChangeKey(pending.Path, pending.Kind, pending.OldPath), out _))
                        continue;
                    var kind = stable && createdReady ? pending.Kind : FileChangeKind.Modified;
                    Emit(pending with { Kind = kind });
                }
                // 否则保留在 _pending，下一个 tick 继续重试（LastSeenUtc 未变，仍满足防抖）。
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private void Emit(PendingChange pending)
    {
        var raw = new RawChange(pending.Path, pending.Kind, pending.OldPath);
        lock (_sinkLock)
            foreach (var ch in _sinks) ch.Writer.TryWrite(raw);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _debounce.Dispose();
        _fsw.Dispose();
        lock (_sinkLock)
        {
            foreach (var ch in _sinks) ch.Writer.TryComplete();
            _sinks.Clear();
        }
    }

    /// <summary>某根目录的订阅句柄；Dispose 时释放池引用。</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly Action _release;
        private bool _done;

        /// <summary>原始变更事件读取端。</summary>
        public ChannelReader<RawChange> Reader { get; }

        internal Subscription(ChannelReader<RawChange> reader, Action release)
        {
            Reader = reader;
            _release = release;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _release();
        }
    }
}
