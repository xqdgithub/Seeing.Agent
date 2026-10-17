namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 实时订阅句柄：由 <see cref="IExecutionEventPublisher.SubscribeLive"/> 创建。
/// <para>
/// 语义（请求/应答消费方专用，区别于含回放的 <see cref="IExecutionEventPublisher.SubscribeAsync"/>）：
/// </para>
/// <list type="bullet">
/// <item>注册即刻生效（同步返回，不回放缓冲历史）；</item>
/// <item>消费方须在触发执行的投递（唤醒）<b>之前</b>完成注册，保证整轮事件自 LoopStart 起不漏；</item>
/// <item><see cref="Dispose"/> 注销订阅并收口事件流；可安全地多次调用。</item>
/// </list>
/// </summary>
public interface ILiveEventSubscription : IAsyncEnumerable<IMessageEvent>, IDisposable
{
}
