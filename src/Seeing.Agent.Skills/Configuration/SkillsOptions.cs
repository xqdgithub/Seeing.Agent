namespace Seeing.Agent.Skills.Configuration;

/// <summary>
/// 技能配置（JSON: SeeingAgent:Skills）
/// </summary>
public class SkillsOptions
{
    public const string SectionName = "Skills";

    /// <summary>本地技能路径列表</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>远程技能 URL 列表（index.json 格式）</summary>
    public List<string> Urls { get; set; } = new();
}
