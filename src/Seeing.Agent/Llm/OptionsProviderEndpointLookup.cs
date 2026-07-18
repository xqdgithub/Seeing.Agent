using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// 从 <see cref="SeeingAgentOptions.Providers"/> 解析端点。
/// </summary>
public sealed class OptionsProviderEndpointLookup : IProviderEndpointLookup
{
    private readonly IOptions<SeeingAgentOptions> _options;

    public OptionsProviderEndpointLookup(IOptions<SeeingAgentOptions> options)
    {
        _options = options;
    }

    public bool TryGet(string providerName, out ProviderEndpoint? endpoint)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || !_options.Value.Providers.TryGetValue(providerName, out var config)
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
