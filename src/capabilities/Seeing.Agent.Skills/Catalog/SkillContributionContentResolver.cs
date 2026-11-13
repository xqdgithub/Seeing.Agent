using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// 贡献技能加载期回源的唯一出口（命令与工具共用）。
/// </summary>
internal static class SkillContributionContentResolver
{
    /// <summary>回源取 Markdown 正文；登记表缺失或源返回空则返回 null。</summary>
    public static async Task<string?> LoadBodyAsync(
        ISkillContributionRegistry? registry,
        string key,
        CancellationToken ct)
    {
        if (registry is null || string.IsNullOrWhiteSpace(key))
            return null;

        var content = await registry.LoadAsync(key, ct).ConfigureAwait(false);
        if (content is null || string.IsNullOrWhiteSpace(content.MarkdownBody))
            return null;

        return content.MarkdownBody;
    }
}
