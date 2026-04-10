using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.NewTui.Services;

public class TuiPermissionChannel : IPermissionChannel
{
    public async Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        var tcs = new TaskCompletionSource<PermissionDecision>();

        Application.MainLoop.Invoke(() =>
        {
            tcs.SetResult(PermissionDecision.Allow());
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return PermissionDecision.Deny("Timeout");
        }
    }
    
    public Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        return Task.FromResult(PermissionDecision.Allow());
    }
    
    public Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        return Task.FromResult(PermissionDecision.Allow());
    }
    
    public async Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        var decision = await RequestToolPermissionAsync(
            request.Permission,
            request.Metadata,
            new AgentContext());
        
        return decision.Action == PermissionAction.Allow;
    }
}
