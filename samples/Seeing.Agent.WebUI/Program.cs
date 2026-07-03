using Seeing.Agent.Acp.Extensions;
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
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
builder.Services.AddSingleton<ISessionLifecycle, SessionLifecycle>();
builder.Services.AddScoped<SessionProvider>();

// === WebUI 服务 ===
builder.Services.AddScoped<BlazorPermissionChannel>();
builder.Services.AddScoped<IPermissionChannel>(sp => sp.GetRequiredService<BlazorPermissionChannel>());
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<EventStreamHandler>();
builder.Services.AddScoped<ErrorHandlingService>();
builder.Services.AddSingleton<McpStateService>();
builder.Services.AddSingleton<SkillStateService>();
builder.Services.AddSingleton<ToolStateService>();
builder.Services.AddSingleton<SeeingConfigService>();
builder.Services.AddSingleton<GatewayClientConfigService>();
builder.Services.AddSingleton<GatewayClientSupervisor>();
builder.Services.AddHostedService<GatewayClientHostedService>();
builder.Services.AddHttpClient();

// === 消息渲染管线 ===
builder.Services.AddMessageRendering();

// AntDesign 2.0 配置
builder.Services.AddAntDesign();

var app = builder.Build();

// 初始化 Seeing.Agent 组件（Skills/MCP/Plugins）
// 使用 ContentRootPath（项目目录），避免 dotnet run --project 时 CWD 落在解决方案根目录
var workspaceRoot = app.Environment.ContentRootPath;
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;

    // 初始化核心组件（不包括 Memory 插件）
    await sp.InitializeSeeingAgentAsync(workspaceRoot);

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

    sp.ReloadGatewayChannelRegistry(workspaceRoot);
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