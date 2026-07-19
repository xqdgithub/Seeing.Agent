using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Endpoints;

/// <summary>
/// Gateway Minimal API 端点注册
/// </summary>
public static class GatewayEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>注册所有 Gateway API 端点</summary>
    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapPost("/api/gateway/submit", SubmitAsync);
        app.MapPost("/api/gateway/cancel", CancelAsync);
        app.MapGet("/api/gateway/events", SubscribeEventsAsync);
        app.MapPost("/api/gateway/sessions/{sessionId}/reset", ResetSessionAsync);
        app.MapGet("/api/gateway/permissions/pending", GetPendingPermissionsAsync);
        app.MapPost("/api/gateway/permissions/{id}/respond", RespondPermissionAsync);
        app.MapGet("/api/gateway/health", HealthAsync);

        return app;
    }

    private static async Task<IResult> SubmitAsync(
        GatewayOrchestratorV2 orchestrator,
        [FromBody] GatewayRequest request,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    private static IResult CancelAsync(
        GatewayOrchestratorV2 orchestrator,
        [FromBody] GatewayCancelRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.ExecutionId))
            return Results.BadRequest(new { error = "executionId is required" });

        var cancelled = orchestrator.Cancel(body.ExecutionId);
        return Results.Ok(new { executionId = body.ExecutionId, cancelled });
    }

    private static async Task SubscribeEventsAsync(
        HttpContext httpContext,
        GatewayOrchestratorV2 orchestrator,
        [FromQuery] string sessionId,
        [FromQuery] string executionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(executionId))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "sessionId and executionId are required" }, cancellationToken);
            return;
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        await foreach (var gatewayEvent in orchestrator.SubscribeExecutionEventsAsync(sessionId, executionId, cancellationToken))
        {
            var json = JsonSerializer.Serialize(gatewayEvent, JsonOptions);
            await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static async Task<IResult> ResetSessionAsync(
        string sessionId,
        GatewaySessionService sessionService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Results.BadRequest(new { error = "sessionId is required" });

        var result = await sessionService.ResetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return result == null
            ? Results.NotFound(new { error = "session not found", sessionId })
            : Results.Ok(result);
    }

    private static IResult GetPendingPermissionsAsync(
        [FromQuery] string sessionId,
        GatewayPermissionChannel permissionChannel)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Results.BadRequest(new { error = "sessionId is required" });

        var pending = permissionChannel.GetPendingPermissions(sessionId);
        return Results.Ok(pending);
    }

    private static IResult RespondPermissionAsync(
        string id,
        [FromBody] GatewayPermissionRespondRequest body,
        GatewayPermissionChannel permissionChannel)
    {
        if (string.IsNullOrWhiteSpace(body.SessionId))
            return Results.BadRequest(new { error = "sessionId is required" });

        var result = permissionChannel.Respond(body.SessionId, id, body.Allow, body.Reason);
        return result.Success
            ? Results.Ok(result)
            : Results.NotFound(result);
    }

    private static IResult HealthAsync(GatewayRunTracker runTracker) =>
        Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.Now
        });
}

/// <summary>取消执行请求体</summary>
public record GatewayCancelRequest
{
    public required string ExecutionId { get; init; }
}

/// <summary>权限响应请求体</summary>
public record GatewayPermissionRespondRequest
{
    public required string SessionId { get; init; }

    public bool Allow { get; init; }

    public string? Reason { get; init; }
}
