using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 场景目录：内置预设 ∪ seeing.json <c>Scenarios</c> 自定义（同名时配置覆盖内置）。
/// </summary>
public interface IScenarioCatalog
{
    /// <summary>全部场景（内置在前，再按名称排序的自定义/覆盖）。</summary>
    IReadOnlyList<ScenarioDefinition> ListAll();

    /// <summary>按名查找；未知返回 null。</summary>
    ScenarioDefinition? Get(string name);

    /// <summary>是否为内置名（即使被配置覆盖仍为 true）。</summary>
    bool IsBuiltInName(string name);

    /// <summary>配置中是否存在该 key（自定义或覆盖）。</summary>
    bool HasConfiguredOverride(string name);
}

/// <inheritdoc />
public sealed class ScenarioCatalog : IScenarioCatalog
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;

    public ScenarioCatalog(IOptionsMonitor<SeeingAgentOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScenarioDefinition> ListAll()
    {
        var configured = _options.CurrentValue.Scenarios
                         ?? new Dictionary<string, ScenarioConfig>(StringComparer.OrdinalIgnoreCase);

        var result = new List<ScenarioDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtin in BuiltInScenarios.All)
        {
            seen.Add(builtin.Name);
            result.Add(Resolve(builtin.Name, configured) ?? builtin);
        }

        foreach (var key in configured.Keys
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(key.Trim()))
                continue;
            var def = Resolve(key.Trim(), configured);
            if (def is not null)
                result.Add(def);
        }

        return result;
    }

    /// <inheritdoc />
    public ScenarioDefinition? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var configured = _options.CurrentValue.Scenarios
                         ?? new Dictionary<string, ScenarioConfig>(StringComparer.OrdinalIgnoreCase);
        return Resolve(name.Trim(), configured);
    }

    /// <inheritdoc />
    public bool IsBuiltInName(string name) =>
        !string.IsNullOrWhiteSpace(name) && BuiltInScenarios.TryGet(name.Trim()) is not null;

    /// <inheritdoc />
    public bool HasConfiguredOverride(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var map = _options.CurrentValue.Scenarios;
        return map is not null && map.ContainsKey(name.Trim());
    }

    private static ScenarioDefinition? Resolve(
        string name,
        IReadOnlyDictionary<string, ScenarioConfig> configured)
    {
        if (configured.TryGetValue(name, out var cfg) && cfg is not null)
            return cfg.ToDefinition(name);

        return BuiltInScenarios.TryGet(name);
    }
}
