namespace Seeing.Agent.MCP.Factory;

using Microsoft.Extensions.Logging;

public class McpWrapperFactoryRegistry
{
    private readonly Dictionary<McpTransportType, IMcpClientWrapperFactory> _factories = new();

    public void Register(IMcpClientWrapperFactory factory)
    {
        _factories[factory.TransportType] = factory;
    }

    public IMcpClientWrapper Create(
        McpServerConfig config,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        if (_factories.TryGetValue(config.TransportType, out var factory))
        {
            return factory.Create(config, httpClientFactory, loggerFactory);
        }

        throw new NotSupportedException($"传输类型 {config.TransportType} 未注册工厂");
    }

    public bool IsRegistered(McpTransportType type) => _factories.ContainsKey(type);
}