using System.Text.Json;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.QQ;

public sealed class QQPermissionPolicy
{
    private readonly QQOptions _options;

    public QQPermissionPolicy(IOptions<QQOptions> options) => _options = options.Value;

    public bool ShouldPrompt(GatewayEvent permissionEvent)
    {
        var risk = permissionEvent.Data?.RiskLevel?.ToLowerInvariant();
        var kind = permissionEvent.Data?.PermissionKind?.ToLowerInvariant();

        if (_options.AutoApproveLowRisk
            && (risk is null or "low" or "medium")
            && (kind is null || !_options.PromptPermissionKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (risk != null && _options.PromptRiskLevels.Contains(risk, StringComparer.OrdinalIgnoreCase))
            return true;

        if (kind != null && _options.PromptPermissionKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

public sealed class QQPermissionState
{
    private readonly Dictionary<string, (string SessionId, string PermissionId, DateTimeOffset Expires)> _map = new();
    private readonly object _lock = new();

    public void Remember(string requestId, string sessionId, string permissionId, TimeSpan ttl)
    {
        lock (_lock)
        {
            _map[requestId] = (sessionId, permissionId, DateTimeOffset.Now + ttl);
        }
    }

    public bool TryTake(string requestId, out string sessionId, out string permissionId)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(requestId, out var entry) && entry.Expires > DateTimeOffset.Now)
            {
                _map.Remove(requestId);
                sessionId = entry.SessionId;
                permissionId = entry.PermissionId;
                return true;
            }
        }

        sessionId = "";
        permissionId = "";
        return false;
    }
}

public static class QQPermissionCardBuilder
{
    public const string ActionPrefix = "seeing_perm:";

    public static object BuildKeyboard(string requestId) => new
    {
        content = new
        {
            rows = new object[]
            {
                new
                {
                    buttons = new object[]
                    {
                        new
                        {
                            id = "1",
                            render_data = new { label = "批准", style = 1 },
                            action = new
                            {
                                type = 2,
                                data = ActionPrefix + "allow:" + requestId,
                                permission = new { type = 2 }
                            }
                        },
                        new
                        {
                            id = "2",
                            render_data = new { label = "拒绝", style = 2 },
                            action = new
                            {
                                type = 2,
                                data = ActionPrefix + "deny:" + requestId,
                                permission = new { type = 2 }
                            }
                        }
                    }
                }
            }
        }
    };

    public static bool TryParseAction(string? data, out bool allow, out string requestId)
    {
        allow = false;
        requestId = "";
        if (string.IsNullOrEmpty(data) || !data.StartsWith(ActionPrefix, StringComparison.Ordinal))
            return false;

        var rest = data[ActionPrefix.Length..];
        var parts = rest.Split(':', 2);
        if (parts.Length != 2)
            return false;

        allow = parts[0].Equals("allow", StringComparison.OrdinalIgnoreCase);
        requestId = parts[1];
        return !string.IsNullOrEmpty(requestId);
    }
}

public sealed class QQPermissionResponder
{
    private readonly Cards.QQCardDispatcher _dispatcher;

    public QQPermissionResponder(Cards.QQCardDispatcher dispatcher) => _dispatcher = dispatcher;

    public Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken cancellationToken) =>
        _dispatcher.TryHandleInteractionAsync(d, cancellationToken);
}
