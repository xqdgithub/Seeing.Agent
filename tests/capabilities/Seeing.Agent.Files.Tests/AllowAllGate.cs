using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Files.Tests;

/// <summary>测试用：一律放行的路径门闸。</summary>
internal sealed class AllowAllGate : IWorkspacePathGate
{
    /// <inheritdoc />
    public string? EnsureAllowed(string sessionId, string path) => null;

    /// <inheritdoc />
    public string? EnsureWithinWorkspace(string sessionId, string path) => null;

    /// <inheritdoc />
    public string? ResolveAllowed(string sessionId, string path, string? baseDirectory, out string fullPath)
    {
        fullPath = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(baseDirectory)
            ? path
            : Path.GetFullPath(path, baseDirectory);
        return null;
    }
}
