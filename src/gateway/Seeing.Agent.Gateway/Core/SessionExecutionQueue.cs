using System.Collections.Concurrent;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 按 (channelId, sessionId) 串行化执行，同一通道同一会话同时仅允许一个 Agent 运行。
/// </summary>
public sealed class SessionExecutionQueue
{
    private readonly ConcurrentDictionary<(string ChannelId, string SessionId), SemaphoreSlim> _locks = new();

    /// <summary>获取执行锁（等待前序任务完成）</summary>
    public async Task WaitAsync(string channelId, string sessionId, CancellationToken cancellationToken = default)
    {
        var key = (channelId, sessionId);
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
    }

    /// <summary>释放执行锁</summary>
    public void Release(string channelId, string sessionId)
    {
        var key = (channelId, sessionId);
        if (_locks.TryGetValue(key, out var semaphore))
        {
            semaphore.Release();
        }
    }
}
