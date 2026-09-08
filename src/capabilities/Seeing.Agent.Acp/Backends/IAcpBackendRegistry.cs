namespace Seeing.Agent.Acp.Backends;

/// <summary>
/// ACP 后端注册表。
/// </summary>
public interface IAcpBackendRegistry
{
    AcpBackendDescriptor GetBackend(string backendId);

    IReadOnlyList<AcpBackendDescriptor> GetEnabledBackends();

    string ResolveDefault(string? preferredBackend = null);

    bool TryGetBackend(string backendId, out AcpBackendDescriptor? descriptor);
}
