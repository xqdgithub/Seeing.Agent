using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Scheduler.Skills;

/// <summary>Scheduler 内嵌 cron Skill 注册器 — 供模块 Activate/Deactivate 对称调用（写入口为 ISkillCatalog）。</summary>
internal static class SchedulerSkillRegistrar
{
    /// <summary>注册通道的提供方标识。</summary>
    public const string ProviderId = "scheduler";

    private static readonly Regex FrontmatterRegex = new(
        @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>扫描程序集内嵌 SKILL.md 并注册，返回已注册技能名列表。</summary>
    public static async Task<IReadOnlyList<string>> RegisterAsync(
        ISkillCatalog catalog,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var assembly = typeof(SchedulerSkillRegistrar).Assembly;
        var registered = new List<string>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                !resourceName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer names under Skills.* that end with SKILL.md
            if (resourceName.IndexOf(".Skills.", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                var skill = ParseSkill(content);
                if (skill is null)
                {
                    logger.LogWarning("Failed to parse embedded skill resource: {Resource}", resourceName);
                    continue;
                }

                skill.Location = $"scheduler/{skill.Name}";
                if (await catalog.RegisterAsync(skill, ProviderId, cancellationToken).ConfigureAwait(false))
                    registered.Add(skill.Name);
                else
                    logger.LogWarning("Embedded skill rejected (duplicate name): {Name} 来源={Resource}", skill.Name, resourceName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load embedded skill resource: {Resource}", resourceName);
            }
        }

        logger.LogInformation("Registered {Count} embedded scheduler skills", registered.Count);
        return registered;
    }

    /// <summary>注销先前注册的内嵌技能。</summary>
    public static async Task UnregisterAsync(
        ISkillCatalog catalog,
        IEnumerable<string> names,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        foreach (var name in names)
        {
            if (!await catalog.UnregisterAsync(name, cancellationToken).ConfigureAwait(false))
                logger.LogDebug("Unregister embedded skill missed the registration channel: {Name}", name);
        }
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
