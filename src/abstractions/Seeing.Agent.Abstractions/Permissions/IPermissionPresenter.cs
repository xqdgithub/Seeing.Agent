namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 宿主呈现端：声明在自身上下文（含宿主自定义冒泡）中可呈现的会话集合。
/// 由宿主注册到 <see cref="IPermissionPresentationStore"/>；集合可随宿主状态变化。
/// <para>
/// 契约约定：<see cref="SurfaceSessionIds"/> 必须返回<b>已确认的稳定快照</b>（缓存字段），
/// <b>不得</b>实时读取底层易变状态；空集候选须经宿主确认期后才写入缓存。
/// 宿主自行为瞬态去抖，避免短时抖动触发在途收敛或新请求被 fail-closed。
/// </para>
/// </summary>
public interface IPermissionPresenter
{
    /// <summary>当前可呈现的会话集合快照。</summary>
    IReadOnlyCollection<string> SurfaceSessionIds { get; }

    /// <summary>可呈现集合变化（宿主稳定态变化后触发）。</summary>
    event Action? SurfacedChanged;
}
