using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Gateway;

/// <summary>
/// Gateway 能力模块 — id=<c>gateway</c>；Web Host 登记 Gateway 管理页导航。
/// </summary>
public sealed class GatewayModule : ISeeingModule, IUiContribution
{
    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public GatewayModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public GatewayModule(IUiContributionRegistry? uiRegistry)
    {
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "gateway";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 实际 DI 登记由 AddSeeingGatewayServer 完成。
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/gateway", "Gateway", "global", ["gateway"]),
        new NavContribution("/gateway-clients", "Gateway 客户端", "api", ["gateway"]),
    ];

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ui?.Register(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        _ui?.Unregister(Id);
        return Task.CompletedTask;
    }
}
