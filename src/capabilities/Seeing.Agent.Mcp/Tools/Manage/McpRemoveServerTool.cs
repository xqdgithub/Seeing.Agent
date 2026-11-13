using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>删除 MCP server 配置。</summary>
public sealed class McpRemoveServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpRemoveServerTool(ILogger<McpRemoveServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_remove_server";

    public override string Description =>
        "删除 MCP server 配置并断开连接。\n\n" +
        "- 不可恢复；临时停用请用 `mcp_disable_server` 或 `mcp_pause_server`。\n" +
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
                .RemoveServerAsync(name, persist: true, context.CancellationToken)
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
