using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Hosting.Headless;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.Web;
using Seeing.IO.Local;
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
        builder.Services.AddSeeingModule<LocalExecutionWorldModule>(registry);
        builder.Services.AddSeeingModule<FileSystemModule>(registry);
        builder.Services.AddSeeingModule<WebModule>(registry);
        builder.Services.AddSeeingModule<ShellModule>(registry);
        builder.Services.AddSeeingModule<BasicModule>(registry);
        builder.Services.AddSeeingModule<GitModule>(registry);
        builder.Services.AddSeeingModule<SkillsModule>(registry);
        builder.Services.AddSeeingModule<McpModule>(registry);
        builder.Services.AddSeeingModule<OpenAiLlmModule>(registry);
        builder.Services.AddSeeingModule<AnthropicLlmModule>(registry);
        builder.Services.AddSeeingModule<AgentsBuiltInModule>(registry);
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
