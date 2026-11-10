using System.Text.Json;

namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>工作台调用结果状态（已从宿主响应归一化）。</summary>
public enum WorkbenchCallStatus
{
    /// <summary>已完成。</summary>
    Completed,

    /// <summary>界面未打开该工作台 / 无可呈现界面。</summary>
    Unavailable,

    /// <summary>等待超时。</summary>
    Timeout,

    /// <summary>调用方取消。</summary>
    Cancelled,

    /// <summary>工作台执行失败。</summary>
    Failed,

    /// <summary>同 id 工作台在多个 dock 各有一个实例，且调用方未指定 dock。</summary>
    Ambiguous
}

/// <summary>工作台调用结果。</summary>
/// <param name="Status">归一化状态。</param>
/// <param name="Message">给 agent 看的说明（成功为「已完成」，失败为原因）。</param>
/// <param name="Payload">工作台返回的载荷（可空）。</param>
/// <param name="RequestId">宿主请求标识（诊断用，可空）。</param>
public sealed record WorkbenchCallResult(
    WorkbenchCallStatus Status,
    string Message,
    JsonElement? Payload = null,
    string? RequestId = null)
{
    /// <summary>是否成功。</summary>
    public bool Success => Status == WorkbenchCallStatus.Completed;
}

/// <summary>
/// 工作台调用端口：能力包（插件工具）用它驱动自己的工作台 UI 实例。
/// <para>
/// <b>为什么端口在契约层、实现在能力包</b>：插件包（如 <c>Seeing.Agent.Tools.Editor</c>）
/// 只依赖 Abstractions，不能引用工作台实现包，否则能力包之间互相依赖。
/// </para>
/// <para>
/// 实现负责：拼 <c>workbench.&lt;id&gt;.&lt;action&gt;</c> kind、经 <c>IHostRequestManager</c> 往返、
/// 把宿主响应与歧义判定归一化为 <see cref="WorkbenchCallResult"/>。
/// 插件不必重复这段映射。
/// </para>
/// </summary>
public interface IWorkbenchClient
{
    /// <summary>
    /// 向指定工作台实例发自有动作请求。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="workbenchId">工作台 id。</param>
    /// <param name="action">动作名（工作台自定义）。</param>
    /// <param name="payload">载荷（可空）。</param>
    /// <param name="dock">目标 dock；同一工作台在两侧都开着时必须指定，否则返回 <see cref="WorkbenchCallStatus.Ambiguous"/>。</param>
    /// <param name="ct">取消令牌。</param>
    Task<WorkbenchCallResult> SendAsync(
        string sessionId,
        string workbenchId,
        string action,
        JsonElement? payload = null,
        WorkbenchDock? dock = null,
        CancellationToken ct = default);
}
