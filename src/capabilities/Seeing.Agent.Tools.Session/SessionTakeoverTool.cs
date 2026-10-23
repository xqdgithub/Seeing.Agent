using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.Tools.Session;

/// <summary>
/// 会话接管工具 — 将目标会话归属置为人类（Owner=Human），暂停父 agent 自动驱动。
/// </summary>
public sealed class SessionTakeoverTool : SessionToolBase
{
    /// <summary>创建 SessionTakeoverTool 实例。</summary>
    public SessionTakeoverTool(
        ILogger<SessionTakeoverTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger, sessions, groups)
    {
    }

    /// <inheritdoc />
    public override string Id => "session_takeover";

    /// <inheritdoc />
    public override string Description =>
        "接管目标会话（归属改为人类），父代理不再驱动；只改归属。\n\n" +
        "- 不终止正在运行的执行。\n" +
        "- 范围：默认同组与父链；跨组需审批。\n" +
        "- 交回用 `session_release`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            session_id = new { type = "string", description = "目标会话 ID（必填）" }
        },
        required = new[] { "session_id" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;

        var sessionId = GetStringArgument(arguments, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return Failure("session_id 参数是必需的");

        var targetId = sessionId!;
        if (!await IsInScopeAsync(context.SessionId, targetId, ct).ConfigureAwait(false))
        {
            var denied = await AuthorizeAsync(
                context,
                Id,
                $"接管会话 {targetId} 需要授权",
                new Dictionary<string, object> { ["target_session_id"] = targetId },
                ct).ConfigureAwait(false);
            if (denied is not null)
                return denied;
        }

        SessionData target;
        try
        {
            target = await Sessions.GetOrLoadAsync(targetId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Failure($"会话不存在: {targetId}");
        }

        target.SetOwner(SessionOwner.Human, "agent");
        await Sessions.SaveAsync(targetId).ConfigureAwait(false);

        return Success(
            $"会话 {targetId} 已接管（Owner=Human）。",
            new Dictionary<string, object>
            {
                ["target_session_id"] = targetId,
                ["owner"] = SessionOwner.Human.ToString().ToLowerInvariant(),
            });
    }
}
