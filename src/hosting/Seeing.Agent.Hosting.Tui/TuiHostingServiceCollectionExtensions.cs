using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Tui.Permissions;

namespace Seeing.Agent.Hosting.Tui;

/// <summary>
/// Tui Host Shape DI 扩展（薄接线，镜像 Hosting.Web / Hosting.Headless）。
/// </summary>
public static class TuiHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Tui Host Shape：HostShapeDescriptor + ProcessSettlementOptions + IPermissionChannel
    /// + ChatOrchestrator + ExecutionEngine。
    /// <para>
    /// 宿主 sample 仍须自行注册：
    /// <list type="bullet">
    /// <item><c>TuiSurfaceProvider</c>（向两个 SurfaceRegistry 注册）</item>
    /// <item>引擎 / 渲染 / 输入 / 提示等 TUI 实现</item>
    /// <item>能力包（Tools.* / Memory / Scheduler 等）— 本包不引用</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureExecution">可选执行引擎配置</param>
    public static IServiceCollection AddSeeingHostingTui(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        services.AddSingleton(TuiHostShape.Descriptor);

        // 与其它 Host Shape 一致：HostDefaultSeams=executionWorld→io.local；Boot 未设 → 回退 *
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = TuiHostShape.Descriptor.DefaultScenario,
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });

        // 事件流承载通道（语义同 Hosting.Web 的 EventStreamPermissionChannel）
        services.TryAddSingleton<IPermissionChannel, TuiPermissionChannel>();

        services.AddChatOrchestrator();
        services.AddExecutionEngine(configureExecution);

        return services;
    }
}
