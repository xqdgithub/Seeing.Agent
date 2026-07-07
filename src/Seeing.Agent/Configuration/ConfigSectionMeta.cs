namespace Seeing.Agent.Configuration;

/// <summary>
/// 配置节元信息 - 描述配置节的存储位置、适用范围等属性
/// </summary>
public sealed class ConfigSectionMeta
{
    /// <summary>配置节名称（如 Gateway、Scheduler）</summary>
    public string SectionName { get; }
    
    /// <summary>配置文件名（如 seeing.json、mcp.json）</summary>
    public string FileName { get; }
    
    /// <summary>配置适用范围</summary>
    public ConfigScope Scope { get; }
    
    /// <summary>配置值类型</summary>
    public Type ValueType { get; }
    
    /// <summary>范围限制原因说明（仅 ProjectOnly 时需要）</summary>
    public string? ScopeReason { get; }
    
    /// <summary>UI 显示名称</summary>
    public string DisplayName { get; }
    
    /// <summary>UI 显示顺序</summary>
    public int DisplayOrder { get; }
    
    /// <summary>
    /// 创建配置节元信息
    /// </summary>
    public ConfigSectionMeta(
        string sectionName,
        string fileName,
        ConfigScope scope,
        Type valueType,
        string? scopeReason = null,
        string? displayName = null,
        int displayOrder = 0)
    {
        SectionName = sectionName;
        FileName = fileName;
        Scope = scope;
        ValueType = valueType;
        ScopeReason = scopeReason;
        DisplayName = displayName ?? sectionName;
        DisplayOrder = displayOrder;
    }
}