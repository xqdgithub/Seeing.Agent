namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 技能管理器契约 - 技能发现与加载
/// </summary>
public interface ISkillManager
{
    /// <summary>获取所有技能搜索目录</summary>
    IReadOnlyList<string> GetSkillDirectories();

    /// <summary>添加技能搜索目录</summary>
    void AddSearchDirectory(string directory);

    /// <summary>发现并加载技能</summary>
    Task DiscoverSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>获取全部技能信息</summary>
    IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfos();
}