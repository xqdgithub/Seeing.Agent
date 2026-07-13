using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.App;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.WebUI.Rendering;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingAcp();
builder.Services.AddSeeingScheduler();
builder.Services.AddSeeingGatewayServer(builder.Configuration);
builder.Services.AddGatewayChannelRegistry();

// === Memory 模块（直接 DI 注入，便于调试）===
builder.Services.AddSeeingAgentMemory(options =>
{
    // 配置 Memory 存储目录
    options.MemoryStore.MemoryDirectory = "~/.seeing/memories";
    options.MemoryStore.MaxFileSizeKB = 1024;
    options.MemoryStore.EnableChunking = true;
});

// === Session 管理服务（统一使用 Seeing.Session）===
builder.Services.AddSingleton<ISessionStore, FileSessionStore>();
builder.Services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
builder.Services.AddSingleton<SessionManager>();  // 需要在 ISessionEventPublisher 之后注册
builder.Services.AddSingleton<ISessionLifecycle, SessionLifecycle>();

// === WebUI 服务 ===
builder.Services.AddScoped<BlazorPermissionChannel>();
builder.Services.AddScoped<IPermissionChannel>(sp => sp.GetRequiredService<BlazorPermissionChannel>());
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<EventStreamHandler>();
builder.Services.AddScoped<ErrorHandlingService>();
builder.Services.AddScoped<SessionCompactionService>();
builder.Services.AddSingleton<McpStateService>();
builder.Services.AddSingleton<SkillStateService>();
builder.Services.AddSingleton<ToolStateService>();
builder.Services.AddSingleton<SeeingConfigService>();
builder.Services.AddSingleton<GatewayClientConfigService>();
builder.Services.AddSingleton<GatewayClientSupervisor>();
builder.Services.AddSingleton<WorkspaceSwitchService>();

// === ChatOrchestrator 统一入口 ===
builder.Services.AddChatOrchestrator();

// === 执行引擎（后台执行服务）===
builder.Services.AddExecutionEngine(options =>
{
    options.MaxConcurrentExecutions = -1;  // -1 = 无限制
    options.EventBufferSize = 100;
    options.ExecutionHistoryLimit = 100;
    options.PermissionTimeout = TimeSpan.FromSeconds(30);
    options.SessionIdleTimeout = TimeSpan.FromMinutes(30);
    options.CleanupInterval = TimeSpan.FromMinutes(5);
});

// === 命令服务 ===
builder.Services.AddScoped<CommandListService>();

// === 调度器状态服务 ===
builder.Services.AddSingleton<SchedulerStatusService>();
builder.Services.AddSingleton<JobNotificationService>();

builder.Services.AddHostedService<GatewayClientHostedService>();
builder.Services.AddHttpClient();

// === 消息渲染管线 ===
builder.Services.AddMessageRendering();

// AntDesign 2.0 配置
builder.Services.AddAntDesign();

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

    // === Memory 手动初始化（直接注入方式）===
    var memoryManager = sp.GetRequiredService<IMemoryManager>();
    await memoryManager.InitializeAsync();

    // 注册 Memory Hook Handler（自动捕获对话）
    var hookManager = sp.GetRequiredService<IHookManager>();
    var chatHandler = sp.GetRequiredService<ChatMemoryHandler>();
    var toolHandler = sp.GetRequiredService<ToolMemoryHandler>();
    hookManager.Register(chatHandler);
    hookManager.Register(toolHandler);

    // 启动后台写入队列
    var writeQueue = sp.GetRequiredService<MemoryWriteQueue>();
    _ = writeQueue.StartProcessingAsync();

    // 记录日志
    var logger = sp.GetRequiredService<ILogger<MemoryWriteQueue>>();
    logger.LogInformation("Memory 模块已通过 DI 直接注入并初始化");

    var skillState = sp.GetRequiredService<SkillStateService>();
    var toolState = sp.GetRequiredService<ToolStateService>();
    await skillState.LoadAsync();
    await toolState.LoadAsync();

    var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
    sp.ReloadGatewayChannelRegistry(workspaceProvider.WorkspaceRoot);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();