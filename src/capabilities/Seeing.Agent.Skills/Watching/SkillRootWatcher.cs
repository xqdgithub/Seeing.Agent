using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Skills.Watching;

/// <summary>
/// 技能根目录文件监听器：每个根一个 <see cref="FileSystemWatcher"/>，突发事件经去抖后合并为一次回调。
/// <para>根不存在时监听其「最近存在的祖先目录」，检测到根被创建后自动重挂到根本身
/// （安装器写到一半 / 首次创建工作区目录都必须可用）。</para>
/// <para>生命周期只由 <c>SkillsModule</c> 拥有：<see cref="Dispose"/> 停止全部监听并阻断后续回调。
/// 本类不持有目录、不扫盘、不解析技能，只负责回答「根下可能变了」。</para>
/// </summary>
public sealed class SkillRootWatcher : IDisposable
{
    /// <summary>默认去抖窗口：足够覆盖「建目录 + 写文件」的成串事件，又不至于让 UI 明显滞后。</summary>
    private static readonly TimeSpan s_defaultDebounce = TimeSpan.FromMilliseconds(400);

    private readonly Func<CancellationToken, Task> _onChange;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounceWindow;

    /// <summary>回调令牌：<see cref="Dispose"/> 取消进行中的回调。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>监听表闸：根集合与 watcher 实例的替换都在它之内。</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, RootWatch> _watches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全局去抖定时器：任一事件重置截止时间，成串事件只留最后一次触发。</summary>
    private readonly Timer _debounce;

    /// <summary>回调单飞标记（0 = 空闲，1 = 正在回调）；回调期间到达的事件留给下一轮。</summary>
    private int _callbackRunning;

    private int _disposed;

    /// <summary>
    /// 创建监听器并立即挂载全部根。
    /// </summary>
    /// <param name="roots">技能根目录（优先级从高到低；不存在的根监听最近存在祖先）。</param>
    /// <param name="onChange">变更回调（通常为 <c>catalog.RefreshAsync</c>）；异常由本类记录，不向外抛。</param>
    /// <param name="logger">日志。</param>
    /// <param name="debounce">去抖窗口；为空或非正数时用 400ms。</param>
    public SkillRootWatcher(
        IReadOnlyList<string> roots,
        Func<CancellationToken, Task> onChange,
        ILogger logger,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(onChange);
        ArgumentNullException.ThrowIfNull(logger);

        _onChange = onChange;
        _logger = logger;
        _debounceWindow = debounce is { } window && window > TimeSpan.Zero ? window : s_defaultDebounce;
        _debounce = new Timer(OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);

        foreach (var root in NormalizeRoots(roots))
            AddRoot(root);

        _logger.LogInformation("技能根监听已启动：根数={RootCount} 去抖={DebounceMs}ms", _watches.Count, _debounceWindow.TotalMilliseconds);
    }

    /// <summary>
    /// 校正监听范围（切换工作区 / 重算根之后调用）：丢掉旧根、只新增未监听的根。
    /// </summary>
    public void SyncRoots(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var desired = NormalizeRoots(roots);
        var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);

        List<RootWatch> removed = [];
        List<string> added = [];
        lock (_gate)
        {
            foreach (var root in _watches.Keys.Where(key => !desiredSet.Contains(key)).ToArray())
            {
                removed.Add(_watches[root]);
                _watches.Remove(root);
            }

            foreach (var root in desired)
            {
                if (!_watches.ContainsKey(root))
                    added.Add(root);
            }
        }

        foreach (var watch in removed)
        {
            watch.Watcher?.Dispose();
            _logger.LogDebug("技能根监听已移除：{Root}", watch.Root);
        }

        foreach (var root in added)
            AddRoot(root);

        _logger.LogInformation("技能根监听范围已同步：根数={RootCount} 移除={RemovedCount} 新增={AddedCount}",
            _watches.Count, removed.Count, added.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // 先停定时器：之后即使还有 FS 事件到达也不会再排回调
        _debounce.Dispose();

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：无需再取消
        }

        RootWatch[] watches;
        lock (_gate)
        {
            watches = _watches.Values.ToArray();
            _watches.Clear();
        }

        foreach (var watch in watches)
        {
            try
            {
                watch.Watcher?.Dispose();
            }
            catch (Exception ex)
            {
                // 释放失败不得阻断其它根的释放；记录以便排查句柄泄漏
                _logger.LogDebug(ex, "释放技能根监听失败：{Root}", watch.Root);
            }
        }

