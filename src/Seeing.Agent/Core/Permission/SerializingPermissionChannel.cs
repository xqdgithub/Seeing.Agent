using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限 Ask 全局串行包装：多并行工具不得并发弹出多个 Ask。
/// </summary>
public sealed class SerializingPermissionChannel : IPermissionChannel
{
    private readonly IPermissionChannel _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SerializingPermissionChannel(IPermissionChannel inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _inner.RequestConfirmationAsync(request).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName, object? arguments, AgentContext context)
    {
        await _gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.RequestToolPermissionAsync(toolName, arguments, context)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName, string prompt, AgentContext context)
    {
        await _gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.RequestSubAgentPermissionAsync(agentName, prompt, context)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath, string? contentPreview, AgentContext context)
    {
        await _gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.RequestWritePermissionAsync(filePath, contentPreview, context)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
