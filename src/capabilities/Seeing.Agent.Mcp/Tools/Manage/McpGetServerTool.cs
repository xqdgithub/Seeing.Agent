using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>获取单个 MCP server 详情。</summary>
public sealed class McpGetServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpGetServerTool(ILogger<McpGetServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_get_server";

    public override string Description =>
        "获取单个 MCP server 的状态、配置级别与能力摘要。\n\n" +
        "- 含工具名列表与 capabilities；列表总览用 `mcp_list_servers`。\n" +
        "- 需审批：权限 kind `mcp.manage.read`，记忆键为 server 名。";

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
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindRead, name, arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var status = _manager.GetStatus(name);
            if (status is null)
                return Failure($"未找到 MCP server：{name}");

            var sb = new StringBuilder();
            sb.AppendLine(McpManageToolHelpers.FormatStatus(status));
            var json = _manager.GetServerConfigAsJson(name);
            if (!string.IsNullOrWhiteSpace(json))
            {
                sb.AppendLine();
                sb.AppendLine("配置 JSON：");
                sb.AppendLine(json);
            }

            return Success($"MCP server {name}", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
