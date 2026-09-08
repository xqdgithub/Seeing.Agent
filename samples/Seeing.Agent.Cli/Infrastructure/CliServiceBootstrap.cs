using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Hosting.Headless;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        var registry = new ConfigSectionRegistry();
        builder.Services.AddSingleton<IConfigSectionRegistry>(registry);
        builder.Services.AddSeeingAcp(registry);
        builder.Services.AddSeeingScheduler(registry);
        builder.Services.AddSeeingGatewayServer(registry, builder.Configuration);
        builder.Services.AddMemoryServices(registry);
        builder.Services.AddSeeingCore(registry);
        builder.Services.AddSeeingHostingHeadless();

        var host = builder.Build();

        await host.Services.InitializeSeeingAsync();

        return host;
    }
}
