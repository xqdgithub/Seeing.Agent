using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Llm;

/// <summary>
/// 从用户级 providers.json 解析端点。
/// </summary>
public sealed class OptionsProviderEndpointLookup : IProviderEndpointLookup
{
    private readonly UnifiedConfigManager _configManager;
    private readonly IProviderRegistry _registry;

    public OptionsProviderEndpointLookup(
        UnifiedConfigManager configManager,
        IProviderRegistry registry)
    {
        _configManager = configManager;
        _registry = registry;
    }

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
