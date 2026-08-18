namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供 Skill 搜索路径的扩展
/// </summary>
public interface ISkillPathExtension
{
    /// <summary>提供的 Skill 搜索路径</summary>
    IEnumerable<string> GetSkillPaths();
}