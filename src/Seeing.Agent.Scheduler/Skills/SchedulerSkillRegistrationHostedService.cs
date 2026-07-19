using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Skills;

namespace Seeing.Agent.Scheduler.Skills;

/// <summary>启动时从嵌入资源注册 scheduler cron Skills。</summary>
public sealed class SchedulerSkillRegistrationHostedService : IHostedService
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IServiceProvider _services;
    private readonly ILogger<SchedulerSkillRegistrationHostedService> _logger;

    public SchedulerSkillRegistrationHostedService(
        IServiceProvider services,
        ILogger<SchedulerSkillRegistrationHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var skillManager = _services.GetService<SkillManager>();
        if (skillManager is null)
        {
            _logger.LogWarning("SkillManager not registered; skipping scheduler skill registration");
            return Task.CompletedTask;
        }

        var assembly = typeof(SchedulerSkillRegistrationHostedService).Assembly;
        var registered = 0;

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
                    _logger.LogWarning("Failed to parse embedded skill resource: {Resource}", resourceName);
                    continue;
                }

                skill.Location = $"embedded://scheduler/{skill.Name}";
                skillManager.RegisterSkill(skill);
                registered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load embedded skill resource: {Resource}", resourceName);
            }
        }

        _logger.LogInformation("Registered {Count} embedded scheduler skills", registered);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
