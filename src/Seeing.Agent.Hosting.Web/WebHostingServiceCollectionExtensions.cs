using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.Hosting.Web.Permissions;
using Seeing.Agent.Modules;

namespace Seeing.Agent.Hosting.Web;

/// <summary>
/// Web Host Shape DI 扩展。
/// </summary>
public static class WebHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Web Host Shape：Circuit 壳 + <see cref="BlazorPermissionChannel"/>（经 SerializingPermissionChannel 包装）。
    /// <para>
    /// 宿主 sample 仍须自行注册：
    /// <list type="bullet">
    /// <item><see cref="IPermissionEventSink"/>（通常适配 EventStreamHandler）</item>
    /// <item><see cref="ICircuitResourceCleanup"/>（通常适配 SessionEventStreamRouter）</item>
    /// <item>能力包（Tools.* / Memory / Scheduler 等）— 本包不引用</item>
    /// </list>
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingHostingWeb(this IServiceCollection services)
    {
        services.AddSingleton(WebHostShape.Descriptor);
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = WebHostShape.Descriptor.DefaultScenario,
        });

        services.AddScoped<CircuitContext>();
        services.AddSingleton<CircuitTracker>();
        services.AddScoped<CircuitHandler, SeeingCircuitHandler>();

        services.AddScoped<BlazorPermissionChannel>();
        services.AddScoped<IPermissionChannel>(sp =>
        {
            var memory = sp.GetRequiredService<IPermissionMemory>();
            var workspace = sp.GetService<IWorkspaceProvider>();
            var inner = sp.GetRequiredService<BlazorPermissionChannel>();
            return new SerializingPermissionChannel(inner, memory, workspace);
        });

        return services;
    }
}
