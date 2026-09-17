using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 按需构造执行级授权器的工厂（供 ACP 等非执行链调用方）。
/// </summary>
public sealed class DefaultPermissionAuthorizerFactory : IPermissionAuthorizerFactory
{
    private readonly IPermissionService _service;

    /// <summary>创建工厂。</summary>
    public DefaultPermissionAuthorizerFactory(IPermissionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <inheritdoc />
    public IPermissionAuthorizer Create(string sessionId, SessionAutoApprove? @override = null) =>
        new ExecutionContextPermissionAuthorizer(_service, sessionId, @override);
}
