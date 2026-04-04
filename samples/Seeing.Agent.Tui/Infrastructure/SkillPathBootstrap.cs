using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Skills;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// Skill：先 <c>~/.seeing/skills</c>，再 <see cref="SeeingAgentOptions.Skills"/>.Paths，再项目
/// <c>skills/</c>、<c>.seeing/skills</c>、<c>.agent/skills</c>（后扫描覆盖同名 SKILL.md）。
/// </summary>
public static class SkillPathBootstrap
{
    public static async Task ApplyAsync(
        SkillManager skillManager,
        IOptions<SeeingAgentOptions> options,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        skillManager.ResetSearchDirectoriesToDefault();

        var userSkills = SeeingLayout.UserSkillsDirectory;
        if (Directory.Exists(userSkills))
            skillManager.AddSearchDirectory(userSkills);

        foreach (var p in options.Value.Skills.Paths)
        {
            if (string.IsNullOrWhiteSpace(p))
                continue;
            var full = ExpandSkillPath(p.Trim(), workspaceRoot);
            if (Directory.Exists(full))
                skillManager.AddSearchDirectory(full);
        }

        AddIfExists(skillManager, SeeingLayout.ProjectSeeingSkillsDirectory(workspaceRoot));
        AddIfExists(skillManager, SeeingLayout.ProjectAgentSkillsDirectory(workspaceRoot));

        await skillManager.DiscoverSkillsAsync(cancellationToken);
    }

    private static string ExpandSkillPath(string p, string workspaceRoot)
    {
        if (p.Length >= 2 && p[0] == '~' && (p[1] == '/' || p[1] == '\\'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, p[2..]));
        }

        return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(workspaceRoot, p));
    }

    private static void AddIfExists(SkillManager skillManager, string directory)
    {
        if (Directory.Exists(directory))
            skillManager.AddSearchDirectory(directory);
    }

    /// <summary>
    /// 切换工作区后重新发现技能。
    /// </summary>
    public static async Task ReloadForWorkspaceAsync(
        SkillManager skillManager,
        IOptions<SeeingAgentOptions> options,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        skillManager.ClearSkillInfos();
        await ApplyAsync(skillManager, options, workspaceRoot, cancellationToken);
    }
}
