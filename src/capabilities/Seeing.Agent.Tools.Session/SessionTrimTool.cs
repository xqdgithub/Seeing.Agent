using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Tools.Session;

/// <summary>
/// 会话裁剪工具 — 裁剪活跃消息中的早期历史；摘要、最后一条 user 消息及其之后不可裁剪。
/// <para>裁剪前强制备份（fail-closed）：备份失败则不删除任何消息。</para>
/// </summary>
public sealed class SessionTrimTool : SessionToolBase
{
    /// <summary>创建 SessionTrimTool 实例。</summary>
    public SessionTrimTool(
        ILogger<SessionTrimTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger, sessions, groups)
    {
    }

    /// <inheritdoc />
    public override string Id => "session_trim";

    /// <inheritdoc />
    public override string Description =>
        "裁剪当前会话的早期消息。mode=before/range/after 配合 message_id 或 from_id/to_id 指定范围。" +
        "最后一条摘要、最后一条 user 消息及其之后永不裁剪；已压缩历史保留。" +
        "裁剪前自动创建备份（备份失败则取消裁剪）。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            mode = new
            {
                type = "string",
                @enum = new[] { "before", "range", "after" },
                description = "裁剪模式：before=message_id 之前；after=message_id 之后；range=from_id..to_id"
            },
            message_id = new { type = "string", description = "before/after 模式的基准消息 ID" },
            from_id = new { type = "string", description = "range 模式起始消息 ID" },
            to_id = new { type = "string", description = "range 模式结束消息 ID" }
        },
        required = new[] { "mode" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;
        var mode = GetStringArgument(arguments, "mode") ?? "before";
        if (mode is not ("before" or "range" or "after"))
            return Failure($"未知的 mode: {mode}。可选值：before、range、after");

        SessionData session;
        try
        {
            session = await Sessions.GetOrLoadAsync(context.SessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Failure($"会话不存在: {context.SessionId}");
        }

        // 快照化：避免并发追加/裁剪时遍历到变动中的集合
        var active = session.GetActiveMessages().ToList();
        if (active.Count == 0)
            return Failure("会话无活跃消息，无可裁剪内容");

        var floor = LastIndexOfUser(active);
        var lastSummary = LastIndexOfSummary(active);

        var messageId = GetStringArgument(arguments, "message_id");
        var fromId = GetStringArgument(arguments, "from_id");
        var toId = GetStringArgument(arguments, "to_id");

        var anchorIndex = -1;
        var rangeFrom = -1;
        var rangeTo = -1;

        if (mode == "range")
        {
            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
                return Failure("range 模式需要同时提供 from_id 与 to_id");

            rangeFrom = IndexOf(active, fromId!);
            rangeTo = IndexOf(active, toId!);
            if (rangeFrom < 0)
                return Failure($"未找到消息: {fromId}");
            if (rangeTo < 0)
                return Failure($"未找到消息: {toId}");
            if (rangeFrom > rangeTo)
                return Failure("from_id 必须不晚于 to_id");
        }
        else
        {
            if (string.IsNullOrEmpty(messageId))
                return Failure($"{mode} 模式需要提供 message_id");

            anchorIndex = IndexOf(active, messageId!);
            if (anchorIndex < 0)
                return Failure($"未找到消息: {messageId}");
        }

        var targets = new List<SessionMessage>();
        for (var i = 0; i < active.Count; i++)
        {
            if (i == lastSummary)
                continue;
            if (floor >= 0 && i >= floor)
                continue;

            var selected = mode switch
            {
                "before" => i < anchorIndex,
                "after" => i > anchorIndex,
                _ => i >= rangeFrom && i <= rangeTo,
            };

            if (selected)
                targets.Add(active[i]);
        }

        if (targets.Count == 0)
        {
            return Failure(
                "没有可裁剪的消息（最后一条摘要、最后一条 user 消息及其之后不可裁剪）");
        }

        var targetIds = targets
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .Select(m => m.Id!)
            .ToHashSet(StringComparer.Ordinal);

        SessionData backup;
        try
        {
            backup = await Groups.CreateBackupForkAsync(
                session.Id, "trim-backup " + DateTime.Now.ToString("s"), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex, "创建裁剪备份失败，已取消裁剪");
        }

        var updated = await Sessions.UpdateSessionAsync(
            session.Id,
            s => s.ReplaceMessages(
                s.Messages.ToList().Where(m => string.IsNullOrEmpty(m.Id) || !targetIds.Contains(m.Id))),
            ct).ConfigureAwait(false);

        var removedCount = targets.Count;
        var remainingActive = updated.GetActiveMessages().Count;
        var output =
            $"已裁剪 {removedCount} 条消息。备份会话: {backup.Id}（{backup.Messages.Count} 条）。" +
            $"剩余活跃消息: {remainingActive} 条。";

        return Success(output, new Dictionary<string, object>
        {
            ["removed_count"] = removedCount,
            ["backup_session_id"] = backup.Id,
            ["backup_message_count"] = backup.Messages.Count,
            ["remaining_active_count"] = remainingActive,
        });
    }

    private static int LastIndexOfUser(List<SessionMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, MessageRole.User, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int LastIndexOfSummary(List<SessionMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].IsSummary)
                return i;
        }

        return -1;
    }

    private static int IndexOf(List<SessionMessage> messages, string id)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
