using System;
using System.Collections.Generic;

namespace Seeing.Agent.MCP.Core;

public sealed class McpServerStatus
{
    public string Name { get; }
    public McpConnectionState State { get; }
    public McpServerConfig? Config { get; }
    public McpServerPriority Priority { get; }
    public int ToolCount { get; }
    public IReadOnlyList<string> ToolNames { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastConnectedAt { get; }
    public DateTimeOffset? LastErrorAt { get; }
    public McpErrorInfo? LastError { get; }
    public int ReconnectAttempts { get; }
    public string? ActivePolicy { get; }

    public bool IsDisabled => State == McpConnectionState.Disabled || Config?.Disabled == true;
    public bool IsAvailable => State == McpConnectionState.Connected && !IsDisabled;
    public bool CanReconnect => !IsDisabled && State is McpConnectionState.Error or McpConnectionState.Reconnecting;

    internal McpServerStatus(
        string name,
        McpConnectionState state,
        McpServerConfig? config,
        McpServerPriority priority,
        int toolCount,
        IReadOnlyList<string> toolNames,
        DateTimeOffset createdAt,
        DateTimeOffset? lastConnectedAt,
        DateTimeOffset? lastErrorAt,
        McpErrorInfo? lastError,
        int reconnectAttempts,
        string? activePolicy)
    {
        Name = name;
        State = state;
        Config = config;
        Priority = priority;
        ToolCount = toolCount;
        ToolNames = toolNames;
        CreatedAt = createdAt;
        LastConnectedAt = lastConnectedAt;
        LastErrorAt = lastErrorAt;
        LastError = lastError;
        ReconnectAttempts = reconnectAttempts;
        ActivePolicy = activePolicy;
    }

    public static McpServerStatus Create(
        string name,
        McpServerConfig? config = null,
        McpServerPriority priority = McpServerPriority.Normal)
        => new(name, McpConnectionState.Pending, config, priority,
            0, Array.Empty<string>(), DateTimeOffset.UtcNow,
            null, null, null, 0, null);

    public McpServerStatus Clone() => new(
        Name, State, Config, Priority, ToolCount,
        new List<string>(ToolNames), CreatedAt,
        LastConnectedAt, LastErrorAt, LastError,
        ReconnectAttempts, ActivePolicy);

    internal McpServerStatus WithBuilder(McpServerStatusBuilder builder)
        => new(
            builder.Name ?? Name,
            builder.State ?? State,
            builder.Config ?? Config,
            builder.Priority ?? Priority,
            builder.ToolCount ?? ToolCount,
            builder.ToolNames ?? ToolNames,
            builder.CreatedAt ?? CreatedAt,
            builder.LastConnectedAt ?? LastConnectedAt,
            builder.LastErrorAt ?? LastErrorAt,
            builder.LastError ?? LastError,
            builder.ReconnectAttempts ?? ReconnectAttempts,
            builder.ActivePolicy ?? ActivePolicy);
}