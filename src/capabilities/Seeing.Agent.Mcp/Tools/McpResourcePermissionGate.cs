using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools;

/// <summary>
/// MCP Resource 读权限门：kind=<c>mcp.resource.read</c>；
/// Resource 记忆键为 <c>server|uri</c>（list/templates 用 <c>server|*</c>）。
/// 与 <c>mcp.execute</c> grant 精确分离，不互通。
/// </summary>
internal static class McpResourcePermissionGate
{
    internal const string PermissionKind = "mcp.resource.read";

    /// <summary>构造记忆键：uri 为空时用 <c>*</c>（list / templates）。</summary>
    internal static string BuildResourceKey(string serverName, string? uri)
        => $"{serverName}|{(string.IsNullOrEmpty(uri) ? "*" : uri)}";

    /// <summary>
    /// 发起 resource 粒度审批。无授权器时 fail-closed Deny。
    /// </summary>
    internal static async Task<PermissionResolution?> AuthorizeAsync(
        string serverName,
        string? uri,
        JsonElement arguments,
        ToolContext context)
    {
        var authorizer = context.PermissionAuthorizer;
        if (authorizer is null)
        {
            var factory = context.Services?.GetService<IPermissionAuthorizerFactory>();
            if (factory is not null)
                authorizer = factory.Create(context.SessionId);
        }

        if (authorizer is null)
        {
            return new PermissionResolution
            {
                RequestId = context.CallId ?? Guid.NewGuid().ToString("N"),
                SessionId = context.SessionId,
                CallId = context.CallId,
                Decision = PermissionEffect.Deny,
                ResolvedBy = PermissionResolvedBy.Policy,
                Reason = "无权限授权器，已拒绝（fail-closed）"
            };
        }

        return await AuthorizeWithAuthorizerAsync(
                serverName,
                uri,
                authorizer,
                context.CancellationToken,
                sessionId: string.IsNullOrEmpty(context.SessionId) ? authorizer.SessionId : context.SessionId,
                callId: context.CallId,
                agentName: context.Agent?.Name,
                arguments: arguments)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 无 <see cref="ToolContext"/> 时的 resource 审批（技能加载回源）。无授权器时由调用方 Fail-closed。
    /// </summary>
    internal static Task<PermissionResolution> AuthorizeWithAuthorizerAsync(
        string serverName,
        string? uri,
        IPermissionAuthorizer authorizer,
        CancellationToken cancellationToken,
        string? sessionId = null,
        string? callId = null,
        string? agentName = null,
        object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        var resourceKey = BuildResourceKey(serverName, uri);
        return authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = string.IsNullOrEmpty(sessionId) ? authorizer.SessionId : sessionId,
            CallId = callId,
            AgentName = agentName,
            PermissionKind = PermissionKind,
            Resource = resourceKey,
            Arguments = arguments ?? default(JsonElement),
            Metadata = new Dictionary<string, object>
            {
                ["server"] = serverName,
                ["uri"] = uri ?? "*",
                ["resourceKey"] = resourceKey
            }
        }, cancellationToken);
    }
}
