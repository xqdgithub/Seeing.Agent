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
        app.MapPost("/api/gateway/chat", ChatAsync);
        app.MapPost("/api/gateway/chat/stop", StopChatAsync);
        app.MapGet("/api/gateway/permissions/pending", GetPendingPermissionsAsync);
        app.MapPost("/api/gateway/permissions/{id}/respond", RespondPermissionAsync);
        app.MapGet("/api/gateway/health", HealthAsync);

        return app;
    }

    private static async Task ChatAsync(
        HttpContext httpContext,
        GatewayOrchestrator orchestrator,
        [FromBody] GatewayRequest request,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        await foreach (var gatewayEvent in orchestrator.ExecuteChatAsync(request, cancellationToken))
        {
            var json = JsonSerializer.Serialize(gatewayEvent, JsonOptions);
            await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static IResult StopChatAsync(
        [FromQuery] string sessionId,
        GatewayOrchestrator orchestrator)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Results.BadRequest(new { error = "sessionId is required" });

        var stopped = orchestrator.StopChat(sessionId);
        return Results.Ok(new { sessionId, stopped });
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
            timestamp = DateTime.UtcNow
        });
}

/// <summary>权限响应请求体</summary>
public record GatewayPermissionRespondRequest
{
    public required string SessionId { get; init; }

    public bool Allow { get; init; }

    public string? Reason { get; init; }
}
