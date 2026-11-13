using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>新增 MCP server 配置（必填 level；connect 三态覆盖 AutoStartOnAdd）。</summary>
public sealed class McpAddServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpAddServerTool(ILogger<McpAddServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_add_server";

    public override string Description =>
        "新增 MCP server 并持久化到指定配置级别。\n\n" +
        "- 必填 level：User（用户级）或 Project（项目级）；未选定工作区时 Project 会被拒绝。\n" +
        "- connect：true 立即连接、false 只写配置、省略则跟随全局 AutoStartOnAdd。\n" +
        "- 更新已有配置用 `mcp_update_server`；导入 JSON 用 `mcp_import_servers`。\n" +
        "- 需审批：权限 kind `mcp.manage.write`，记忆键为 server 名。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "MCP server 名称（必填）" },
            level = new
            {
                type = "string",
                @enum = new[] { "User", "Project" },
                description = "配置级别：User|Project（必填）"
            },
            connect = new
            {
                type = "boolean",
                description = "是否立即连接；省略则跟随 AutoStartOnAdd；true/false 显式覆盖"
            },
            transportType = new
            {
                type = "string",
                @enum = new[] { "stdio", "streamableHttp", "sse" },
                description = "传输类型：stdio|streamableHttp|sse；默认 stdio"
            },
            command = new { type = "string", description = "stdio 可执行命令（stdio 必填）" },
            args = new
            {
                type = "array",
                description = "stdio 命令行参数",
                items = new { type = "string", description = "单个参数" }
            },
            env = new
            {
                type = "object",
                description = "stdio 环境变量（键值均为字符串）",
                additionalProperties = new { type = "string", description = "环境变量值" }
            },
            workingDirectory = new { type = "string", description = "stdio 工作目录" },
            url = new { type = "string", description = "HTTP/SSE 端点绝对 URI" },
            headers = new
            {
                type = "object",
                description = "HTTP 请求头",
                additionalProperties = new { type = "string", description = "请求头值" }
            },
            description = new { type = "string", description = "可选描述" },
            disabled = new { type = "boolean", description = "是否以禁用状态添加；默认 false" }
        },
        required = new[] { "name", "level" },
        additionalProperties = false
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var name = McpManageToolHelpers.RequireName(arguments);
            if (name is null)
                return Failure("缺少必填参数 name。");

            if (!McpManageToolHelpers.TryParseLevel(arguments, out var level, out var levelError))
                return Failure(levelError!);

            var config = McpManageToolHelpers.ParseServerConfig(arguments, out var configError);
            if (config is null)
                return Failure(configError!);

            var denied = await McpManageToolHelpers
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindWrite, name, arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var connect = McpManageToolHelpers.ParseConnect(arguments);
            var result = await _manager
                .AddServerAsync(name, config, level, persist: true, connect, context.CancellationToken)
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
