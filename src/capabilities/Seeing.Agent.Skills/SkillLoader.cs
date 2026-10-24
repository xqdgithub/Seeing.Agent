using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Configuration;

namespace Seeing.Agent.Skills;

/// <summary>
/// 技能组件加载器 — 由 <see cref="SkillsModule"/> 登记到 <see cref="IComponentManager"/>。
/// <para>只有一条幂等路径：算出根列表 → <see cref="SkillCatalogCore.SetRoots"/> → 目录重扫。
/// 首次加载与重载共用同一条路径（不再 clear-then-fill，也不再有字典空窗）。</para>
/// </summary>
public sealed class SkillLoader : IComponentLoader
{
    public string Type => "Skill";

    /// <inheritdoc />
    public string ModuleId => "skills";

    /// <inheritdoc />
    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var catalog = services.GetRequiredService<SkillCatalogCore>();
        var skillsOptions = services.GetService<IOptionsMonitor<SkillsOptions>>()?.CurrentValue
            ?? services.GetService<IOptions<SkillsOptions>>()?.Value;
        var directories = services.GetService<ISeeingDirectories>();

        // 根只在加载器此处算一次：目录不重复推导，watcher 通过目录的根快照跟随
        var roots = ResolveRoots(catalog, skillsOptions, directories, workspaceRoot);
        catalog.SetRoots(roots);
        var snapshot = await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);

        // 动态技能命令由 SkillsModule 订阅目录事件同步（此处只负责触发一次目录重建）
        var enabledNames = catalog.GetEnabledEntries(snapshot)
            .Select(entry => entry.Info.Name)
            .ToList();

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = enabledNames.Count,
            Details = enabledNames
        };
    }

    /// <summary>
    /// 重载：与 <see cref="LoadAsync"/> 是同一条幂等路径（重算根 + 重扫目录）。
    /// </summary>
    public Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
        => LoadAsync(services, workspaceRoot, cancellationToken);

    /// <summary>
    /// 根列表 = 目录默认根（与 <see cref="SkillCatalogCore.DefaultRoots"/> 同一份定义）+ <see cref="SkillsOptions.Paths"/>。
    /// <para>优先级从高到低保持默认根顺序，配置路径追加在最后（与旧行为一致：默认根优先）。</para>
    /// </summary>
    private static IReadOnlyList<string> ResolveRoots(
        SkillCatalogCore catalog,
        SkillsOptions? options,
        ISeeingDirectories? directories,
        string workspaceRoot)
    {
        var projectRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : ResolveProjectRoot(directories);

        var roots = new List<string>(catalog.DefaultRoots);
        var seen = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);

        if (options?.Paths is { Count: > 0 } paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var resolved = ExpandPath(path.Trim(), projectRoot);
                if (seen.Add(resolved))
                    roots.Add(resolved);
            }
        }

        return roots;
    }

    private static string ResolveProjectRoot(ISeeingDirectories? directories)
    {
        if (directories is null)
            return Directory.GetCurrentDirectory();

        var projectSeeing = directories.ProjectSeeingDirectory;
        var parent = Directory.GetParent(projectSeeing);
        return parent?.FullName ?? projectSeeing;
    }

    /// <summary>展开配置里的技能路径（支持 <c>~</c> 与环境变量；相对路径按项目根解析）。</summary>
    private static string ExpandPath(string path, string workspaceRoot)
    {
        path = Environment.ExpandEnvironmentVariables(path);

        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path[1..].TrimStart('/', '\\')));
        }

        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }
}
