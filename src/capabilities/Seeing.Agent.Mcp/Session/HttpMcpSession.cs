using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Session;

/// <summary>HTTP / SSE / Streamable HTTP 传输的 MCP 会话实现。</summary>
internal sealed class HttpMcpSession : McpClientSessionBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpTransportMode _transportMode;
    private HttpClientTransport? _transport;

    public HttpMcpSession(
        McpServerConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        HttpTransportMode transportMode = HttpTransportMode.StreamableHttp)
        : base(config, logger)
    {
        _httpClientFactory = httpClientFactory;
        _transportMode = transportMode;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (Config.Url is null)
            throw new InvalidOperationException("HTTP 传输需要配置 endpoint");

        Logger.LogInformation(
            "正在连接 MCP Server (HTTP): {Name}, Endpoint: {Endpoint}",
            Config.Name, Config.Url);

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = Config.Url,
            TransportMode = _transportMode,
            ConnectionTimeout = Config.ConnectionTimeout,
            MaxReconnectionAttempts = Config.MaxReconnectionAttempts,
            DefaultReconnectionInterval = Config.ReconnectionInterval
        };

        if (Config.Headers is { Count: > 0 })
            transportOptions.AdditionalHeaders = new Dictionary<string, string>(Config.Headers);

        var httpClient = _httpClientFactory.CreateClient($"Mcp_{Config.Name}");
        _transport = new HttpClientTransport(transportOptions, httpClient, null, ownsHttpClient: false);
        SetClient(await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

        Logger.LogInformation("MCP Server {Name} 连接成功 (Mode: {Mode})", Config.Name, _transportMode);
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await base.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _transport = null;
    }
}
