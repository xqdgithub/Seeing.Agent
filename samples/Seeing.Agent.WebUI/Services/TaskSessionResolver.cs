using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 共享的 task 工具调用 → 子会话 ID 解析器（Scoped，注入 ISessionManager）。
/// 从 TaskCardAggregator.ResolveTaskIdAsync 提取，TaskCardAggregator 与 ConferenceRegistry 共用：
/// origin_tool_call_id 精确匹配（内存缓存枚举 → 磁盘冷缓存兜底）。
/// </summary>
public sealed class TaskSessionResolver
{
    private readonly ISessionManager _sessionManager;

    public TaskSessionResolver(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    /// <summary>
    /// 解析 task 工具调用对应的子会话 ID。
    /// 优先返回 toolCall.TaskId（续跑分支场景已写入）；否则按 origin_tool_call_id 匹配，
    /// 内存缓存枚举未命中时走磁盘冷缓存兜底。
    /// </summary>
    public async Task<string?> ResolveTaskIdAsync(string parentSessionId, SessionToolCall toolCall)
    {
        if (toolCall == null)
            return null;

        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        if (string.IsNullOrEmpty(parentSessionId))
            return null;

        var children = await _sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent);
        var match = children?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        if (match != null)
            return match.Id;

        var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(parentSessionId);
        match = diskChildren?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }
}
