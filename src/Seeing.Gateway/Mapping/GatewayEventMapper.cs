using Seeing.Agent.Core.Events;
using Seeing.Agent.Llm;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.Mapping;

/// <summary>
/// 将 Seeing.Agent 的 <see cref="IMessageEvent"/> 映射为 <see cref="GatewayEvent"/>。
/// <para>
/// 完成信号契约：渠道侧 finish=true 仅响应 <see cref="LoopCompleteEvent"/> 或 WS chat.complete；
/// <see cref="StreamCompleteEvent"/>（assistant）映射为 Message/Completed，不表示整轮对话结束。
/// </para>
/// </summary>
public static class GatewayEventMapper
{
    public static GatewayEvent Map(IMessageEvent source, GatewayEventMapperOptions? options = null)
    {
        options ??= new GatewayEventMapperOptions();

        if (source is StreamDeltaEvent deltaEvent)
        {
            var mapped = MapStreamDelta(deltaEvent, options)
                ?? throw new NotSupportedException($"Stream delta filtered or empty: {source.Type}");
            return WithMeta(deltaEvent, mapped);
        }

        return source switch
        {
            LoopStartEvent e => WithMeta(e, MapLoopStart(e)),
            StreamStartEvent e => WithMeta(e, MapStreamStart(e)),
            StreamCompleteEvent e => WithMeta(e, MapStreamComplete(e)),
            ToolCallEvent e => WithMeta(e, MapToolCall(e)),
            PermissionRequestEvent e => WithMeta(e, MapPermissionRequest(e)),
            PermissionResponseEvent e => WithMeta(e, MapPermissionResponse(e)),
            LoopCompleteEvent e => WithMeta(e, MapLoopComplete(e)),
            LoopCancelledEvent e => WithMeta(e, MapLoopCancelled(e)),
            ErrorEvent e => WithMeta(e, MapError(e)),
            SubAgentEvent e => WithMeta(e, MapSubAgent(e)),
            _ => throw new NotSupportedException($"Unsupported message event type: {source.Type}")
        };
    }

    public static bool TryMap(
        IMessageEvent source,
        out GatewayEvent? gatewayEvent,
        GatewayEventMapperOptions? options = null)
    {
        options ??= new GatewayEventMapperOptions();

        if (source is StreamDeltaEvent deltaEvent)
        {
            var mapped = MapStreamDelta(deltaEvent, options);
            if (mapped == null)
            {
                gatewayEvent = null;
                return false;
            }

            gatewayEvent = WithMeta(deltaEvent, mapped);
            return true;
        }

        try
        {
            gatewayEvent = Map(source, options);
            return true;
        }
        catch (NotSupportedException)
        {
            gatewayEvent = null;
            return false;
        }
    }

    /// <summary>从待处理权限构建 Permission 事件（与 PermissionRequest 映射 payload 一致）</summary>
    public static GatewayEvent MapPendingPermission(GatewayPendingPermission pending) => new()
    {
        Object = GatewayEventObject.Permission,
        Status = GatewayEventStatus.InProgress,
        SessionId = pending.SessionId,
        LoopId = pending.LoopId,
        Timestamp = pending.CreatedAt,
        SourceType = MessageEventType.PermissionRequest.ToString(),
        Data = new GatewayEventData
        {
            PermissionId = pending.PermissionId,
            PermissionKind = pending.PermissionKind,
            Resource = pending.Resource,
            PermissionArguments = pending.Arguments,
            PermissionMessage = pending.Message,
            RiskLevel = pending.RiskLevel
        }
    };

    private static GatewayEvent WithMeta(IMessageEvent source, GatewayEvent gatewayEvent) =>
        gatewayEvent with
        {
            Timestamp = source.Timestamp,
            SourceType = source.Type.ToString()
        };

