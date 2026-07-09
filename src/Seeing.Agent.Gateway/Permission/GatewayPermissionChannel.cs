using System.Collections.Concurrent;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Permission;

/// <summary>
/// Gateway 权限通道：interactive 模式下通过 HTTP/WS API 等待客户端响应；auto_approve 模式下自动批准。
/// 权限请求会通过 <see cref="IGatewayEventSink"/> 注入 Chat 事件流。
/// </summary>
public sealed class GatewayPermissionChannel : IPermissionChannel
{
    private static readonly AsyncLocal<PermissionRunContext?> CurrentRun = new();

    private readonly GatewayOptions _options;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public GatewayPermissionChannel(GatewayOptions options)
    {
        _options = options;
    }

    /// <summary>设置当前 Chat 运行的权限上下文（Orchestrator 调用）</summary>
    public static void SetRunContext(PermissionRunContext? context) => CurrentRun.Value = context;

    private bool AutoApprove =>
        string.Equals(_options.PermissionMode, "auto_approve", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        if (AutoApprove)
            return Task.FromResult(true);

        return RequestDecisionAsync("confirmation", request.Permission ?? "permission", request, null)
            .ContinueWith(t => t.Result.Action == PermissionAction.Allow);
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        if (AutoApprove)
            return Task.FromResult(PermissionDecision.Allow());

        return RequestDecisionAsync("tool", toolName, arguments, context);
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        if (AutoApprove)
            return Task.FromResult(PermissionDecision.Allow());

        return RequestDecisionAsync("subagent", agentName, prompt, context);
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        if (AutoApprove)
            return Task.FromResult(PermissionDecision.Allow());

        return RequestDecisionAsync("write", filePath, contentPreview, context);
    }

    /// <summary>获取指定会话的待处理权限请求</summary>
    public IReadOnlyList<GatewayPendingPermission> GetPendingPermissions(string sessionId)
    {
        return _pending.Values
            .Where(p => p.SessionId == sessionId)
            .Select(ToPendingModel)
            .ToList();
    }

    /// <summary>响应权限请求</summary>
    public GatewayPermissionRespondResult Respond(string sessionId, string permissionId, bool allow, string? reason = null)
    {
        if (!_pending.TryGetValue(permissionId, out var entry))
            return GatewayPermissionRespondResult.Fail("权限请求不存在或已过期");

        if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            return GatewayPermissionRespondResult.Fail("sessionId 与权限请求不匹配");

        var decision = allow
            ? PermissionDecision.Allow(reason)
            : PermissionDecision.Deny(reason ?? "用户拒绝");

        entry.Completion.TrySetResult(decision);
        _pending.TryRemove(permissionId, out _);
        return GatewayPermissionRespondResult.Ok();
    }

    /// <summary>取消指定 session 的所有 pending 权限（连接断开时）</summary>
    public void CancelPendingForSession(string sessionId)
    {
        foreach (var (permissionId, entry) in _pending)
        {
            if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                continue;

            entry.Completion.TrySetResult(PermissionDecision.Deny("连接断开"));
            _pending.TryRemove(permissionId, out _);
        }
    }

    private async Task<PermissionDecision> RequestDecisionAsync(
        string kind,
        string resource,
        object? arguments,
        AgentContext? context)
    {
        var permissionId = Guid.NewGuid().ToString("N");
        var runContext = CurrentRun.Value;
        var sessionId = runContext?.SessionId ?? context?.SessionId ?? string.Empty;
        var loopId = runContext?.LoopId;
        var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var entry = new PendingEntry
        {
            PermissionId = permissionId,
            SessionId = sessionId,
            LoopId = loopId,
            PermissionKind = kind,
            Resource = resource,
            Arguments = arguments,
            Message = BuildMessage(kind, resource),
            RiskLevel = kind is "write" or "tool" ? "medium" : "low",
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
            return PermissionDecision.Deny("权限请求超时");
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
        "tool" => $"工具 {resource} 需要权限确认",
        "write" => $"写入文件 {resource} 需要权限确认",
        "subagent" => $"子代理 {resource} 需要权限确认",
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
        public required TaskCompletionSource<PermissionDecision> Completion { get; init; }
    }
}
