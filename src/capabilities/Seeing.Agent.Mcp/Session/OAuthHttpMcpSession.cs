using System.Net;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp.OAuth;

namespace Seeing.Agent.Mcp.Session;

/// <summary>
/// OAuth 装饰在 HTTP 会话外：连接前注入 Bearer；工具调用遇 401 时强制刷新并重试一次。
/// </summary>
internal sealed class OAuthHttpMcpSession : IMcpSession
{
    private readonly McpServerConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly HttpTransportMode _mode;
    private readonly McpOAuthStorage _storage;
    private readonly McpOAuthTokenClient _tokenClient;

    private IMcpSession? _inner;

    public OAuthHttpMcpSession(
        McpServerConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        HttpTransportMode mode,
        McpOAuthStorage storage,
        McpOAuthTokenClient tokenClient)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _mode = mode;
        _storage = storage;
        _tokenClient = tokenClient;
    }

    public string ServerName => _config.Name;

    public McpServerCapabilitiesInfo Capabilities =>
        _inner?.Capabilities
        ?? new McpServerCapabilitiesInfo(false, false, false, false, false, false, false, false);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var effective = await ResolveEffectiveConfigAsync(cancellationToken).ConfigureAwait(false);
        _inner = new HttpMcpSession(effective, _httpClientFactory, _logger, _mode);
        await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析实际使用的配置：载入令牌、必要时刷新，并在副本上注入 Bearer 头（不修改原配置）。
    /// </summary>
    internal async Task<McpServerConfig> ResolveEffectiveConfigAsync(CancellationToken cancellationToken)
    {
        var oauth = _config.OAuth;
        if (oauth is null || oauth.Disabled)
            return _config;

        var token = await _storage.LoadTokenAsync(_config.Name).ConfigureAwait(false);
        if (token is null)
            return _config;

        if (token.IsExpiringSoon && !string.IsNullOrEmpty(token.RefreshToken))
        {
            try
            {
                var refreshed = await _tokenClient
                    .RefreshAsync(oauth, token.RefreshToken!, cancellationToken)
                    .ConfigureAwait(false);
                token = refreshed;
                await _storage.SaveTokenAsync(_config.Name, token).ConfigureAwait(false);
            }
            catch (McpOAuthException ex)
            {
                _logger.LogWarning(ex, "主动刷新 MCP OAuth 令牌失败: {Server}", _config.Name);
            }
        }

        if (token.IsExpired || string.IsNullOrEmpty(token.AccessToken))
            return _config;

        var effective = Clone(_config);
        effective.Headers ??= new Dictionary<string, string>();
        effective.Headers["Authorization"] = $"{token.TokenType} {token.AccessToken}";
        return effective;
    }

    public Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult<IReadOnlyList<McpSessionToolInfo>>(Array.Empty<McpSessionToolInfo>())
            : _inner.ListToolsAsync(cancellationToken);

    public async Task<McpSessionToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (_inner is null)
            return new McpSessionToolCallResult(true, "OAuth MCP 客户端未连接", "OAuth MCP 客户端未连接");

        try
        {
            return await _inner.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(ex, "MCP OAuth 令牌被拒绝（401），尝试刷新后重试: {Server}", _config.Name);

            if (!await TryForceRefreshAsync(cancellationToken).ConfigureAwait(false))
                throw;

            await _inner.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            var effective = await ResolveEffectiveConfigAsync(cancellationToken).ConfigureAwait(false);
            _inner = new HttpMcpSession(effective, _httpClientFactory, _logger, _mode);
            await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _inner is null ? Task.CompletedTask : _inner.DisconnectAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
            await _inner.DisposeAsync().ConfigureAwait(false);
    }

    public Task<McpResourceListResult> ListResourcesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpResourceListResult(Array.Empty<McpResourceInfo>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.ListResourcesAsync(cursor, cancellationToken);

    public Task<McpResourceReadResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpResourceReadResult(Array.Empty<McpResourceContent>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.ReadResourceAsync(uri, cancellationToken);

    public Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpResourceTemplateListResult(
                Array.Empty<McpResourceTemplateInfo>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.ListResourceTemplatesAsync(cursor, cancellationToken);

    public Task<McpPromptListResult> ListPromptsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpPromptListResult(Array.Empty<McpPromptInfo>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.ListPromptsAsync(cursor, cancellationToken);

    public Task<McpPromptGetResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpPromptGetResult(Array.Empty<McpPromptMessage>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.GetPromptAsync(name, arguments, cancellationToken);

    public Task<McpSkillExtListResult> ListSkillsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpSkillExtListResult(Array.Empty<McpSkillExtInfo>(), ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.ListSkillsAsync(cursor, cancellationToken);

    public Task<McpSkillExtGetResult> GetSkillAsync(
        string uri,
        CancellationToken cancellationToken = default)
        => _inner is null
            ? Task.FromResult(new McpSkillExtGetResult(ErrorMessage: "OAuth MCP 客户端未连接"))
            : _inner.GetSkillAsync(uri, cancellationToken);

    private async Task<bool> TryForceRefreshAsync(CancellationToken cancellationToken)
    {
        var oauth = _config.OAuth;
        if (oauth is null || oauth.Disabled)
            return false;

        var token = await _storage.LoadTokenAsync(_config.Name).ConfigureAwait(false);
        if (token is null || string.IsNullOrEmpty(token.RefreshToken))
            return false;

        try
        {
            var refreshed = await _tokenClient
                .RefreshAsync(oauth, token.RefreshToken!, cancellationToken)
                .ConfigureAwait(false);
            await _storage.SaveTokenAsync(_config.Name, refreshed).ConfigureAwait(false);
            return true;
        }
        catch (McpOAuthException ex)
        {
            _logger.LogWarning(ex, "MCP OAuth 401 刷新失败: {Server}", _config.Name);
            return false;
        }
    }

    private static McpServerConfig Clone(McpServerConfig source) => new()
    {
        Name = source.Name,
        TransportType = source.TransportType,
        Command = source.Command,
        Args = source.Args is null ? null : new List<string>(source.Args),
        Env = source.Env is null ? null : new Dictionary<string, string>(source.Env),
        WorkingDirectory = source.WorkingDirectory,
        Url = source.Url,
        Headers = source.Headers is null ? null : new Dictionary<string, string>(source.Headers),
        ConnectionTimeoutSeconds = source.ConnectionTimeoutSeconds,
        ShutdownTimeoutSeconds = source.ShutdownTimeoutSeconds,
        MaxReconnectionAttempts = source.MaxReconnectionAttempts,
        ReconnectionIntervalMs = source.ReconnectionIntervalMs,
        Priority = source.Priority,
        ReconnectionPolicy = source.ReconnectionPolicy,
        AutoStart = source.AutoStart,
        ConnectionTimeoutSecondsOverride = source.ConnectionTimeoutSecondsOverride,
        Tags = source.Tags is null ? null : new List<string>(source.Tags),
        Description = source.Description,
        Disabled = source.Disabled,
        ConfigLevel = source.ConfigLevel,
        OAuth = source.OAuth
    };
}
