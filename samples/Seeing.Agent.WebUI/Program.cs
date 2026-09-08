using System.Net;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Web;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.Hosting.Web.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.IO.Local;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.Web;
using Seeing.Agent.WebUI.Rendering;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Provider.DeepSeek;
using Seeing.Provider.OpenCodeZen;

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

// 共享配置节注册表：模块先于 Core 登记节
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
builder.Services.AddTokenBudgetIntegration(registry, builder.Configuration);
builder.Services.AddSeeingGatewayServer(registry, builder.Configuration);
builder.Services.AddGatewayChannelRegistry();

// === Memory 服务（混合检索、图谱、成本控制）===
builder.Services.AddMemoryServices(registry);
builder.Services.AddDeepSeekProvider();
builder.Services.AddOpenCodeZenProvider();

builder.Services.AddSeeingCore(registry);

// === Session 管理：由 AddSeeingCore 统一注册 ISessionStore + SessionManager + ISessionManager + ISessionEventPublisher ===
// 勿再调用 AddSessionManager() / 重复注册 ISessionEventPublisher，避免双实例分裂

// === Web Host Shape：Circuit + BlazorPermissionChannel ===
builder.Services.AddSeeingHostingWeb();
builder.Services.AddSingleton<UiContributionRegistry>();
builder.Services.AddSingleton<IUiContributionRegistry>(sp => sp.GetRequiredService<UiContributionRegistry>());

builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<MessageTimelineStore>();

// 会话事件流路由（Singleton）：按会话统一订阅 + 按 circuit 关联 Scoped 消费者。
// CircuitContext（Scoped）：载入 circuit.Id，供页面经 Router.GetOrCreateConsumer 关联消费者。
// TaskCardAggregator（Scoped）：每父会话一实例，聚合子代理 TaskSteps。
builder.Services.AddSingleton<SessionEventStreamRouter>();
builder.Services.AddSingleton<ICircuitResourceCleanup>(sp => sp.GetRequiredService<SessionEventStreamRouter>());
builder.Services.AddScoped<TaskCardAggregator>();
builder.Services.AddScoped<TaskSessionResolver>();
builder.Services.AddScoped<ConferenceRegistry>();

// EventStreamHandler：页面渲染实例经 SessionEventStreamRouter.GetOrCreateConsumer 按会话创建（Session.razor）。
// 此处保留的 Scoped 注册作为"全局权限事件总线"占位实例（sessionId 为空串）：
// BlazorPermissionChannel.RequestAsync 经它 PublishAsync 触发 OnPermissionRequest，
// PermissionHost 订阅该实例弹权限窗（主渲染 handler 的权限事件无人订阅，无副作用）。
// 此注册为权限链路必需，不可移除。
builder.Services.AddScoped<EventStreamHandler>(sp =>
    new EventStreamHandler(string.Empty, sp.GetRequiredService<ISessionManager>()));
builder.Services.AddScoped<IPermissionEventSink>(sp => sp.GetRequiredService<EventStreamHandler>());
builder.Services.AddScoped<ErrorHandlingService>();
builder.Services.AddSingleton<McpStateService>();
builder.Services.AddSingleton<SeeingConfigService>();
builder.Services.AddSingleton<ISeeingConfigService>(sp => sp.GetRequiredService<SeeingConfigService>());
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

var app = builder.Build();


// 仅允许本机调用的快速关闭接口。使用 StopApplication 触发正常 Host 生命周期，
// 不直接 Kill 进程，确保连接、后台任务和资源按 .NET Host 规则释放。
var shutdownRequested = 0;
app.MapPost("/api/webui/shutdown", (
    HttpContext context,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    if (Interlocked.Exchange(ref shutdownRequested, 1) == 0)
    {
        loggerFactory.CreateLogger("WebUiShutdown")
            .LogInformation("收到本机 WebUI 关闭请求");

        context.Response.OnCompleted(() =>
        {
            loggerFactory.CreateLogger("WebUiShutdown")
                .LogInformation("关闭响应已完成，触发 WebUI Host 停止");
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
    }

    return Results.Accepted();
});

// 初始化 Seeing.Agent 组件（Skills/MCP/Plugins）
// 工作区自动根据配置解析：环境变量 > 项目自定义路径 > 全局默认 > 启动目录
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;

    // 初始化核心组件（自动解析工作区）
    await sp.InitializeSeeingAsync();

    // 壳层 *Page + 模块 Nav 绑定 ComponentType，供 ModuleRouter 渲染
    if (sp.GetService<IUiContributionRegistry>() is { } uiRegistry)
    {
        uiRegistry.Register(new CoreShellUiContribution());
        ModulePageRouteBinder.BindExistingPages(uiRegistry);
    }

    // 初始化命令发现
    sp.InitializeCommands();
    sp.InitializeAcpCommands();  // 注册 ACP 专属命令

    // 注册 Memory Hook / Tools 由 AddMemoryServices 内 Bootstrap + ITool 自注册
    
    // 注册 TokenBudget Hook Handler（自动管理 token 预算）
    sp.UseTokenBudgetHooks();

    var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
    sp.ReloadGatewayChannelRegistry(workspaceProvider.GetProjectRoot());
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
