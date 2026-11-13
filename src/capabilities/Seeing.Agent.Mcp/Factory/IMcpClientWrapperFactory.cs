using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Factory;

/// <summary>按传输类型创建 <see cref="IMcpSession"/>。</summary>
public interface IMcpClientWrapperFactory
{
    McpTransportType TransportType { get; }

    IMcpSession Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory);
}
