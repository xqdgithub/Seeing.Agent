namespace Seeing.Agent.Core.CapabilitySets;

/// <summary>
/// seeing.json <c>CapabilitySets</c> 节中单条能力集的可序列化形状。
/// </summary>
public sealed class CapabilitySetConfig
{
    /// <summary>模块 id 列表；可为 <c>["*"]</c> 表示全 Available（解析层展开）。</summary>
    public List<string> Modules { get; set; } = [];

    /// <summary>随该能力集扣除的模块 id（模块层黑名单）。</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>转为不可变 <see cref="CapabilitySetDefinition"/>。</summary>
    public CapabilitySetDefinition ToDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var modules = Modules
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var disabled = Disabled
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CapabilitySetDefinition(
            Name: name.Trim(),
            Modules: modules,
            Disabled: disabled);
    }

    /// <summary>从定义创建可写配置副本。</summary>
    public static CapabilitySetConfig FromDefinition(CapabilitySetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new CapabilitySetConfig
        {
            Modules = definition.Modules.ToList(),
            Disabled = definition.Disabled.ToList(),
        };
    }
}
