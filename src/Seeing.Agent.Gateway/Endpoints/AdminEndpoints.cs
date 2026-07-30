using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static DateTime _startTime = DateTime.UtcNow;

    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/status", GetStatusAsync);
        app.MapPost("/api/admin/shutdown", ShutdownAsync);
        app.MapGet("/api/admin/sessions", GetSessionsAsync);
        app.MapGet("/api/admin/jobs", GetJobsAsync);
        app.MapGet("/api/admin/channels/connected", GetConnectedChannelsAsync);
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext httpContext,
        GatewayRunTracker runTracker,
        ISessionManager sessionManager,
        IScheduleManager? scheduleManager)
    {
        var uptime = DateTime.UtcNow - _startTime;
        var schedulerRunning = scheduleManager?.IsStarted ?? false;
        var sessions = sessionManager.List();

        return Results.Ok(new
        {
            status = "running",
            uptime = $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
            uptimeSeconds = uptime.TotalSeconds,
            gatewayPort = httpContext.Connection.LocalPort,
            activeSessions = sessions.Count,
            activeExecutions = 0,
            schedulerRunning = schedulerRunning
        });
    }

    private static IResult ShutdownAsync(HttpContext httpContext)
    {
        var lifetime = httpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        return Results.Ok(new { message = "shutting down" });
    }

    private static async Task<IResult> GetSessionsAsync(
        ISessionManager sessionManager,
        CancellationToken ct)
    {
        var sessions = sessionManager.List();
        var result = sessions.Select(s => new
        {
            id = s.Id,
            agent = s.SelectedAgent ?? "-",
            messageCount = s.Messages.Count,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt
        });
        return Results.Ok(result);
    }

    private static async Task<IResult> GetJobsAsync(
        IScheduleManager? scheduleManager,
        CancellationToken ct)
    {
        if (scheduleManager == null)
            return Results.Ok(Array.Empty<object>());

        var statuses = await scheduleManager.GetAllJobStatusesAsync(ct);
        var result = statuses.Select(s => new
        {
            id = s.JobId,
            name = s.JobName ?? s.JobId,
            state = s.State.ToString(),
            previousFireTime = s.PreviousFireTime,
            nextFireTime = s.NextFireTime,
            lastError = s.LastError
        });
        return Results.Ok(result);
    }

    private static IResult GetConnectedChannelsAsync(GatewayConnectionManager connectionManager)
    {
        var channels = connectionManager.GetRegisteredChannels();
        return Results.Ok(new { channels });
    }
}
