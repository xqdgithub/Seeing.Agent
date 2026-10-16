using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Directories;

namespace Seeing.Agent.Comms.Extensions;

/// <summary>
/// 通联子系统 DI。须在 <c>AddSeeingCore</c> 之前调用。
/// </summary>
public static class CommsServiceCollectionExtensions
{
    public static IServiceCollection AddSeeingComms(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(new ConfigSectionMeta(
            CommsOptions.SectionName, "seeing.json", ConfigScope.Both, typeof(CommsOptions)));

        services.AddOptions<CommsOptions>();
        services.AddSingleton(sp =>
            new ConfigSectionOptionsMonitor<CommsOptions>(
                sp.GetRequiredService<IConfigSectionStore>(),
                CommsOptions.SectionName));
        services.AddSingleton<IOptionsMonitor<CommsOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<CommsOptions>>());
        services.AddSingleton<IOptions<CommsOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<CommsOptions>>());

        services.AddSingleton<JsonChannelDirectoryStore>();
        services.AddSingleton<IChannelDirectoryStore>(sp => sp.GetRequiredService<JsonChannelDirectoryStore>());
        services.AddSingleton<ChannelDirectoryService>();
        services.AddSingleton<IChannelDirectory>(sp => sp.GetRequiredService<ChannelDirectoryService>());
        services.AddSingleton<ChannelAdapterRegistry>();
        services.AddSingleton<IChannelAdapterRegistry>(sp => sp.GetRequiredService<ChannelAdapterRegistry>());
        services.AddSingleton<OutboundDeliveryService>();
        services.AddSingleton<ChannelMessageSink>();
        services.AddSingleton<IChannelMessageSink>(sp => sp.GetRequiredService<ChannelMessageSink>());
        services.AddSingleton<NoopDeliveryNotifier>();
        services.AddSingleton<IChannelDeliveryNotifier>(sp => sp.GetRequiredService<NoopDeliveryNotifier>());
        services.AddSingleton<ISeeingModule, CommsModule>();
        return services;
    }
}
