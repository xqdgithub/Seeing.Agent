namespace Seeing.Agent.Acp.Transport;

/// <summary>
/// ACP 连接管理器的模块级所有者 — DI 仅登记空壳；
/// <see cref="EnsureCreated"/> / <see cref="ReleaseAsync"/> 由模块 Activate / Deactivate 调用。
/// </summary>
public sealed class AcpConnectionOwner
{
    private readonly Func<AcpConnectionManager> _factory;
    private readonly object _gate = new();
    private AcpConnectionManager? _manager;

    public AcpConnectionOwner(Func<AcpConnectionManager> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>当前是否持有已创建的管理器。</summary>
    public bool HasManager
    {
        get
        {
            lock (_gate)
                return _manager is not null;
        }
    }

    /// <summary>已激活时的管理器；未激活时抛出。</summary>
    public AcpConnectionManager Manager
    {
        get
        {
            lock (_gate)
            {
                return _manager
                    ?? throw new InvalidOperationException(
                        "ACP connection manager is not available; activate the acp module first.");
            }
        }
    }

    /// <summary>Activate：若尚无实例则经工厂创建。</summary>
    public AcpConnectionManager EnsureCreated()
    {
        lock (_gate)
            return _manager ??= _factory();
    }

    /// <summary>Deactivate：停止全部租约并释放所有权（下次 Activate 再新建）。</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        AcpConnectionManager? manager;
        lock (_gate)
        {
            manager = _manager;
            _manager = null;
        }

        if (manager is null)
            return;

        await manager.DisposeAsync().ConfigureAwait(false);
    }
}
