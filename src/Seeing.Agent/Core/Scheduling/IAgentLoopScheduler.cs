using Seeing.Session.Core;

namespace Seeing.Agent.Core.Scheduling;

/// <summary>
/// Agent Loop 调度：synthetic 注入与 idle 时自动 resume。
/// </summary>
public interface IAgentLoopScheduler
{
    /// <summary>标记会话是否处于 Loop 忙碌</summary>
    void SetLoopBusy(string sessionId, bool busy);

    /// <summary>该 Session 是否有进行中的 Loop</summary>
    bool IsLoopBusy(string sessionId);

    /// <summary>
    /// 向会话注入 synthetic 消息（metadata 应含 synthetic=true）。
    /// </summary>
    Task InjectSyntheticAsync(
        string sessionId,
        string text,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// 若父 Session idle，则触发 resume 回调；busy 时返回 false。
    /// </summary>
    Task<bool> TryResumeWhenIdleAsync(string sessionId, CancellationToken ct = default);

    /// <summary>注册 resume 处理器（由 App/Orchestrator 注入真正跑 Loop 的逻辑）</summary>
    void RegisterResumeHandler(Func<string, CancellationToken, Task> handler);
}
