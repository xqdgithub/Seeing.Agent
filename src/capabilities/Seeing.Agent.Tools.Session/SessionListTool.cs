using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Tools.Session;

/// <summary>
/// 会话列表工具 — 列出与当前会话相关的会话，或经授权列出同分区全部会话。
/// </summary>
public sealed class SessionListTool : SessionToolBase
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    /// <summary>创建 SessionListTool 实例。</summary>
    public SessionListTool(
        ILogger<SessionListTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger, sessions, groups)
    {
    }

    /// <inheritdoc />
    public override string Id => "session_list";

    /// <inheritdoc />
    public override string Description =>
        "列出会话。scope=related（默认）返回当前会话所在组的成员；" +
        "scope=all 返回同分区全部会话（需权限审批）。" +
        "支持 limit（默认 30，上限 100）与 offset 分页。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            scope = new
            {
                type = "string",
                @enum = new[] { "related", "all" },
                description = "related=当前会话组内成员（默认）；all=同分区全部会话（需审批）"
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLimit,
                description = $"返回条数上限（默认 {DefaultLimit}，上限 {MaxLimit}）"
            },
            offset = new
            {
                type = "integer",
                minimum = 0,
                description = "分页起始偏移（默认 0）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;
        var scope = GetStringArgument(arguments, "scope") ?? "related";
        var limit = Math.Clamp(GetIntArgument(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Max(GetIntArgument(arguments, "offset") ?? 0, 0);

        List<(SessionData Session, SessionRelation Relation)> entries;
        switch (scope)
        {
            case "related":
                entries = await CollectRelatedAsync(context, ct).ConfigureAwait(false);
                break;

            case "all":
            {
                var denied = await AuthorizeAsync(
                    context,
                    Id,
                    "列出全部会话需要授权",
                    new Dictionary<string, object> { ["scope"] = "all" },
                    ct).ConfigureAwait(false);
                if (denied is not null)
                    return denied;

                entries = await CollectAllAsync(context, ct).ConfigureAwait(false);
                break;
            }

            default:
                return Failure($"未知的 scope: {scope}。可选值：related、all");
        }

        var total = entries.Count;
        var page = entries.Skip(offset).Take(limit).ToList();
        var truncated = offset + page.Count < total;
        var nextOffset = offset + page.Count;

        var lines = new List<string>();
        if (page.Count == 0)
        {
            lines.Add("未找到会话");
        }
        else
        {
            foreach (var (session, relation) in page)
            {
                lines.Add(string.Join(" | ",
                    session.Id,
                    relation,
                    session.Kind,
                    session.Title,
                    session.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                    session.MessageCount));
            }

            if (truncated)
            {
                lines.Add("");
                lines.Add($"(显示 {page.Count}/{total}，offset={nextOffset} 续读)");
            }
        }

        return Success(
            string.Join("\n", lines),
            new Dictionary<string, object>
            {
                ["scope"] = scope,
                ["total"] = total,
                ["returned"] = page.Count,
                ["truncated"] = truncated,
                ["next_offset"] = nextOffset,
            });
    }

    private async Task<List<(SessionData, SessionRelation)>> CollectRelatedAsync(
        ToolContext context, CancellationToken ct)
    {
        var result = new List<(SessionData, SessionRelation)>();
        var group = await Groups.GetGroupForSessionAsync(context.SessionId, ct).ConfigureAwait(false);
        if (group is null)
        {
            var current = await Sessions.GetOrLoadAsync(context.SessionId, ct).ConfigureAwait(false);
            result.Add((current, SessionRelation.None));
            return result;
        }

        var members = await Groups.ListMembersAsync(group.Id, ct).ConfigureAwait(false);
        foreach (var member in members)
        {
            var session = await Sessions.GetOrLoadAsync(member.SessionId, ct).ConfigureAwait(false);
            result.Add((session, member.Relation));
        }

        return result;
    }

    private async Task<List<(SessionData, SessionRelation)>> CollectAllAsync(
        ToolContext context, CancellationToken ct)
    {
        var current = await Sessions.GetOrLoadAsync(context.SessionId, ct).ConfigureAwait(false);
        var partition = current.PartitionId;
        var all = await Sessions.LoadAllFromStorageAsync(ct).ConfigureAwait(false);

        var result = new List<(SessionData, SessionRelation)>();
        foreach (var session in all)
        {
            if (session.IsArchived)
                continue;
            if (!string.Equals(session.PartitionId, partition, StringComparison.Ordinal))
                continue;

            result.Add((session, await ResolveRelationAsync(session, ct).ConfigureAwait(false)));
        }

        return result;
    }

    private async Task<SessionRelation> ResolveRelationAsync(SessionData session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.GroupId))
            return SessionRelation.None;

        var group = await Groups.GetGroupAsync(session.GroupId, ct).ConfigureAwait(false);
        var member = group?.Members.FirstOrDefault(m => string.Equals(m.SessionId, session.Id, StringComparison.Ordinal));
        return member?.Relation ?? SessionRelation.None;
    }
}
