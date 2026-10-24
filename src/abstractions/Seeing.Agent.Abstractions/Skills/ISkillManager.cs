namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 技能管理器契约 — 只读投影：技能可见性的唯一真相源是 <see cref="ISkillCatalog"/>。
/// <para>写面成员（<see cref="AddSearchDirectory"/> / <see cref="DiscoverSkillsAsync"/> /
/// <see cref="RegisterEmbeddedSkill"/> / <see cref="Unregister"/>）保留签名但标注
/// <c>[Obsolete(error: true)]</c>：类型系统必须拦住「第二条写路径」，删除签名反而会让调用点悄悄改走其它入口。</para>
/// </summary>
public interface ISkillManager
{
    /// <summary>获取所有技能搜索目录</summary>
    IReadOnlyList<string> GetSkillDirectories();

    /// <summary>添加技能搜索目录</summary>
    [Obsolete("请使用 ISkillCatalog", error: true)]
    void AddSearchDirectory(string directory);

    /// <summary>发现并加载技能</summary>
    [Obsolete("请使用 ISkillCatalog", error: true)]
    Task DiscoverSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>按名称获取技能信息（禁用的返回 null）</summary>
    SkillInfo? GetSkillInfo(string name);

    /// <summary>获取全部技能信息（仅启用的）</summary>
    IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfos();

    /// <summary>获取全部技能信息（含被禁用的，供管理界面使用）</summary>
    IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfosIncludingDisabled();

    /// <summary>注册内嵌技能（如模块 EmbeddedResource）</summary>
    [Obsolete("请使用 ISkillCatalog", error: true)]
    void RegisterEmbeddedSkill(SkillInfo skillInfo);

    /// <summary>注销技能（模块 Deactivate 时对称撤销内嵌技能）</summary>
    /// <param name="name">技能名称</param>
    /// <returns>是否成功注销</returns>
    [Obsolete("请使用 ISkillCatalog", error: true)]
    bool Unregister(string name);
}
