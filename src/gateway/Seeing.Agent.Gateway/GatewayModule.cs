using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Gateway;

/// <summary>
/// Gateway 能力模块 — id=<c>gateway</c>；Web Host 登记 Gateway 管理页与客户端页。
/// </summary>
public sealed class GatewayModule : ISeeingModule, IUiContributor
{
    /// <inheritdoc />
    public string Id => "gateway";

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
    /// <remarks>两条页面都归在侧栏「设置」分组（Gateway 是运维入口，不是工作区能力）。</remarks>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.gateway",
            Route = "/gateway",
            Title = "Gateway",
            ViewKey = "page.gateway",
            Requires = [Id],
            Order = 40,
            Nav = new NavPlacement
            {
                Icon = "global",
                Group = NavGroups.Settings,
                GroupIcon = NavGroups.SettingsIcon,
                GroupOrder = 30,
            },
        },
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.gateway.clients",
            Route = "/gateway-clients",
            Title = "Gateway 客户端",
            ViewKey = "page.gateway.clients",
            Requires = [Id],
            Order = 50,
            Nav = new NavPlacement
            {
                Icon = "api",
                Group = NavGroups.Settings,
                GroupIcon = NavGroups.SettingsIcon,
                GroupOrder = 30,
            },
        },
    ];

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
