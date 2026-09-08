namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// 在线技能解析结果
/// </summary>
public class OnlineSkillResult
{
    /// <summary>技能名称</summary>
    public string Name { get; set; } = "";

    /// <summary>技能描述</summary>
    public string Description { get; set; } = "";

    /// <summary>作者</summary>
    public string? Author { get; set; }

    /// <summary>版本</summary>
    public string? Version { get; set; }

    /// <summary>标签列表</summary>
    public List<string>? Tags { get; set; }

    /// <summary>SKILL.md 完整内容</summary>
    public string Content { get; set; } = "";

    /// <summary>来源 URL</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>
    /// ZIP 下载链接（包含所有技能文件）
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// 技能文件列表（相对路径，如 "scripts/helper.py", "references/api.md"）
    /// </summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// 源仓库 URL（如 GitHub 仓库地址）
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// 是否有额外文件（除了 SKILL.md）
    /// </summary>
    public bool HasAdditionalFiles => Files.Count > 0;
}

