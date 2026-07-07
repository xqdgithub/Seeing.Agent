namespace Seeing.Agent.Configuration;

/// <summary>
/// 配置范围不匹配异常 - 当尝试将 ProjectOnly 配置保存到 User 级时抛出
/// </summary>
public sealed class ConfigScopeException : InvalidOperationException
{
    /// <summary>配置节名称</summary>
    public string SectionName { get; }
    
    /// <summary>尝试保存的层级</summary>
    public ConfigLevel AttemptedLevel { get; }
    
    /// <summary>预期的范围</summary>
    public ConfigScope ExpectedScope { get; }
    
    /// <summary>
    /// 创建配置范围异常
    /// </summary>
    public ConfigScopeException(
        string sectionName,
        ConfigLevel attemptedLevel,
        ConfigScope expectedScope,
        string? reason = null) 
        : base(BuildMessage(sectionName, attemptedLevel, expectedScope, reason))
    {
        SectionName = sectionName;
        AttemptedLevel = attemptedLevel;
        ExpectedScope = expectedScope;
    }
    
    private static string BuildMessage(
        string sectionName,
        ConfigLevel attemptedLevel,
        ConfigScope expectedScope,
        string? reason)
    {
        var levelName = attemptedLevel == ConfigLevel.User ? "用户级" : "项目级";
        var scopeDesc = expectedScope == ConfigScope.ProjectOnly 
            ? "仅支持项目级" 
            : "支持用户级和项目级";
        
        return $"配置节 '{sectionName}' {scopeDesc}保存，无法保存到{levelName}。原因：{reason ?? "该配置与项目上下文绑定"}";
    }
}