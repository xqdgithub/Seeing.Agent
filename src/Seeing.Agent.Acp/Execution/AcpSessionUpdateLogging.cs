using Acp.Types;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Acp.Execution;

internal static class AcpSessionUpdateLogging
{
    public static string Describe(SessionUpdate update) => update switch
    {
        AgentMessageChunk => "agent_message_chunk",
        AgentThoughtChunk => "agent_thought_chunk",
        UserMessageChunk => "user_message_chunk",
        ToolCallStart start => $"tool_call_start:{start.ToolCallId}",
        ToolCallProgress progress => $"tool_call_progress:{progress.ToolCallId}:{progress.Status}",
        AgentPlanUpdate => "agent_plan_update",
        SessionInfoUpdate => "session_info_update",
        UsageUpdate => "usage_update",
        CurrentModeUpdate mode => $"mode_update:{mode.CurrentModeId}",
        ConfigOptionUpdate => "config_option_update",
        AvailableCommandsUpdate => "available_commands_update",
        UnknownSessionUpdate unknown => unknown.SessionUpdateKind ?? unknown.Type ?? "unknown",
        _ => update.GetType().Name
    };

    public static LogLevel GetLogLevel(SessionUpdate update) => update switch
    {
        AgentMessageChunk or AgentThoughtChunk or UserMessageChunk => LogLevel.Trace,
        ToolCallStart or ToolCallProgress or AgentPlanUpdate => LogLevel.Information,
        UsageUpdate or SessionInfoUpdate or CurrentModeUpdate => LogLevel.Debug,
        _ => LogLevel.Information
    };
}
