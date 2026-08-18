namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置级别
/// </summary>
public enum ConfigLevel
{
    /// <summary>用户级：~/.seeing/</summary>
    User,

    /// <summary>项目级：{WorkspaceRoot}/.seeing/</summary>
    Project
}