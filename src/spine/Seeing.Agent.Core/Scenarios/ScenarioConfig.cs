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

    /// <summary>
    /// 禁用工具 id（扁平字段；已弃用，请改用 <see cref="Tools"/>.<see cref="ScenarioToolsConfig.Disabled"/>）。
    /// 与 <see cref="Tools"/> 并存时合并。
    /// </summary>
    [Obsolete("改用 Tools.Disabled；扁平 ToolsDisabled 仅作迁移兼容。")]
    public List<string> ToolsDisabled { get; set; } = [];

    /// <summary>嵌套 tools 节（规范字段：<c>Tools.Disabled</c>）。</summary>
    public ScenarioToolsConfig? Tools { get; set; }

    /// <summary>
    /// 将弃用扁平 <see cref="ToolsDisabled"/> 合并进 <see cref="Tools"/>.<see cref="ScenarioToolsConfig.Disabled"/>。
    /// 加载/解析路径调用，保证定义与后续保存只消费规范字段。
    /// </summary>
    public void MergeFlatToolsDisabledIntoNested()
    {
#pragma warning disable CS0618 // ToolsDisabled 迁移兼容
        if (ToolsDisabled.Count == 0)
            return;

        Tools ??= new ScenarioToolsConfig();
        var set = new HashSet<string>(Tools.Disabled, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ToolsDisabled)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var trimmed = id.Trim();
            if (set.Add(trimmed))
                Tools.Disabled.Add(trimmed);
        }
#pragma warning restore CS0618
    }

    /// <summary>转为不可变 <see cref="ScenarioDefinition"/>。</summary>
    public ScenarioDefinition ToDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 加载路径：扁平 ToolsDisabled → Tools.Disabled
        MergeFlatToolsDisabledIntoNested();

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            Tools = new ScenarioToolsConfig
            {
                Disabled = definition.ToolsDisabled.ToList(),
            },
        };
    }
}

/// <summary>场景工具裁剪嵌套节。</summary>
public sealed class ScenarioToolsConfig
{
    /// <summary>需禁用的工具 ID 列表。</summary>
    public List<string> Disabled { get; set; } = [];
}
