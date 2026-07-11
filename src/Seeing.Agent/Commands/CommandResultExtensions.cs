using Seeing.Agent.Llm;

namespace Seeing.Agent.Commands;

/// <summary>
/// CommandResult 扩展方法
/// </summary>
public static class CommandResultExtensions
{
    private const string ModifiedHistoryKey = "modified_history";
    private const string NavigationTargetKey = "navigation_target";
    private const string ExpandedContentKey = "expanded_content";
    private const string OriginalContentKey = "original_content";

    /// <summary>
    /// 设置修改后的 History
    /// </summary>
    public static CommandResult WithModifiedHistory(this CommandResult result, List<ChatMessage> history)
    {
        var metadata = result.Metadata != null
            ? new Dictionary<string, object>(result.Metadata)
            : new Dictionary<string, object>();
        metadata[ModifiedHistoryKey] = history;
        return CommandResult.WithData(result.Message, metadata);
    }

    /// <summary>
    /// 设置导航目标
    /// </summary>
    public static CommandResult WithNavigation(this CommandResult result, string target)
    {
        var metadata = result.Metadata != null
            ? new Dictionary<string, object>(result.Metadata)
            : new Dictionary<string, object>();
        metadata[NavigationTargetKey] = target;
        return CommandResult.WithData(result.Message, metadata);
    }

    /// <summary>
    /// 设置展开后的 Skill 内容
    /// </summary>
    public static CommandResult WithExpandedContent(this CommandResult result, string expandedContent, string originalContent)
    {
        var metadata = result.Metadata != null
            ? new Dictionary<string, object>(result.Metadata)
            : new Dictionary<string, object>();
        metadata[ExpandedContentKey] = expandedContent;
        metadata[OriginalContentKey] = originalContent;
        return CommandResult.WithData(result.Message, metadata);
    }

    /// <summary>
    /// 获取修改后的 History
    /// </summary>
    public static List<ChatMessage>? GetModifiedHistory(this CommandResult result)
        => result.Metadata?.TryGetValue(ModifiedHistoryKey, out var history) == true
            ? history as List<ChatMessage>
            : null;

    /// <summary>
    /// 获取导航目标
    /// </summary>
    public static string? GetNavigationTarget(this CommandResult result)
        => result.Metadata?.TryGetValue(NavigationTargetKey, out var target) == true
            ? target as string
            : null;

    /// <summary>
    /// 获取展开后的 Skill 内容
    /// </summary>
    public static string? GetExpandedContent(this CommandResult result)
        => result.Metadata?.TryGetValue(ExpandedContentKey, out var content) == true
            ? content as string
            : null;

    /// <summary>
    /// 获取原始命令内容
    /// </summary>
    public static string? GetOriginalContent(this CommandResult result)
        => result.Metadata?.TryGetValue(OriginalContentKey, out var content) == true
            ? content as string
            : null;
}