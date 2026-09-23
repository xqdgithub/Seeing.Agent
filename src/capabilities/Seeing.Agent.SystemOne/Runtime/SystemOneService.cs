using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Runtime;

/// <summary>SystemOne 业务门面：按默认 provider 转发调用。</summary>
public sealed class SystemOneService : ISystemOneService
{
    private readonly ISystemOneProviderRegistry _registry;

    /// <summary>注入 provider 注册表。</summary>
    public SystemOneService(ISystemOneProviderRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public Task<SystemOneResponse> EvaluateAsync(
        SystemOneRequest request, CancellationToken cancellationToken = default)
        => RequireProvider(_registry.DefaultProvider).GetClient().EvaluateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
        => (providerId is null
                ? RequireProvider(_registry.DefaultProvider)
                : RequireProvider(_registry.Providers.FirstOrDefault(
                    p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))))
            .GetModelsAsync(cancellationToken);

    private static ISystemOneProvider RequireProvider(ISystemOneProvider? provider)
        => provider ?? throw new InvalidOperationException(
            "未配置可用的 SystemOne provider（检查 SYSTEMONE_API_KEY 与 systemone.json）");
}