        _logger.LogInformation("技能根监听已停止：根数={RootCount}", watches.Length);
    }

    // ===== 挂载 =====

    /// <summary>登记根并挂载监听。</summary>
    private void AddRoot(string root)
    {
        var watch = new RootWatch(root);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _watches[root] = watch;
        }

        Attach(watch);
    }

    /// <summary>
    /// 把某个根的监听重挂到「最近存在的祖先或根本身」。
    /// <para>挂载失败只降级为不监听（记录警告），不得让宿主启动失败。</para>
    /// </summary>
    private void Attach(RootWatch watch)
    {
        var target = NearestExistingDirectory(watch.Root);
        if (target is null)
        {
            _logger.LogWarning("技能根及其祖先均不存在，暂时不监听：{Root}", watch.Root);
            return;
        }

        FileSystemWatcher? created = null;
        try
        {
            created = new FileSystemWatcher(target)
            {
                IncludeSubdirectories = true,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 32 * 1024
            };

            created.Created += (_, _) => OnFileSystemEvent(watch);
            created.Changed += (_, _) => OnFileSystemEvent(watch);
            created.Deleted += (_, _) => OnFileSystemEvent(watch);
            created.Renamed += (_, _) => OnFileSystemEvent(watch);
            created.Error += (_, e) => OnWatcherError(watch, e.GetException());
            created.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            created?.Dispose();
            _logger.LogWarning(ex, "技能根监听挂载失败（该根本轮不监听）：根={Root} 目标={Target}", watch.Root, target);
            return;
        }

        FileSystemWatcher? previous = null;
        var tracked = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0
                && _watches.TryGetValue(watch.Root, out var current)
                && ReferenceEquals(current, watch))
            {
                previous = watch.Watcher;
                watch.Watcher = created;
                watch.Watching = target;
                tracked = true;
            }
        }

        if (!tracked)
        {
            // 期间被 SyncRoots 移除或已释放：不得留下孤儿 watcher
            created.Dispose();
            return;
        }

        previous?.Dispose();
        _logger.LogDebug("技能根监听已挂载：根={Root} 监听={Target}", watch.Root, target);
    }

    // ===== 事件 =====

    /// <summary>文件系统事件：先校正挂载层级（根可能刚出现 / 刚消失），再排一次去抖回调。</summary>
    private void OnFileSystemEvent(RootWatch watch)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        EnsureAttachment(watch);
        ScheduleDebouncedCallback();
    }

    /// <summary>缓冲区溢出：事件已丢，必须重扫（回调本身即重扫）。</summary>
    private void OnWatcherError(RootWatch watch, Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _logger.LogWarning(exception, "技能根监听报告错误，将重新校准并重扫：{Root}", watch.Root);
        EnsureAttachment(watch);
        ScheduleDebouncedCallback();
    }

    /// <summary>当前监听的目录与「根 / 最近存在祖先」不一致时重挂。</summary>
    private void EnsureAttachment(RootWatch watch)
    {
        var target = NearestExistingDirectory(watch.Root);
        if (target is null)
            return;

        if (watch.Watcher is not null && string.Equals(target, watch.Watching, StringComparison.OrdinalIgnoreCase))
            return;

        Attach(watch);
    }

    private void ScheduleDebouncedCallback()
    {
        try
        {
            _debounce.Change(_debounceWindow, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 释放后晚到的 FS 事件：静默忽略（已不需要回调）
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // 已在回调中：本轮事件并入下一轮，避免同一窗口重复重扫
        if (Interlocked.CompareExchange(ref _callbackRunning, 1, 0) != 0)
            return;

        _ = InvokeCallbackAsync();
    }

    /// <summary>触发一次变更回调；异常不外泄（记录后等待下一次变更重试）。</summary>
    private async Task InvokeCallbackAsync()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            await _onChange(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 释放导致的中止：正常路径
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能根变更回调失败（等待下一次变更重试）");
        }
        finally
        {
            Interlocked.Exchange(ref _callbackRunning, 0);
        }
    }

    // ===== 工具 =====

    /// <summary>规范化根：去空白、转绝对路径、按序去重（非法路径记录后跳过）。</summary>
    private List<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var result = new List<string>(roots.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning(ex, "技能根路径非法，已跳过监听：{Root}", root);
                continue;
            }

            if (seen.Add(full))
                result.Add(full);
        }

        return result;
    }

    /// <summary>最近存在目录：从给定路径向上找到第一个真实存在的目录；全部不存在返回 null。</summary>
    private static string? NearestExistingDirectory(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
                return current;

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>单个根的监听状态。</summary>
    private sealed class RootWatch(string root)
    {
        /// <summary>逻辑根（可能尚不存在）。</summary>
        public string Root { get; } = root;

        /// <summary>当前实际监听的目录（根本身或最近存在祖先）。</summary>
        public string? Watching { get; set; }

        /// <summary>当前 watcher 实例（重挂时整体替换）。</summary>
        public FileSystemWatcher? Watcher { get; set; }
    }
}
