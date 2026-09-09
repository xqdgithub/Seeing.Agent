using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级结算引擎 — 计算 available / bootEnabled / 独占 seam 绑定，并写入 <see cref="ModuleCatalog"/>。
/// </summary>
/// <remarks>
/// 公式（模块层）：
/// <c>boot = BootOverride ?? ConfiguredBoot ?? HostDefaultBoot ?? "*"</c>；
/// <c>boot = "*"</c> → <c>bootEnabled = Available − Modules.Disabled</c>；
/// 否则 <c>bootEnabled = resolve(CapabilitySet).Modules ∩ Available − set.Disabled − Modules.Disabled</c>
/// （CapabilitySet.<c>Modules</c> 含 <c>"*"</c> 时在解析层展开为全 Available，不进字面交）。
/// <c>Modules.Enabled</c> / Scenario.Modules <b>不</b>参与 boot base。
/// 未知 Boot → <see cref="SettlementException"/>；启用集中 <c>DependsOn</c> 未启用则拒启。
/// BoundSeams 仅来自 <c>Seams</c> + <c>HostDefaultSeams</c>（不读 Scenario.Seams）。
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
    /// 执行进程级结算并更新目录。硬依赖缺失 / seam 冲突 / 未知 Boot 时抛 <see cref="SettlementException"/>
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

        if (input.UserEnabled is { Count: > 0 })
        {
            var msg =
                "Modules.Enabled 已废除，结算忽略该字段；请改用 CapabilitySets + Boot。";
            warnings.Add(msg);
            _logger.LogWarning("{Warning}", msg);
        }

        var boot = ResolveBootName(input.BootOverride, input.ConfiguredBoot, input.HostDefaultBoot);
        var enabledSet = ResolveBootEnabled(boot, input, availableMap, warnings);

        if (input.UserDisabled is { Count: > 0 })
            ApplyDisabled(enabledSet, input.UserDisabled, availableMap, warnings, origin: "modules.disabled");

        ValidateHardDependencies(enabledSet, availableMap);

        var resolvedSeams = ResolveSeamsMap(input);
        var boundSeams = ValidateAndBindExclusiveSeams(enabledSet, availableMap, resolvedSeams);

        var enabled = enabledSet
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _catalog.ReplaceEnabled(enabled);
        _catalog.ReplaceBoundSeams(boundSeams);

        var scenarioName = ResolveScenarioName(input.ConfiguredScenario, input.HostDefaultScenario);

        var result = new SettlementResult
        {
            Boot = boot,
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

    /// <summary>BootOverride &gt; ConfiguredBoot &gt; HostDefaultBoot &gt; <c>*</c>。</summary>
    private static string ResolveBootName(string? bootOverride, string? configured, string? hostDefault)
    {
        if (!string.IsNullOrWhiteSpace(bootOverride))
            return bootOverride.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (!string.IsNullOrWhiteSpace(hostDefault))
            return hostDefault.Trim();
        return "*";
    }

    private static string? ResolveScenarioName(string? configured, string? hostDefault)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (!string.IsNullOrWhiteSpace(hostDefault))
            return hostDefault.Trim();
        return null;
    }

    private HashSet<string> ResolveBootEnabled(
        string boot,
        SettlementInput input,
        IReadOnlyDictionary<string, ModuleDescriptor> availableMap,
        List<string> warnings)
    {
        if (string.Equals(boot, "*", StringComparison.Ordinal))
        {
            return new HashSet<string>(availableMap.Keys, StringComparer.OrdinalIgnoreCase);
        }

        var set = ResolveCapabilitySet(boot, input)
                  ?? throw new SettlementException($"未知 Boot '{boot}'，拒绝启动。");

        var basis = ResolveModuleBasis(set.Modules, availableMap, warnings, origin: $"capabilitySet '{boot}'");
        var enabled = new HashSet<string>(basis, StringComparer.OrdinalIgnoreCase);

        if (set.Disabled is { Count: > 0 })
            ApplyDisabled(enabled, set.Disabled, availableMap, warnings, origin: $"capabilitySet '{boot}'.Disabled");

        return enabled;
    }

    private static CapabilitySetDefinition? ResolveCapabilitySet(string name, SettlementInput input)
    {
        if (input.ResolveCapabilitySet is not null)
        {
            var fromDelegate = input.ResolveCapabilitySet(name);
            if (fromDelegate is not null)
                return fromDelegate;
        }

        if (input.CapabilitySets.TryGetValue(name, out var fromDict))
            return fromDict;

        return BuiltInCapabilitySets.TryGet(name);
    }

    /// <summary>
    /// 解析能力集 Modules 基线：空 → 空；含 <c>*</c> → 全 Available（展开，不字面交）；否则 ∩ Available。
    /// </summary>
    private List<string> ResolveModuleBasis(
        IReadOnlyList<string> modules,
        IReadOnlyDictionary<string, ModuleDescriptor> available,
        List<string> warnings,
        string origin)
    {
        if (modules.Count == 0)
            return [];

        var hasStar = false;
        var explicitIds = new List<string>();
        foreach (var raw in modules)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var id = raw.Trim();
            if (id == "*")
            {
                hasStar = true;
                continue;
            }

            explicitIds.Add(id);
        }

        if (hasStar)
        {
            // ["*"] 展开为全 Available；若同时混有显式 id，仍以 Available 为基（* 已覆盖全集）
            return available.Keys.ToList();
        }

        return IntersectWithAvailable(explicitIds, available, warnings, origin);
    }

    private void ApplyDisabled(
        HashSet<string> enabledSet,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, ModuleDescriptor> availableMap,
        List<string> warnings,
        string origin)
    {
        foreach (var id in disabled)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var trimmed = id.Trim();
            if (trimmed == "*")
            {
                // Disabled 不含星号语义；忽略
                continue;
            }

            if (!availableMap.ContainsKey(trimmed))
            {
                var msg = $"忽略未知 {origin} 模块 id '{trimmed}'（不在宿主 available 目录）。";
                warnings.Add(msg);
                _logger.LogWarning("{Warning}", msg);
                continue;
            }

            enabledSet.Remove(trimmed);
        }
    }

    /// <summary>BoundSeams 仅来自 HostDefaultSeams + UserSeams（用户覆盖宿主）。</summary>
    private static Dictionary<string, string> ResolveSeamsMap(SettlementInput input)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (input.HostDefaultSeams is not null)
        {
            foreach (var (key, value) in input.HostDefaultSeams)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                map[key.Trim()] = value.Trim();
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
    /// 配置绑定的提供方必须 ∈ bootEnabled（enabled）。
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

                if (!enabled.Contains(configuredId))
                {
                    throw new SettlementException(
                        $"seam '{seam}' 绑定到 '{configuredId}' 但该模块未在 bootEnabled 中，拒绝启动。");
                }

                if (providersEnabled.Length == 1 &&
                    !string.Equals(providersEnabled[0].Id, configuredId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SettlementException(
                        $"seam '{seam}' 配置绑定 '{configuredId}'，但启用集中的提供方是 '{providersEnabled[0].Id}'，拒绝启动。");
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
