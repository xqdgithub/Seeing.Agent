using Microsoft.Extensions.Logging;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Hooks.Directory;

/// <summary>
/// 用户钩子目录监听。只发出「请重扫」：去抖 400ms，同步单飞，进行中的信号在本轮结束后再跑一轮。
/// 根不存在时只监听最近已存在祖先的一层，且只回应通向 <c>hooks</c> 的下一段。
/// </summary>
public sealed class HookDirectoryWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(400);

    private readonly string _hooksRoot;
    private readonly Func<CancellationToken, Task> _onRescan;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITimer _timer;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private string? _watching;
    private int _running;
    private int _again;
    private int _disposed;
    private int _missingNoted;

    public HookDirectoryWatcher(
        string hooksRoot,
        Func<CancellationToken, Task> onRescan,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksRoot);
        ArgumentNullException.ThrowIfNull(onRescan);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _hooksRoot = Path.GetFullPath(hooksRoot);
        _onRescan = onRescan;
        _logger = logger;
        _timer = time.CreateTimer(OnDebounceElapsed, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        EnsureAttached();
    }

    /// <summary>跳过去抖，直接进入单飞入口。文件事件不得调用本方法。</summary>
    internal void ArmForTests() => EnterSingleFlight();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已取消。
        }

        FileSystemWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        try
        {
            watcher?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "释放钩子目录监听失败：{Root}", _hooksRoot);
        }
    }

    private void OnDebounceElapsed(object? state) => EnterSingleFlight();

    private void EnterSingleFlight()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Volatile.Write(ref _again, 1);
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            do
            {
                Volatile.Write(ref _again, 0);
                if (Volatile.Read(ref _disposed) != 0)
                    break;

                try
                {
                    EnsureAttached();
                    await _onRescan(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "钩子目录重扫失败，等待下一次变更：{Root}", _hooksRoot);
                }
            }
            while (Volatile.Read(ref _again) == 1 && Volatile.Read(ref _disposed) == 0);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _again) == 1
                && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = RunAsync();
            }
        }
    }

    private void EnsureAttached()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var target = System.IO.Directory.Exists(_hooksRoot)
            ? _hooksRoot
            : NearestExistingDirectory(_hooksRoot);
        if (target is null)
        {
            if (Interlocked.Exchange(ref _missingNoted, 1) == 0)
                _logger.LogWarning("钩子目录及其祖先均不存在，暂时不监听：{Root}", _hooksRoot);
            return;
        }

        var watching = Volatile.Read(ref _watching);
        if (watching is not null && PathBoundary.AreSame(watching, target) && Volatile.Read(ref _watcher) is not null)
            return;

        Attach(target);
    }

    private void Attach(string target)
    {
        var recursive = PathBoundary.AreSame(target, _hooksRoot);
        FileSystemWatcher? created = null;
        FileSystemWatcher? previous = null;
        string? previousWatching = null;
        var tracked = false;

        try
        {
            created = new FileSystemWatcher(target)
            {
                IncludeSubdirectories = recursive,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024
            };

            created.Created += OnFileEvent;
            created.Changed += OnFileEvent;
            created.Deleted += OnFileEvent;
            created.Renamed += OnRenamed;
            created.Error += OnError;

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    created.Dispose();
                    return;
                }

                previous = _watcher;
                previousWatching = _watching;
                _watcher = created;
                Volatile.Write(ref _watching, target);
                created.EnableRaisingEvents = true;
                tracked = true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_watcher, created))
                {
                    _watcher = previous;
                    Volatile.Write(ref _watching, previousWatching);
                }
            }

            created?.Dispose();
            _logger.LogWarning(ex, "钩子目录监听挂载失败：根={Root} 目标={Target}", _hooksRoot, target);
            return;
        }

        if (!tracked)
        {
            created.Dispose();
            return;
        }

        previous?.Dispose();
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsRelevant(e.FullPath))
            return;

        ScheduleDebounce();
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!IsRelevant(e.FullPath) && !IsRelevant(e.OldFullPath))
            return;

        ScheduleDebounce();
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _logger.LogWarning(e.GetException(), "钩子目录监听报告错误，将重扫：{Root}", _hooksRoot);
        ScheduleDebounce();
    }

    private bool IsRelevant(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;

        var watching = Volatile.Read(ref _watching);
        if (watching is null)
            return false;

        if (PathBoundary.AreSame(watching, _hooksRoot))
            return true;

        var next = NextSegmentPath(watching, _hooksRoot);
        return next is not null && PathBoundary.AreSame(fullPath, next);
    }

    private void ScheduleDebounce()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _timer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 释放后晚到的文件事件。
        }
    }

    private static string? NextSegmentPath(string watching, string root)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(watching, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(relative)
            || relative == "."
            || Path.IsPathRooted(relative)
            || IsParentReference(relative))
        {
            return null;
        }

        var segment = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)[0];

        return segment.Length == 0 ? null : Path.Combine(watching, segment);
    }

    private static bool IsParentReference(string relative) =>
        relative == ".."
        || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string? NearestExistingDirectory(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (System.IO.Directory.Exists(current))
                return current;

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}
