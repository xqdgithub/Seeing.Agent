using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>从 JSON 导入 MCP server（必填 level）。</summary>
public sealed class McpImportServersTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpImportServersTool(ILogger<McpImportServersTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_import_servers";

    public override string Description =>
        "从 JSON（含 mcpServers 对象）批量导入 MCP server 配置。\n\n" +
        "- 必填 level：User|Project；未选定工作区时 Project 会被拒绝。\n" +
        "- 单条新增用 `mcp_add_server`。\n" +
        "- 需审批：权限 kind `mcp.manage.write`，记忆键为目标级别名（User/Project）。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            level = new
            {
                type = "string",
                @enum = new[] { "User", "Project" },
                description = "导入目标级别：User|Project（必填）"
            },
            json = new { type = "string", description = "含 mcpServers 对象的 JSON 字符串（必填）" },
            overwrite = new
            {
                type = "boolean",
                description = "是否覆盖同名 server；默认 false"
            }
        },
        required = new[] { "level", "json" },
        additionalProperties = false
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            if (!McpManageToolHelpers.TryParseLevel(arguments, out var level, out var levelError))
                return Failure(levelError!);

            if (!arguments.TryGetProperty("json", out var jsonProp) || jsonProp.ValueKind != JsonValueKind.String)
                return Failure("缺少必填参数 json。");

            var json = jsonProp.GetString();
            if (string.IsNullOrWhiteSpace(json))
                return Failure("json 不能为空。");

            var overwrite = false;
            if (arguments.TryGetProperty("overwrite", out var ow) &&
                (ow.ValueKind is JsonValueKind.True or JsonValueKind.False))
                overwrite = ow.GetBoolean();

            var denied = await McpManageToolHelpers
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindWrite, level.ToString(), arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var count = await _manager
                .ImportFromJsonAsync(json, level, overwrite, persist: true, context.CancellationToken)
                .ConfigureAwait(false);

            return Success($"已导入 {count} 个 MCP server 到 {level} 级别。");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
