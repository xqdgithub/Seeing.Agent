using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Hosting;

namespace Seeing.Agent.Hosting.Headless;

/// <summary>Headless Host Shape 常量。</summary>
public static class HeadlessHostShape
{
    public const string Id = "headless";

    /// <summary>形态自描述（声明用；实际启用集由 sample 引用 + 结算决定）。</summary>
    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full,
        DefaultScenario = "full",
    };
}

/// <summary>
/// Headless Host Shape DI 扩展（无 UI 执行宿主）。
/// </summary>
public static class HeadlessHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Headless Host Shape：宿主基线 + 默认 <see cref="DenyAllPermissionChannel"/>（后台/CLI 安全默认）。
    /// 能力包由宿主 sample 自行引用；本包不 ProjectReference Tools.* / Memory / Scheduler 等。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureExecution">可选执行引擎配置</param>
    public static IServiceCollection AddSeeingHostingHeadless(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        services.AddHostShapeBaseline(
            HeadlessHostShape.Descriptor,
            configureExecution,
            withPermissionChannel: false);

        // 无 UI：未显式注册 IPermissionChannel 时使用 DenyAll（后台/CLI 安全默认）
        services.TryAddSingleton<IPermissionChannel, DenyAllPermissionChannel>();

        return services;
    }
}
