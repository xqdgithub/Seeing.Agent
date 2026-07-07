using Microsoft.Extensions.Options;

namespace Seeing.Agent.Configuration;

/// <summary>
/// SeeingAgentOptions 的 IOptions 实现 - 从 UnifiedConfigManager 获取配置
/// </summary>
public sealed class SeeingAgentOptionsMonitor : IOptions<SeeingAgentOptions>
{
    private readonly UnifiedConfigManager _manager;
    
    public SeeingAgentOptionsMonitor(UnifiedConfigManager manager)
    {
        _manager = manager;
    }
    
    public SeeingAgentOptions Value => _manager.GetSeeingAgentOptions();
}