    private static GatewayEvent MapLoopStart(LoopStartEvent e) => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.InProgress,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            UserInput = e.UserInput
        }
    };

    private static GatewayEvent MapStreamStart(StreamStartEvent e) => new()
    {
        Object = GatewayEventObject.Content,
        Status = GatewayEventStatus.InProgress,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            Step = e.Step
        }
    };

    private static GatewayEvent? MapStreamDelta(StreamDeltaEvent e, GatewayEventMapperOptions options)
    {
        var hasContent = !string.IsNullOrEmpty(e.ContentDelta);
        var hasReasoning = !string.IsNullOrEmpty(e.ReasoningDelta);

        if (!hasContent && (!hasReasoning || options.FilterThinking))
            return null;

        var streamKind = hasContent ? "content" : "reasoning";

        return new GatewayEvent
        {
            Object = GatewayEventObject.Content,
            Status = GatewayEventStatus.InProgress,
            SessionId = e.SessionId,
            LoopId = e.LoopId,
            Data = new GatewayEventData
            {
                Delta = true,
                Text = e.ContentDelta,
                Reasoning = options.FilterThinking ? null : e.ReasoningDelta,
                StreamKind = streamKind,
                Usage = GatewayTokenUsage.FromTokenUsage(e.Usage)
            }
        };
    }

    private static GatewayEvent MapStreamComplete(StreamCompleteEvent e)
    {
        var role = e.Message.Role.ToString().ToLowerInvariant();
        var isTool = e.Message.Role == ChatRole.Tool;

        return new GatewayEvent
        {
            Object = isTool ? GatewayEventObject.Content : GatewayEventObject.Message,
            Status = isTool ? GatewayEventStatus.InProgress : GatewayEventStatus.Completed,
            SessionId = e.SessionId,
            LoopId = e.LoopId,
            Data = new GatewayEventData
            {
                Role = e.Message.Role,
                MessageRole = role,
                Text = e.Message.Content,
                Reasoning = e.Message.ReasoningContent,
                StreamKind = isTool ? "tool" : "content",
                Usage = GatewayTokenUsage.FromTokenUsage(e.Usage)
            }
        };
    }

    private static GatewayEvent MapToolCall(ToolCallEvent e) => new()
    {
        Object = GatewayEventObject.Content,
        Status = GatewayEventStatus.InProgress,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            ToolCallId = e.ToolCallId,
            ToolName = e.ToolName,
            ToolArguments = e.Arguments,
            ToolStatus = e.Status.ToString().ToLowerInvariant(),
            ToolOutput = e.Output,
            ToolError = e.Error,
            ToolTitle = e.Title,
            Duration = e.Duration,
            StreamKind = "tool"
        }
    };

    private static GatewayEvent MapPermissionRequest(PermissionRequestEvent e) => new()
    {
        Object = GatewayEventObject.Permission,
        Status = GatewayEventStatus.InProgress,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            PermissionId = e.PermissionId,
            PermissionKind = e.PermissionKind,
            Resource = e.Resource,
            PermissionArguments = e.Arguments,
            PermissionMessage = e.Message,
            RiskLevel = e.RiskLevel
        }
    };

    private static GatewayEvent MapPermissionResponse(PermissionResponseEvent e) => new()
    {
        Object = GatewayEventObject.Permission,
        Status = GatewayEventStatus.Completed,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            PermissionId = e.PermissionId,
            PermissionDecision = e.Decision,
            PermissionReason = e.Reason
        }
    };

    private static GatewayEvent MapLoopComplete(LoopCompleteEvent e) => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.Completed,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            TotalSteps = e.TotalSteps,
            Success = e.Success,
            Error = e.Error,
            Duration = e.Duration,
            Usage = GatewayTokenUsage.FromTokenUsage(e.Usage)
        }
    };

    private static GatewayEvent MapLoopCancelled(LoopCancelledEvent e) => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.Cancelled,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            CancelReason = e.Reason,
            CompletedSteps = e.CompletedSteps,
            Usage = GatewayTokenUsage.FromTokenUsage(e.PartialUsage)
        }
    };

    private static GatewayEvent MapError(ErrorEvent e) => new()
    {
        Object = GatewayEventObject.Error,
        Status = GatewayEventStatus.Failed,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            Error = e.Message,
            ErrorSource = e.Source
        }
    };

    private static GatewayEvent MapSubAgent(SubAgentEvent e) => new()
    {
        Object = GatewayEventObject.Content,
        Status = GatewayEventStatus.InProgress,
        SessionId = e.SessionId,
        LoopId = e.LoopId,
        Data = new GatewayEventData
        {
            SubAgentName = e.AgentName,
            SubSessionId = e.SubSessionId,
            Text = e.Result,
            Error = e.Error,
            Success = e.Status == "completed" ? true : e.Status == "failed" ? false : null
        }
    };
}
