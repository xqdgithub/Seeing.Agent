using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.Hosting.Web.Permissions;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Hosting.Web;

/// <summary>
/// Web Host Shape DI 扩展。
/// </summary>
public static class WebHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Web Host Shape：Circuit 壳 + <see cref="EventStreamPermissionChannel"/>。
    /// <para>
    /// 宿主 sample 仍须自行注册：
    /// <list type="bullet">
    /// <item>权限呈现/交互服务（通常适配权限管理器与事件流）</item>
    /// <item><see cref="ICircuitResourceCleanup"/>（通常适配 SessionEventStreamRouter）</item>
    /// <item>能力包（Tools.* / Memory / Scheduler 等）— 本包不引用</item>
    /// </list>
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingHostingWeb(this IServiceCollection services)
    {
        services.AddSingleton(WebHostShape.Descriptor);
        // D10 Host Shape：HostDefaultSeams 至少 executionWorld=io.local；Boot 未设 → 回退 *
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = WebHostShape.Descriptor.DefaultScenario,
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });

        services.AddScoped<CircuitContext>();
        services.AddSingleton<CircuitTracker>();
        services.AddScoped<CircuitHandler, SeeingCircuitHandler>();

        services.AddSingleton<IPermissionChannel, EventStreamPermissionChannel>();

        return services;
    }
}
