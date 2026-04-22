using System.Threading.Channels;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 事件通道服务 - 基于 System.Threading.Channels 的异步事件发布/订阅
/// </summary>
/// <remarks>
/// 设计目标：
/// 1. 非阻塞：使用 Channel 实现生产者-消费者模式
/// 2. 多生产者：支持多个 Agent 同时发布事件
/// 3. 单消费者：EventRouter 在 Live 上下文中消费
/// 4. 有界容量：防止内存溢出，默认 1000 事件
/// 5. 背压支持：满时等待，不丢弃事件
/// </remarks>
public sealed class EventChannelService
{
    /// <summary>默认通道容量</summary>
    public const int DefaultCapacity = 1000;

    /// <summary>事件通道（多生产者，单消费者）</summary>
    private readonly Channel<IMessageEvent> _channel;

    /// <summary>通道读取器</summary>
    public ChannelReader<IMessageEvent> Reader => _channel.Reader;

    /// <summary>通道写入器</summary>
    public ChannelWriter<IMessageEvent> Writer => _channel.Writer;

    /// <summary>当前待处理事件数</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>通道容量</summary>
    public int Capacity { get; }

    /// <summary>
    /// 构造函数 - 创建有界通道
    /// </summary>
    /// <param name="capacity">通道容量（默认 1000）</param>
    public EventChannelService(int capacity = DefaultCapacity)
    {
        Capacity = capacity;

        // 创建多生产者、单消费者的有界通道
        // 满时等待（BoundedChannelFullMode.Wait）实现背压
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,  // EventRouter 是唯一消费者
            SingleWriter = false  // 支持多个 Agent 发布事件
        };

        _channel = Channel.CreateBounded<IMessageEvent>(options);
    }

    /// <summary>
    /// 发布事件（异步，支持背压）
    /// </summary>
    /// <param name="evt">事件对象</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async ValueTask PublishAsync(IMessageEvent evt, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(evt, cancellationToken);
    }

    /// <summary>
    /// 发布事件（同步，尝试写入）
    /// </summary>
    /// <param name="evt">事件对象</param>
    /// <returns>是否成功写入</returns>
    public bool TryPublish(IMessageEvent evt)
    {
        return _channel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// 读取所有待处理事件（批量读取）
    /// </summary>
    /// <param name="maxCount">最大读取数量</param>
    /// <returns>事件列表</returns>
    public List<IMessageEvent> ReadBatch(int maxCount = 100)
    {
        var events = new List<IMessageEvent>();

        while (events.Count < maxCount && _channel.Reader.TryRead(out var evt))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// 等待读取事件（异步）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>事件对象</returns>
    public async ValueTask<IMessageEvent> ReadAsync(CancellationToken cancellationToken = default)
    {
        return await _channel.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// 等待有事件可读
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async ValueTask WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        await _channel.Reader.WaitToReadAsync(cancellationToken);
    }

    /// <summary>
    /// 关闭通道（完成写入）
    /// </summary>
    public void Complete()
    {
        _channel.Writer.Complete();
    }

    /// <summary>
    /// 清空通道中所有待处理事件
    /// </summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
            // 清空所有事件
        }
    }
}