using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>暂停 MCP server 连接（不改 Disabled 配置）。</summary>
public sealed class McpPauseServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpPauseServerTool(ILogger<McpPauseServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_pause_server";

    public override string Description =>
        "暂停 MCP server 连接（不持久化 Disabled）。\n\n" +
        "- 恢复用 `mcp_resume_server`；持久禁用用 `mcp_disable_server`。\n" +
        "- 需审批：权限 kind `mcp.manage.lifecycle`，记忆键为 server 名。";

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
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindLifecycle, name, arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var result = await _manager
                .PauseServerAsync(name, context.CancellationToken)
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
