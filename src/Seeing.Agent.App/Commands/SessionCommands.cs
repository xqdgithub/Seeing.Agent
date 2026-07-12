using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Session.Core;

namespace Seeing.Agent.App.Commands;

/// <summary>
/// 会话命令提供者 - 提供会话管理命令
/// </summary>
[CommandProvider]
public class SessionCommands
{
    private readonly ISessionManager _sessionManager;

    public SessionCommands(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// /fork - 分叉会话
    /// </summary>
    [Command(
        "分叉当前会话创建副本",
        Name = "fork",
        Usage = "/fork [title]",
        Category = CommandCategory.Navigation,
        Aliases = new[] { "branch" },
        Type = CommandType.System)]
    public async Task<CommandResult> ForkSession(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.SessionId))
        {
            return CommandResult.Fail("No active session");
        }

        var title = context.Arguments.Trim();
        var newSession = await _sessionManager.ForkAsync(context.SessionId, label: title, ct: ct);

        return CommandResult.Ok($"会话已分叉: {newSession.Id}")
            .WithNavigation($"/session/{newSession.Id}");
    }
}