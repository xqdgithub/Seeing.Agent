using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Commands;
using Seeing.Agent.Skills.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Skills;

/// <summary>技能组件加载器 — 由 <see cref="SkillsModule"/> 登记到 <see cref="IComponentManager"/>。</summary>
public sealed class SkillLoader : IComponentLoader
{
    public string Type => "Skill";

    /// <inheritdoc />
    public string ModuleId => "skills";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var skillManager = services.GetRequiredService<ISkillManager>();
        var skillsOptions = services.GetService<IOptionsMonitor<SkillsOptions>>()?.CurrentValue
            ?? services.GetService<IOptions<SkillsOptions>>()?.Value;
        var directories = services.GetService<ISeeingDirectories>();
        var projectRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : ResolveProjectRoot(directories);

        // SkillManager 具体 API（Reset / Clear）经具体类型
        var concrete = services.GetRequiredService<SkillManager>();
        concrete.ResetSearchDirectoriesToDefault();

        if (directories is not null)
            AddIfExists(concrete, Path.Combine(directories.UserSeeingDirectory, "skills"));

        if (skillsOptions?.Paths != null)
        {
            foreach (var p in skillsOptions.Paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    AddIfExists(concrete, ExpandPath(p.Trim(), projectRoot));
            }
        }

        await skillManager.DiscoverSkillsAsync(cancellationToken);
        await concrete.LoadSkillStateAsync(cancellationToken);

        RegisterDynamicSkillCommands(services, skillManager);

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
        var concrete = services.GetRequiredService<SkillManager>();
        UnregisterDynamicSkillCommands(services, concrete);
        concrete.ClearSkillInfos();
        return await LoadAsync(services, workspaceRoot, cancellationToken);
    }

    private static void RegisterDynamicSkillCommands(IServiceProvider services, ISkillManager skillManager)
    {
        var registry = services.GetService<ICommandRegistry>();
        var sessionManager = services.GetService<ISessionManager>();
        if (registry is null || sessionManager is null)
            return;

        foreach (var skillInfo in skillManager.GetAllSkillInfos().Values)
        {
            registry.Register(new DynamicSkillCommand(sessionManager, skillInfo));
        }
    }

    private static void UnregisterDynamicSkillCommands(IServiceProvider services, SkillManager skillManager)
    {
        var registry = services.GetService<ICommandRegistry>();
        if (registry is null)
            return;

        foreach (var name in skillManager.GetAllSkillInfos().Keys)
            registry.Unregister(name);
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
