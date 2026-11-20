using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Tests.Tools;

/// <summary>测试用：解析相对路径但不做边界拦截。</summary>
internal sealed class AllowAllWorkspacePathGate : IWorkspacePathGate
{
    public static AllowAllWorkspacePathGate Instance { get; } = new();

    public string? EnsureAllowed(string sessionId, string path) => null;

    public string? EnsureWithinWorkspace(string sessionId, string path) => null;

    public string? ResolveAllowed(
        string sessionId, string path, string? baseDirectory, out string fullPath)
    {
        fullPath = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(baseDirectory)
            ? path
            : Path.GetFullPath(path, baseDirectory);
        return null;
    }
}
