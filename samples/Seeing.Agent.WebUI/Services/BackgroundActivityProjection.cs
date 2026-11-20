using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 后台活动 → 时间线的投影规则（纯函数，无状态）。
/// <para>
/// 优先把活动附着到发起它的工具调用卡片（按 <c>OriginToolCallId</c> 反查，其次按工具卡片上
/// 已记录的 <c>BackgroundActivityId</c> 正查）；找不到工具卡片时才在时间线末尾新建独立条目。
/// </para>
/// <para>
/// 版本契约：<see cref="BackgroundActivityViewModel.Seq"/> 单调递增，任何路径都不得用更旧的快照
/// 覆盖更新状态。
/// </para>
/// </summary>
public static class BackgroundActivityProjection
{
    /// <summary>
    /// 应用一条活动快照。
    /// </summary>
    /// <param name="timeline">目标时间线。</param>
    /// <param name="activity">活动视图（调用方已按事件类型决定是否允许新增条目）。</param>
    /// <param name="allowAppend">
    /// 找不到工具卡片时是否允许在时间线末尾新建条目。
    /// <c>activity.progress</c> 只更新既有状态，不新增条目（否则首次进度就会凭空多出一行）。
    /// </param>
    /// <returns>是否发生了可见变更。</returns>
    public static bool Apply(
        MessageTimelineStore timeline,
        BackgroundActivityViewModel activity,
        bool allowAppend = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(activity);

        foreach (var item in timeline.Items)
        {
            if (item.Turn is { } turn)
            {
                foreach (var message in turn.Messages)
                {
                    if (!TryAttach(message, activity))
                        continue;

                    // 消息片段按 MessageViewModel.Revision 缓存；Touch 让 LoopGroupItem 重建该分段。
                    item.Touch();
                    return true;
                }

                continue;
            }

            if (item.Message is { } plain
                && TryAttach(plain, activity))
            {
                item.Touch();
                return true;
            }
        }

        if (!allowAppend)
            return false;

        timeline.UpsertBackgroundActivity(activity);
        return true;
    }

    private static bool TryAttach(MessageViewModel message, BackgroundActivityViewModel activity)
    {
        if (message.ToolCalls.Count == 0)
            return false;

        foreach (var toolCall in message.ToolCalls)
        {
            var matchesOrigin = !string.IsNullOrEmpty(activity.OriginToolCallId)
                && string.Equals(toolCall.Id, activity.OriginToolCallId, StringComparison.Ordinal);

            var matchesActivityId = !string.IsNullOrEmpty(toolCall.BackgroundActivityId)
                && string.Equals(toolCall.BackgroundActivityId, activity.Id, StringComparison.Ordinal);

            if (!matchesOrigin && !matchesActivityId)
                continue;

            if (toolCall.BackgroundActivity is { } current && current.Seq > activity.Seq)
                return true;

            toolCall.BackgroundActivity = activity;
            toolCall.BackgroundActivityId ??= activity.Id;
            message.Touch();
            return true;
        }

        return false;
    }
}
