using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// 「可用技能」投影的唯一实现：<see cref="SkillCatalogSnapshot.Entries"/> − <see cref="SkillCatalogSnapshot.DisabledNames"/>。
/// <para>此前该投影在目录、源、工具、命令、加载器、ACP、UI 等 8 处各写一遍，
/// 任一处漏掉禁用判断就会出现「已禁用但仍出现在清单 / 命令里」的回归；因此只保留这一份。</para>
/// <para>目录经 <see cref="ISkillCatalog.EnabledEntries"/> / <see cref="ISkillCatalog.GetEnabledEntries"/>
/// 对外暴露同一实现（Abstractions 保持零实现，快照本身不承载计算属性）。</para>
/// </summary>
internal static class SkillCatalogProjection
{
    /// <summary>按快照计算可用条目（保持快照内的原顺序）。</summary>
    public static IReadOnlyList<SkillCatalogEntry> EnabledEntries(SkillCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DisabledNames.Count == 0)
            return snapshot.Entries;

        var entries = new List<SkillCatalogEntry>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            if (!snapshot.DisabledNames.Contains(entry.Info.Name))
                entries.Add(entry);
        }

        return entries;
    }
}
