using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Hosting.Headless;

/// <summary>
/// Headless Host Shape DI 扩展（无 UI 执行宿主）。
/// </summary>
public static class HeadlessHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Headless Host Shape：默认 <see cref="DenyAllPermissionChannel"/> + ChatOrchestrator + ExecutionEngine。
    /// 能力包由宿主 sample 自行引用；本包不 ProjectReference Tools.* / Memory / Scheduler 等。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureExecution">可选执行引擎配置</param>
    public static IServiceCollection AddSeeingHostingHeadless(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        services.AddSingleton(HeadlessHostShape.Descriptor);
        // D10：HostDefaultSeams=executionWorld→io.local；Boot 未设 → 回退 *
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = HeadlessHostShape.Descriptor.DefaultScenario,
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });

        // 无 UI：未显式注册 IPermissionChannel 时使用 DenyAll（后台/CLI 安全默认）
        services.TryAddSingleton<IPermissionChannel>(DenyAllPermissionChannel.Instance);

        services.AddChatOrchestrator();
        services.AddExecutionEngine(configureExecution);

        return services;
    }
}
