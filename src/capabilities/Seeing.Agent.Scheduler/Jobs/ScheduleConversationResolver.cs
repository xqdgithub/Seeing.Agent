using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>
/// 投递目标解析（<see cref="AgentJob"/> / <see cref="HeartbeatJob"/> 共享）。
/// <para>
/// 目标矩阵（设计规格 2026-10-17-cron-conversation-target-and-binding-picker-design）：
/// 显式会话 + conversation → 该会话（无 <c>ChannelRoute</c> 绑定时挂上，first-write-wins 与入站回填一致）；
/// 仅 conversation → 每次触发新建 Root 会话并挂上；无 conversation → 现状（显式会话 / 默认 main）。
/// </para>
/// </summary>
internal static class ScheduleConversationResolver
{
    /// <summary>读取投递 conversation 四键（ChannelId / Kind / ExternalId 齐备才生效）。</summary>
    public static DispatchConversation? ReadConversation(JobDataMap data)
    {
        var channelId = Normalize(data.GetStringValue(JobDataKeys.ConversationChannelId));
        var kind = Normalize(data.GetStringValue(JobDataKeys.ConversationKind))?.ToLowerInvariant();
        var externalId = Normalize(data.GetStringValue(JobDataKeys.ConversationExternalId));
        var title = Normalize(data.GetStringValue(JobDataKeys.ConversationTitle));

        if (channelId is null || kind is null || externalId is null)
            return null;

        return new DispatchConversation
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
            Title = title
        };
    }

    /// <summary>
    /// 解析执行会话（目标矩阵）。未注册 <see cref="ISessionManager"/> 或无 conversation 时退化为现状行为。
    /// </summary>
    /// <param name="services">任务执行服务容器（Scheduler 宿主组合 Seeing.Session）</param>
    /// <param name="configuredSessionId">显式配置的会话 ID（JobDataMap 无 SessionId 键时为 null）</param>
    public static async Task<string> ResolveSessionIdAsync(
        IServiceProvider services,
        JobDataMap data,
        string jobId,
        string? jobName,
        string? configuredSessionId,
        CancellationToken ct)
    {
        var conversation = ReadConversation(data);
        var sessionManager = services.GetService<ISessionManager>();

        if (sessionManager is null || conversation is null)
            return configuredSessionId ?? "main";

        if (configuredSessionId is not null)
        {
            // 显式会话 + conversation：无绑定时挂上（first-write-wins，与入站回填 TryBackfillChannelRoute 一致）
            await AttachRouteAsync(sessionManager, configuredSessionId, conversation).ConfigureAwait(false);
            return configuredSessionId;
        }

        // 仅 conversation：每次触发新建 Root 会话并挂上（cron 每轮独立上下文，避免粘性会话历史膨胀）
        // id = 时间戳前缀（可读/可排序）+ 短随机后缀（同秒多次触发唯一）
        var newSessionId = $"cron_{jobId}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        await sessionManager.EnsureSessionAsync(newSessionId).ConfigureAwait(false);
        await AttachRouteAsync(sessionManager, newSessionId, conversation).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(jobName))
        {
            await sessionManager.SetTitleAsync(
                newSessionId,
                $"⏰ {jobName} {DateTime.Now:HH:mm}",
                ct).ConfigureAwait(false);
        }

        return newSessionId;
    }

    /// <summary>会话无 <see cref="SessionData.ChannelRoute"/> 绑定时挂上 conversation（first-write-wins）。</summary>
    public static async Task AttachRouteAsync(
        ISessionManager sessionManager,
        string sessionId,
        DispatchConversation conversation)
    {
        var session = await sessionManager.EnsureSessionAsync(sessionId).ConfigureAwait(false);
        if (session.ChannelRoute is null || string.IsNullOrWhiteSpace(session.ChannelRoute.ChannelId))
        {
            session.ChannelRoute = ToRoute(conversation);
            await sessionManager.SaveAsync(session.Id).ConfigureAwait(false);
        }
    }

    private static SessionChannelRoute ToRoute(DispatchConversation conversation) => new()
    {
        ChannelId = conversation.ChannelId,
        Kind = conversation.Kind,
        ExternalId = conversation.ExternalId,
        Title = conversation.Title
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
