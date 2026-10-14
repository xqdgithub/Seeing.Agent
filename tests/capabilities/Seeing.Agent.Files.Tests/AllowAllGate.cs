using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Files.Tests;

/// <summary>测试用：一律放行的路径门闸。</summary>
internal sealed class AllowAllGate : IWorkspacePathGate
{
    /// <inheritdoc />
    public string? EnsureAllowed(string sessionId, string path) => null;
}
