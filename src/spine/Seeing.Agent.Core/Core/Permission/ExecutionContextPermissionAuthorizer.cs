using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 执行级授权器：绑定 <c>sessionId</c> 与覆盖，按 kind 注入 <c>AllowedScopes</c> 后委托 <see cref="IPermissionService"/>。
/// </summary>
public sealed class ExecutionContextPermissionAuthorizer : IPermissionAuthorizer
{
    private static readonly IReadOnlyList<PermissionGrantScope> FilesystemScopes =
        [PermissionGrantScope.Once, PermissionGrantScope.Session, PermissionGrantScope.SessionDirectory];

    private static readonly IReadOnlyList<PermissionGrantScope> DefaultScopes =
        [PermissionGrantScope.Once, PermissionGrantScope.Session];

    private readonly IPermissionService _service;
    private readonly SessionAutoApprove? _override;

    /// <summary>创建执行级授权器。</summary>
    public ExecutionContextPermissionAuthorizer(
        IPermissionService service,
        string sessionId,
        SessionAutoApprove? @override = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        SessionId = sessionId ?? string.Empty;
        _override = @override;
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = request with
        {
            SessionId = string.IsNullOrEmpty(request.SessionId) ? SessionId : request.SessionId,
            Override = request.Override ?? _override,
            AllowedScopes = ResolveScopes(request.PermissionKind)
        };

        return _service.AuthorizeAsync(normalized, ct);
    }

    private static IReadOnlyList<PermissionGrantScope> ResolveScopes(string? permissionKind) =>
        permissionKind?.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase) == true
            ? FilesystemScopes
            : DefaultScopes;
}
