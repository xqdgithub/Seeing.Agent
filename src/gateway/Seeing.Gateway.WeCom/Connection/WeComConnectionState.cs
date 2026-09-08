namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 企微长连接生命周期状态。
/// </summary>
public enum WeComConnectionState
{
    Disconnected,
    Connecting,
    Subscribed,
    Active,
    Superseded,
    Stopping,
    Backoff,
    Failed
}

/// <summary>
/// 连接状态变更事件参数。
/// </summary>
public sealed class WeComConnectionChangedEventArgs : EventArgs
{
    public required WeComConnectionState PreviousState { get; init; }

    public required WeComConnectionState CurrentState { get; init; }

    public required long Epoch { get; init; }

    public string? Reason { get; init; }

    /// <summary>本次连接自 Active 起的存活时长；仅在断线类状态迁移时有值。</summary>
    public TimeSpan? ConnectedDuration { get; init; }
}

/// <summary>
/// 达到最大重连次数后抛出，由 Bridge / Host 决定进程退出策略。
/// </summary>
public sealed class WeComConnectionFatalException : Exception
{
    public WeComConnectionFatalException(string message) : base(message)
    {
    }
}

/// <summary>
/// 出站发送时连接 epoch 已过期。
/// </summary>
public sealed class WeComConnectionEpochException : InvalidOperationException
{
    public WeComConnectionEpochException(long expectedEpoch, long currentEpoch)
        : base($"WeCom 连接已切换（epoch {expectedEpoch} → {currentEpoch}），请重试")
    {
        ExpectedEpoch = expectedEpoch;
        CurrentEpoch = currentEpoch;
    }

    public long ExpectedEpoch { get; }

    public long CurrentEpoch { get; }
}
