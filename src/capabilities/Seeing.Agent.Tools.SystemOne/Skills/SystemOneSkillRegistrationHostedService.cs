using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Core.Tools.SystemOne.Skills;

/// <summary>启动时从嵌入资源注册 SystemOne Skill。</summary>
public sealed class SystemOneSkillRegistrationHostedService : IModuleHostedService, IHostedService
{
    /// <summary>所属模块 id。</summary>
    public const string ModuleIdValue = "systemone.tools";

    private static readonly Regex FrontmatterRegex = new(
        @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IServiceProvider _services;
    private readonly ILogger<SystemOneSkillRegistrationHostedService> _logger;
    private bool _running;

    /// <summary>注入服务定位器与日志。</summary>
    public SystemOneSkillRegistrationHostedService(
        IServiceProvider services,
        ILogger<SystemOneSkillRegistrationHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;

        var skillManager = _services.GetService<ISkillManager>();
        if (skillManager is null)
        {
            _logger.LogWarning("ISkillManager 未注册，跳过 SystemOne skill 注册");
            return Task.CompletedTask;
        }

        var assembly = typeof(SystemOneSkillRegistrationHostedService).Assembly;
        var registered = 0;

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
                    _logger.LogWarning("解析内嵌 skill 失败：{Resource}", resourceName);
                    continue;
                }

                skill.Location = $"systemone/{skill.Name}";
                skillManager.RegisterEmbeddedSkill(skill);
                registered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载内嵌 skill 失败：{Resource}", resourceName);
            }
        }

        _logger.LogInformation("已注册 {Count} 个内嵌 SystemOne skill", registered);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        return Task.CompletedTask;
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
