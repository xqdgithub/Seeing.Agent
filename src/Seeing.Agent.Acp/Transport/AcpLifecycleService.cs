using Acp.Messages;
using Acp.Transport;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Session;

namespace Seeing.Agent.Acp.Transport;

/// <summary>
/// ACP 客户端生命周期：Start / Initialize / Auth / SessionEnsure。
/// </summary>
public sealed class AcpLifecycleService
{
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly AcpSessionStore _sessionStore;
    private readonly AcpTaskStore _taskStore;
    private readonly AcpMcpServerMapper _mcpMapper;
    private readonly ILogger<AcpLifecycleService> _logger;

    public AcpLifecycleService(
        IAcpBackendRegistry backendRegistry,
        AcpSessionStore sessionStore,
        AcpTaskStore taskStore,
        AcpMcpServerMapper mcpMapper,
        ILogger<AcpLifecycleService> logger)
    {
        _backendRegistry = backendRegistry;
        _sessionStore = sessionStore;
        _taskStore = taskStore;
        _mcpMapper = mcpMapper;
        _logger = logger;
    }

    public async Task<SeeingAcpClient> EnsureClientReadyAsync(
        SeeingAcpClient client,
        AcpBackendDescriptor backend,
        CancellationToken cancellationToken = default)
    {
        if (client.State == SubprocessClientState.Running)
        {
            // Already started; ensure initialized via a cheap check on session capability
            return client;
        }

        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        return await InitializeClientAsync(client, backend, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SeeingAcpClient> InitializeClientAsync(
        SeeingAcpClient client,
        AcpBackendDescriptor backend,
        CancellationToken cancellationToken = default)
    {
        var init = await client.InitializeAsync(
            protocolVersion: 1,
            clientCapabilities: BuildClientCapabilities(),
            clientInfo: Implementation.Create("Seeing.Agent.Acp", "1.0.0"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "ACP initialized backend {BackendId} with agent {AgentName}",
            backend.Id,
            init.AgentInfo.Name);

        if (!string.IsNullOrWhiteSpace(backend.AuthMethodId) &&
            init.AuthMethods?.Any(m => m.Id == backend.AuthMethodId) == true)
        {
            await client.AuthenticateAsync(backend.AuthMethodId, cancellationToken).ConfigureAwait(false);
        }

        return client;
    }

    public async Task<string> EnsureSessionAsync(
        string scope,
        string scopeKey,
        string backendId,
        SeeingAcpClient client,
        string cwd,
        CancellationToken cancellationToken = default)
    {
        var mcpServers = await _mcpMapper.MapAsync(cancellationToken).ConfigureAwait(false);
        AcpSessionMapping? mapping = scope == "tool"
            ? _taskStore.GetMapping(scopeKey)
            : _sessionStore.GetMapping(scopeKey);

        if (mapping != null &&
            mapping.BackendId == backendId &&
            !string.IsNullOrWhiteSpace(mapping.AcpSessionId))
        {
            try
            {
                var loaded = await client.SessionLoadAsync(
                        mapping.AcpSessionId,
                        cwd,
                        mcpServers,
                        cancellationToken)
                    .ConfigureAwait(false);

                // OpenCode 等实现：session/load 成功时 result 可能不含 sessionId（仅 models/modes），
                // ACP 标准也允许 result 为 null；此时应继续使用已存储的 id。
                var sessionId = !string.IsNullOrWhiteSpace(loaded?.SessionId)
                    ? loaded!.SessionId
                    : mapping.AcpSessionId;

                if (!string.IsNullOrWhiteSpace(sessionId))
                    return sessionId;

                _logger.LogWarning(
                    "ACP session/load returned empty session id for scope={Scope} key={ScopeKey}, creating new session",
                    scope,
                    scopeKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ACP session/load failed for acpSession={AcpSessionId}; creating new session",
                    mapping.AcpSessionId);
            }
        }
        else if (mapping != null)
        {
            _logger.LogWarning(
                "Ignoring invalid ACP mapping for scope={Scope} key={ScopeKey} backend={BackendId} acpSession={AcpSessionId}",
                scope,
                scopeKey,
                mapping.BackendId,
                mapping.AcpSessionId);
        }

        var created = await client.SessionNewAsync(cwd, mcpServers, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.SessionId))
        {
            throw new InvalidOperationException("ACP backend returned an empty session id from session/new.");
        }

        var newMapping = new AcpSessionMapping
        {
            BackendId = backendId,
            AcpSessionId = created.SessionId
        };

        if (scope == "tool")
            _taskStore.SaveMapping(scopeKey, newMapping);
        else
            _sessionStore.SaveMapping(scopeKey, newMapping);

        _logger.LogInformation(
            "ACP session/new scope={Scope} key={ScopeKey} acpSession={AcpSessionId}",
            scope,
            scopeKey,
            created.SessionId);

        return created.SessionId;
    }

    private static ClientCapabilities BuildClientCapabilities() => new()
    {
        Fs = new FsCapabilities { ReadTextFile = true, WriteTextFile = true },
        Terminal = true,
        Prompt = new PromptCapabilities { Image = true, Audio = false, EmbeddedContext = true }
    };
}
