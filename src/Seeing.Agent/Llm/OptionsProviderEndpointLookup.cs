using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// 从 <see cref="SeeingAgentOptions.Providers"/> 解析端点。
/// </summary>
public sealed class OptionsProviderEndpointLookup : IProviderEndpointLookup
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly IProviderRegistry _registry;

    public OptionsProviderEndpointLookup(
        IOptionsMonitor<SeeingAgentOptions> options,
        IProviderRegistry registry)
    {
        _options = options;
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

        if (!_options.CurrentValue.Providers.TryGetValue(providerName, out var config)
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
