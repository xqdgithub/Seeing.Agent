namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>
/// 可呈现性提供方：声明在自身上下文（含宿主自定义冒泡）中可呈现的会话集合。
/// <para>
/// 契约约定：<see cref="SurfaceSessionIds"/> 必须返回<b>已确认的稳定快照</b>（缓存字段），
/// <b>不得</b>实时读取底层易变状态。
/// </para>
/// </summary>
public interface ISurfaceProvider
{
    /// <summary>当前可呈现的会话集合快照。</summary>
    IReadOnlyCollection<string> SurfaceSessionIds { get; }

    /// <summary>可呈现集合变化（宿主稳定态变化后触发）。</summary>
    event Action? SurfacedChanged;
}
