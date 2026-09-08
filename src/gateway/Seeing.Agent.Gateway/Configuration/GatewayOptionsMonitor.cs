using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Gateway.Configuration;

/// <summary>
/// GatewayOptions 的 IOptions 实现 - 从 IConfigSectionStore 获取配置
/// </summary>
public sealed class GatewayOptionsMonitor : IOptions<GatewayOptions>
{
    private readonly IConfigSectionStore _store;

    public GatewayOptionsMonitor(IConfigSectionStore store)
    {
        _store = store;
    }

    public GatewayOptions Value => _store.GetSection<GatewayOptions>(GatewayOptions.SectionName);
}
