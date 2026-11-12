namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 手动复盘入口：把一个会话放进复盘队列，由后台 Worker 立即排空。
/// <para>
/// <b>为什么只是入队而不是同步跑</b>：一次复盘要建子会话、投递、调模型，
/// 典型耗时几十秒到数分钟。放在 UI 请求里会让页面卡住直到超时，
/// 也会和后台 Worker 形成两条并发执行路径。这里只做「入队 + 唤醒」，
/// 结果仍由 Worker 走同一条通路落 journal，用户到「复盘记录」页看结果。
/// </para>
/// <para>
/// 与自动触发的差别只有一处门禁：手动请求是用户的显式动作，因此
/// <c>Enabled</c> 开关不拦它（否则用户为了验证功能还得先改配置），
/// 但 <see cref="Configuration.ReflectionMode.Off"/>、每日预算与每小时上限
/// 仍然生效——那三项是成本与硬停语义，不该被一次点击绕过。
/// </para>
/// </summary>
public interface IReflectionManualTrigger
{
    /// <summary>把指定会话加入复盘队列并唤醒后台。</summary>
    /// <param name="sessionId">目标会话 ID（必须是已加载的顶层会话）。</param>
    /// <returns>入队结果；失败时带可展示的原因。</returns>
    ReflectionManualTriggerResult Trigger(string sessionId);
}

/// <summary>手动触发的结果。</summary>
/// <param name="Status">入队状态。</param>
/// <param name="Message">可展示给用户的说明（成功或失败原因）。</param>
public sealed record ReflectionManualTriggerResult(ReflectionManualTriggerStatus Status, string Message)
{
    /// <summary>是否已进入队列（成功或已存在的去重）。</summary>
    public bool Accepted => Status is ReflectionManualTriggerStatus.Enqueued or ReflectionManualTriggerStatus.Updated;
}

/// <summary>手动触发的入队状态。</summary>
public enum ReflectionManualTriggerStatus
{
    /// <summary>新请求入队，后台将立即处理。</summary>
    Enqueued = 0,

    /// <summary>该会话已在队列中：内容已更新为最新，排队位置不变。</summary>
    Updated = 1,

    /// <summary>队列已满，本次请求被丢弃。</summary>
    Dropped = 2,

    /// <summary>请求无效（会话 ID 为空 / 会话不存在 / 模块未激活 / 模式为 Off）。</summary>
    Invalid = 3
}
