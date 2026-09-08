using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具事件推送出口 - 向父事件流推送事件（子任务投影等）
/// </summary>
public interface IToolEventSink
{
    /// <summary>向父事件流推送事件</summary>
    ValueTask EmitAsync(IMessageEvent evt);
}