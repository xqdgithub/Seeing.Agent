using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 子会话窗口注册表（circuit 维度 IStreamConsumer，经 Router.GetOrCreateCircuitConsumer 获取/登记）。
/// 订阅主会话父流：初始枚举 SubAgent 子会话 + 动态识别 task 工具调用新增窗口。
/// 只维护窗口集合（SessionId/ParentId/Kind/Title），执行状态由各窗口自身 handler 提供（spec B2）。
/// 完成保留：子会话终态不影响窗口集合。
/// </summary>
public sealed class ConferenceRegistry : IStreamConsumer
{
    /// <summary>窗口树节点（本期为单 root + 一级子会话，DTO 为后续树扩展留路）</summary>
    public sealed record WindowNode(string SessionId, string? ParentId, SessionKind Kind, string Title);

    private readonly SessionEventStreamRouter _router;
    private readonly ISessionManager _sessionManager;
    private readonly TaskSessionResolver _taskResolver;
    private readonly ILogger<ConferenceRegistry>? _logger;
    private readonly List<WindowNode> _windows = new();
    private readonly object _lock = new();

    public string SessionId => ParentSessionId ?? string.Empty;
    public string? ParentSessionId { get; private set; }
    public IReadOnlyList<WindowNode> Windows { get { lock (_lock) return _windows.ToArray(); } }

    /// <summary>窗口集合变化（初始枚举/动态新增/Rebind）后触发</summary>
    public event Action? WindowsChanged;

    public ConferenceRegistry(
        SessionEventStreamRouter router,
        ISessionManager sessionManager,
        TaskSessionResolver taskResolver,
        ILogger<ConferenceRegistry>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _taskResolver = taskResolver ?? throw new ArgumentNullException(nameof(taskResolver));
        _logger = logger;
    }

    /// <summary>绑定主会话：摘除旧父订阅、清空集合、重新枚举并挂新父（同 circuit 换主会话路由时调用）</summary>
    public void Rebind(string mainId)
    {
        if (!string.IsNullOrEmpty(ParentSessionId)
            && !string.Equals(ParentSessionId, mainId, StringComparison.Ordinal))
            _router.DetachConsumer(ParentSessionId, this);

        lock (_lock)
        {
            _windows.Clear();
        }
        // UI 立即清空旧路由窗口（换父后即使枚举无新增也须触发，避免残留旧子窗口）；
        // 枚举完成后经 AddChildren 增量补（added 逻辑不变）
        WindowsChanged?.Invoke();

        ParentSessionId = mainId;
        _ = EnumerateAsync(mainId);
        _router.AttachConsumer(mainId, this);
    }

    /// <summary>初始/刷新枚举：内存缓存 → 磁盘冷缓存兜底。捕获 parentId 做竞态防护（连续 Rebind 时旧枚举晚到不污染新集合）</summary>
    private async Task EnumerateAsync(string parentId)
    {
        try
        {
            var children = await _sessionManager.ListChildrenAsync(parentId, SessionKind.SubAgent);
            _logger?.LogInformation("[ConferenceRegistry] 内存枚举父 {ParentId} 子会话 {Count} 个", parentId, children.Count);
            AddChildren(parentId, children);

            var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(parentId);
            _logger?.LogInformation("[ConferenceRegistry] 磁盘枚举父 {ParentId} 子会话 {Count} 个", parentId, diskChildren.Count);
            AddChildren(parentId, diskChildren);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ConferenceRegistry] 枚举失败（后续动态事件仍可补窗）");
        }
    }

    private void AddChildren(string parentId, IReadOnlyList<SessionData>? children)
    {
        if (children == null)
            return;

        var added = false;
        lock (_lock)
        {
            // 竞态防护：连续 Rebind 后旧枚举晚到，ParentSessionId 已变 → 丢弃
            if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal))
            {
                _logger?.LogInformation("[ConferenceRegistry] 竞态丢弃：当前父 {Current} != 枚举父 {Target}", ParentSessionId, parentId);
                return;
            }

            foreach (var c in children)
            {
                if (_windows.Any(w => string.Equals(w.SessionId, c.Id, StringComparison.Ordinal)))
                    continue;
                _windows.Add(new WindowNode(c.Id, c.ParentSessionId, c.Kind, c.Title ?? string.Empty));
                added = true;
            }
        }
        _logger?.LogInformation("[ConferenceRegistry] AddChildren 新增 {Added} 个，当前窗口 {Total} 个", added, Windows.Count);
        if (added)
            WindowsChanged?.Invoke();
    }

    /// <summary>
    /// 移除指定会话窗口（主会话被清空连带删除子会话时调用，避免大屏残留已删除子窗口）。
    /// 仅移除与入参匹配的窗口，未命中不触发 WindowsChanged。
    /// </summary>
    public void RemoveWindows(IEnumerable<string> sessionIds)
    {
        if (sessionIds == null)
            return;

        var removed = false;
        lock (_lock)
        {
            foreach (var id in sessionIds)
            {
                var node = _windows.FirstOrDefault(w => string.Equals(w.SessionId, id, StringComparison.Ordinal));
                if (node != null)
                {
                    _windows.Remove(node);
                    removed = true;
                }
            }
        }
        if (removed)
            WindowsChanged?.Invoke();
    }

    public void OnEvent(IMessageEvent evt)
    {
        if (evt == null || string.IsNullOrEmpty(evt.SessionId))
            return;

        // 仅处理父流中的 task 工具调用
        if (evt is not ToolCallEvent tool || !IsTaskToolName(tool.ToolName))
            return;

        _ = AttachFromToolCallAsync(tool);
    }

    public void OnStreamEnd()
    {
        // 无清理需求（窗口集合由 Rebind/Dispose 管理）
    }

    private async Task AttachFromToolCallAsync(ToolCallEvent tool)
    {
        try
        {
            var parentId = ParentSessionId;
            if (string.IsNullOrEmpty(parentId))
                return;

            var taskId = await _taskResolver.ResolveTaskIdAsync(parentId,
                new SessionToolCall { Id = tool.ToolCallId });

            if (string.IsNullOrEmpty(taskId))
                return;

            // 竞态防护：await 期间 Rebind 换父后，旧任务晚到不得向新集合添加
            if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal))
                return;

            lock (_lock)
            {
                if (_windows.Any(w => string.Equals(w.SessionId, taskId, StringComparison.Ordinal)))
                    return;
                var child = _sessionManager.Get(taskId);
                _windows.Add(new WindowNode(taskId, parentId, SessionKind.SubAgent, child?.Title ?? string.Empty));
            }
            WindowsChanged?.Invoke();  // 已在 lock 外触发
            return;
        }
        catch
        {
            // 挂载失败不阻断事件流（消费者异常隔离）
        }
    }

    private static bool IsTaskToolName(string? name) =>
        string.Equals(name, "task", StringComparison.OrdinalIgnoreCase);
}
