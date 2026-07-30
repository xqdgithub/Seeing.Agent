using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.App;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;

namespace Seeing.Agent.Cli.Infrastructure;

public static class CliServiceBootstrap
{
    /// <summary>
    /// 构建 DI 容器，复用 WebUI 的核心注册链。
    /// 不使用 Blazor/AntDesign/Circuit/MessageRendering。
    /// </summary>
    public static async Task<IHost> BuildHostAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSeeingAgent(builder.Configuration);
        builder.Services.AddSeeingAcp();
        builder.Services.AddSeeingScheduler();
        builder.Services.AddSeeingGatewayServer(builder.Configuration);
        builder.Services.AddMemoryServices();
        builder.Services.AddChatOrchestrator();

        var host = builder.Build();

        await host.Services.InitializeSeeingAgentAsync();

        return host;
    }
}
