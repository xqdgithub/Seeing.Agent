using System;
using System.Collections.Generic;

namespace Seeing.Agent.MCP.Core;

internal static class McpStateTransitions
{
    private static readonly Dictionary<McpConnectionState, HashSet<McpConnectionState>> AllowedTransitions = new()
    {
        [McpConnectionState.Pending] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Connecting,
            McpConnectionState.Disabled,
            McpConnectionState.Removed
        },
        [McpConnectionState.Connecting] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Connected,
            McpConnectionState.Disabled,
            McpConnectionState.Paused,
            McpConnectionState.Error,
            McpConnectionState.Removed
        },
        [McpConnectionState.Connected] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Pending,
            McpConnectionState.Paused,
            McpConnectionState.Disabled,
            McpConnectionState.Reconnecting,
            McpConnectionState.Error,
            McpConnectionState.Removed
        },
        [McpConnectionState.Paused] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Pending,
            McpConnectionState.Connecting,
            McpConnectionState.Disabled,
            McpConnectionState.Reconnecting,
            McpConnectionState.Removed
        },
        [McpConnectionState.Disabled] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Connecting,
            McpConnectionState.Pending,
            McpConnectionState.Removed
        },
        [McpConnectionState.Reconnecting] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Connected,
            McpConnectionState.Pending,
            McpConnectionState.Disabled,
            McpConnectionState.Paused,
            McpConnectionState.Error,
            McpConnectionState.Removed
        },
        [McpConnectionState.Error] = new HashSet<McpConnectionState>
        {
            McpConnectionState.Reconnecting,
            McpConnectionState.Connecting,
            McpConnectionState.Disabled,
            McpConnectionState.Paused,
            McpConnectionState.Removed
        },
        [McpConnectionState.Removed] = new HashSet<McpConnectionState>()
    };

    public static bool CanTransition(McpConnectionState from, McpConnectionState to)
    {
        if (from == to) return true;
        return AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static void ValidateTransition(McpConnectionState from, McpConnectionState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"无效的状态转换: {from} -> {to}。" +
                $"允许的转换: {GetAllowedTransitionsString(from)}");
        }
    }

    private static string GetAllowedTransitionsString(McpConnectionState state)
    {
        if (!AllowedTransitions.TryGetValue(state, out var allowed) || allowed.Count == 0)
            return "无";

        return string.Join(", ", allowed);
    }
}
