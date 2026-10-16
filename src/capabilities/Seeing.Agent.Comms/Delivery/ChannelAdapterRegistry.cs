using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>
/// 每通道单实例适配器注册表。同通道重复注册时后注册者生效并记 Warning。
/// </summary>
public sealed class ChannelAdapterRegistry : IChannelAdapterRegistry
{
    private readonly ILogger<ChannelAdapterRegistry> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, IChannelAdapter> _adapters = new(StringComparer.Ordinal);

    public ChannelAdapterRegistry(ILogger<ChannelAdapterRegistry>? logger = null)
    {
        _logger = logger ?? NullLogger<ChannelAdapterRegistry>.Instance;
    }

    public void Register(IChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (_gate)
        {
            if (_adapters.ContainsKey(adapter.ChannelId))
            {
                _logger.LogWarning(
                    "通道 {ChannelId} 重复注册，后注册者生效",
                    adapter.ChannelId);
            }

            _adapters[adapter.ChannelId] = adapter;
        }
    }

    public bool TryGet(string channelId, out IChannelAdapter? adapter)
    {
        lock (_gate)
            return _adapters.TryGetValue(channelId, out adapter);
    }

    public IReadOnlyList<string> RegisteredChannels
    {
        get
        {
            lock (_gate)
                return _adapters.Keys.ToList();
        }
    }

    public void Unregister(string channelId)
    {
        lock (_gate)
            _adapters.Remove(channelId);
    }
}
