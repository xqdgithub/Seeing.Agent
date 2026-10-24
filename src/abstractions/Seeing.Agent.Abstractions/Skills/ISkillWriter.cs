using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 待写入的技能草稿：frontmatter 元数据 + 正文。
/// <para>只承载内容，不承载位置；目标层级由调用方在写方法上显式给出。</para>
/// </summary>
/// <param name="Name">技能名称（必须满足 <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>）。</param>
/// <param name="Description">技能描述（必填，进入 frontmatter）。</param>
/// <param name="Content">技能正文（不含 frontmatter）。</param>
/// <param name="Metadata">附加 frontmatter 键值（可选，原样写入）。</param>
public sealed record SkillDraft(
    string Name,
    string Description,
    string Content,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// 技能写入结果：失败时带可直接展示给用户的原因。
/// </summary>
/// <param name="Success">是否成功（成功即目录快照已反映本次变更）。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
/// <param name="Entry">写入后目录中的条目（删除成功时为 null）。</param>
public sealed record SkillWriteResult(bool Success, string? Error, SkillCatalogEntry? Entry);

/// <summary>
/// 技能写入服务：文件通道的唯一写入口（注册通道技能只读）。
/// <para>所有方法在返回前刷新 <see cref="ISkillCatalog"/>，因此返回值即目录的最终状态；
/// 路径安全（名称正则 / 根内 / 禁 <c>..</c>）与原子写由实现保证。</para>
/// </summary>
public interface ISkillWriter
{
    /// <summary>在指定层级新建技能目录并写入 SKILL.md。</summary>
    /// <param name="draft">技能草稿。</param>
    /// <param name="level">目标层级（决定落盘根目录）。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> CreateAsync(
        SkillDraft draft,
        ConfigLevel level,
        bool overwrite,
        CancellationToken ct = default);

    /// <summary>
    /// 原样写入 SKILL.md（保留未知 frontmatter）；用于在线安装的「仅 Markdown」分支。
    /// </summary>
    /// <param name="name">技能名称（同时作为目录名）。</param>
    /// <param name="rawContent">SKILL.md 全文（逐字节写入）。</param>
    /// <param name="level">目标层级。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> WriteMarkdownAsync(
        string name,
        string rawContent,
        ConfigLevel level,
        bool overwrite,
        CancellationToken ct = default);

    /// <summary>就地更新已有技能：只替换 SKILL.md，保留目录内其它文件。</summary>
    /// <param name="name">技能名称（定位现有条目）。</param>
    /// <param name="draft">新的技能草稿（名称不得与原名称不同）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> UpdateAsync(
        string name,
        SkillDraft draft,
        CancellationToken ct = default);

    /// <summary>删除文件通道技能所在目录；注册通道技能拒绝删除。</summary>
    /// <param name="name">技能名称。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 从 ZIP 安装技能：只提取 <paramref name="subdirectoryPrefix"/> 之下的条目并剥离该前缀，
    /// 越出目标目录的条目（zip-slip）整单拒绝。
    /// </summary>
    /// <param name="name">技能名称（同时作为目录名）。</param>
    /// <param name="zip">ZIP 内容流。</param>
    /// <param name="level">目标层级。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <param name="subdirectoryPrefix">仅提取该前缀之下的条目（如仓库根 / 技能目录）；null 表示整包。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> InstallZipAsync(
        string name,
        Stream zip,
        ConfigLevel level,
        bool overwrite,
        string? subdirectoryPrefix,
        CancellationToken ct = default);

    /// <summary>
    /// 设置技能启用状态：层级由调用方显式给出（UI 预选当前生效层级）。
    /// <para>启用时清除<b>所有</b>层级的禁用项；禁用时只写指定层级。生效判定保持并集语义。</para>
    /// </summary>
    /// <param name="name">技能名称。</param>
    /// <param name="enabled">true = 启用（清除所有层级禁用项）；false = 禁用（写指定层级）。</param>
    /// <param name="level">写盘层级。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SkillWriteResult> SetEnabledAsync(
        string name,
        bool enabled,
        ConfigLevel level,
        CancellationToken ct = default);
}
