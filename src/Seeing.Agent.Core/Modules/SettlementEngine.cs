using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Modules;

/// <summary>
/// 进程级结算引擎 — 计算 available / scenario base / enabled，并写入 <see cref="ModuleCatalog"/>。
/// </summary>
/// <remarks>
/// 公式：
/// <c>available = 宿主目录</c>；
/// <c>scenario = seeing.json.scenario ?? Host 默认</c>；
/// <c>base = scenario.modules ∩ available</c>；
/// <c>enabled = (用户 modules.enabled ?? base) ∩ available − 用户 modules.disabled</c>。
/// 未知 id 告警忽略；启用集中 <c>DependsOn</c> 未启用则拒绝启动；
/// Scenario 引用不在 available 的 id 告警忽略、不拒启。
/// </remarks>
public sealed class SettlementEngine
{
    private readonly ModuleCatalog _catalog;
    private readonly ILogger<SettlementEngine> _logger;

    /// <summary>创建结算引擎。</summary>
    public SettlementEngine(ModuleCatalog catalog, ILogger<SettlementEngine>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<SettlementEngine>.Instance;
    }

    /// <summary>
    /// 执行进程级结算并更新目录。硬依赖缺失时抛 <see cref="SettlementException"/>（目录已写入 available，enabled 保持不变）。
    /// </summary>
    public Task<SettlementResult> SettleAsync(
        SettlementInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Available);
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var availableMap = BuildAvailableMap(input.Available);
        _catalog.ReplaceAvailable(availableMap.Values);

        var scenarioName = ResolveScenarioName(input.ConfiguredScenario, input.HostDefaultScenario);
        var scenarioModules = ResolveScenarioModuleIds(scenarioName, input, warnings);

        var baseline = IntersectWithAvailable(
            scenarioModules,
            availableMap,
            warnings,
            origin: scenarioName is null ? "scenario-base" : $"scenario '{scenarioName}'");

        IReadOnlyList<string> candidateSource = input.UserEnabled is null
            ? baseline
            : IntersectWithAvailable(
                input.UserEnabled,
                availableMap,
                warnings,
                origin: "modules.enabled");

        var enabledSet = new HashSet<string>(candidateSource, StringComparer.OrdinalIgnoreCase);

        if (input.UserDisabled is { Count: > 0 })
        {
            foreach (var id in input.UserDisabled)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (!availableMap.ContainsKey(id))
                {
                    var msg = $"忽略未知 modules.disabled 模块 id '{id}'（不在宿主 available 目录）。";
                    warnings.Add(msg);
                    _logger.LogWarning("{Warning}", msg);
                    continue;
                }

                enabledSet.Remove(id);
            }
        }

        ValidateHardDependencies(enabledSet, availableMap);

        var enabled = enabledSet
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _catalog.ReplaceEnabled(enabled);

        var result = new SettlementResult
        {
            Scenario = scenarioName,
            Enabled = enabled,
            Warnings = warnings,
        };

        return Task.FromResult(result);
    }

    /// <summary>从 <see cref="ISeeingModule"/> 构建描述符列表。</summary>
    public static IReadOnlyList<ModuleDescriptor> ToDescriptors(IEnumerable<ISeeingModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return modules
            .Select(m =>
            {
                ArgumentNullException.ThrowIfNull(m);
                return new ModuleDescriptor(
                    m.Id,
                    m.ProvidedTools,
                    m.ProvidedSeams,
                    m.DependsOn);
            })
            .ToArray();
    }

    private static Dictionary<string, ModuleDescriptor> BuildAvailableMap(
        IReadOnlyList<ModuleDescriptor> available)
    {
        var map = new Dictionary<string, ModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in available)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (string.IsNullOrWhiteSpace(descriptor.Id))
                throw new ArgumentException("ModuleDescriptor.Id 不能为空", nameof(available));
            map[descriptor.Id] = descriptor;
        }

        return map;
    }

    private static string? ResolveScenarioName(string? configured, string? hostDefault)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (!string.IsNullOrWhiteSpace(hostDefault))
            return hostDefault.Trim();
        return null;
    }

    private IReadOnlyList<string> ResolveScenarioModuleIds(
        string? scenarioName,
        SettlementInput input,
        List<string> warnings)
    {
        if (scenarioName is null)
            return Array.Empty<string>();

        IReadOnlyList<string>? modules = null;
        if (input.ResolveScenarioModules is not null)
            modules = input.ResolveScenarioModules(scenarioName);

        if (modules is null &&
            input.Scenarios.TryGetValue(scenarioName, out var fromDict))
        {
            modules = fromDict;
        }

        if (modules is null)
        {
            var msg = $"未知 scenario '{scenarioName}'，进程级 base 为空集。";
            warnings.Add(msg);
            _logger.LogWarning("{Warning}", msg);
            return Array.Empty<string>();
        }

        return modules;
    }

    private List<string> IntersectWithAvailable(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, ModuleDescriptor> available,
        List<string> warnings,
        string origin)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var id = raw.Trim();
            if (!available.ContainsKey(id))
            {
                var msg = $"忽略 {origin} 中未引用模块 id '{id}'（不在宿主 available 目录）。";
                warnings.Add(msg);
                _logger.LogWarning("{Warning}", msg);
                continue;
            }

            if (seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    private static void ValidateHardDependencies(
        HashSet<string> enabled,
        IReadOnlyDictionary<string, ModuleDescriptor> available)
    {
        foreach (var id in enabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!available.TryGetValue(id, out var descriptor))
                continue;

            foreach (var dep in descriptor.DependsOn)
            {
                if (string.IsNullOrWhiteSpace(dep))
                    continue;

                if (enabled.Contains(dep))
                    continue;

                throw new SettlementException(
                    $"模块 '{id}' 硬依赖 '{dep}' 未启用，拒绝启动。");
            }
        }
    }
}
