using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP server 粒度资源门：以 <c>mcp.execute</c> kind、resource=server 名发起审批。
/// <para>
/// <c>mcp.execute</c> 归资源类（<c>PermissionService.IsResourceKind</c> 含 <c>mcp.</c> 前缀），
/// Agent 的 <c>Allow(Tool,"*")</c> 不短路；审批记忆按 server 粒度命中（批一次=信任该 server 全部工具）。
/// </para>
/// </summary>
internal static class McpToolPermissionGate
{
    /// <summary>MCP 资源审批 kind。</summary>
    internal const string PermissionKindMcpExecute = "mcp.execute";

    /// <summary>
    /// 发起 server 粒度审批。
    /// <para>
    /// 授权器解析：优先用执行级 <see cref="ToolContext.PermissionAuthorizer"/>（ToolManager 从执行链注入），
    /// 缺失时回退 <see cref="IPermissionAuthorizerFactory"/>（对齐既有资源门模式）。
    /// 两者均不可用时 fail-closed 返回 Deny——绝不静默放行（脱离执行链直调亦然）。
    /// </para>
    /// </summary>
    internal static async Task<PermissionResolution?> AuthorizeAsync(
        string serverName,
        string toolName,
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

        return await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = string.IsNullOrEmpty(context.SessionId) ? authorizer.SessionId : context.SessionId,
            CallId = context.CallId,
            AgentName = context.Agent?.Name,
            PermissionKind = PermissionKindMcpExecute,
            Resource = serverName,
            Arguments = arguments,
            Metadata = new Dictionary<string, object>
            {
                ["server"] = serverName,
                ["tool"] = toolName
            }
        }, context.CancellationToken).ConfigureAwait(false);
    }
}
