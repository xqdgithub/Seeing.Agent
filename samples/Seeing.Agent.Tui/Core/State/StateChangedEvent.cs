namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// 渲染区域枚举 - 标识需要刷新的UI区域
/// </summary>
public enum RenderRegion
{
    /// <summary>顶部信息栏</summary>
    Header,

    /// <summary>消息列表区域</summary>
    Messages,

    /// <summary>流式输出区域</summary>
    Streaming,

    /// <summary>输入区域</summary>
    Input
}

/// <summary>
/// 状态变更事件参数
/// </summary>
public class StateChangedEvent : EventArgs
{
    /// <summary>需要刷新的区域</summary>
    public RenderRegion Region { get; }

    /// <summary>构造事件参数</summary>
    public StateChangedEvent(RenderRegion region) => Region = region;
}