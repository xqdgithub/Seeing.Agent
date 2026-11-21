using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Endpoints;

/// <summary>
/// 会话内后台活动的控制端点。
/// <para>
/// 目前只有「转后台」：让前台工具调用的等待者以 Detached 返回，活动本身继续运行。
/// 成功的判定完全交给活动事件推送（<c>activity.*</c>），端点只回传是否被接受。
/// </para>
/// <para>
/// 越权/不存在一律 <c>200 + { ok: false }</c>：这是用户点按钮的入口，不是 API 语义错误，
/// 在 UI 侧抛 4xx/5xx 只会让 Blazor 的 <c>HttpClient</c> 变成未处理异常。
/// 归属校验按会话列出活动完成——不能只凭活动 id 放行。
/// </para>
/// </summary>
public static class SessionActivityEndpoints
{
    /// <summary>「转后台」路由模板。</summary>
    public const string DetachRoute =
        "/api/webui/sessions/{sessionId}/activities/{activityId}/detach";

    /// <summary>映射会话活动端点。</summary>
    public static IEndpointRouteBuilder MapSessionActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(DetachRoute, HandleDetachAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleDetachAsync(
        string sessionId,
        string activityId,
        BackgroundActivityUiService activities,
        ISessionManager sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(activityId))
            return Results.Json(new { ok = false });

        if (sessions.Get(sessionId) is null)
            return Results.Json(new { ok = false });

        // 越权保护：活动必须归属当前会话。List 是按会话过滤的唯一读口，
        // 直接调 control.DetachAsync 会把别的会话的前台等待者踢走。
        var owned = activities.List(sessionId).Any(
            activity => string.Equals(activity.Id, activityId, StringComparison.Ordinal));

        if (!owned)
            return Results.Json(new { ok = false });

        var ok = await activities.DetachAsync(activityId, ct);
        return Results.Json(new { ok });
    }
}
