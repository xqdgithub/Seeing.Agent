using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.Support;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Tools.Session;

/// <summary>
/// 会话工具共享基类：会话解析、同组判定与跨组授权。
/// </summary>
public abstract class SessionToolBase : ToolBase
{
    /// <summary>会话管理器。</summary>
    protected ISessionManager Sessions { get; }

    /// <summary>会话组管理器。</summary>
    protected ISessionGroupManager Groups { get; }

    /// <summary>创建会话工具基类实例。</summary>
    protected SessionToolBase(
        ILogger logger,
        ISessionManager sessions,
        ISessionGroupManager groups) : base(logger)
    {
        Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Groups = groups ?? throw new ArgumentNullException(nameof(groups));
    }

    /// <summary>解析目标会话；<paramref name="sessionId"/> 为空时回退当前会话。</summary>
    protected Task<SessionData> ResolveSessionAsync(
        string? sessionId, ToolContext context, CancellationToken ct) =>
        Sessions.GetOrLoadAsync(
            string.IsNullOrWhiteSpace(sessionId) ? context.SessionId : sessionId!,
            ct);

    /// <summary>目标会话是否与当前会话同组（同会话天然视为同组）。</summary>
    protected async Task<bool> IsSameGroupAsync(
        string currentSessionId, string targetSessionId, CancellationToken ct)
    {
        if (string.Equals(currentSessionId, targetSessionId, StringComparison.Ordinal))
            return true;

        var group = await Groups.GetGroupForSessionAsync(currentSessionId, ct).ConfigureAwait(false);
        return group is not null
            && group.Members.Any(m => string.Equals(m.SessionId, targetSessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 跨组访问授权：授权工厂缺失时放行（宿主未接入授权链）；
    /// 拒绝返回 <see cref="ToolResult"/>，允许返回 <c>null</c>。
    /// </summary>
    protected async Task<ToolResult?> AuthorizeAsync(
        ToolContext context,
        string resource,
        string message,
        IReadOnlyDictionary<string, object> metadata,
        CancellationToken ct)
    {
        var authorizer = context.PermissionAuthorizer;
        if (authorizer is null)
        {
            var factory = context.Services?.GetService<IPermissionAuthorizerFactory>();
            if (factory is null)
                return null;

            authorizer = factory.Create(context.SessionId, null);
        }

        var resolution = await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = context.SessionId,
            CallId = context.CallId,
            PermissionKind = "tool.execute",
            Resource = resource,
            Message = message,
            Metadata = metadata,
        }, ct).ConfigureAwait(false);

        return resolution.Decision == PermissionEffect.Allow
            ? null
            : Failure(resolution.Reason ?? "权限被拒绝");
    }

    /// <summary>消息可展示文本（Content 优先，否则拼接文本段）；绝不包含思考内容。</summary>
    protected static string GetMessageText(SessionMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
            return message.Content;

        if (message.Parts is { Count: > 0 })
        {
            return string.Join(
                "\n",
                message.Parts
                    .Where(p => !string.IsNullOrEmpty(p.Text))
                    .Select(p => p.Text));
        }

        return string.Empty;
    }
}
