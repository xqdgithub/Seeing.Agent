namespace Seeing.Agent.MCP.Factory;

using Microsoft.Extensions.Logging;

public class StdioWrapperFactory : IMcpClientWrapperFactory
{
    public McpTransportType TransportType => McpTransportType.Stdio;

    public IMcpClientWrapper Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<StdioMcpClientWrapper>();
        return new StdioMcpClientWrapper(config, logger);
    }
}