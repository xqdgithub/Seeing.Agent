using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Resources;

/// <summary>读取指定 MCP server 上的单个 Resource（二进制仅摘要）。</summary>
public sealed class McpReadResourceTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpReadResourceTool(ILogger<McpReadResourceTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_read_resource";

    public override string Description =>
        "读取指定 MCP server 上某个 Resource 的内容。\n\n" +
        "- 参数：server + uri（来自 `mcp_list_resources` 或嵌入资源提示）。\n" +
        "- 文本原样返回；二进制只给 mime 与约略字节数摘要，不塞入整段 base64。\n" +
        "- 需审批：权限 kind `mcp.resource.read`，记忆键 `server|uri`。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server = new { type = "string", description = "MCP server 名称（必填）" },
            uri = new { type = "string", description = "Resource URI（必填）" }
        },
        required = new[] { "server", "uri" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var server = McpResourceToolHelpers.RequireServer(arguments);
            if (server is null)
                return Failure("缺少必填参数 server。");

            var uri = McpResourceToolHelpers.RequireUri(arguments);
            if (uri is null)
                return Failure("缺少必填参数 uri。");

            var (session, error) = await McpResourceToolHelpers
                .AuthorizeAndResolveSessionAsync(_manager, server, uriForPermission: uri, arguments, context)
                .ConfigureAwait(false);
            if (error is not null)
                return error;

            var result = await session!.ReadResourceAsync(uri, context.CancellationToken)
                .ConfigureAwait(false);

            if (result.NotSupported)
                return Failure(result.ErrorMessage ?? McpResourceToolHelpers.NotSupportedMessage);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.Contents.Count == 0)
                return Failure(result.ErrorMessage);

            return Success(
                "MCP Resource",
                McpResourceToolHelpers.FormatReadContents(result.Contents),
                new Dictionary<string, object>
                {
                    ["server"] = server,
                    ["uri"] = uri
                });
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
