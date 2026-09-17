using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>按需构造执行级授权器（ACP 等非执行链调用方）。</summary>
public interface IPermissionAuthorizerFactory
{
    IPermissionAuthorizer Create(string sessionId, SessionAutoApprove? @override = null);
}
