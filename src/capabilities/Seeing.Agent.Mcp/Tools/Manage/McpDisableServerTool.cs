using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>禁用 MCP server（持久化 Disabled）。</summary>
public sealed class McpDisableServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpDisableServerTool(ILogger<McpDisableServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_disable_server";

    public override string Description =>
        "禁用 MCP server：断开连接并持久化 Disabled 标志。\n\n" +
        "- 配置保留；临时暂停且不改配置用 `mcp_pause_server`；删除用 `mcp_remove_server`。\n" +
        "- 需审批：权限 kind `mcp.manage.write`，记忆键为 server 名。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "MCP server 名称（必填）" }
        },
        required = new[] { "name" },
        additionalProperties = false
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var name = McpManageToolHelpers.RequireName(arguments);
            if (name is null)
                return Failure("缺少必填参数 name。");

            var denied = await McpManageToolHelpers
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindWrite, name, arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var result = await _manager
                .DisableServerAsync(name, persist: true, context.CancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? Success(McpManageToolHelpers.FormatOperation(result))
                : Failure(McpManageToolHelpers.FormatOperation(result));
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
