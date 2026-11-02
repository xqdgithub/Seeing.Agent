namespace Seeing.Agent.Reflection.Core;

/// <summary>复盘触发原因。</summary>
public enum ReflectionTriggerKind
{
    /// <summary>轮次触发：同一会话累计完成 N 轮顶层执行。</summary>
    TurnNudge,

    /// <summary>空闲触发：会话空闲超过阈值后由后台扫描产出。</summary>
    Idle,

    /// <summary>手动触发：用户或外部调用方显式请求。</summary>
    Manual
}

/// <summary>
/// 一次复盘请求（入队事实，不含会话内容）。
/// <para>
/// 刻意保持为纯数据：观察者只负责把「哪个会话需要复盘」放进队列，
/// 读取会话正文、调模型等耗时工作一律由后台 Worker 完成。
/// </para>
/// </summary>
/// <param name="SessionId">目标会话 ID。</param>
/// <param name="ExecutionId">触发该请求的源执行 ID；空闲/手动触发时为空字符串。</param>
/// <param name="Kind">触发原因。</param>
/// <param name="At">请求产生时刻。</param>
public sealed record ReflectionRequest(
    string SessionId,
    string ExecutionId,
    ReflectionTriggerKind Kind,
    DateTimeOffset At);
