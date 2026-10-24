using System.Text;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// 清单条目：渲染与 diff 的最小单位（<c>id</c> 为技能名，<c>name</c> 为展示名）。
/// <para>单独存在的原因：增量渲染的 <c>previous</c> 只来自落盘的 canonical（不含 Payload），
/// 需要一种与目录条目解耦、可从 canonical 重建的稳定形状。</para>
/// </summary>
/// <param name="Id">条目标识（技能名，ordinal 排序与 diff 的键）。</param>
/// <param name="Name">展示名。</param>
/// <param name="Description">说明（空表示不参与渲染）。</param>
public readonly record struct SkillListEntry(string Id, string Name, string Description);

/// <summary>
/// 技能清单文本的唯一格式化入口：系统基线（<c>## Skills</c> 区块）、<c>/skill</c> 无参数列表、
/// <c>/skills</c> 列表、<c>skill</c> 工具的错误提示全部复用本类，禁止旁路拼接。
/// <para>排序固定为 <c>Info.Name</c> ordinal；同一份输入重复渲染逐字节相等（前缀缓存的前提）。</para>
/// </summary>
public static class SkillCatalogFormatter
{
    /// <summary>基线区块标题（基线拥有整个区块，其它渲染不得自带同名标题）。</summary>
    public const string Heading = "## Skills";

    /// <summary>移除叙述必须包含的判定短语（模型据此丢弃已不可用的技能）。</summary>
    public const string RemovedPhrase = "must not be used";

    private const string UsageLine =
        "按名加载技能全文（`skill` 工具或 `/<名称>` 命令）；只使用下列名称，未列出的技能不可用。";

    private const string ListHeading = "**Available Skills**";

    private const string NoSkillsText = "No skills available.";

    /// <summary>
    /// 初始基线：<c>## Skills</c> + 一行使用说明 + 条目（<c>- name: description</c>）。
    /// <para>无 description 的条目不参与渲染；全部条目都无说明时返回空串（不产出空区块）。</para>
    /// </summary>
    public static string RenderBaseline(IReadOnlyList<SkillCatalogEntry> entries) =>
        RenderBaseline(ToListEntries(entries));

    /// <summary>初始基线（从清单条目渲染，供 canonical 重建路径复用同一份文本）。</summary>
    public static string RenderBaseline(IReadOnlyList<SkillListEntry> entries)
    {
        var rendered = Sort(entries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Description))
            .ToList();

        if (rendered.Count == 0)
            return string.Empty;

        var lines = new List<string>
        {
            Heading,
            string.Empty,
            UsageLine
        };

        foreach (var entry in rendered)
        {
            lines.Add(string.Empty);
            lines.Add($"- {entry.Name}: {CollapseWhitespace(entry.Description)}");
        }

        // 显式使用 \n：提示词文本不随平台变化（字节稳定的实现细节，而非依赖 Environment.NewLine）
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 列表形态（<c>/skill</c> 无参数、<c>/skills</c>、工具错误提示共用）。
    /// 与基线不同，这里保留无说明条目（管理视角需要看到全部可调用技能）。
    /// </summary>
    public static string RenderList(IReadOnlyList<SkillCatalogEntry> entries)
    {
        var sorted = Sort(entries);
        if (sorted.Count == 0)
            return NoSkillsText;

        var lines = new List<string>
        {
            ListHeading,
            string.Empty
        };

        foreach (var entry in sorted)
        {
            lines.Add(string.IsNullOrWhiteSpace(entry.Info.Description)
                ? $"- **{entry.Info.Name}**"
                : $"- **{entry.Info.Name}**: {CollapseWhitespace(entry.Info.Description)}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 增量叙述：新增 / 说明变更 / 移除。无变化时返回空串（调用方据此不推进已叙述值）。
    /// <para>移除文案必须含 <see cref="RemovedPhrase"/>，避免模型继续调用已不存在的技能。</para>
    /// </summary>
    public static string RenderDelta(
        IReadOnlyList<SkillListEntry> previous,
        IReadOnlyList<SkillListEntry> current)
    {
        var previousById = new Dictionary<string, SkillListEntry>(StringComparer.Ordinal);
        foreach (var entry in previous)
            previousById[entry.Id] = entry;

        var currentById = new Dictionary<string, SkillListEntry>(StringComparer.Ordinal);
        foreach (var entry in current)
            currentById[entry.Id] = entry;

        var lines = new List<string>();

        foreach (var entry in Sort(current))
        {
            var description = CollapseWhitespace(entry.Description);

            if (!previousById.TryGetValue(entry.Id, out var before))
            {
                lines.Add(description.Length == 0
                    ? $"- 新增技能：{entry.Name}（无说明）"
                    : $"- 新增技能：{entry.Name}：{description}");
                continue;
            }

            var beforeDescription = CollapseWhitespace(before.Description);
            if (!string.Equals(beforeDescription, description, StringComparison.Ordinal))
            {
                lines.Add(string.IsNullOrEmpty(beforeDescription)
                    ? $"- 技能 {entry.Name} 的说明已更新：{description}"
                    : $"- 技能 {entry.Name} 的说明已更新：{beforeDescription} → {description}");
            }
        }

        foreach (var entry in Sort(previous))
        {
            if (currentById.ContainsKey(entry.Id))
                continue;

            lines.Add($"- 技能 {entry.Name} 已不存在，{RemovedPhrase}（不得再调用）。");
        }

        return lines.Count == 0
            ? string.Empty
            : "技能清单已变化：\n\n" + string.Join('\n', lines);
    }

    /// <summary>把目录条目投影成清单条目（保持输入顺序，排序由各渲染方法负责）。</summary>
    public static IReadOnlyList<SkillListEntry> ToListEntries(IEnumerable<SkillCatalogEntry> entries)
    {
        var result = new List<SkillListEntry>();
        foreach (var entry in entries)
            result.Add(new SkillListEntry(entry.Info.Name, entry.Info.Name, entry.Info.Description));

        return result;
    }

    /// <summary>按展示名 ordinal 排序（确定性顺序的唯一实现点）。</summary>
    private static List<SkillListEntry> Sort(IEnumerable<SkillListEntry> entries) =>
        entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();

    /// <summary>按名称 ordinal 排序目录条目。</summary>
    private static List<SkillCatalogEntry> Sort(IReadOnlyList<SkillCatalogEntry> entries) =>
        entries.OrderBy(entry => entry.Info.Name, StringComparer.Ordinal).ToList();

    /// <summary>说明压成单行：多行说明会破坏「一行一条目」的清单结构。</summary>
    private static string CollapseWhitespace(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        var sb = new StringBuilder(description.Length);
        var lastWasSpace = false;
        foreach (var ch in description.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    sb.Append(' ');

                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString();
    }
}
