using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.Core.Interactions;

/// <summary>
/// 可呈现性注册表（Singleton，单实现双角色）：线程安全；Register/Unregister 幂等；
/// 订阅/退订各提供方的 <see cref="ISurfaceProvider.SurfacedChanged"/> 并转发 <see cref="Changed"/>。
/// <para>
/// <see cref="ProviderUnregistered"/> 为收敛信号：仅在成功 Unregister 后触发（先于 <see cref="Changed"/>）。
/// </para>
/// </summary>
public sealed class SurfaceRegistry : IPermissionSurfaceRegistry, IQuestionSurfaceRegistry
{
    private readonly ConcurrentDictionary<ISurfaceProvider, byte> _providers = new();
    private readonly ILogger<SurfaceRegistry>? _logger;

    public SurfaceRegistry(ILogger<SurfaceRegistry>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public event Action? ProviderUnregistered;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Register(ISurfaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!_providers.TryAdd(provider, 0))
            return;

        provider.SurfacedChanged += OnProviderSurfacedChanged;
        RaiseChanged();
    }

    /// <inheritdoc />
    public void Unregister(ISurfaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!_providers.TryRemove(provider, out _))
            return;

        provider.SurfacedChanged -= OnProviderSurfacedChanged;
        RaiseProviderUnregistered();
        RaiseChanged();
    }

    /// <inheritdoc />
    public bool CanSurface(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        foreach (var provider in _providers.Keys)
        {
            IReadOnlyCollection<string>? surface;
            try
            {
                surface = provider.SurfaceSessionIds;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "读取可呈现提供方快照失败: {Provider}", provider.GetType().Name);
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

    private void OnProviderSurfacedChanged() => RaiseChanged();

    private void RaiseProviderUnregistered()
    {
        try
        {
            ProviderUnregistered?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "可呈现提供方注销通知失败");
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
            _logger?.LogError(ex, "可呈现性变更通知失败");
        }
    }
}
