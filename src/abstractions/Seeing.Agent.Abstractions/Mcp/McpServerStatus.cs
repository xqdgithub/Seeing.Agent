namespace Seeing.Agent.Abstractions.Mcp;

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

    /// <summary>握手后能力摘要；未连接或尚未探测时为 null。</summary>
    public McpServerCapabilitiesInfo? Capabilities { get; }

    /// <summary>list 成功后的资源数；未支持或未探测时为 null。</summary>
    public int? ResourceCount { get; }

    /// <summary>list 成功后的 prompt 数；未支持或未探测时为 null。</summary>
    public int? PromptCount { get; }

    /// <summary>list 成功后的 skill 数；未支持或未探测时为 null。</summary>
    public int? SkillCount { get; }

    public bool IsDisabled => State == McpConnectionState.Disabled || Config?.Disabled == true;
    public bool IsAvailable => State == McpConnectionState.Connected && !IsDisabled;
    public bool CanReconnect => !IsDisabled && State is McpConnectionState.Error or McpConnectionState.Reconnecting;

    public McpServerStatus(
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
        string? activePolicy,
        McpServerCapabilitiesInfo? capabilities = null,
        int? resourceCount = null,
        int? promptCount = null,
        int? skillCount = null)
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
        Capabilities = capabilities;
        ResourceCount = resourceCount;
        PromptCount = promptCount;
        SkillCount = skillCount;
    }

    public static McpServerStatus Create(
        string name,
        McpServerConfig? config = null,
        McpServerPriority priority = McpServerPriority.Normal)
        => new(name, McpConnectionState.Pending, config, priority,
            0, Array.Empty<string>(), DateTimeOffset.Now,
            null, null, null, 0, null);

    public McpServerStatus Clone() => new(
        Name, State, Config, Priority, ToolCount,
        new List<string>(ToolNames), CreatedAt,
        LastConnectedAt, LastErrorAt, LastError,
        ReconnectAttempts, ActivePolicy,
        Capabilities, ResourceCount, PromptCount, SkillCount);
}
