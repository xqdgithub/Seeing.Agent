using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Skills.Configuration;

namespace Seeing.Agent.Skills;

/// <summary>技能组件加载器 — 由 <see cref="SkillsModule"/> 登记到 <see cref="IComponentManager"/>。</summary>
public sealed class SkillLoader : IComponentLoader
{
    public string Type => "Skill";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var skillManager = services.GetRequiredService<SkillManager>();
        var skillsOptions = services.GetService<IOptionsMonitor<SkillsOptions>>()?.CurrentValue
            ?? services.GetService<IOptions<SkillsOptions>>()?.Value;
        var directories = services.GetService<ISeeingDirectories>();
        var projectRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : ResolveProjectRoot(directories);

        skillManager.ResetSearchDirectoriesToDefault();

        if (directories is not null)
            AddIfExists(skillManager, Path.Combine(directories.UserSeeingDirectory, "skills"));

        if (skillsOptions?.Paths != null)
        {
            foreach (var p in skillsOptions.Paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    AddIfExists(skillManager, ExpandPath(p.Trim(), projectRoot));
            }
        }

        await skillManager.DiscoverSkillsAsync(cancellationToken);
        await skillManager.LoadSkillStateAsync(cancellationToken);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = skillManager.GetAllSkillInfos().Count,
            Details = skillManager.GetAllSkillInfos().Keys.ToList()
        };
    }

    public async Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var skillManager = services.GetRequiredService<SkillManager>();
        skillManager.ClearSkillInfos();
        return await LoadAsync(services, workspaceRoot, cancellationToken);
    }

    private static void AddIfExists(SkillManager manager, string dir)
    {
        if (Directory.Exists(dir))
            manager.AddSearchDirectory(dir);
    }

    private static string ResolveProjectRoot(ISeeingDirectories? directories)
    {
        if (directories is null)
            return Directory.GetCurrentDirectory();

        var projectSeeing = directories.ProjectSeeingDirectory;
        var parent = Directory.GetParent(projectSeeing);
        return parent?.FullName ?? projectSeeing;
    }

    private static string ExpandPath(string path, string workspaceRoot)
    {
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path.Substring(1).TrimStart('/', '\\')));
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }
}
