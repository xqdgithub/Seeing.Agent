using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级结算引擎 — 计算 available / scenario base / enabled / 独占 seam 绑定，并写入 <see cref="ModuleCatalog"/>。
/// </summary>
/// <remarks>
/// 公式：
/// <c>available = 宿主目录</c>；
/// <c>scenario = seeing.json.scenario ?? Host 默认</c>；
/// <c>base = scenario.modules ∩ available</c>；
/// <c>enabled = (用户 modules.enabled ?? base) ∩ available − 用户 modules.disabled</c>。
/// 未知 id 告警忽略；启用集中 <c>DependsOn</c> 未启用则拒绝启动；
/// Scenario 引用不在 available 的 id 告警忽略、不拒启。
/// 独占 seam（<c>executionWorld</c> / <c>permissionChannel</c>）按模块 id 绑定；多提供方启用或
/// 有消费方却未绑定 → 拒启。禁止 Core 按通道逻辑名（blazor/deny-all 等）写死 switch。
/// </remarks>
public sealed class SettlementEngine
{
    /// <summary>目录内同时只能绑定一个提供方的 seam 名。</summary>
    public static readonly IReadOnlyList<string> ExclusiveSeams =
        ["executionWorld", "permissionChannel"];

    private readonly ModuleCatalog _catalog;
    private readonly ILogger<SettlementEngine> _logger;

    /// <summary>创建结算引擎。</summary>
    public SettlementEngine(ModuleCatalog catalog, ILogger<SettlementEngine>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<SettlementEngine>.Instance;
    }

    /// <summary>
    /// 执行进程级结算并更新目录。硬依赖缺失 / seam 冲突时抛 <see cref="SettlementException"/>
    /// （目录已写入 available，enabled 保持不变）。
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

        var resolvedSeams = ResolveSeamsMap(scenarioName, input);
        var boundSeams = ValidateAndBindExclusiveSeams(enabledSet, availableMap, resolvedSeams);

        var enabled = enabledSet
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _catalog.ReplaceEnabled(enabled);
        _catalog.ReplaceBoundSeams(boundSeams);

        var result = new SettlementResult
        {
            Scenario = scenarioName,
            Enabled = enabled,
            BoundSeams = boundSeams,
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

    private static Dictionary<string, string> ResolveSeamsMap(
        string? scenarioName,
        SettlementInput input)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (scenarioName is not null && input.ResolveScenarioSeams is not null)
        {
            var scenarioSeams = input.ResolveScenarioSeams(scenarioName);
            if (scenarioSeams is not null)
            {
                foreach (var (key, value) in scenarioSeams)
                {
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                        continue;
                    map[key.Trim()] = value.Trim();
                }
            }
        }

        if (input.UserSeams is not null)
        {
            foreach (var (key, value) in input.UserSeams)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var seam = key.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    map.Remove(seam);
                    continue;
                }

                map[seam] = value.Trim();
            }
        }

        return map;
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

    /// <summary>
    /// 绑定独占 seams：按模块 id 查找提供方；多启用提供方 / 有消费方未绑定 → 拒启。
    /// </summary>
    private static IReadOnlyDictionary<string, string> ValidateAndBindExclusiveSeams(
        HashSet<string> enabled,
        IReadOnlyDictionary<string, ModuleDescriptor> available,
        IReadOnlyDictionary<string, string> resolvedSeams)
    {
        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seam in ExclusiveSeams)
        {
            var providersAvailable = available.Values
                .Where(d => ProvidesSeam(d, seam))
                .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var providerIds = new HashSet<string>(
                providersAvailable.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase);

            var providersEnabled = providersAvailable
                .Where(p => enabled.Contains(p.Id))
                .ToArray();

            if (providersEnabled.Length > 1)
            {
                var ids = string.Join(", ", providersEnabled.Select(p => p.Id));
                throw new SettlementException(
                    $"seam '{seam}' 有多个启用中的提供方（{ids}），拒绝启动。");
            }

            var hasConsumer = HasSeamConsumer(enabled, available, providerIds, seam);

            resolvedSeams.TryGetValue(seam, out var configuredRaw);
            var configuredId = string.IsNullOrWhiteSpace(configuredRaw)
                ? null
                : configuredRaw.Trim();

            if (configuredId is not null)
            {
                if (!available.TryGetValue(configuredId, out var provider) ||
                    !ProvidesSeam(provider, seam))
                {
                    throw new SettlementException(
                        $"seams.{seam}='{configuredId}' 不是已登记的 {seam} 提供方模块，拒绝启动。");
                }

                if (providersEnabled.Length == 1 &&
                    !string.Equals(providersEnabled[0].Id, configuredId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SettlementException(
                        $"seam '{seam}' 配置绑定 '{configuredId}'，但启用集中的提供方是 '{providersEnabled[0].Id}'，拒绝启动。");
                }

                if (hasConsumer && !enabled.Contains(configuredId))
                {
                    throw new SettlementException(
                        $"seam '{seam}' 绑定到 '{configuredId}' 但该模块未启用，而启用集含消费方，拒绝启动。");
                }

                bound[seam] = configuredId;
                continue;
            }

            // 无显式 seams.*：有消费方则必须已绑定 → 拒启；无消费方且唯一启用提供方可记入绑定。
            if (hasConsumer)
            {
                throw new SettlementException(
                    $"seam '{seam}' 未绑定但启用集含消费方，拒绝启动。");
            }

            if (providersEnabled.Length == 1)
                bound[seam] = providersEnabled[0].Id;
        }

        return bound;
    }

    private static bool HasSeamConsumer(
        HashSet<string> enabled,
        IReadOnlyDictionary<string, ModuleDescriptor> available,
        HashSet<string> providerIds,
        string seam)
    {
        if (providerIds.Count == 0)
            return false;

        foreach (var id in enabled)
        {
            if (!available.TryGetValue(id, out var descriptor))
                continue;

            if (ProvidesSeam(descriptor, seam))
                continue;

            foreach (var dep in descriptor.DependsOn)
            {
                if (!string.IsNullOrWhiteSpace(dep) && providerIds.Contains(dep))
                    return true;
            }
        }

        return false;
    }

    private static bool ProvidesSeam(ModuleDescriptor descriptor, string seam)
    {
        foreach (var provided in descriptor.ProvidedSeams)
        {
            if (string.Equals(provided, seam, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
