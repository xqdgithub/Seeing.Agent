using Seeing.Agent.Abstractions.Mcp;
namespace Seeing.Agent.Mcp.Factory;

using Microsoft.Extensions.Logging;

public interface IMcpClientWrapperFactory
{
    McpTransportType TransportType { get; }

    IMcpClientWrapper Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory);
}