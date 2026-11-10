using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Tools.Basic;

/// <summary>
/// 基础工具模块 — 提供 current_time；并贡献 /tools 聚合页。
/// </summary>
public sealed class BasicModule : ISeeingModule, IUiContributor
{
    private static readonly IReadOnlyList<string> s_providedTools = ["current_time"];

    /// <inheritdoc />
    public string Id => "basic";

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
    /// <remarks>
    /// UI 声明是<b>纯数据、无副作用</b>：结算完成后统一收集（含未启用模块），
    /// 因此不再需要 Activate 期登记 / Deactivate 期对称注销。
    /// </remarks>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.tools",
            Route = "/tools",
            Title = "工具",
            ViewKey = "page.tools",
            Requires = [Id],
            Order = 20,
            Nav = new NavPlacement
            {
                Icon = "tool",
                Group = NavGroups.Workspace,
                GroupIcon = NavGroups.WorkspaceIcon,
                GroupOrder = 20,
            },
        },
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
    }
}
