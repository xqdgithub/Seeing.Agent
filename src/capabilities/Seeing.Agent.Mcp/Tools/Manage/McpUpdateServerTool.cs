using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>更新已有 MCP server 配置（保留原 ConfigLevel）。</summary>
public sealed class McpUpdateServerTool : ToolBase
{
    private readonly IMcpManager _manager;

    public McpUpdateServerTool(ILogger<McpUpdateServerTool> logger, IMcpManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "mcp_update_server";

    public override string Description =>
        "更新已有 MCP server 的传输配置并持久化到原配置级别。\n\n" +
        "- 不改变 ConfigLevel；新建用 `mcp_add_server`，删除用 `mcp_remove_server`。\n" +
        "- 需审批：权限 kind `mcp.manage.write`，记忆键为 server 名。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "MCP server 名称（必填）" },
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
                description = "stdio 环境变量",
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
            disabled = new { type = "boolean", description = "是否禁用；默认 false" }
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

            var config = McpManageToolHelpers.ParseServerConfig(arguments, out var configError);
            if (config is null)
                return Failure(configError!);

            var denied = await McpManageToolHelpers
                .AuthorizeOrDenyAsync(this, McpManagePermissionGate.KindWrite, name, arguments, context)
                .ConfigureAwait(false);
            if (denied is not null)
                return denied;

            var existingLevel = _manager.GetConfigLevel(name);
            if (existingLevel is not null)
                config.ConfigLevel = existingLevel;

            var result = await _manager
                .UpdateConfigAsync(name, config, persist: true, context.CancellationToken)
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
