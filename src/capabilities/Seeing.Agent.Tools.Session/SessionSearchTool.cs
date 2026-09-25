using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Tools.Session;

/// <summary>
/// 会话内容搜索工具 — 在会话消息中按正则搜索，支持角色过滤、上下文与分页。
/// </summary>
[ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
public sealed class SessionSearchTool : SessionToolBase
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;
    private const int MaxContext = 3;
    private const int MaxPreviewChars = 200;

    /// <summary>用户正则匹配超时（ReDoS 防护）。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>创建 SessionSearchTool 实例。</summary>
    public SessionSearchTool(
        ILogger<SessionSearchTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger, sessions, groups)
    {
    }

    /// <inheritdoc />
    public override string Id => "session_search";

    /// <inheritdoc />
    public override string Description =>
        "在会话消息中按正则表达式搜索。session_id 缺省为当前会话；" +
        "搜索其他会话需权限审批。支持 role 过滤、context 上下文行（上限 3）、" +
        "limit（默认 30，上限 100）与 offset 分页。不返回思考内容。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            session_id = new { type = "string", description = "目标会话 ID；缺省为当前会话" },
            pattern = new { type = "string", description = "正则表达式（必填）" },
            role = new
            {
                type = "string",
                @enum = new[] { "system", "user", "assistant", "tool" },
                description = "仅搜索指定角色"
            },
            context = new
            {
                type = "integer",
                minimum = 0,
                maximum = MaxContext,
                description = $"命中消息前后附带的上下文条数（默认 0，上限 {MaxContext}）"
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLimit,
                description = $"返回命中数上限（默认 {DefaultLimit}，上限 {MaxLimit}）"
            },
            offset = new { type = "integer", minimum = 0, description = "分页起始偏移（默认 0）" }
        },
        required = new[] { "pattern" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;
        var pattern = GetStringArgument(arguments, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return Failure("pattern 参数是必需的");

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return Failure($"无效的正则表达式: {ex.Message}");
        }

        var role = GetStringArgument(arguments, "role");
        var contextCount = Math.Clamp(GetIntArgument(arguments, "context") ?? 0, 0, MaxContext);
        var limit = Math.Clamp(GetIntArgument(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Max(GetIntArgument(arguments, "offset") ?? 0, 0);

        var requestedId = GetStringArgument(arguments, "session_id");
        var targetId = string.IsNullOrWhiteSpace(requestedId) ? context.SessionId : requestedId!;

        if (!await IsSameGroupAsync(context.SessionId, targetId, ct).ConfigureAwait(false))
        {
            var denied = await AuthorizeAsync(
                context,
                Id,
                $"搜索会话 {targetId} 需要授权",
                new Dictionary<string, object>
                {
                    ["target_session_id"] = targetId,
                    ["pattern"] = pattern,
                },
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

        var messages = session.GetActiveMessages();

        var hits = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!string.IsNullOrEmpty(role)
                && !string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (regex.IsMatch(GetMessageText(message)))
                hits.Add(i);
        }

        var total = hits.Count;
        var page = hits.Skip(offset).Take(limit).ToList();
        var truncated = offset + page.Count < total;
        var nextOffset = offset + page.Count;

        var lines = new List<string>();
        if (total == 0)
        {
            lines.Add("未找到匹配的消息");
        }
        else
        {
            var hitSet = page.ToHashSet();
            var marked = new SortedSet<int>();
            foreach (var index in page)
            {
                for (var j = Math.Max(0, index - contextCount);
                     j <= Math.Min(messages.Count - 1, index + contextCount);
                     j++)
                {
                    marked.Add(j);
                }
            }

            foreach (var index in marked)
            {
                var prefix = hitSet.Contains(index) ? string.Empty : ">";
                lines.Add(prefix + FormatLine(messages[index]));
            }

            if (truncated)
            {
                lines.Add("");
                lines.Add($"(显示前 {page.Count}，命中 {total}，offset={nextOffset} 续读)");
            }
        }

        return Success(
            string.Join("\n", lines),
            new Dictionary<string, object>
            {
                ["matches"] = total,
                ["returned"] = page.Count,
                ["truncated"] = truncated,
                ["next_offset"] = nextOffset,
            });
    }

    private static string FormatLine(SessionMessage message) =>
        $"{message.Id ?? "-"}:{message.Role}:step{message.Step}:{Preview(GetMessageText(message))}";

    private static string Preview(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxPreviewChars
            ? flat
            : flat[..MaxPreviewChars] + "…";
    }
}
