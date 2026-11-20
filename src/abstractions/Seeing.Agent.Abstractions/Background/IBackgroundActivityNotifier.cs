using Seeing.Agent.Abstractions.Runtime;

namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 完成通知投递端口：把合并后的唤醒消息送入目标会话收件箱。
/// <para>幂等键由 dispatcher 以 <c>activity-digest:{digestId}</c> 形式给出，重试只重放同一 digest。</para>
/// </summary>
public interface IBackgroundActivityNotifier
{
    /// <summary>投递一条收件箱消息。</summary>
    Task DeliverAsync(InboxMessage message, CancellationToken ct = default);
}
