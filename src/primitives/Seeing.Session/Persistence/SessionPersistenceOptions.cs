namespace Seeing.Session.Persistence;

/// <summary>
/// 会话写回持久化配置。对应设计规格 §4.6。
/// </summary>
public sealed class SessionPersistenceOptions
{
    /// <summary>
    /// 是否启用写回调度（<c>false</c> 表示直写后端，不等价于禁用持久化）。
    /// 默认 <c>true</c>。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 尾沿去抖窗口：窗口内后写覆盖前写。默认 400ms。
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 饥饿保护上限：自某个键首次变脏起，最迟在该期限内必须落盘。默认 2s。
    /// </summary>
    public TimeSpan MaxFlushDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 单次写入失败后的指数退避上限。默认 30s。
    /// </summary>
    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 释放期的最终 flush 等待上限。默认 5s。
    /// </summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 读路径（<c>TryFlushAsync</c>）的有界等待上限。默认 1s。
    /// </summary>
    public TimeSpan ReadFlushTimeout { get; set; } = TimeSpan.FromSeconds(1);
}
