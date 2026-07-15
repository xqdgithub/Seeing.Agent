namespace Seeing.Agent.Configuration;

/// <summary>
/// Agent MD 配置变更事件参数
/// </summary>
public sealed class AgentConfigChangedEventArgs : EventArgs
{
    /// <summary>
    /// Agent 名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 配置层级
    /// </summary>
    public ConfigLevel Level { get; init; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public ConfigChangeAction Action { get; init; }
}

/// <summary>
/// 配置变更动作类型
/// </summary>
public enum ConfigChangeAction
{
    /// <summary>配置已创建</summary>
    Created,

    /// <summary>配置已更新</summary>
    Updated,

    /// <summary>配置已删除</summary>
    Deleted
}
