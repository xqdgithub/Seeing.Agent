using System.Collections.Concurrent;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Permission;

public sealed class GatewayPermissionChannel : IPermissionChannel
{
    private static readonly AsyncLocal<PermissionRunContext?> CurrentRun = new();

    private readonly GatewayOptions _options;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public GatewayPermissionChannel(GatewayOptions options)
    {
        _options = options;
    }

    public static void SetRunContext(PermissionRunContext? context) => CurrentRun.Value = context;

    private bool AutoApprove =>
        string.Equals(_options.PermissionMode, "auto_approve", StringComparison.OrdinalIgnoreCase);

    public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        if (AutoApprove)
            return PermissionChannelResult.Allowed();

        return await RequestDecisionAsync(request.PermissionKind, request.Resource ?? string.Empty, request.Metadata, request.SessionId);
    }

    public IReadOnlyList<GatewayPendingPermission> GetPendingPermissions(string sessionId)
    {
        return _pending.Values
            .Where(p => p.SessionId == sessionId)
            .Select(ToPendingModel)
            .ToList();
    }

    public GatewayPermissionRespondResult Respond(string sessionId, string permissionId, bool allow, string? reason = null, string? resourceToRemember = null)
    {
        if (!_pending.TryGetValue(permissionId, out var entry))
            return GatewayPermissionRespondResult.Fail("权限请求不存在或已过期");

        if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            return GatewayPermissionRespondResult.Fail("sessionId 与权限请求不匹配");

        var result = allow
            ? PermissionChannelResult.Allowed(resourceToRemember)
            : PermissionChannelResult.Denied(reason ?? "用户拒绝", resourceToRemember);

        entry.Completion.TrySetResult(result);
        _pending.TryRemove(permissionId, out _);
        return GatewayPermissionRespondResult.Ok();
    }

    public void CancelPendingForSession(string sessionId)
    {
        foreach (var (permissionId, entry) in _pending)
        {
            if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                continue;

            entry.Completion.TrySetResult(PermissionChannelResult.Denied("连接断开"));
            _pending.TryRemove(permissionId, out _);
        }
    }

    private async Task<PermissionChannelResult> RequestDecisionAsync(
        string kind, string resource, object? arguments, string? sessionId)
    {
        var permissionId = Guid.NewGuid().ToString("N");
        var runContext = CurrentRun.Value;
        var sid = runContext?.SessionId ?? sessionId ?? string.Empty;
        var loopId = runContext?.LoopId;
        var tcs = new TaskCompletionSource<PermissionChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var entry = new PendingEntry
        {
            PermissionId = permissionId,
            SessionId = sid,
            LoopId = loopId,
            PermissionKind = kind,
            Resource = resource,
            Arguments = arguments,
            Message = BuildMessage(kind, resource),
            RiskLevel = kind is "filesystem.write" or "shell.execute" ? "high" : "medium",
            CreatedAt = DateTime.Now,
            Completion = tcs
        };

        _pending[permissionId] = entry;

        var pendingModel = ToPendingModel(entry);
        runContext?.Sink.Emit(GatewayEventMapper.MapPendingPermission(pendingModel));

        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.PermissionTimeoutSeconds));
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(permissionId, out _);
            return PermissionChannelResult.Denied("权限请求超时");
        }
    }

    private static GatewayPendingPermission ToPendingModel(PendingEntry entry) => new()
    {
        PermissionId = entry.PermissionId,
        SessionId = entry.SessionId,
        LoopId = entry.LoopId,
        PermissionKind = entry.PermissionKind,
        Resource = entry.Resource,
        Arguments = entry.Arguments,
        Message = entry.Message,
        RiskLevel = entry.RiskLevel,
        CreatedAt = entry.CreatedAt
    };

    private static string BuildMessage(string kind, string resource) => kind switch
    {
        "tool.execute" => $"工具 {resource} 需要权限确认",
        "filesystem.write" => $"写入文件 {resource} 需要权限确认",
        _ => $"操作 {resource} 需要权限确认"
    };

    private sealed class PendingEntry
    {
        public required string PermissionId { get; init; }
        public required string SessionId { get; init; }
        public string? LoopId { get; init; }
        public required string PermissionKind { get; init; }
        public required string Resource { get; init; }
        public object? Arguments { get; init; }
        public required string Message { get; init; }
        public string RiskLevel { get; init; } = "medium";
        public DateTime CreatedAt { get; init; }
        public required TaskCompletionSource<PermissionChannelResult> Completion { get; init; }
    }
}
