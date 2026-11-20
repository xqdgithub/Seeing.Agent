using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台活动「终态是否值得唤醒归属会话」的<b>唯一判定点</b>。
/// <para>
/// 入队（<see cref="BackgroundActivityManager"/>）与泵取兜底（<see cref="BackgroundActivityOutbox"/>）
/// 必须用同一份规则：入队侧拦截新增，泵取侧兜底升级前已经落盘的历史 pending。
/// </para>
/// </summary>
internal static class BackgroundActivityNotificationPolicy
{
    /// <summary>
    /// 该终态是否应当产生「完成」通知。
    /// <para>
    /// <c>Succeeded</c> / <c>Failed</c> 通知；<c>Cancelled</c> 只有 <c>Timeout</c> 通知
    /// （任务超时是<b>失败</b>，不是用户意图，不能被静默）；其它取消原因
    /// （<c>User</c> / <c>SessionStopped</c> / <c>HostShutdown</c> / <c>WorkspaceSwitched</c> /
    /// <c>ModuleDeactivated</c> / <c>Superseded</c> / <c>CascadeFromParent</c>）与
    /// <c>Interrupted</c>（宿主关闭、孤儿收敛）一律静默 —— 唤醒已停止的会话会重新产出内容。
    /// </para>
    /// </summary>
    public static bool IsCompletionNotifiable(BackgroundActivitySnapshot snapshot)
        => snapshot.Status switch
        {
            BackgroundActivityStatus.Succeeded or BackgroundActivityStatus.Failed => true,
            BackgroundActivityStatus.Cancelled => snapshot.CancelReason == BackgroundActivityCancelReason.Timeout,
            _ => false
        };
}
