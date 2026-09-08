namespace Seeing.Agent.Modules;

/// <summary>
/// 进程级结算失败（硬依赖未满足等）— 应拒绝启动。
/// </summary>
public sealed class SettlementException : InvalidOperationException
{
    /// <summary>创建结算失败异常。</summary>
    public SettlementException(string message)
        : base(message)
    {
    }

    /// <summary>创建结算失败异常。</summary>
    public SettlementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
