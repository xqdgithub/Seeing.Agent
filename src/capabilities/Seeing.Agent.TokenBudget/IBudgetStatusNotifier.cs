using Seeing.Agent.TokenBudget.Api.Responses;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// Token Budget 状态变更通知器
/// <para>
/// 使用单例模式，允许 Hook（单例）通知所有订阅者（包括 UI SessionState）
/// </para>
/// </summary>
public interface IBudgetStatusNotifier
{
    /// <summary>
    /// 订阅指定会话的 Budget 状态变更
    /// </summary>
    IDisposable Subscribe(string sessionId, Action<BudgetStatusResponse> onUpdate);

    /// <summary>
    /// 发布 Budget 状态更新
    /// </summary>
    void Publish(string sessionId, BudgetStatusResponse status);

    /// <summary>
    /// 获取指定会话的当前状态
    /// </summary>
    BudgetStatusResponse? GetCurrentStatus(string sessionId);
}
