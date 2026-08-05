using Microsoft.AspNetCore.Components.Server.Circuits;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.App;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.WebUI.Rendering;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.Agent.TokenBudget.Extensions;

var builder = WebApplication.CreateBuilder(args);

// 启用 .NET 9+ StaticWebAssets 管道。
// 在 .NET 9+，Microsoft.NET.Sdk.Web 不再把 wwwroot 复制到 bin\<Config>\<TFM>\wwwroot，
// 改为生成 staticwebassets 清单（runtime.json + endpoints.json）。
// 单独调用 app.UseStaticFiles() 找不到 wwwroot 物理目录，会导致 js/app.js、css/*.css、
// _framework/blazor.server.js 全部 404，破坏 13 处 JS 互操作（'isMobileBrowser is not a function'）。
// 在 builder 上调用 UseStaticWebAssets() 让中间件从清单路由文件，适用于所有环境。
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingAcp();
builder.Services.AddSeeingScheduler();
builder.Services.AddTokenBudgetIntegration(builder.Configuration);

// === ISummarizer for LLM-based compression ===
builder.Services.AddSingleton<ISummarizer, LlmSummarizer>();
builder.Services.AddSingleton<SummarizingStrategy>();
builder.Services.AddSingleton<HybridStrategy>();

builder.Services.AddSeeingGatewayServer(builder.Configuration);
builder.Services.AddGatewayChannelRegistry();

// === Memory 服务（混合检索、图谱、成本控制）===
builder.Services.AddMemoryServices();

// === Session 管理：由 AddSeeingAgent 统一注册 ISessionStore + SessionManager + ISessionManager + ISessionEventPublisher ===
// 勿再调用 AddSessionManager() / 重复注册 ISessionEventPublisher，避免双实例分裂

// === WebUI 服务 ===
builder.Services.AddScoped<BlazorPermissionChannel>();
// BlazorPermissionChannel 包 SerializingPermissionChannel（记忆层 + 工作区检查 + 串行化）
builder.Services.AddScoped<IPermissionChannel>(sp =>
{
    var memory = sp.GetRequiredService<Seeing.Agent.Core.Permission.IPermissionMemory>();
    var workspace = sp.GetService<Seeing.Agent.Configuration.IWorkspaceProvider>();
    var inner = sp.GetRequiredService<BlazorPermissionChannel>();
    return new Seeing.Agent.Core.Permission.SerializingPermissionChannel(inner, memory, workspace);
});
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<MessageTimelineStore>();
builder.Services.AddScoped<EventStreamHandler>();
builder.Services.AddScoped<ErrorHandlingService>();
builder.Services.AddScoped<SessionCompactionService>();
builder.Services.AddSingleton<McpStateService>();
builder.Services.AddSingleton<SeeingConfigService>();
builder.Services.AddSingleton<GatewayClientConfigService>();
builder.Services.AddChannelHostManagement();
builder.Services.AddSingleton<GatewayClientSupervisor>();
builder.Services.AddSingleton<WorkspaceSwitchService>();

// TokenBudget Notifier (必须在 AddTokenBudgetHooks 之前注册)
builder.Services.AddSingleton<Seeing.Agent.TokenBudget.IBudgetStatusNotifier, BudgetStatusNotifier>();

// TokenBudget Hooks (依赖 IBudgetStatusNotifier)
builder.Services.AddTokenBudgetHooks();

// === ChatOrchestrator 统一入口 ===
builder.Services.AddChatOrchestrator();

// === 执行引擎（后台执行服务）===
builder.Services.AddExecutionEngine(options =>
{
    options.MaxConcurrentExecutions = -1;  // -1 = 无限制
    options.EventBufferSize = 100;
    options.ExecutionHistoryLimit = 100;
    options.SessionIdleTimeout = TimeSpan.FromMinutes(30);
    options.CleanupInterval = TimeSpan.FromMinutes(5);
});

// === 命令服务 ===
builder.Services.AddScoped<CommandListService>();

// === 调度器状态服务 ===
builder.Services.AddSingleton<SchedulerStatusService>();
builder.Services.AddSingleton<JobNotificationService>();

builder.Services.AddHttpClient();

// === 消息渲染管线 ===
builder.Services.AddMessageRendering();

// AntDesign 2.0 配置
builder.Services.AddAntDesign();

// Circuit 生命周期管理（JSDisconnectedException 防护）
builder.Services.AddSingleton<CircuitTracker>();
builder.Services.AddScoped<CircuitHandler, SeeingCircuitHandler>();

var app = builder.Build();

// 初始化 Seeing.Agent 组件（Skills/MCP/Plugins）
// 工作区自动根据配置解析：环境变量 > 项目自定义路径 > 全局默认 > 启动目录
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;

    // 初始化核心组件（自动解析工作区）
    await sp.InitializeSeeingAgentAsync();

    // 初始化命令发现
    sp.InitializeCommands();
    sp.InitializeAcpCommands();  // 注册 ACP 专属命令

    // 注册 Memory Hook / Tools 由 AddMemoryServices 内 Bootstrap + ITool 自注册
    
    // 注册 TokenBudget Hook Handler（自动管理 token 预算）
    sp.UseTokenBudgetHooks();

    var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
    sp.ReloadGatewayChannelRegistry(workspaceProvider.WorkspaceRoot);
}

// 局域网 HTTP 分发默认关闭 HTTPS 跳转（SEEING_DISABLE_HTTPS_REDIRECTION=true 或配置 DisableHttpsRedirection）
var disableHttpsRedirection =
    app.Configuration.GetValue("DisableHttpsRedirection", false)
    || string.Equals(
        Environment.GetEnvironmentVariable("SEEING_DISABLE_HTTPS_REDIRECTION"),
        "true",
        StringComparison.OrdinalIgnoreCase);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    if (!disableHttpsRedirection)
        app.UseHsts();
}

if (!disableHttpsRedirection)
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();