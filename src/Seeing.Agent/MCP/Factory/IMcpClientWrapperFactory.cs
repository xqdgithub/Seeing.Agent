namespace Seeing.Agent.MCP.Factory;

using Microsoft.Extensions.Logging;

public interface IMcpClientWrapperFactory
{
    McpTransportType TransportType { get; }

    IMcpClientWrapper Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory);
}