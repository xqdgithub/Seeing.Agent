using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 会话级工具暴露状态的**唯一写出口**：把"上一轮登记、本轮生效"的提升与每轮结算提交
/// 收敛到同一实现，主执行链与 Scheduler 旁路共用。
/// <para>
/// 只写 `<see cref="SessionData.ToolExposure"/>`，不写消息内容；不触发落盘
/// （保存仍由会话运行时/事件驱动负责，提升幂等）。
/// </para>
/// </summary>
public interface ISessionToolExposureWriter
{
    /// <summary>把待生效发现提升为已生效（主链在轮首调用）。</summary>
    void PromotePending(SessionData session);

    /// <summary>
    /// 按会话 id 提升待生效发现。会话不存在或加载失败时返回 <c>false</c> 且不抛异常
    /// （供 Scheduler 旁路使用，保持"不阻断任务"语义）。
    /// </summary>
    Task<bool> PromotePendingAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>提交一次结算增量（发现集裁剪结果、升级粘滞位、每轮度量）。</summary>
    void Commit(
        SessionData session,
        IReadOnlyList<string>? discovered,
        bool escalated,
        int schemaTokens,
        int catalogTokens);
}
