using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Hosting.Gateway;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.IO.Local;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Tools.Basic;
using Seeing.Agent.Core.Tools.FileSystem;
using Seeing.Agent.Core.Tools.Git;
using Seeing.Agent.Core.Tools.Question;
using Seeing.Agent.Core.Tools.Shell;
using Seeing.Agent.Core.Tools.Web;

var builder = Host.CreateApplicationBuilder(args);

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);
builder.Services.AddSeeingModule<LocalExecutionWorldModule>(registry);
builder.Services.AddSeeingModule<FileSystemModule>(registry);
builder.Services.AddSeeingModule<WebModule>(registry);
builder.Services.AddSeeingModule<ShellModule>(registry);
builder.Services.AddSeeingModule<BasicModule>(registry);
builder.Services.AddSeeingModule<GitModule>(registry);
builder.Services.AddSeeingModule<QuestionToolsModule>(registry);
builder.Services.AddSeeingModule<SkillsModule>(registry);
builder.Services.AddSeeingModule<McpModule>(registry);
builder.Services.AddSeeingModule<OpenAiLlmModule>(registry);
builder.Services.AddSeeingModule<AnthropicLlmModule>(registry);
builder.Services.AddSeeingModule<AgentsBuiltInModule>(registry);
builder.Services.AddSeeingAcp(registry);
builder.Services.AddSeeingScheduler(registry);
builder.Services.AddSeeingHostingGateway();
BootOverrideSource.ApplyToServices(builder.Services, args);
builder.Services.AddSeeingGatewayServer(registry, builder.Configuration);
builder.Services.AddGatewayChannelRegistry();
builder.Services.AddChannelHostManagement();
builder.Services.AddSeeingCore(registry);

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Gateway.Server");

// 初始化（自动解析工作区）— 必须在 Host.StartAsync 之前
await host.Services.InitializeSeeingAsync();

// 初始化 ChannelHost 注册表
host.Services.ReloadGatewayChannelRegistry();

logger.LogInformation("Gateway Server 就绪，按 Ctrl+C 退出。");

await host.RunAsync();
