using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Execution;

namespace Seeing.Agent.Hosting.Tui;

/// <summary>Tui Host Shape 常量。</summary>
public static class TuiHostShape
{
    public const string Id = "tui";

    /// <summary>形态自描述（声明用；实际启用集由 sample 引用 + 结算决定）。</summary>
    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full,
        DefaultScenario = "full",
    };
}

/// <summary>
/// Tui Host Shape DI 扩展（薄接线，镜像 Hosting.Web / Hosting.Headless）。
/// </summary>
public static class TuiHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Tui Host Shape：宿主基线（形态描述 + 进程结算选项 + 事件流权限通道
    /// + AddSessionHosting + ExecutionEngine）。
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
        // 权限通道与 Hosting.Web 共用同一个 EventStreamPermissionChannel（此前是两份逐字相同的副本）
        => services.AddHostShapeBaseline(TuiHostShape.Descriptor, configureExecution);
}
