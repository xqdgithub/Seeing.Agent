using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Runtime;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 目标会话已不存在（被删除/归档目录不可见）时由 notifier 抛出的信号。
/// <para>
/// 契约 <see cref="IBackgroundActivityNotifier"/> 只有 <c>Task</c>，没有返回值，因此「丢弃」
/// 语义用异常表达：outbox 捕获它后把批次标 <see cref="BackgroundActivityOutboxStatus.Dropped"/>，
/// 不再重试（重试不会改变会话是否存在）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityTargetSessionMissingException : Exception
{
    public BackgroundActivityTargetSessionMissingException(string targetSessionId)
        : base($"后台活动完成通知的目标会话不存在：'{targetSessionId}'。")
        => TargetSessionId = targetSessionId;

    /// <summary>不存在的目标会话 id。</summary>
    public string TargetSessionId { get; }
}

/// <summary>
/// <see cref="IBackgroundActivityNotifier"/> 的默认实现：把 outbox 构造好的唤醒消息投进目标会话收件箱。
/// <para>
/// <b>为什么先查会话是否存在</b>：<c>SessionRuntime.DeliverAsync</c> 在目标会话缺失时会
/// <c>EnsureSessionAsync</c> 自动建一个空会话——通知会「唤醒」一个刚被删掉的会话，语义是错的。
/// 因此这里先判定存在性：目录摘要一次批量取齐，未命中再按 id 单查（子会话不在锚点摘要里）。
/// </para>
/// <para>
/// <b>不负责构造正文</b>：正文（摘要，不含输出全文）由 outbox 在合并批次时构造——
/// 它已经为「已观察过滤」批量读过活动快照，再让 notifier 读一遍只会多一次 IO。
/// </para>
/// </summary>
internal sealed class SessionRuntimeActivityNotifier : IBackgroundActivityNotifier
{
    private readonly ISessionRuntime _runtime;
    private readonly ISessionDirectory _directory;

    public SessionRuntimeActivityNotifier(ISessionRuntime runtime, ISessionDirectory directory)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    /// <inheritdoc />
    public async Task DeliverAsync(InboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.TargetSessionId);

        if (!await ExistsAsync(message.TargetSessionId, ct).ConfigureAwait(false))
            throw new BackgroundActivityTargetSessionMissingException(message.TargetSessionId);

        var result = await _runtime.DeliverAsync(message, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"后台活动完成通知投递失败（SessionId={message.TargetSessionId}）：{result.Error}");
        }
    }

    /// <summary>
    /// 会话存在性判定：先一次批量取锚点摘要（覆盖绝大多数归属会话），未命中再按 id 单查
    /// （子会话/归档会话不在锚点摘要里）。不会逐条循环查目录。
    /// </summary>
    private async Task<bool> ExistsAsync(string sessionId, CancellationToken ct)
    {
        var summaries = await _directory.ListSummariesAsync(ct).ConfigureAwait(false);
        foreach (var summary in summaries)
        {
            if (string.Equals(summary.Id, sessionId, StringComparison.Ordinal))
                return true;
        }

        return await _directory.GetAsync(sessionId, ct).ConfigureAwait(false) is not null;
    }
}
