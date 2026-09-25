using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp.OAuth;
using System.Net;

namespace Seeing.Agent.Mcp.Factory;

/// <summary>
/// OAuth 感知的 HTTP 传输包装器。
/// <para>
/// 连接前从 <see cref="McpOAuthStorage"/> 取有效访问令牌，过期前主动刷新，
/// 并注入 <c>Authorization: Bearer {token}</c>；工具调用遇 401 时强制刷新后重试一次。
/// 无令牌或刷新失败时不注入头（由服务端 401 暴露），绝不伪造令牌。
/// </para>
/// </summary>
internal sealed class OAuthHttpClientWrapper : IMcpClientWrapper
{
    private readonly McpServerConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly HttpTransportMode _mode;
    private readonly McpOAuthStorage _storage;
    private readonly McpOAuthTokenClient _tokenClient;

    private IMcpClientWrapper? _inner;

    public OAuthHttpClientWrapper(
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

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var effective = await ResolveEffectiveConfigAsync(cancellationToken).ConfigureAwait(false);
        _inner = new HttpMcpClientWrapper(effective, _httpClientFactory, _logger, _mode);
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

        // 过期前主动刷新
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

    public async Task<IReadOnlyList<Management.McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_inner is null)
            return Array.Empty<Management.McpToolInfo>();

        return await _inner.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        if (_inner is null)
            return new McpToolResult { IsError = true, Content = "OAuth MCP 客户端未连接" };

        try
        {
            return await _inner.CallToolAsync(toolName, args, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(ex, "MCP OAuth 令牌被拒绝（401），尝试刷新后重试: {Server}", _config.Name);

            if (!await TryForceRefreshAsync(cancellationToken).ConfigureAwait(false))
                throw;

            await _inner.DisconnectAsync().ConfigureAwait(false);
            var effective = await ResolveEffectiveConfigAsync(cancellationToken).ConfigureAwait(false);
            _inner = new HttpMcpClientWrapper(effective, _httpClientFactory, _logger, _mode);
            await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.CallToolAsync(toolName, args, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_inner is not null)
            await _inner.DisconnectAsync().ConfigureAwait(false);
    }

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
