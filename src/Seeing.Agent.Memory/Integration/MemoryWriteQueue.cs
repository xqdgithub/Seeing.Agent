using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using System.Threading.Channels;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// 后台异步写入队列，使用 Channel&lt;T&gt; 实现。
/// 解决 Oracle P0 建议：Hook 中禁止同步写入。
/// </summary>
public class MemoryWriteQueue : IDisposable
{
    private readonly Channel<MemoryEntry> _channel;
    private readonly IMemoryManager _memoryManager;
    private readonly ILogger<MemoryWriteQueue>? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    /// <summary>
    /// 创建 MemoryWriteQueue 实例
    /// </summary>
    /// <param name="memoryManager">Memory 管理器</param>
    /// <param name="capacity">队列容量，默认 1000</param>
    /// <param name="logger">日志记录器</param>
    public MemoryWriteQueue(
        IMemoryManager memoryManager,
        int capacity = 1000,
        ILogger<MemoryWriteQueue>? logger = null)
    {
        _memoryManager = memoryManager ?? throw new ArgumentNullException(nameof(memoryManager));
        _logger = logger;

        _channel = Channel.CreateBounded<MemoryEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// 加入写入队列（非阻塞）
    /// </summary>
    /// <param name="entry">记忆条目</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async ValueTask EnqueueWriteAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        await _channel.Writer.WriteAsync(entry, cancellationToken);
        _logger?.LogDebug("记忆加入写入队列: {MemoryId}", entry.Id);
    }

    /// <summary>
    /// 启动后台消费线程
    /// </summary>
    public Task StartProcessingAsync()
    {
        if (_processingTask != null)
        {
            _logger?.LogWarning("写入队列已在运行中");
            return _processingTask;
        }

        _processingTask = Task.Run(async () =>
        {
            _logger?.LogInformation("MemoryWriteQueue 后台处理线程已启动");

            await foreach (var entry in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await _memoryManager.CreateMemoryAsync(entry);
                    _logger?.LogDebug("记忆写入完成: {MemoryId}", entry.Id);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation("MemoryWriteQueue 后台处理线程已停止");
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "记忆写入失败: {MemoryId}", entry.Id);
                }
            }

            _logger?.LogInformation("MemoryWriteQueue 后台处理线程已结束");
        }, _cts.Token);

        return _processingTask;
    }

    /// <summary>
    /// 停止后台线程
    /// </summary>
    public void StopProcessing()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _logger?.LogInformation("MemoryWriteQueue 停止信号已发送");
    }

    /// <summary>
    /// 获取当前队列长度
    /// </summary>
    public int QueueLength => _channel.Reader.Count;

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Dispose()
    {
        StopProcessing();

        // 等待处理任务完成（最多 5 秒）
        if (_processingTask != null)
        {
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* 忽略取消异常 */ }
        }

        _cts.Dispose();
        _logger?.LogInformation("MemoryWriteQueue 已清理");
    }
}