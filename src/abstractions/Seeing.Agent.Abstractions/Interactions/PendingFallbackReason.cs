namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>在途请求未获正常决议时的兜底原因。</summary>
public enum PendingFallbackReason
{
    /// <summary>等待超时。</summary>
    Timeout,

    /// <summary>等待方取消。</summary>
    Cancelled,

    /// <summary>管理器已释放。</summary>
    Disposed,

    /// <summary>呈现端不可用（如移除收敛）。</summary>
    Unavailable
}
