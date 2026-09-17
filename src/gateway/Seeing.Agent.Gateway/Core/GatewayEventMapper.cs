using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// IMessageEvent 到 GatewayEvent 映射器
/// </summary>
public static class ChatEventMapper
{
    /// <summary>
    /// 将 IMessageEvent 映射为 GatewayEvent
    /// </summary>
    public static bool TryMap(IMessageEvent evt, out GatewayEvent? gatewayEvent)
    {
        gatewayEvent = null;

        switch (evt)
        {
            case LoopStartEvent loopStart:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Response,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "loop_start",
                    Data = new GatewayEventData
                    {
                        UserInput = loopStart.UserInput
                    }
                };
                return true;

            case StreamStartEvent streamStart:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Response,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "stream_start",
                    Data = new GatewayEventData
                    {
                        Step = streamStart.Step
                    }
                };
                return true;

            case StreamDeltaEvent deltaEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Content,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "stream_delta",
                    Data = new GatewayEventData
                    {
                        Delta = true,
                        Text = deltaEvt.ContentDelta,
                        Reasoning = deltaEvt.ReasoningDelta
                    }
                };
                return true;

            case StreamCompleteEvent completeEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Response,
                    Status = GatewayEventStatus.Completed,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "stream_complete",
                    Data = new GatewayEventData
                    {
                        Text = completeEvt.Message?.Content,
                        Reasoning = completeEvt.Message?.ReasoningContent
                    }
                };
                return true;

            case ToolCallEvent toolEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Content,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "tool_call",
                    Data = new GatewayEventData
                    {
                        ToolCallId = toolEvt.ToolCallId,
                        ToolName = toolEvt.ToolName,
                        ToolStatus = toolEvt.Status.ToString(),
                        ToolOutput = toolEvt.Output,
                        ToolError = toolEvt.Error
                    }
                };
                return true;

            case PermissionRequestEvent permEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Permission,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "permission_request",
                    Data = new GatewayEventData
                    {
                        PermissionId = permEvt.RequestId,
                        CallId = permEvt.CallId,
                        PermissionKind = permEvt.PermissionKind,
                        Resource = permEvt.Resource,
                        PermissionMessage = permEvt.Message,
                        RiskLevel = permEvt.RiskLevel,
                        PermissionArguments = permEvt.Arguments,
                        PermissionAllowedScopes = MapAllowedScopes(permEvt.AllowedScopes)
                    }
                };
                return true;

            case PermissionResolvedEvent resolvedEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Permission,
                    Status = GatewayEventStatus.Completed,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "permission_resolved",
                    Data = new GatewayEventData
                    {
                        PermissionId = resolvedEvt.RequestId,
                        CallId = resolvedEvt.CallId,
                        PermissionDecision = resolvedEvt.Decision.ToString().ToLowerInvariant(),
                        PermissionScope = ToCamelCase(resolvedEvt.Scope.ToString()),
                        PermissionResolvedBy = ToCamelCase(resolvedEvt.ResolvedBy.ToString()),
                        PermissionReason = resolvedEvt.Reason
                    }
                };
                return true;

            case LoopCompleteEvent loopComplete:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Response,
                    Status = loopComplete.Success ? GatewayEventStatus.Completed : GatewayEventStatus.Failed,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "loop_complete",
                    Data = new GatewayEventData
                    {
                        Success = loopComplete.Success,
                        Error = loopComplete.Error,
                        Duration = loopComplete.Duration
                    }
                };
                return true;

            case ErrorEvent errorEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Error,
                    Status = GatewayEventStatus.Failed,
                    SessionId = evt.SessionId,
                    LoopId = evt.LoopId,
                    SourceType = "error",
                    Data = new GatewayEventData
                    {
                        Error = errorEvt.Message,
                        ErrorSource = errorEvt.Source ?? "agent"
                    }
                };
                return true;

            case CommandResultEvent cmdEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Message,
                    Status = cmdEvt.Success ? GatewayEventStatus.Completed : GatewayEventStatus.Failed,
                    SessionId = evt.SessionId,
                    SourceType = "command_result",
                    Data = new GatewayEventData
                    {
                        Text = cmdEvt.Message,
                        Success = cmdEvt.Success
                    }
                };
                return true;

            case NavigateEvent navEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Message,
                    Status = GatewayEventStatus.Completed,
                    SessionId = evt.SessionId,
                    SourceType = "navigate",
                    Data = new GatewayEventData
                    {
                        Text = navEvt.Target
                    }
                };
                return true;

            case SkillContentEvent skillEvt:
                gatewayEvent = new GatewayEvent
                {
                    Object = GatewayEventObject.Content,
                    Status = GatewayEventStatus.InProgress,
                    SessionId = evt.SessionId,
                    SourceType = "skill_content",
                    Data = new GatewayEventData
                    {
                        Text = skillEvt.ExpandedContent
                    }
                };
                return true;

            case SessionUpdatedEvent:
                // Session 更新事件不发送到 Gateway，内部处理
                return false;

            default:
                return false;
        }
    }

    /// <summary>枚举 → 协议 camelCase 串（如 SessionDirectory → sessionDirectory、NoChannel → noChannel）。</summary>
    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static IReadOnlyList<string>? MapAllowedScopes(IReadOnlyList<PermissionGrantScope> scopes) =>
        scopes.Count > 0 ? scopes.Select(s => ToCamelCase(s.ToString())).ToList() : null;
}
