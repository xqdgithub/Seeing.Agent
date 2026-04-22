// 类型已迁移至 State/StateChangedEvent.cs
// 此文件保留向后兼容的类型别名

namespace Seeing.Agent.Tui.Core;

/// <summary>渲染区域枚举（别名）</summary>
public enum RenderRegion
{
    Header = State.RenderRegion.Header,
    Messages = State.RenderRegion.Messages,
    Streaming = State.RenderRegion.Streaming,
    Input = State.RenderRegion.Input
}

/// <summary>状态变更事件参数（别名）</summary>
public class StateChangedEvent : State.StateChangedEvent
{
    public StateChangedEvent(RenderRegion region) : base((State.RenderRegion)region) { }
}
