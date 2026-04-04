namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// ~/.seeing 与项目下 .seeing 的约定路径（Provider/Model/Plugin 仅用户 seeing.json；MCP/Skill/Rule 用户+项目，项目优先）。
/// </summary>
public static class SeeingLayout
{
    public static string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    public static string UserSeeingJsonPath => Path.Combine(UserSeeingDirectory, "seeing.json");

    public static string UserMcpJsonPath => Path.Combine(UserSeeingDirectory, "mcp.json");

    public static string UserSkillsDirectory => Path.Combine(UserSeeingDirectory, "skills");

    public static string UserRulesDirectory => Path.Combine(UserSeeingDirectory, "rules");

    public static string ProjectSeeingDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".seeing");

    public static string ProjectMcpJsonPath(string workspaceRoot) =>
        Path.Combine(ProjectSeeingDirectory(workspaceRoot), "mcp.json");

    public static string ProjectSkillsDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "skills");

    /// <summary>项目 <c>.seeing/skills</c>。</summary>
    public static string ProjectSeeingSkillsDirectory(string workspaceRoot) =>
        Path.Combine(ProjectSeeingDirectory(workspaceRoot), "skills");

    public static string ProjectAgentSkillsDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".agent", "skills");

    public static string ProjectRulesDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "rules");

    public static string ProjectSeeingRulesDirectory(string workspaceRoot) =>
        Path.Combine(ProjectSeeingDirectory(workspaceRoot), "rules");

    public static string ProjectAgentRulesDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".agent", "rules");
}
