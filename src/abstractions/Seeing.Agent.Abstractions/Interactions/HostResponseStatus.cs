namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用结果状态。</summary>
public enum HostResponseStatus
{
    /// <summary>已完成。</summary>
    Completed,

    /// <summary>无可用宿主（不可呈现/无目标）。</summary>
    Unavailable,

    /// <summary>等待超时。</summary>
    Timeout,

    /// <summary>等待方取消。</summary>
    Cancelled,

    /// <summary>宿主执行失败。</summary>
    Failed
}
