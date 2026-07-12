using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Tools;

namespace Seeing.Agent.App.Commands;

/// <summary>
/// 工具命令提供者 - 提供工具管理命令
/// </summary>
[CommandProvider]
public class ToolsCommands
{
    private readonly ToolInvoker _toolInvoker;

    public ToolsCommands(ToolInvoker toolInvoker)
    {
        _toolInvoker = toolInvoker;
    }

    /// <summary>
    /// /mcp - 管理 MCP 服务器
    /// </summary>
    [Command(
        "管理 MCP 服务器",
        Name = "mcp",
        Usage = "/mcp [list]",
        Category = CommandCategory.Tools,
        Type = CommandType.System)]
    public async Task<CommandResult> ManageMcp(CommandContext context, CancellationToken ct = default)
    {
        var args = context.Arguments.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(args) || args == "list")
        {
            // 显示 MCP 工具列表
            var schemas = await _toolInvoker.GetToolSchemasAsync();
            var mcpTools = schemas.Where(s => s.Function.Name.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)).ToList();

            var list = "**MCP Tools**\n\n";
            if (mcpTools.Count == 0)
            {
                list += "No MCP tools available.\n";
            }
            else
            {
                foreach (var tool in mcpTools)
                {
                    list += $"- **{tool.Function.Name}**: {tool.Function.Description ?? ""}\n";
                }
            }

            list += "\nUse the Settings page to manage MCP servers.";
            return CommandResult.Ok(list);
        }

        // 其他 MCP 操作需要 MCP 管理服务
        return CommandResult.Fail($"MCP command '{args}' not implemented yet. Use the Settings page to manage MCP servers.");
    }

    /// <summary>
    /// /tools - 列出所有工具
    /// </summary>
    [Command(
        "列出所有可用工具",
        Name = "tools",
        Usage = "/tools",
        Category = CommandCategory.Tools,
        Type = CommandType.System)]
    public async Task<CommandResult> ListTools(CommandContext context, CancellationToken ct = default)
    {
        var schemas = await _toolInvoker.GetToolSchemasAsync();

        var list = "**Available Tools**\n\n";
        foreach (var tool in schemas.OrderBy(s => s.Function.Name))
        {
            list += $"- **{tool.Function.Name}**: {tool.Function.Description ?? ""}\n";
        }

        return CommandResult.Ok(list);
    }
}