using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.HostComposition;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Gateway;
using Seeing.Agent.TokenBudget.Extensions;

var builder = Host.CreateApplicationBuilder(args);

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

// ── Full 能力集：与 WebUI / Tui / CLI 共用同一注册点 ──
builder.Services.AddSeeingFullCapabilities(registry, builder.Configuration, includeGatewayServer: true);
builder.Services.AddTokenBudgetHooks();

builder.Services.AddSeeingHostingGateway();
// Gateway 宿主不经过 Hosting.Web/Headless，必须自行补会话运行时与执行引擎；
// 否则 GatewayHost.StartAsync 解析 ISessionRuntime 会失败。
builder.Services.AddSessionHosting();
builder.Services.AddExecutionEngine();

BootOverrideSource.ApplyToServices(builder.Services, args);
builder.Services.AddGatewayChannelRegistry();
builder.Services.AddChannelHostManagement();
builder.Services.AddSeeingCore(registry);

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Gateway.Server");

// 初始化（自动解析工作区）— 必须在 Host.StartAsync 之前
await host.Services.InitializeSeeingAsync();
host.Services.UseTokenBudgetHooks();

// 初始化 ChannelHost 注册表
host.Services.ReloadGatewayChannelRegistry();

logger.LogInformation("Gateway Server 就绪，按 Ctrl+C 退出。");

await host.RunAsync();
