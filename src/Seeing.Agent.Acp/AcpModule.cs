using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Transport;

namespace Seeing.Agent.Acp;

/// <summary>
/// ACP 能力模块 — id=<c>acp</c>；租约连接由 Activate/Deactivate 自管启停。
/// </summary>
public sealed class AcpModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools = ["acp", "acp_status"];

    private readonly AcpModuleActivity? _activity;
    private readonly Func<AcpConnectionManager>? _connectionFactory;
    private readonly AcpConnectionManager? _connectionManager;
    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public AcpModule()
    {
    }

    /// <summary>DI 解析用：持有活动门控与连接工厂。</summary>
    public AcpModule(
        AcpModuleActivity activity,
        Func<AcpConnectionManager> connectionFactory,
        AcpConnectionManager connectionManager,
        IUiContributionRegistry? uiRegistry = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
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
        new NavContribution("/acp", "ACP", "robot", ["acp"]),
    ];

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is null || _connectionFactory is null || _connectionManager is null)
        {
            throw new InvalidOperationException(
                "AcpModule.ActivateAsync requires DI-resolved AcpModule (activity + connection factory).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 工厂可供热路径创建新实例；进程内消费者仍用共享 manager，Activate 验证可建并置运行态。
        _ = _connectionFactory;
        _ = _connectionManager;
        _activity.MarkActive();
        _ui?.Register(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        _ui?.Unregister(Id);

        if (_activity is null || _connectionManager is null)
            return;

        _activity.MarkInactiveAndWake();
        await _connectionManager.StopAllAsync(cancellationToken).ConfigureAwait(false);
    }
}
