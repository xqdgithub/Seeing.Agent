using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Tools.Session;

/// <summary>
/// 会话读取工具 — 按区间 / 窗口 / 分页读取会话消息，默认隐藏工具输出与思考内容。
/// </summary>
[ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
public sealed class SessionReadTool : SessionToolBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int DefaultMaxChars = 2000;
    private const int MaxCharsLimit = 2000;

    /// <summary>创建 SessionReadTool 实例。</summary>
    public SessionReadTool(
        ILogger<SessionReadTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger, sessions, groups)
    {
    }

    /// <inheritdoc />
    public override string Id => "session_read";

    /// <inheritdoc />
    public override string Description =>
        "读取会话消息。支持 from_id/to_id 闭区间、message_id+before/after 窗口、" +
        "offset+limit 分页三种方式。默认只读活跃消息（不含已压缩历史），" +
        "默认隐藏工具输出；include_compacted=true 可读完整历史。绝不返回思考内容。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            session_id = new { type = "string", description = "目标会话 ID；缺省为当前会话" },
            from_id = new { type = "string", description = "区间起始消息 ID（含）" },
            to_id = new { type = "string", description = "区间结束消息 ID（含）" },
            message_id = new { type = "string", description = "窗口中心消息 ID" },
            before = new { type = "integer", minimum = 0, description = "窗口前向条数（默认 0）" },
            after = new { type = "integer", minimum = 0, description = "窗口后向条数（默认 0）" },
            offset = new { type = "integer", minimum = 0, description = "分页起始偏移（默认 0）" },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLimit,
                description = $"返回消息数上限（默认 {DefaultLimit}，上限 {MaxLimit}）"
            },
            max_chars = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxCharsLimit,
                description = $"单条消息内容最大字符数（默认 {DefaultMaxChars}，上限 {MaxCharsLimit}）"
            },
            include_tool_output = new
            {
                type = "boolean",
                description = "是否渲染工具输出（默认 false，仅显示 tool: <name>(<status>)）"
            },
            include_compacted = new
            {
                type = "boolean",
                description = "是否读取已压缩历史（默认 false，仅活跃消息）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;

        var requestedId = GetStringArgument(arguments, "session_id");
        var targetId = string.IsNullOrWhiteSpace(requestedId) ? context.SessionId : requestedId!;

        if (!await IsSameGroupAsync(context.SessionId, targetId, ct).ConfigureAwait(false))
        {
            var denied = await AuthorizeAsync(
                context,
                Id,
                $"读取会话 {targetId} 需要授权",
                new Dictionary<string, object> { ["target_session_id"] = targetId },
                ct).ConfigureAwait(false);
            if (denied is not null)
                return denied;
        }

        SessionData session;
        try
        {
            session = await Sessions.GetOrLoadAsync(targetId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Failure($"会话不存在: {targetId}");
        }

        var includeToolOutput = GetBoolArgument(arguments, "include_tool_output") ?? false;
        var includeCompacted = GetBoolArgument(arguments, "include_compacted") ?? false;
        var maxChars = Math.Clamp(GetIntArgument(arguments, "max_chars") ?? DefaultMaxChars, 1, MaxCharsLimit);
        var limit = Math.Clamp(GetIntArgument(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);

        List<SessionMessage> source = includeCompacted
            ? session.Messages.ToList()
            : session.GetActiveMessages();
        var total = source.Count;

        if (total == 0)
        {
            return Success("会话无消息", new Dictionary<string, object>
            {
                ["total"] = 0,
                ["from_index"] = -1,
                ["to_index"] = -1,
                ["truncated"] = false,
                ["next_cursor"] = null,
            });
        }

        var fromId = GetStringArgument(arguments, "from_id");
        var toId = GetStringArgument(arguments, "to_id");
        var messageId = GetStringArgument(arguments, "message_id");
        var offset = Math.Max(GetIntArgument(arguments, "offset") ?? 0, 0);
        var before = Math.Max(GetIntArgument(arguments, "before") ?? 0, 0);
        var after = Math.Max(GetIntArgument(arguments, "after") ?? 0, 0);

        int start;
        int end;

        if (fromId is not null || toId is not null)
        {
            if (fromId is null || toId is null)
                return Failure("from_id 与 to_id 必须同时提供");

            var fromIndex = IndexOf(source, fromId);
            var toIndex = IndexOf(source, toId);
            if (fromIndex < 0)
                return Failure($"未找到消息: {fromId}");
            if (toIndex < 0)
                return Failure($"未找到消息: {toId}");
            if (fromIndex > toIndex)
                return Failure("from_id 必须不晚于 to_id");

            start = fromIndex;
            end = toIndex;
        }
        else if (messageId is not null)
        {
            var center = IndexOf(source, messageId);
            if (center < 0)
                return Failure($"未找到消息: {messageId}");

            start = Math.Max(0, center - before);
            end = Math.Min(total - 1, center + after);
        }
        else
        {
            start = Math.Min(offset, total);
            end = total - 1;
        }

        end = Math.Min(end, start + limit - 1);

        var lines = new List<string>();
        var displayed = 0;
        if (start <= end && start < total)
        {
            for (var i = start; i <= end; i++)
            {
                var message = source[i];
                lines.Add(
                    $"[#{i} | {message.Id ?? "-"} | {message.Role} | step={message.Step} | " +
                    $"{message.CreatedAt:yyyy-MM-ddTHH:mm:ss}]");
                var body = RenderBody(message, includeToolOutput, maxChars);
                if (!string.IsNullOrEmpty(body))
                    lines.Add(body);
                displayed++;
            }
        }

        var truncated = displayed > 0 && start + displayed < total;
        var nextCursor = truncated ? source[start + displayed].Id : null;

        if (displayed == 0)
        {
            lines.Add("(无消息)");
        }
        else
        {
            var footer = $"(显示 {start + 1}-{start + displayed}/共 {total} 条";
            if (truncated)
                footer += $"；续读 from_id={nextCursor}";
            footer += ")";
            lines.Add("");
            lines.Add(footer);
        }

        return Success(
            string.Join("\n", lines),
            new Dictionary<string, object>
            {
                ["total"] = total,
                ["from_index"] = displayed > 0 ? start : -1,
                ["to_index"] = displayed > 0 ? start + displayed - 1 : -1,
                ["truncated"] = truncated,
                ["next_cursor"] = nextCursor,
            });
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

    private static string RenderBody(
        SessionMessage message, bool includeToolOutput, int maxChars)
    {
        if (string.Equals(message.Role, MessageRole.Tool, StringComparison.OrdinalIgnoreCase))
        {
            return includeToolOutput
                ? Truncate(GetMessageText(message), maxChars)
                : $"tool: {message.ToolName ?? "-"}(completed)";
        }

        var parts = new List<string>();
        var text = GetMessageText(message);
        if (!string.IsNullOrEmpty(text))
            parts.Add(Truncate(text, maxChars));

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                var line = $"tool: {call.Name}({call.Status})";
                if (includeToolOutput && !string.IsNullOrEmpty(call.Result))
                    line += $"\n  result: {Truncate(call.Result, maxChars)}";
                parts.Add(line);
            }
        }

        return string.Join("\n", parts);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        var removed = text.Length - maxChars;
        return string.Concat(text.AsSpan(0, maxChars), $"…[截断 {removed} 字符]");
    }
}
