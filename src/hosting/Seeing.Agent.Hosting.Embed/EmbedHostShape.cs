using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Hosting;

namespace Seeing.Agent.Hosting.Embed;

/// <summary>Embed Host Shape 常量（进程内嵌入、无 UI）。</summary>
public static class EmbedHostShape
{
    public const string Id = "embed";

    /// <summary>形态自描述（声明用；实际启用集由宿主引用 + 结算决定）。</summary>
    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full,
        DefaultScenario = "full",
    };
}

/// <summary>
/// Embed Host Shape DI 扩展（进程内嵌入、无 UI）。
/// </summary>
public static class EmbedHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Embed Host Shape：宿主基线（默认 Boot = <c>minimal</c>）+ 默认 <see cref="DenyAllPermissionChannel"/>。
    /// 能力包由宿主自行引用；本包不 ProjectReference Tools.* / Memory / Scheduler / Web / Blazor 等。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureExecution">可选执行引擎配置</param>
    public static IServiceCollection AddSeeingHostingEmbed(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        // Embed 与其它形态的唯一差异：进程级默认 Boot 落到 minimal（嵌入方通常只想跑最小集）
        services.AddHostShapeBaseline(
            EmbedHostShape.Descriptor,
            configureExecution,
            withPermissionChannel: false,
            defaultBoot: "minimal");

        // 无 UI：未显式注册 IPermissionChannel 时使用 DenyAll（嵌入方应自行提供通道或 AutoApprove）
        services.TryAddSingleton<IPermissionChannel, DenyAllPermissionChannel>();

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
