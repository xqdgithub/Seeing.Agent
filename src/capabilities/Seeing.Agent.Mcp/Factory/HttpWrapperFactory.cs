using Seeing.Agent.Abstractions.Mcp;
namespace Seeing.Agent.Mcp.Factory;

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Mcp.OAuth;

public class HttpWrapperFactory : IMcpClientWrapperFactory
{
    private readonly HttpTransportMode _mode;
    private readonly McpOAuthStorage? _oauthStorage;
    private readonly McpOAuthTokenClient? _oauthTokenClient;

    public HttpWrapperFactory(
        HttpTransportMode mode = HttpTransportMode.StreamableHttp,
        McpOAuthStorage? oauthStorage = null,
        McpOAuthTokenClient? oauthTokenClient = null)
    {
        _mode = mode;
        _oauthStorage = oauthStorage;
        _oauthTokenClient = oauthTokenClient;
    }

    public McpTransportType TransportType => _mode == HttpTransportMode.Sse
        ? McpTransportType.Sse
        : McpTransportType.StreamableHttp;

    public IMcpClientWrapper Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        if (httpClientFactory == null)
        {
            throw new InvalidOperationException("HTTP 传输需要 IHttpClientFactory");
        }

        var logger = loggerFactory.CreateLogger<HttpMcpClientWrapper>();

        // 配置了 OAuth 且未禁用时，走 OAuth 感知传输以注入 Bearer 令牌
        if (config.OAuth is { Disabled: false })
        {
            var storage = _oauthStorage
                ?? new McpOAuthStorage(loggerFactory.CreateLogger<McpOAuthStorage>());
            var tokenClient = _oauthTokenClient
                ?? new McpOAuthTokenClient(loggerFactory.CreateLogger<McpOAuthTokenClient>(), httpClientFactory);

            return new OAuthHttpClientWrapper(config, httpClientFactory, logger, _mode, storage, tokenClient);
        }

        return new HttpMcpClientWrapper(config, httpClientFactory, logger, _mode);
    }
}
