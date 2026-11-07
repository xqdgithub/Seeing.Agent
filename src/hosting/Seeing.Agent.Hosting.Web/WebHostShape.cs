using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Web.Circuits;

namespace Seeing.Agent.Hosting.Web;

/// <summary>Web Host Shape 常量。</summary>
public static class WebHostShape
{
    public const string Id = "web";

    /// <summary>
    /// Web 形态自描述。<see cref="HostShapeDescriptor.CarriableModuleTypes"/> 只作声明，
    /// 实际启用集由 sample 引用 + Boot/能力集结算决定。
    /// </summary>
    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full,
        DefaultScenario = "full",
    };
}

/// <summary>
/// Web Host Shape DI 扩展。
/// </summary>
public static class WebHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Web Host Shape：宿主基线（会话运行时 + 执行引擎 + 事件流权限通道）+ Blazor Circuit 壳。
    /// <para>
    /// <b>为什么这里也要 AddSessionHosting/AddExecutionEngine：</b>此前本扩展只注册 Circuit 与权限通道，
    /// 于是 <c>WebUiHost</c> 与 <c>Gateway.Server/Program.cs</c> 各自手抄同样两行 —— 定义「最小可执行宿主」
    /// 的地方有四份、参数必须人工对齐。现在统一走 <see cref="HostShapeRegistration.AddHostShapeBaseline"/>。
    /// </para>
    /// <para>
    /// 宿主 sample 仍须自行注册：
    /// <list type="bullet">
    /// <item>权限呈现/交互服务（通常适配权限管理器与事件流）</item>
    /// <item><see cref="ICircuitResourceCleanup"/>（通常适配 SessionEventStreamRouter）</item>
    /// <item>能力包（Tools.* / Memory / Scheduler 等）— 本包不引用</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configureExecution">可选执行引擎配置。</param>
    public static IServiceCollection AddSeeingHostingWeb(
        this IServiceCollection services,
        Action<ExecutionOptions>? configureExecution = null)
    {
        // 基线：HostShapeDescriptor + ProcessSettlementOptions + IPermissionChannel + SessionHosting + Engine
        services.AddHostShapeBaseline(WebHostShape.Descriptor, configureExecution);

        services.AddScoped<CircuitContext>();
        services.AddSingleton<CircuitTracker>();
        services.AddScoped<CircuitHandler, SeeingCircuitHandler>();

        return services;
    }
}
