using Acp.Helpers;
using Acp.Messages;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Acp.Permission;

/// <summary>
/// 将 ACP request_permission 桥接到 Seeing 权限授权器。
/// </summary>
public sealed class AcpPermissionBridge
{
    private static readonly AsyncLocal<Stack<AcpPermissionContext>> ContextStack = new();

    private readonly IPermissionAuthorizerFactory _authorizerFactory;
    private readonly ILogger<AcpPermissionBridge> _logger;

    public AcpPermissionBridge(
        IPermissionAuthorizerFactory authorizerFactory,
        ILogger<AcpPermissionBridge> logger)
    {
        _authorizerFactory = authorizerFactory;
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
        var toolName = string.IsNullOrWhiteSpace(toolCall.ToolName) ? "acp_tool" : toolCall.ToolName;

        var authorizer = _authorizerFactory.Create(ctx.SeeingSessionId);
        var resolution = await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = ctx.SeeingSessionId,
            LoopId = ctx.LoopId,
            AgentName = ctx.AgentName,
            PermissionKind = "tool.execute",
            Resource = toolName,
            RequireInteraction = true
        }, cancellationToken).ConfigureAwait(false);

        if (resolution.Decision != PermissionEffect.Allow)
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