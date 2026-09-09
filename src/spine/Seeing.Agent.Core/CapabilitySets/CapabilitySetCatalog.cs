using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.CapabilitySets;

/// <summary>
/// 能力集目录：内置预设 ∪ seeing.json <c>CapabilitySets</c>（同名时配置覆盖内置）。
/// </summary>
public interface ICapabilitySetCatalog
{
    /// <summary>全部能力集（内置在前，再按名称排序的自定义/覆盖）。</summary>
    IReadOnlyList<CapabilitySetDefinition> ListAll();

    /// <summary>按名查找；未知返回 null。</summary>
    CapabilitySetDefinition? Get(string name);

    /// <summary>是否为内置名（即使被配置覆盖仍为 true）。</summary>
    bool IsBuiltInName(string name);

    /// <summary>配置中是否存在该 key（自定义或覆盖）。</summary>
    bool HasConfiguredOverride(string name);
}

/// <inheritdoc />
public sealed class CapabilitySetCatalog : ICapabilitySetCatalog
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;

    public CapabilitySetCatalog(IOptionsMonitor<SeeingAgentOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilitySetDefinition> ListAll()
    {
        var configured = _options.CurrentValue.CapabilitySets
                         ?? new Dictionary<string, CapabilitySetConfig>(StringComparer.OrdinalIgnoreCase);

        var result = new List<CapabilitySetDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtin in BuiltInCapabilitySets.All)
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
    public CapabilitySetDefinition? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var configured = _options.CurrentValue.CapabilitySets
                         ?? new Dictionary<string, CapabilitySetConfig>(StringComparer.OrdinalIgnoreCase);
        return Resolve(name.Trim(), configured);
    }

    /// <inheritdoc />
    public bool IsBuiltInName(string name) =>
        !string.IsNullOrWhiteSpace(name) && BuiltInCapabilitySets.TryGet(name.Trim()) is not null;

    /// <inheritdoc />
    public bool HasConfiguredOverride(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var map = _options.CurrentValue.CapabilitySets;
        return map is not null && map.ContainsKey(name.Trim());
    }

    private static CapabilitySetDefinition? Resolve(
        string name,
        IReadOnlyDictionary<string, CapabilitySetConfig> configured)
    {
        if (configured.TryGetValue(name, out var cfg) && cfg is not null)
            return cfg.ToDefinition(name);

        return BuiltInCapabilitySets.TryGet(name);
    }
}
