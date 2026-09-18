using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 共享的 task 工具调用 → 子会话 ID 解析器（Scoped，注入 ISessionGroupManager）。
/// 从 TaskCardAggregator.ResolveTaskIdAsync 提取，TaskCardAggregator 与 ConferenceRegistry 共用：
/// origin_tool_call_id 精确匹配（组管理器内含缓存枚举 → 存储冷兜底）。
/// </summary>
public sealed class TaskSessionResolver
{
    private readonly ISessionGroupManager _groupManager;

    public TaskSessionResolver(ISessionGroupManager groupManager)
    {
        _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
    }

    /// <summary>
    /// 解析 task 工具调用对应的子会话 ID。
    /// 优先返回 toolCall.TaskId（续跑分支场景已写入）；否则按 origin_tool_call_id 匹配，
    /// 内存缓存枚举未命中时由组管理器走存储冷兜底。
    /// </summary>
    public async Task<string?> ResolveTaskIdAsync(string parentSessionId, SessionToolCall toolCall)
    {
        if (toolCall == null)
            return null;

        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        if (string.IsNullOrEmpty(parentSessionId))
            return null;

        var children = await _groupManager.ListChildrenAsync(parentSessionId);
        var match = children?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }
}
