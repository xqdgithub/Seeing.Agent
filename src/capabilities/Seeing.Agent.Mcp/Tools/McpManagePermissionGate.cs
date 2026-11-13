using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools;

/// <summary>
/// MCP 管理权限门：kind 为 <c>mcp.manage.read|write|lifecycle</c>，与 <c>mcp.execute</c> 精确分离。
/// （Task 4 实现；Task 5 重建被并行覆盖删除的文件以保持可编译。）
/// </summary>
internal static class McpManagePermissionGate
{
    internal const string KindRead = "mcp.manage.read";
    internal const string KindWrite = "mcp.manage.write";
    internal const string KindLifecycle = "mcp.manage.lifecycle";

    internal static async Task<PermissionResolution?> AuthorizeAsync(
        string permissionKind,
        string resource,
        string toolId,
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
            PermissionKind = permissionKind,
            Resource = resource,
            Arguments = arguments,
            Metadata = new Dictionary<string, object>
            {
                ["tool"] = toolId,
                ["resource"] = resource
            }
        }, context.CancellationToken).ConfigureAwait(false);
    }
}
