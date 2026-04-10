using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Skills;

namespace Seeing.Agent.NewTui.Infrastructure;

/// <summary>
/// Skill 路径初始化
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

        // 用户级技能
        if (Directory.Exists(SeeingLayout.UserSkillsDirectory))
            skillManager.AddSearchDirectory(SeeingLayout.UserSkillsDirectory);

        // 配置中的技能路径
        foreach (var p in options.Value.Skills.Paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var full = ExpandPath(p.Trim(), workspaceRoot);
            if (Directory.Exists(full))
                skillManager.AddSearchDirectory(full);
        }

        // 项目级技能
        AddIfExists(skillManager, SeeingLayout.ProjectSkillsDirectory(workspaceRoot));

        await skillManager.DiscoverSkillsAsync(cancellationToken);
    }

    private static string ExpandPath(string p, string workspaceRoot)
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
}