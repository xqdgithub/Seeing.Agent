namespace Seeing.Agent.Core.Events;

/// <summary>
/// 向指定 Session 的实时事件总线发布事件（后台 Task 完成后父卡片仍可更新）。
/// App 层适配到 ExecutionEventPublisher。
/// </summary>
public interface ISessionEventBus
{
    void Publish(string sessionId, IMessageEvent evt);
}
