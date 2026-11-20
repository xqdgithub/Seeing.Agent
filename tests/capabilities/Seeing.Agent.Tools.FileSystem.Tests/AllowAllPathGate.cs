using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Tests.Tools;

internal sealed class AllowAllPathGate : IWorkspacePathGate
{
    public static AllowAllPathGate Instance { get; } = new();
    public string? EnsureAllowed(string sessionId, string path) => null;
    public string? EnsureWithinWorkspace(string sessionId, string path) => null;

    public string? ResolveAllowed(string sessionId, string path, string? baseDirectory, out string fullPath)
    {
        fullPath = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(baseDirectory)
            ? path
            : Path.GetFullPath(path, baseDirectory);
        return null;
    }
}
