using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Hosting.Gateway;

/// <summary>Gateway Host Shape 常量。</summary>
public static class GatewayHostShape
{
    public const string Id = "gateway";

    /// <summary>形态自描述（声明用；实际启用集由 sample 引用 + 结算决定）。</summary>
    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full,
        DefaultScenario = "full",
    };
}

/// <summary>
/// Gateway Host Shape DI 扩展（不引用/不转发能力集成包）。
/// </summary>
public static class GatewayHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Gateway Host Shape：宿主基线（会话运行时 + 执行引擎，共享空权限通道）。
    /// <para>
    /// <b>此前本扩展只登记形态描述符</b>，导致 <c>Gateway.Server/Program.cs</c> 必须自己手抄
    /// <c>AddSessionHosting()</c> + <c>AddExecutionEngine()</c>（注释也写明「GatewayHost 不经过
    /// Hosting.Web/Headless，必须自行补」）。现统一走共享基线，宿主只剩能力组合。
    /// </para>
    /// <para>
    /// 宿主 sample 须自行组合能力：例如 <c>AddSeeingGatewayServer</c> / <c>AddSeeingModule&lt;GatewayModule&gt;</c>，
    /// 以及 Tools.* / Scheduler 等 — 本包不 ProjectReference 能力集成包。
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingHostingGateway(this IServiceCollection services)
        => services.AddHostShapeBaseline(GatewayHostShape.Descriptor);
}
