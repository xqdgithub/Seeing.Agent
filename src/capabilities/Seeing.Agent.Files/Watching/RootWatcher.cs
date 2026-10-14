using System.Collections.Concurrent;
using System.Threading.Channels;
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.Files.Watching;

/// <summary>单根 FileSystemWatcher 包装 + 防抖。</summary>
internal sealed class RootWatcher : IDisposable
{
    /// <summary>归一化后的原始事件。</summary>
    public readonly record struct RawChange(string Path, FileChangeKind Kind, string? OldPath);

    private static readonly string[] Ignored = [".git", "bin", "obj", "node_modules", ".vs"];

    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, DateTime> _pending = new();
    private readonly Timer _debounce;
    private readonly List<Channel<RawChange>> _sinks = [];
    private readonly object _sinkLock = new();

    /// <summary>对指定根目录启动递归监听。</summary>
    public RootWatcher(string root)
    {
        _fsw = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => Queue(e.FullPath, FileChangeKind.Created, null);
        _fsw.Changed += (_, e) => Queue(e.FullPath, FileChangeKind.Modified, null);
        _fsw.Deleted += (_, e) => Queue(e.FullPath, FileChangeKind.Deleted, null);
        _fsw.Renamed += (_, e) => Queue(e.FullPath, FileChangeKind.Renamed, e.OldFullPath);
        _debounce = new Timer(_ => Flush(), null, 200, 200);
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
        _pending[path + "|" + kind + "|" + (oldPath ?? "")] = DateTime.UtcNow;
    }

    private void Flush()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, ts) in _pending.ToArray())
        {
            if ((now - ts).TotalMilliseconds < 200) continue;
            if (!_pending.TryRemove(key, out _)) continue;
            var parts = key.Split('|');
            var raw = new RawChange(parts[0], Enum.Parse<FileChangeKind>(parts[1]), parts[2].Length == 0 ? null : parts[2]);
            lock (_sinkLock)
                foreach (var ch in _sinks) ch.Writer.TryWrite(raw);
        }
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
