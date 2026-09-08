using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Scenarios;

namespace Seeing.Agent.Modules;

/// <summary>
/// 进程级模块结算热重载：配置变更 → 重新结算 → Activate 新增 / Deactivate 移除（含在途边界）。
/// </summary>
public sealed class ModuleSettlementReloadHandler : IReloadHandler
{
    private static readonly string[] s_relevantSections =
        ["Modules", "Scenario", "Seams", "modules", "scenario", "seams"];

    private readonly SettlementEngine _engine;
    private readonly ModuleLifecycleManager _lifecycle;
    private readonly ModuleCatalog _catalog;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly IReadOnlyList<ISeeingModule> _modules;
    private readonly ModuleReloadOptions _reloadOptions;
    private readonly ProcessSettlementOptions? _settlementOptions;
    private readonly IExecutionInFlightBoundary? _inFlight;
    private readonly ILogger<ModuleSettlementReloadHandler> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _deferredCts;
    private Task? _deferredTask;
    private HashSet<string> _pendingDeactivate = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>创建处理器。</summary>
    public ModuleSettlementReloadHandler(
        SettlementEngine engine,
        ModuleLifecycleManager lifecycle,
        ModuleCatalog catalog,
        IOptionsMonitor<SeeingAgentOptions> options,
        IEnumerable<ISeeingModule> modules,
        ModuleReloadOptions? reloadOptions = null,
        ProcessSettlementOptions? settlementOptions = null,
        IExecutionInFlightBoundary? inFlight = null,
        ILogger<ModuleSettlementReloadHandler>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules.ToArray();
        _reloadOptions = reloadOptions ?? new ModuleReloadOptions();
        _settlementOptions = settlementOptions;
        _inFlight = inFlight;
        _logger = logger ?? NullLogger<ModuleSettlementReloadHandler>.Instance;
    }

    /// <inheritdoc />
    public string ComponentId => "module-settlement";

    /// <inheritdoc />
    public IReadOnlyList<Type> ChangeTypes { get; } =
        [typeof(ConfigChange), typeof(WorkspaceChange)];

    /// <summary>当前因在途而推迟的待停用模块 id（测试/诊断）。</summary>
    internal IReadOnlyCollection<string> PendingDeactivate
    {
        get
        {
            lock (_gate)
                return _pendingDeactivate.ToArray();
        }
    }

    /// <inheritdoc />
    public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
    {
        return change switch
        {
            ConfigChange cfg when IsRelevant(cfg) => ResettleAsync(ct),
            WorkspaceChange => ResettleAsync(ct),
            _ => Task.CompletedTask,
        };
    }

    private static bool IsRelevant(ConfigChange change)
    {
        if (change.ChangedSections.Count == 0)
            return true;

        foreach (var section in change.ChangedSections)
        {
            foreach (var relevant in s_relevantSections)
            {
                if (string.Equals(section, relevant, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private async Task ResettleAsync(CancellationToken ct)
    {
        var seeing = _options.CurrentValue;
        var modulesOptions = seeing.Modules ?? new ModulesOptions();

        var input = new SettlementInput
        {
            Available = SettlementEngine.ToDescriptors(_modules),
            ConfiguredScenario = seeing.Scenario,
            HostDefaultScenario = _settlementOptions?.HostDefaultScenario,
            UserEnabled = modulesOptions.Enabled,
            UserDisabled = modulesOptions.Disabled,
            UserSeams = seeing.Seams,
            ResolveScenarioModules = name => BuiltInScenarios.TryGet(name)?.Modules,
            ResolveScenarioSeams = name => BuiltInScenarios.TryGet(name)?.Seams,
        };

        var previousActivated = _lifecycle.Activated.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = await _engine.SettleAsync(input, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "模块热重载结算：scenario={Scenario}, enabled={EnabledCount}, warnings={WarningCount}",
            result.Scenario,
            result.Enabled.Count,
            result.Warnings.Count);

        var enabled = result.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toActivate = enabled.Where(id => !previousActivated.Contains(id)).ToArray();
        var toDeactivate = previousActivated.Where(id => !enabled.Contains(id)).ToArray();

        // 新增：立即 Activate（目录已更新为新启用集）
        if (toActivate.Length > 0)
            await _lifecycle.ActivateAsync(ct).ConfigureAwait(false);

        if (toDeactivate.Length == 0)
        {
            CancelDeferred();
            return;
        }

        await ApplyDeactivateBoundaryAsync(toDeactivate, ct).ConfigureAwait(false);
    }

    private async Task ApplyDeactivateBoundaryAsync(
        IReadOnlyList<string> toDeactivate,
        CancellationToken ct)
    {
        var hasInFlight = _inFlight?.HasInFlight() == true;

        if (_reloadOptions.ForceCancelInFlight)
        {
            CancelDeferred();
            var cancelled = 0;
            if (hasInFlight && _inFlight is not null)
            {
                cancelled = await _inFlight.CancelAllInFlightAsync(ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "强制模块热重载：已取消 {Count} 个在途执行后 Deactivate {Modules}",
                    cancelled,
                    string.Join(", ", toDeactivate));
            }

            await _lifecycle.DeactivateAsync(toDeactivate, ct).ConfigureAwait(false);

            if (cancelled > 0 || hasInFlight)
            {
                throw new InvalidOperationException(
                    $"强制模块热重载已取消 {cancelled} 个在途执行并停用模块: {string.Join(", ", toDeactivate)}");
            }

            return;
        }

        if (!hasInFlight)
        {
            CancelDeferred();
            await _lifecycle.DeactivateAsync(toDeactivate, ct).ConfigureAwait(false);
            return;
        }

        // 默认：推迟 Deactivate 至在途结束
        ScheduleDeferredDeactivate(toDeactivate);
        _logger.LogInformation(
            "在途执行中，推迟 Deactivate: {Modules}",
            string.Join(", ", toDeactivate));
    }

    private void ScheduleDeferredDeactivate(IReadOnlyList<string> toDeactivate)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _deferredCts?.Cancel();
            _deferredCts?.Dispose();
            _deferredCts = cts = new CancellationTokenSource();
            _pendingDeactivate = new HashSet<string>(toDeactivate, StringComparer.OrdinalIgnoreCase);
        }

        var token = cts.Token;
        _deferredTask = Task.Run(async () =>
        {
            try
            {
                await WaitUntilIdleAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                string[] targets;
                lock (_gate)
                {
                    targets = _pendingDeactivate.ToArray();
                    _pendingDeactivate.Clear();
                }

                if (targets.Length == 0)
                    return;

                await _lifecycle.DeactivateAsync(targets, token).ConfigureAwait(false);
                _logger.LogInformation(
                    "在途结束后完成推迟 Deactivate: {Modules}",
                    string.Join(", ", targets));
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("推迟 Deactivate 已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推迟 Deactivate 失败");
            }
        }, CancellationToken.None);
    }

    private async Task WaitUntilIdleAsync(CancellationToken ct)
    {
        if (_inFlight is null)
            return;

        while (_inFlight.HasInFlight())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private void CancelDeferred()
    {
        lock (_gate)
        {
            _deferredCts?.Cancel();
            _deferredCts?.Dispose();
            _deferredCts = null;
            _pendingDeactivate.Clear();
        }
    }
}
