using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Tests.Tools;

internal sealed class AllowAllPathGate : IWorkspacePathGate
{
    public static AllowAllPathGate Instance { get; } = new();
    public string? EnsureAllowed(string sessionId, string path) => null;
}
