using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Gateway.Permission;

/// <summary>
/// Gateway 订阅呈现端：每个执行订阅注册一个实例，声明该订阅会话可呈现。
/// <para>订阅生命周期内固定单会话，<see cref="SurfaceSessionIds"/> 为已确认稳定快照，
/// <see cref="SurfacedChanged"/> 永不触发（固定集合无变化）。</para>
/// </summary>
public sealed class GatewaySubscriptionPresenter : IPermissionPresenter
{
    private readonly string[] _surfaceSessionIds;

    public GatewaySubscriptionPresenter(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _surfaceSessionIds = new[] { sessionId };
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SurfaceSessionIds => _surfaceSessionIds;

    /// <inheritdoc />
    public event Action? SurfacedChanged
    {
        add { }
        remove { }
    }
}
