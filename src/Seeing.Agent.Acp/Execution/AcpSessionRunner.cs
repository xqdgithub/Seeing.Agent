using System.Collections.Concurrent;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Transport;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 共享 ACP Session 执行核心（Passthrough / acpTool）。
/// </summary>
public interface IAcpSessionRunner
{
    Task<AcpRunResult> RunAsync(AcpRunRequest request, IAcpUpdateSink sink, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AcpSessionRunner : IAcpSessionRunner
{
    private readonly AcpConnectionManager _connectionManager;
    private readonly AcpLifecycleService _lifecycleService;
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly AcpCancellationCoordinator _cancellationCoordinator;
    private readonly ILogger<AcpSessionRunner> _logger;
    private readonly ConcurrentDictionary<string, byte> _initializedClients = new();

    public AcpSessionRunner(
        AcpConnectionManager connectionManager,
        AcpLifecycleService lifecycleService,
        IAcpBackendRegistry backendRegistry,
        AcpCancellationCoordinator cancellationCoordinator,
        ILogger<AcpSessionRunner> logger)
    {
        _connectionManager = connectionManager;
        _lifecycleService = lifecycleService;
        _backendRegistry = backendRegistry;
        _cancellationCoordinator = cancellationCoordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AcpRunResult> RunAsync(
        AcpRunRequest request,
        IAcpUpdateSink sink,
        CancellationToken cancellationToken = default)
    {
        var backend = _backendRegistry.GetBackend(request.BackendId);
        var leaseKey = request.Scope == "tool"
            ? AcpConnectionManager.BuildToolKey(request.BackendId, request.ScopeKey)
            : AcpConnectionManager.BuildPassthroughKey(request.BackendId, request.ScopeKey);

        await using var lease = await _connectionManager
            .LeaseAsync(leaseKey, request.BackendId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await EnsureInitializedAsync(lease.Client, backend, cancellationToken).ConfigureAwait(false);

        var acpSessionId = await _lifecycleService.EnsureSessionAsync(
            request.Scope,
            request.ScopeKey,
            request.BackendId,
            lease.Client,
            request.WorkingDirectory,
            cancellationToken).ConfigureAwait(false);

        var permissionContext = new AcpPermissionContext
        {
            SeeingSessionId = request.SeeingSessionId,
            LoopId = request.LoopId,
            AcpSessionId = acpSessionId,
            UpdateSink = sink,
            PermissionChannel = request.ParentContext?.PermissionChannel
        };

        lease.Client.ConfigureForRequest(sink, permissionContext, request.WorkingDirectory);

        using var cancelRegistration = _cancellationCoordinator.Register(
            request.SeeingSessionId,
            acpSessionId,
            lease.Client,
            cancellationToken);

        using var promptLock = await lease.AcquirePromptLockAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ACP run start scope={Scope} session={SessionId} loop={LoopId} backend={BackendId} acpSession={AcpSessionId} cwd={Cwd}",
            request.Scope,
            request.SeeingSessionId,
            request.LoopId,
            request.BackendId,
            acpSessionId,
            request.WorkingDirectory);

        try
        {
            var promptBlocks = request.Prompt.ToList();
            _logger.LogDebug(
                "ACP session/prompt sending session={SessionId} acpSession={AcpSessionId} blocks={BlockCount}",
                request.SeeingSessionId,
                acpSessionId,
                promptBlocks.Count);

            var response = await lease.Client
                .SessionPromptAsync(acpSessionId, promptBlocks, cancellationToken)
                .ConfigureAwait(false);

            var text = string.Join(
                "\n",
                response.Content?.OfType<TextContentBlock>().Select(t => t.Text) ?? Array.Empty<string>());

            _logger.LogInformation(
                "ACP session/prompt complete session={SessionId} acpSession={AcpSessionId} stopReason={StopReason} textLength={TextLength}",
                request.SeeingSessionId,
                acpSessionId,
                response.StopReason ?? "(none)",
                text.Length);

            return new AcpRunResult
            {
                Text = text.Trim(),
                StopReason = response.StopReason,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ACP run cancelled for session {SessionId}", request.SeeingSessionId);
            return new AcpRunResult
            {
                Text = "",
                Success = false,
                Error = "cancelled",
                StopReason = "cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACP run failed for session {SessionId}", request.SeeingSessionId);
            return new AcpRunResult
            {
                Text = "",
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task EnsureInitializedAsync(
        SeeingAcpClient client,
        AcpBackendDescriptor backend,
        CancellationToken cancellationToken)
    {
        var key = client.GetHashCode().ToString();
        if (_initializedClients.ContainsKey(key))
            return;

        await _lifecycleService.InitializeClientAsync(client, backend, cancellationToken).ConfigureAwait(false);
        _initializedClients.TryAdd(key, 0);
    }
}
