using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 技能来源通道：注册（插件 / 内嵌资源）优先于文件系统。
/// </summary>
public enum SkillSource
{
    /// <summary>运行时注册（插件 / 内嵌资源），优先级最高。</summary>
    Registered,

    /// <summary>项目级文件技能（工作区内的技能根）。</summary>
    ProjectFile,

    /// <summary>用户级文件技能（主目录下的技能根）。</summary>
    UserFile
}

/// <summary>
/// 技能目录条目：技能信息 + 来源事实。
/// <para><see cref="DirectoryPath"/> 是构建快照时写入的数据字段（不是计算属性），
/// 使条目自身即可回答「从哪个目录来」。</para>
/// </summary>
/// <param name="Info">技能信息（名称 / 描述 / 正文 / 元数据）。</param>
/// <param name="Source">来源通道。</param>
/// <param name="ProviderId">注册通道的提供方标识；文件通道为 null。</param>
/// <param name="DirectoryPath">技能所在目录（文件通道为 SKILL.md 所在目录；注册通道可为空串）。</param>
public sealed record SkillCatalogEntry(
    SkillInfo Info,
    SkillSource Source,
    string? ProviderId,
    string DirectoryPath);

/// <summary>
/// 同名技能被忽略的记录（冲突不静默）。
/// </summary>
/// <param name="Name">冲突的技能名称。</param>
/// <param name="Winner">胜出来源。</param>
/// <param name="WinnerOrigin">胜出来源路径 / 来源描述。</param>
/// <param name="Loser">落选来源。</param>
/// <param name="LoserOrigin">落选来源路径 / 来源描述。</param>
public sealed record SkillConflict(
    string Name,
    SkillSource Winner,
    string WinnerOrigin,
    SkillSource Loser,
    string LoserOrigin);

/// <summary>
/// 技能加载问题（解析失败 / 读取失败）。
/// </summary>
/// <param name="Path">出错文件路径。</param>
/// <param name="Reason">失败原因（可直接展示给用户）。</param>
public sealed record SkillLoadIssue(string Path, string Reason);

/// <summary>
/// 技能目录的不可变快照。<see cref="Version"/> 单调递增，替换即发布。
/// </summary>
/// <param name="Version">快照版本（每次发布 +1）。</param>
/// <param name="Entries">可调用技能条目（注册通道优先，其余按根优先级）。</param>
/// <param name="DisabledNames">被禁用技能名称（用户级 ∪ 项目级）。</param>
/// <param name="DisabledScopes">禁用项上报的层级（项目级优先于用户级）。</param>
/// <param name="Conflicts">被忽略的同名技能记录。</param>
/// <param name="Issues">加载问题（解析 / 读取失败）。</param>
public sealed record SkillCatalogSnapshot(
    long Version,
    IReadOnlyList<SkillCatalogEntry> Entries,
    IReadOnlySet<string> DisabledNames,
    IReadOnlyDictionary<string, ConfigLevel> DisabledScopes,
    IReadOnlyList<SkillConflict> Conflicts,
    IReadOnlyList<SkillLoadIssue> Issues);

/// <summary>
/// 技能目录：技能可见性的唯一真相源（不可变快照 + 单调版本 + 变更事件）。
/// </summary>
public interface ISkillCatalog
{
    /// <summary>当前快照（无空窗：替换是原子的）。</summary>
    SkillCatalogSnapshot Current { get; }

    /// <summary>
    /// 当前生效的技能根（优先级从高到低，已展开为绝对路径）。
    /// <para>这是「技能根」的<b>唯一</b>定义：加载器写入根、写入服务的根内判定都必须读它，
    /// 不得在别处重新推导（否则会出现「能列出、能编辑、删不掉」的读写根分裂）。</para>
    /// </summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>当前快照中的可用条目（条目 − 禁用集合；「什么算可用技能」的唯一投影）。</summary>
    IReadOnlyList<SkillCatalogEntry> EnabledEntries { get; }

    /// <summary>
    /// 按给定快照计算可用条目投影：执行级钉住的旧快照也复用同一实现，
    /// 避免「可用技能」出现第二份判断（漏一次禁用判断就是一次可见性回归）。
    /// </summary>
    IReadOnlyList<SkillCatalogEntry> GetEnabledEntries(SkillCatalogSnapshot snapshot);

    /// <summary>订阅快照变更；返回的 <see cref="IDisposable"/> 用于退订。</summary>
    IDisposable Subscribe(Action<SkillCatalogSnapshot> onChanged);

    /// <summary>按名称查找可调用技能；不存在返回 null。</summary>
    SkillCatalogEntry? Find(string name);

    /// <summary>技能是否可用（存在且未被任何层级禁用）。</summary>
    bool IsEnabled(string name);

    /// <summary>廉价指纹校验：文件未变时直接返回当前快照，变化时才重建。</summary>
    Task<SkillCatalogSnapshot> EnsureFreshAsync(CancellationToken ct = default);

    /// <summary>强制重扫文件通道并发布新快照（版本 +1）。</summary>
    Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default);

    /// <summary>注册一个技能（同名已注册时拒绝，返回 false）。</summary>
    Task<bool> RegisterAsync(SkillInfo info, string providerId, CancellationToken ct = default);

    /// <summary>注销已注册技能（不存在时返回 false）。</summary>
    Task<bool> UnregisterAsync(string name, CancellationToken ct = default);
}
