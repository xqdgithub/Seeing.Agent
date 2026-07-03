using Acp.Helpers;
using Acp.Messages;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Permission;

/// <summary>
/// 将 ACP request_permission 桥接到 Seeing 权限通道。
/// </summary>
public sealed class AcpPermissionBridge
{
    private static readonly AsyncLocal<Stack<AcpPermissionContext>> ContextStack = new();

    private readonly ILogger<AcpPermissionBridge> _logger;

    public AcpPermissionBridge(ILogger<AcpPermissionBridge> logger)
    {
        _logger = logger;
    }

    public IDisposable Push(AcpPermissionContext context) => new Scope(context);

    public async Task<RequestPermissionResponse> HandleAsync(
        string acpSessionId,
        ToolCallUpdate toolCall,
        IEnumerable<PermissionOption> options,
        CancellationToken cancellationToken = default)
    {
        var ctx = ContextStack.Value?.Peek()
            ?? throw new InvalidOperationException("ACP permission context is not available.");

        var optionList = options.ToList();
        var permissionId = Guid.NewGuid().ToString("N");
        var toolName = string.IsNullOrWhiteSpace(toolCall.ToolName) ? "acp_tool" : toolCall.ToolName;

        if (ctx.UpdateSink is EventYieldingSink yieldingSink)
        {
            await yieldingSink.PublishAsync(new PermissionRequestEvent
            {
                SessionId = ctx.SeeingSessionId,
                LoopId = ctx.LoopId,
                PermissionId = permissionId,
                PermissionKind = "tool",
                Resource = toolName,
                Arguments = toolCall.Input,
                Message = optionList.FirstOrDefault()?.Label ?? $"ACP tool permission: {toolName}"
            }, cancellationToken).ConfigureAwait(false);
        }

        var channel = ctx.PermissionChannel ?? DefaultPermissionChannel.Instance;
        var approved = await channel.RequestConfirmationAsync(new PermissionRequest
        {
            Permission = "tool",
            Patterns = new List<string> { toolName },
            Metadata = new Dictionary<string, object>
            {
                ["acpSessionId"] = acpSessionId,
                ["toolCallId"] = toolCall.ToolCallId,
                ["toolName"] = toolName
            }
        }).ConfigureAwait(false);

        if (ctx.UpdateSink is EventYieldingSink sinkAfter)
        {
            await sinkAfter.PublishAsync(new PermissionResponseEvent
            {
                SessionId = ctx.SeeingSessionId,
                LoopId = ctx.LoopId,
                PermissionId = permissionId,
                Decision = approved ? "allow" : "deny"
            }, cancellationToken).ConfigureAwait(false);
        }

        if (!approved)
        {
            _logger.LogInformation("ACP permission denied for tool {ToolName} (session {SessionId})",
                toolName, ctx.SeeingSessionId);
            return PermissionOutcomes.CancelledResponse();
        }

        var selected = optionList.FirstOrDefault()?.Id ?? "allow";
        return PermissionOutcomes.SelectedResponse(selected);
    }

    private sealed class Scope : IDisposable
    {
        public Scope(AcpPermissionContext context)
        {
            ContextStack.Value ??= new Stack<AcpPermissionContext>();
            ContextStack.Value.Push(context);
        }

        public void Dispose()
        {
            if (ContextStack.Value?.Count > 0)
                ContextStack.Value.Pop();
        }
    }
}
