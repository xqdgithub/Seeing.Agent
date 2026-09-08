namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置节适用范围。
/// </summary>
public enum ConfigScope
{
    /// <summary>用户级与项目级均支持。</summary>
    Both,

    /// <summary>仅用户级（~/.seeing/）。</summary>
    UserOnly,

    /// <summary>仅项目级（{WorkspaceRoot}/.seeing/）。</summary>
    ProjectOnly
}

/// <summary>
/// 配置节元信息：描述键名、存储文件、适用范围与值类型。
/// </summary>
public sealed record ConfigSectionMeta(
    string Key,
    string FileName,
    ConfigScope Scope,
    Type SectionType,
    object? DefaultValue = null);
