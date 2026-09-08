using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Mcp;

/// <summary>
/// 由工作区根路径构造的轻量 <see cref="ISeeingDirectories"/>，供配置加载静态入口使用。
/// </summary>
internal sealed class WorkspaceSeeingDirectories : ISeeingDirectories
{
    private readonly string _workspaceRoot;

    public WorkspaceSeeingDirectories(string workspaceRoot)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);
    }

    public string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    public string ProjectSeeingDirectory =>
        Path.Combine(_workspaceRoot, ".seeing");
}
