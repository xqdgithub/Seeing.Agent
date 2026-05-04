namespace Seeing.Agent.MCP.Factory;

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

public class HttpWrapperFactory : IMcpClientWrapperFactory
{
    private readonly HttpTransportMode _mode;

    public HttpWrapperFactory(HttpTransportMode mode = HttpTransportMode.StreamableHttp)
    {
        _mode = mode;
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
        return new HttpMcpClientWrapper(config, httpClientFactory, logger, _mode);
    }
}