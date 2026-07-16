using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Commands;

/// <summary>
/// ACP 专属命令提供者 - 提供仅在 ACP Passthrough 模式下可用的命令
/// </summary>
[CommandProvider]
public class AcpCommands
{
    /// <summary>
    /// /clear - 清除当前会话消息（ACP 版本）
    /// </summary>
    [Command(
        "清除当前会话的消息历史",
        Name = "clear",
        Usage = "/clear",
        Category = CommandCategory.Basic,
        Aliases = new[] { "cls" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.AcpPassthrough })]
    public CommandResult ClearSession(CommandContext context)
    {
        // ACP 清屏逻辑 - 返回需要刷新的标记
        // 实际清屏由前端或 ACP 后端处理
        return CommandResult.Ok("Screen cleared.", needsRefresh: true);
    }
}
