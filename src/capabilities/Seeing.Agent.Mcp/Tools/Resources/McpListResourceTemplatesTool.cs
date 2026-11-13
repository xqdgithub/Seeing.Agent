using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Resources;

/// <summary>列出指定 MCP server 的 Resource Templates。</summary>
public sealed class McpListResourceTemplatesTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpListResourceTemplatesTool(
        ILogger<McpListResourceTemplatesTool> logger,
        IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_list_resource_templates";

    public override string Description =>
        "列出指定 MCP server 的 Resource Templates（URI 模板）。\n\n" +
        "- 需 server 已连接且声明 resources；不支持时返回明确失败。\n" +
        "- 可选 cursor 分页；具体内容仍用 `mcp_read_resource`。\n" +
        "- 需审批：权限 kind `mcp.resource.read`，记忆键 `server|*`。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server = new { type = "string", description = "MCP server 名称（必填）" },
            cursor = new { type = "string", description = "分页游标；省略则从首页开始" }
        },
        required = new[] { "server" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var server = McpResourceToolHelpers.RequireServer(arguments);
            if (server is null)
                return Failure("缺少必填参数 server。");

            var (session, error) = await McpResourceToolHelpers
                .AuthorizeAndResolveSessionAsync(_manager, server, uriForPermission: null, arguments, context)
                .ConfigureAwait(false);
            if (error is not null)
                return error;

            var cursor = McpResourceToolHelpers.OptionalCursor(arguments);
            var result = await session!.ListResourceTemplatesAsync(cursor, context.CancellationToken)
                .ConfigureAwait(false);

            if (result.NotSupported)
                return Failure(result.ErrorMessage ?? McpResourceToolHelpers.NotSupportedMessage);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.Items.Count == 0)
                return Failure(result.ErrorMessage);

            return Success(
                "MCP Resource Templates",
                McpResourceToolHelpers.FormatTemplateList(result.Items, result.NextCursor),
                new Dictionary<string, object> { ["server"] = server });
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
