using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Directories;
using Seeing.Agent.Comms.Scheduling;

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
        // 后注册覆盖：工具模块的 Noop 目标解析器被真实实现替换（不用 TryAdd）
        services.AddSingleton<IChannelTargetResolver, SessionChannelTargetResolver>();
        services.AddSingleton<NoopDeliveryNotifier>();
        services.AddSingleton<IChannelDeliveryNotifier>(sp => sp.GetRequiredService<NoopDeliveryNotifier>());
        services.AddSingleton<ChannelDeliveryScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => sp.GetRequiredService<ChannelDeliveryScheduleDispatcher>());
        MoveCompositeScheduleDispatcherLast(services);
        services.AddSingleton<ISeeingModule, CommsModule>();
        return services;
    }

    /// <summary>
    /// <c>GetRequiredService&lt;IScheduledJobDispatcher&gt;</c> 取最后一条登记。
    /// 调度器若已登记组合器，把它挪到末尾，使后加入的 Comms 分发器仍被组合器枚举到。
    /// </summary>
    internal static void MoveCompositeScheduleDispatcherLast(IServiceCollection services)
    {
        var index = -1;
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IScheduledJobDispatcher)
                && descriptor.ImplementationType?.Name == "CompositeScheduleDispatcher")
                index = i;
        }

        if (index < 0 || index == services.Count - 1)
            return;

        var composite = services[index];
        services.RemoveAt(index);
        services.Add(composite);
    }
}
