using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 从用户级 providers.json 解析端点。
/// </summary>
public sealed class OptionsProviderEndpointLookup : IProviderEndpointLookup
{
    private readonly UnifiedConfigManager _configManager;
    private readonly IProviderRegistry _registry;

    /// <summary>
    /// 注入统一配置管理器与 Provider 注册表构造端点查找器。
    /// </summary>
    public OptionsProviderEndpointLookup(
        UnifiedConfigManager configManager,
        IProviderRegistry registry)
    {
        _configManager = configManager;
        _registry = registry;
    }

    /// <summary>
    /// 解析指定 Provider 的 BaseUrl/ApiKey 端点：注册表优先，回落用户级 Providers 配置。
    /// </summary>
    public bool TryGet(string providerName, out ProviderEndpoint? endpoint)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            endpoint = null;
            return false;
        }

        if (_registry.GetProvider(providerName) is IProviderEndpointInfo providerEndpoint)
        {
            endpoint = new ProviderEndpoint
            {
                BaseUrl = providerEndpoint.BaseUrl,
                ApiKey = providerEndpoint.ApiKey
            };
            return true;
        }

        if (!_configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers")
                .TryGetValue(providerName, out var config)
            || config is null)
        {
            endpoint = null;
            return false;
        }

        endpoint = new ProviderEndpoint
        {
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey
        };
        return true;
    }
}
