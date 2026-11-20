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
        // 不再回落到进程 CWD：没有工作区根就是调用方接线错误，必须显式失败。
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    public string ProjectSeeingDirectory =>
        Path.Combine(_workspaceRoot, ".seeing");
}
