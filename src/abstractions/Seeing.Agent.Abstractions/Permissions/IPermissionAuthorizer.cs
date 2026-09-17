namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>执行级授权入口（窄端口）。</summary>
public interface IPermissionAuthorizer
{
    string SessionId { get; }

    Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default);
}
