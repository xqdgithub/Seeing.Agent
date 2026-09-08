using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Acp.Transport;

namespace Seeing.Agent.Acp;

/// <summary>
/// ACP 能力模块 — id=<c>acp</c>；连接管理器由 Activate/Deactivate 经 <see cref="AcpConnectionOwner"/> 自管。
/// </summary>
public sealed class AcpModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools = ["acp", "acp_status"];

    private readonly AcpModuleActivity? _activity;
    private readonly AcpConnectionOwner? _connectionOwner;
    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public AcpModule()
    {
    }

    /// <summary>DI 解析用：持有活动门控与连接所有者。</summary>
    public AcpModule(
        AcpModuleActivity activity,
        AcpConnectionOwner connectionOwner,
        IUiContributionRegistry? uiRegistry = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectionOwner = connectionOwner ?? throw new ArgumentNullException(nameof(connectionOwner));
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "acp";

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
        // 实际 DI 登记由 AddSeeingAcp 完成。
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/acp", "ACP", "robot", ["acp"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 40),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null || _connectionOwner is null)
        {
            throw new InvalidOperationException(
                "AcpModule.ActivateAsync requires DI-resolved AcpModule (activity + connection owner).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connectionOwner.EnsureCreated();
        _activity.MarkActive();
        _ui?.Register(this);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<AcpTool>() is { } acp)
            await tm.RegisterToolAsync(acp, cancellationToken).ConfigureAwait(false);
        if (services.GetService<AcpStatusTool>() is { } status)
            await tm.RegisterToolAsync(status, cancellationToken).ConfigureAwait(false);
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

        if (_activity is null || _connectionOwner is null)
            return;

        _activity.MarkInactiveAndWake();
        await _connectionOwner.ReleaseAsync(cancellationToken).ConfigureAwait(false);
    }
}
