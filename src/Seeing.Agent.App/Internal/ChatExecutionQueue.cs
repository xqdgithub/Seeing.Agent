using System.Threading.Channels;

namespace Seeing.Agent.App;

/// <summary>
/// 执行队列 - 管理并发执行
/// </summary>
public class ChatExecutionQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _channel;
    private readonly Dictionary<string, Channel<Func<CancellationToken, Task>>> _channelGroups;
    private readonly object _lock = new();

    public ChatExecutionQueue()
    {
        _channel = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
        _channelGroups = new Dictionary<string, Channel<Func<CancellationToken, Task>>>();
    }

    /// <summary>
    /// 等待执行队列
    /// </summary>
    public async Task WaitAsync(string? channelId, string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            // 无通道 ID，使用全局队列
            return;
        }

        lock (_lock)
        {
            if (!_channelGroups.ContainsKey(channelId))
            {
                _channelGroups[channelId] = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
            }
        }
    }

    /// <summary>
    /// 释放队列
    /// </summary>
    public void Release(string? channelId, string sessionId)
    {
        // 当前简化实现，无需显式释放
    }
}
