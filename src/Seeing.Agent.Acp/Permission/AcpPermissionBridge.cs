using Acp.Helpers;
using Acp.Messages;
using Acp.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Permission;

/// <summary>
/// 将 ACP request_permission 桥接到 Seeing 权限通道。
/// </summary>
public sealed class AcpPermissionBridge
{
    private static readonly AsyncLocal<Stack<AcpPermissionContext>> ContextStack = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AcpPermissionBridge> _logger;

    public AcpPermissionBridge(
        IServiceScopeFactory scopeFactory,
        ILogger<AcpPermissionBridge> logger)
    {
        _scopeFactory = scopeFactory;
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

        // 优先使用上下文中的通道，否则从 DI 解析（支持热重载）
        var channel = ctx.PermissionChannel ?? ResolveDefaultChannel();
        var decision = await channel.RequestToolPermissionAsync(
            toolName,
            toolCall.Input,
            new AgentContext
            {
                SessionId = ctx.SeeingSessionId,
                CancellationToken = cancellationToken
            }).ConfigureAwait(false);

        var approved = decision.Action == PermissionAction.Allow;

        if (!approved)
        {
            _logger.LogInformation("ACP permission denied for tool {ToolName} (session {SessionId})",
                toolName, ctx.SeeingSessionId);
            return PermissionOutcomes.CancelledResponse();
        }

        var selected = optionList.FirstOrDefault()?.Id ?? "allow";
        return PermissionOutcomes.SelectedResponse(selected);
    }

    private IPermissionChannel ResolveDefaultChannel()
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPermissionChannel>();
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