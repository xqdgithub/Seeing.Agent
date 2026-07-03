using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// ACP 包 DI 注册扩展。
/// </summary>
public static class AcpServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Seeing.Agent.Acp 全部服务，并将 <see cref="IAgentExecutionRouter"/> 装饰为 ACP 路由。
    /// 需在 <c>AddSeeingAgent</c> 之后调用。
    /// </summary>
    public static IServiceCollection AddSeeingAcp(this IServiceCollection services)
    {
        services.AddSingleton<IAcpBackendRegistry, AcpBackendRegistry>();
        services.AddSingleton<AcpPermissionBridge>();
        services.AddSingleton<AcpFileSystemBridge>();
        services.AddSingleton<AcpTerminalBridge>();
        services.AddSingleton<SeeingAcpClientFactory>();
        services.AddSingleton<AcpConnectionManager>();
        services.AddSingleton<AcpLifecycleService>();
        services.AddSingleton<AcpSessionStore>();
        services.AddSingleton<AcpTaskStore>();
        services.AddSingleton<AcpEventMapper>();
        services.AddSingleton<ContentBlockMapper>();
        services.AddSingleton<AcpMcpServerMapper>();
        services.AddSingleton<AcpCancellationCoordinator>();
        services.AddSingleton<AcpSessionLifecycleHook>();
        services.AddSingleton<IAcpSessionRunner, AcpSessionRunner>();
        services.AddSingleton<AcpPassthroughExecutor>();
        services.AddSingleton<AcpTool>();
        services.AddSingleton<AcpStatusTool>();
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<AcpTool>());
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<AcpStatusTool>());

        services.AddHostedService<AcpHookRegistrationHostedService>();
        services.AddHostedService<AcpAgentRegistrationHostedService>();
        services.AddHostedService<AcpConnectionIdleCleanupHostedService>();

        ReplaceExecutionRouter(services);
        return services;
    }

    private static void ReplaceExecutionRouter(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IAgentExecutionRouter));
        if (existing != null)
            services.Remove(existing);

        services.AddSingleton<NativeAgentExecutionRouter>();
        services.AddSingleton<AcpAgentExecutionRouter>();
        services.AddSingleton<IAgentExecutionRouter>(sp => sp.GetRequiredService<AcpAgentExecutionRouter>());
    }
}
