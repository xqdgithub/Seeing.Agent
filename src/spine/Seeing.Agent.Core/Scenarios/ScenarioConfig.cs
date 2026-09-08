namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// seeing.json <c>Scenarios</c> 节中单条场景的可序列化形状。
/// </summary>
public sealed class ScenarioConfig
{
    /// <summary>启用模块 id 列表。</summary>
    public List<string> Modules { get; set; } = [];

    /// <summary>默认 Agent 名。</summary>
    public string? DefaultAgent { get; set; }

    /// <summary>seam 名 → 提供方模块 id。</summary>
    public Dictionary<string, string> Seams { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>禁用工具 id（扁平字段；与 <see cref="Tools"/> 并存时合并）。</summary>
    public List<string> ToolsDisabled { get; set; } = [];

    /// <summary>可选嵌套 tools 节（spec：<c>tools.disabled</c>）。</summary>
    public ScenarioToolsConfig? Tools { get; set; }

    /// <summary>转为不可变 <see cref="ScenarioDefinition"/>。</summary>
    public ScenarioDefinition ToDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ToolsDisabled)
        {
            if (!string.IsNullOrWhiteSpace(id))
                disabled.Add(id.Trim());
        }

        if (Tools?.Disabled is { Count: > 0 } nested)
        {
            foreach (var id in nested)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    disabled.Add(id.Trim());
            }
        }

        var modules = Modules
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var seams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Seams)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            seams[key.Trim()] = value.Trim();
        }

        return new ScenarioDefinition(
            Name: name.Trim(),
            Modules: modules,
            DefaultAgent: string.IsNullOrWhiteSpace(DefaultAgent) ? "general" : DefaultAgent.Trim(),
            Seams: seams,
            ToolsDisabled: disabled.ToArray());
    }

    /// <summary>从定义创建可写配置副本。</summary>
    public static ScenarioConfig FromDefinition(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ScenarioConfig
        {
            Modules = definition.Modules.ToList(),
            DefaultAgent = definition.DefaultAgent,
            Seams = definition.Seams.ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase),
            ToolsDisabled = definition.ToolsDisabled.ToList(),
        };
    }
}

/// <summary>场景工具裁剪嵌套节。</summary>
public sealed class ScenarioToolsConfig
{
    public List<string> Disabled { get; set; } = [];
}
