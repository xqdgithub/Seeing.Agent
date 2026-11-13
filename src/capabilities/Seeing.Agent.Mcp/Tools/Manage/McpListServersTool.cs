using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>列出全部 MCP server 的状态与 capabilities 摘要。</summary>
public sealed class McpListServersTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpListServersTool(ILogger<McpListServersTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_list_servers";

    public override string Description =>
        "列出全部 MCP server 的连接状态与能力摘要。\n\n" +
        "- 返回名称、状态、工具数、capabilities（tools/resources/prompts/skills）与可选计数。\n" +
        "- 查看单机详情用 `mcp_get_server`；增删改用 `mcp_add_server` 等写入工具。\n" +
        "- 需审批：权限 kind `mcp.manage.read`，记忆键 `*`。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var denied = await McpManageToolHelpers
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindRead, "*", arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var all = _manager.GetAllStatus();
            if (all.Count == 0)
                return Success("当前没有 MCP server 配置。");

            var sb = new StringBuilder();
            sb.AppendLine($"共 {all.Count} 个 MCP server：");
            foreach (var status in all.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(McpManageToolHelpers.FormatStatus(status));

            return Success("MCP server 列表", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
