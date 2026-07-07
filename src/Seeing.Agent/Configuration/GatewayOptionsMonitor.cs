using Microsoft.Extensions.Options;

namespace Seeing.Agent.Configuration;

/// <summary>
/// GatewayOptions 的 IOptions 实现 - 从 UnifiedConfigManager 获取配置
/// </summary>
public sealed class GatewayOptionsMonitor : IOptions<GatewayOptions>
{
    private readonly UnifiedConfigManager _manager;
    
    public GatewayOptionsMonitor(UnifiedConfigManager manager)
    {
        _manager = manager;
    }
    
    public GatewayOptions Value => _manager.GetGatewayOptions();
}