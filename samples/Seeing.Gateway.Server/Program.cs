using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Scheduler.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingAcp();
builder.Services.AddSeeingScheduler();
builder.Services.AddSeeingGatewayServer(builder.Configuration);

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Gateway.Server");

// 初始化（自动解析工作区）
await host.Services.InitializeSeeingAgentAsync();

logger.LogInformation("Gateway Server 就绪，按 Ctrl+C 退出。");

await host.RunAsync();
