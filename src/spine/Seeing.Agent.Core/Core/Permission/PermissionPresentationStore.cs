using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 呈现端登记表（Singleton）：线程安全；Register/Unregister 幂等；
/// 订阅/退订各 presenter 的 <see cref="IPermissionPresenter.SurfacedChanged"/> 并转发 <see cref="Changed"/>。
/// <para>
/// <see cref="PresenterUnregistered"/> 为收敛信号：仅在成功 Unregister 后触发（先于 <see cref="Changed"/>），
/// 供 <c>PermissionRequestManager</c> 在"呈现端注销"时收敛在途请求；
/// Register 与可呈现集合收缩<b>不</b>触发收敛（spec §4.4）。
/// </para>
/// </summary>
public sealed class PermissionPresentationStore : IPermissionPresentationStore
{
    private readonly ConcurrentDictionary<IPermissionPresenter, byte> _presenters = new();
    private readonly ILogger<PermissionPresentationStore>? _logger;

    public PermissionPresentationStore(ILogger<PermissionPresentationStore>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>呈现端注销（仅 Unregister 成功时触发；先于 <see cref="Changed"/>）。</summary>
    public event Action? PresenterUnregistered;

    /// <inheritdoc />
    public void Register(IPermissionPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        if (!_presenters.TryAdd(presenter, 0))
            return;

        presenter.SurfacedChanged += OnPresenterSurfacedChanged;
        RaiseChanged();
    }

    /// <inheritdoc />
    public void Unregister(IPermissionPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        if (!_presenters.TryRemove(presenter, out _))
            return;

        presenter.SurfacedChanged -= OnPresenterSurfacedChanged;
        RaisePresenterUnregistered();
        RaiseChanged();
    }

    /// <inheritdoc />
    public bool CanSurface(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        foreach (var presenter in _presenters.Keys)
        {
            IReadOnlyCollection<string>? surface;
            try
            {
                surface = presenter.SurfaceSessionIds;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "读取呈现端快照失败: {Presenter}", presenter.GetType().Name);
                continue;
            }

            if (surface is null)
                continue;

            foreach (var id in surface)
            {
                if (string.Equals(id, sessionId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private void OnPresenterSurfacedChanged() => RaiseChanged();

    private void RaisePresenterUnregistered()
    {
        try
        {
            PresenterUnregistered?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呈现端注销通知失败");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呈现端变更通知失败");
        }
    }
}
