using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Hosting.Gateway;

/// <summary>
/// Gateway Host Shape DI 扩展（仅登记 Host Shape 描述符，不引用/不转发能力集成包）。
/// </summary>
public static class GatewayHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Gateway Host Shape：声明可承载模块类型与默认 scenario。
    /// <para>
    /// 宿主 sample 须自行组合能力：例如 <c>AddSeeingGatewayServer</c> / <c>AddSeeingModule&lt;GatewayModule&gt;</c>，
    /// 以及 Tools.* / Scheduler 等 — 本包不 ProjectReference 能力集成包。
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingHostingGateway(this IServiceCollection services)
    {
        services.AddSingleton(GatewayHostShape.Descriptor);
        // D10：HostDefaultSeams=executionWorld→io.local；Boot 未设 → 回退 *
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = GatewayHostShape.Descriptor.DefaultScenario,
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });
        return services;
    }
}
