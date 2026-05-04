using System;
using System.Collections.Generic;

namespace Seeing.Agent.MCP.Core;

public sealed class McpServerStatusBuilder
{
    public string? Name { get; set; }
    public McpConnectionState? State { get; set; }
    public McpServerConfig? Config { get; set; }
    public McpServerPriority? Priority { get; set; }
    public int? ToolCount { get; set; }
    public IReadOnlyList<string>? ToolNames { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public McpErrorInfo? LastError { get; set; }
    public int? ReconnectAttempts { get; set; }
    public string? ActivePolicy { get; set; }

    private McpServerStatusBuilder() { }

    public static McpServerStatusBuilder From(McpServerStatus status)
        => new()
        {
            Name = status.Name,
            State = status.State,
            Config = status.Config,
            Priority = status.Priority,
            ToolCount = status.ToolCount,
            ToolNames = status.ToolNames,
            CreatedAt = status.CreatedAt,
            LastConnectedAt = status.LastConnectedAt,
            LastErrorAt = status.LastErrorAt,
            LastError = status.LastError,
            ReconnectAttempts = status.ReconnectAttempts,
            ActivePolicy = status.ActivePolicy
        };

    public McpServerStatus Build()
        => new(
            Name ?? throw new InvalidOperationException("Name is required"),
            State ?? McpConnectionState.Pending,
            Config,
            Priority ?? McpServerPriority.Normal,
            ToolCount ?? 0,
            ToolNames ?? Array.Empty<string>(),
            CreatedAt ?? DateTimeOffset.UtcNow,
            LastConnectedAt,
            LastErrorAt,
            LastError,
            ReconnectAttempts ?? 0,
            ActivePolicy);

    public McpServerStatusBuilder WithState(McpConnectionState state)
    {
        State = state;
        return this;
    }

    public McpServerStatusBuilder WithError(McpErrorInfo? error)
    {
        LastError = error;
        LastErrorAt = error is not null ? DateTimeOffset.UtcNow : null;
        return this;
    }

    public McpServerStatusBuilder WithConnected()
    {
        State = McpConnectionState.Connected;
        LastConnectedAt = DateTimeOffset.UtcNow;
        ReconnectAttempts = 0;
        LastError = null;
        LastErrorAt = null;
        return this;
    }

    public McpServerStatusBuilder IncrementReconnect()
    {
        ReconnectAttempts = (ReconnectAttempts ?? 0) + 1;
        return this;
    }

    public McpServerStatusBuilder WithToolCount(int toolCount)
    {
        ToolCount = toolCount;
        return this;
    }

    public McpServerStatusBuilder WithToolNames(IReadOnlyList<string> toolNames)
    {
        ToolNames = toolNames;
        return this;
    }

    public McpServerStatusBuilder WithConfig(McpServerConfig? config)
    {
        Config = config;
        return this;
    }
}