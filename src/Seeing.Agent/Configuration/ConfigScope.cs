namespace Seeing.Agent.Configuration;

/// <summary>
/// 配置节适用范围
/// </summary>
public enum ConfigScope
{
    /// <summary>用户级和项目级都支持</summary>
    Both = 0,

    /// <summary>仅项目级</summary>
    ProjectOnly = 1,

    /// <summary>仅用户级</summary>
    UserOnly = 2
}
