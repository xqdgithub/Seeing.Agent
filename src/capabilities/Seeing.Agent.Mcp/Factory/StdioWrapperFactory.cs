using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp.Session;

namespace Seeing.Agent.Mcp.Factory;

public class StdioWrapperFactory : IMcpClientWrapperFactory
{
    public McpTransportType TransportType => McpTransportType.Stdio;

    public IMcpSession Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<StdioMcpSession>();
        return new StdioMcpSession(config, logger);
    }
}
