namespace Seeing.Session.Persistence;

/// <summary>
/// 持久化刷新端口：将写回缓冲刷入后端存储的共享契约。
/// </summary>
/// <remarks>
/// <para>会话存储与会话组存储的写回装饰器均实现本端口，供工作区切换等需要
/// "切换目录前落盘旧目录" 的宿主任意持有会话/组存储时统一调用。</para>
/// <para><c>FlushAllAsync</c>：等待所有待写内容落盘，不可恢复失败时抛出（显式屏障语义）。</para>
/// <para><c>TryFlushAllAsync</c>：有界等待，超时或失败返回 <c>false</c>，不抛出（读路径/宿主活性语义）。</para>
/// </remarks>
public interface IPersistenceFlusher
{
    /// <summary>等待所有待写内容落盘；不可恢复失败时抛出。</summary>
    Task FlushAllAsync(CancellationToken ct = default);

    /// <summary>有界等待所有待写内容落盘；超时或失败返回 <c>false</c>，不抛出。</summary>
    Task<bool> TryFlushAllAsync(TimeSpan timeout, CancellationToken ct = default);
}
