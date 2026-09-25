using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Tools.SystemOne.Skills;

/// <summary>SystemOne 内嵌 Skill 注册器 — 供模块 Activate/Deactivate 对称调用。</summary>
internal static class SystemOneSkillRegistrar
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>扫描程序集内嵌 SKILL.md 并注册，返回已注册技能名列表。</summary>
    public static IReadOnlyList<string> Register(ISkillManager skillManager, ILogger logger)
    {
        var assembly = typeof(SystemOneSkillRegistrar).Assembly;
        var registered = new List<string>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (resourceName.IndexOf(".Skills.", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                using var reader = new StreamReader(stream);
                var skill = ParseSkill(reader.ReadToEnd());
                if (skill is null)
                {
                    logger.LogWarning("解析内嵌 skill 失败：{Resource}", resourceName);
                    continue;
                }

                skill.Location = $"systemone/{skill.Name}";
                skillManager.RegisterEmbeddedSkill(skill);
                registered.Add(skill.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "加载内嵌 skill 失败：{Resource}", resourceName);
            }
        }

        logger.LogInformation("已注册 {Count} 个内嵌 SystemOne skill", registered.Count);
        return registered;
    }

    /// <summary>注销先前注册的内嵌技能。</summary>
    public static void Unregister(ISkillManager skillManager, IEnumerable<string> names)
    {
        foreach (var name in names)
            skillManager.Unregister(name);
    }

    private static SkillInfo? ParseSkill(string content)
    {
        var match = FrontmatterRegex.Match(content);
        if (!match.Success)
            return null;

        var frontmatter = match.Groups[1].Value;
        string? name = null;
        string? description = null;

        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = line["name:".Length..].Trim().Trim('"', '\'');
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                description = line["description:".Length..].Trim().Trim('"', '\'');
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
            return null;

        return new SkillInfo
        {
            Name = name,
            Description = description,
            Content = content[match.Length..].Trim()
        };
    }
}
