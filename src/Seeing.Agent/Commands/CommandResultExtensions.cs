using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;

using Seeing.Agent.Abstractions.Commands;
namespace Seeing.Agent.Commands;

/// <summary>
/// CommandResult 扩展方法
/// </summary>
public static class CommandResultExtensions
{
    private const string ModifiedHistoryKey = "modified_history";
    private const string NavigationTargetKey = "navigation_target";

    /// <summary>
    /// 设置修改后的 History
    /// </summary>
    public static CommandResult WithModifiedHistory(this CommandResult result, List<ChatMessage> history)
    {
        var metadata = result.Metadata != null
            ? new Dictionary<string, object>(result.Metadata)
            : new Dictionary<string, object>();
        metadata[ModifiedHistoryKey] = history;
        return CloneWithMetadata(result, metadata);
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
        return CloneWithMetadata(result, metadata);
    }

    /// <summary>
    /// 保留命令消息（默认移除）：命令实现可在 ExecuteAsync 中替换/保留消息内容（如 Skill 展开）。
    /// </summary>
    public static CommandResult WithCommandMessageRetained(this CommandResult result)
        => new()
        {
            Success = result.Success,
            Message = result.Message,
            ErrorMessage = result.ErrorMessage,
            ShouldExit = result.ShouldExit,
            NeedsRefresh = result.NeedsRefresh,
            ShouldContinue = result.ShouldContinue,
            RemoveCommandMessage = false,
            Metadata = result.Metadata
        };

    /// <summary>
    /// 复制结果并合并新 Metadata，保留全部字段（Success/ShouldContinue/ShouldExit/RemoveCommandMessage 等）。
    /// </summary>
    private static CommandResult CloneWithMetadata(CommandResult result, Dictionary<string, object> metadata)
        => new()
        {
            Success = result.Success,
            Message = result.Message,
            ErrorMessage = result.ErrorMessage,
            ShouldExit = result.ShouldExit,
            NeedsRefresh = result.NeedsRefresh,
            ShouldContinue = result.ShouldContinue,
            RemoveCommandMessage = result.RemoveCommandMessage,
            Metadata = metadata
        };

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
}