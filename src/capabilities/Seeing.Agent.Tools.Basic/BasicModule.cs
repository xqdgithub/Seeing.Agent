using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Tools.Basic;

/// <summary>
/// 基础工具模块 — 提供 current_time；并贡献 /tools 聚合页导航。
/// </summary>
public sealed class BasicModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools = ["current_time"];

    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public BasicModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public BasicModule(IUiContributionRegistry? uiRegistry)
    {
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "basic";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<CurrentTimeTool>();
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/tools", "工具", "tool", ["basic"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 20),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ui?.Register(this);

        var tm = services.GetService<IToolManager>();
        var tool = services.GetService<CurrentTimeTool>();
        if (tm is not null && tool is not null)
            await tm.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        _ui?.Unregister(Id);
    }
}
