using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingGatewayServer(builder.Configuration);

var host = builder.Build();

var workspaceRoot = Directory.GetCurrentDirectory();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Gateway.Server");

logger.LogInformation("初始化 Seeing Agent 组件，工作区: {WorkspaceRoot}", workspaceRoot);
await host.Services.InitializeSeeingAgentAsync(workspaceRoot);

logger.LogInformation("Gateway Server 就绪，按 Ctrl+C 退出。");

await host.RunAsync();
