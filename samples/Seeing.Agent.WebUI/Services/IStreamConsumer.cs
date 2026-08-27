using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话事件流消费者策略。Router 按显式 sessionId 参数注册/分发，不读取 SessionId 属性；
/// 同时监听多会话的实现（如 TaskCardAggregator）返回其主会话 Id 作标识。
/// </summary>
public interface IStreamConsumer
{
    string SessionId { get; }

    void OnEvent(IMessageEvent evt);

    void OnStreamEnd();
}
