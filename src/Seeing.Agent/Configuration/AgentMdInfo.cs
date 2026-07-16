namespace Seeing.Agent.Configuration;

/// <summary>
/// Agent MD 配置信息（用于 UI 展示）
/// </summary>
public class AgentMdInfo
{
    /// <summary>
    /// Agent 名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 配置层级（用户级/项目级）
    /// </summary>
    public ConfigLevel Level { get; set; }

    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// 最后修改时间
    /// </summary>
    public DateTimeOffset LastModified { get; set; }
}
