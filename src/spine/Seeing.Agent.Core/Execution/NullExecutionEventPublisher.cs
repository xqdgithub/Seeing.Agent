using System.Runtime.CompilerServices;
using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Core.Execution;

/// <summary>
/// 空执行事件发布器：Core-only 组合（无 Hosting 执行引擎）下为 <see cref="IExecutionEventPublisher"/>
/// 提供兜底实现，所有发布 / 订阅 / 缓冲操作均为 no-op。真实宿主由 Hosting 的 AddExecutionEngine
/// 以 AddSingleton 在后注册覆盖（解析取最后一个注册）。
/// </summary>
public sealed class NullExecutionEventPublisher : IExecutionEventPublisher
{
    /// <inheritdoc />
    public void Publish(string sessionId, IMessageEvent evt)
    {
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IMessageEvent> SubscribeAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

    /// <inheritdoc />
    public void ClearBuffer(string sessionId)
    {
    }

    /// <inheritdoc />
    public void CompleteSession(string sessionId)
    {
    }
}
