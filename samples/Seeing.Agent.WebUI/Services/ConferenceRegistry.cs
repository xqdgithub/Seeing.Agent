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
        TaskSessionResolver taskResolver)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _taskResolver = taskResolver ?? throw new ArgumentNullException(nameof(taskResolver));
    }

    /// <summary>绑定主会话：摘除旧父订阅、清空集合、重新枚举并挂新父（同 circuit 换主会话路由时调用）</summary>
    public void Rebind(string mainId)
    {
        if (!string.IsNullOrEmpty(ParentSessionId)
            && !string.Equals(ParentSessionId, mainId, StringComparison.Ordinal))
            _router.DetachConsumer(ParentSessionId, this);

        lock (_lock)
            _windows.Clear();

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
            AddChildren(parentId, children);

            var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(parentId);
            AddChildren(parentId, diskChildren);
        }
        catch
        {
            // 枚举失败不阻断订阅（后续动态事件仍可补窗）
        }
    }

    private void AddChildren(string parentId, IReadOnlyList<SessionData>? children)
    {
        if (children == null)
            return;

        lock (_lock)
        {
            // 竞态防护：连续 Rebind 后旧枚举晚到，ParentSessionId 已变 → 丢弃
            if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal))
                return;

            var added = false;
            foreach (var c in children)
            {
                if (_windows.Any(w => string.Equals(w.SessionId, c.Id, StringComparison.Ordinal)))
                    continue;
                _windows.Add(new WindowNode(c.Id, c.ParentSessionId, c.Kind, c.Title ?? string.Empty));
                added = true;
            }
            if (added)
                WindowsChanged?.Invoke();
        }
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
            WindowsChanged?.Invoke();
        }
        catch
        {
            // 挂载失败不阻断事件流（消费者异常隔离）
        }
    }

    private static bool IsTaskToolName(string? name) =>
        string.Equals(name, "task", StringComparison.OrdinalIgnoreCase);
}
