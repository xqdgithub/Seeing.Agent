using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Hosting.Embed;

/// <summary>
/// Embed Host Shape DI 扩展（进程内嵌入、无 UI）。
/// </summary>
public static class EmbedHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Embed Host Shape：默认 <see cref="DenyAllPermissionChannel"/> + ChatOrchestrator + ExecutionEngine。
    /// 能力包由宿主自行引用；本包不 ProjectReference Tools.* / Memory / Scheduler / Web / Blazor 等。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureExecution">可选执行引擎配置</param>
    public static IServiceCollection AddSeeingHostingEmbed(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        services.AddSingleton(EmbedHostShape.Descriptor);
        // D10：HostDefaultSeams=executionWorld→io.local；HostDefaultBoot=minimal
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = EmbedHostShape.Descriptor.DefaultScenario,
            HostDefaultBoot = "minimal",
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });

        // 无 UI：未显式注册 IPermissionChannel 时使用 DenyAll（嵌入方应自行提供通道或 AutoApprove）
        services.TryAddSingleton<IPermissionChannel>(DenyAllPermissionChannel.Instance);

        services.AddChatOrchestrator();
        services.AddExecutionEngine(configureExecution);

        return services;
    }

    /// <summary>
    /// <see cref="AddSeeingHostingEmbed"/> 的计划别名（W5-7）。
    /// </summary>
    public static IServiceCollection AddSeeingEmbedHost(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
        => AddSeeingHostingEmbed(services, configureExecution);
}
