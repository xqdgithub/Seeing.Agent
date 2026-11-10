using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Ui;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.HostComposition;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Web;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Agent.WebUI.Endpoints;
using Seeing.Agent.WebUI.Rendering;
using Seeing.Agent.WebUI.Rendering.Providers;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.Shell;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using System.Net;

namespace Seeing.Agent.WebUI.Hosting;

/// <summary>
/// WebUI 宿主组装（单一真相源）。
/// <para>
/// 浏览器宿主（<c>Seeing.Agent.WebUI</c> 的可执行入口）与桌面宿主
/// （<c>Seeing.Agent.Desktop</c> 的原生窗口）共用本方法，
/// 避免两份 DI 注册表漂移：新增能力模块只改这里。
/// </para>
/// </summary>
public static class WebUiHost
{
    /// <summary>
    /// 组装 WebUI 宿主但 <b>不启动</b>。
    /// 调用方负责 <c>StartAsync</c>/<c>RunAsync</c>，以便：
    /// 绑定动态端口后读取实际地址（桌面外壳需要先知道 URL 才能开窗），
    /// 或直接 <c>Run</c>（浏览器宿主）。
    /// </summary>
    /// <param name="args">命令行参数（透传给配置与 <c>--urls</c>）。</param>
    /// <param name="contentRoot">
    /// 内容根；桌面宿主须传 <see cref="AppContext.BaseDirectory"/>，
    /// 否则 <c>WebApplication.CreateBuilder</c> 会以宿主进程的当前工作目录为准，
    /// 导致静态资源清单与 wwwroot 定位失败。
    /// </param>
    /// <param name="configureLogging">
    /// 可选的日志配置钩子。桌面宿主用它把日志改到文件（桌面形态没有控制台窗口，
    /// 默认 Console 日志会随窗口一起消失）。为 null 时保持浏览器宿主的默认行为。
    /// </param>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        string? contentRoot = null,
        Action<ILoggingBuilder>? configureLogging = null,
        string? startupDirectory = null)
    {
        var builder = contentRoot is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

        configureLogging?.Invoke(builder.Logging);

        // 启用 .NET 9+ StaticWebAssets 管道。
        // 在 .NET 9+，Microsoft.NET.Sdk.Web 不再把 wwwroot 复制到 bin\<Config>\<TFM>\wwwroot，
        // 改为生成 staticwebassets 清单（runtime.json + endpoints.json）。
        // 单独调用 app.UseStaticFiles() 找不到 wwwroot 物理目录，会导致 js/app.js、css/*.css、
        // _framework/blazor.server.js 全部 404，破坏 13 处 JS 互操作（'isMobileBrowser is not a function'）。
        // 在 builder 上调用 UseStaticWebAssets() 让中间件从清单路由文件，适用于所有环境。
        builder.WebHost.UseStaticWebAssets();

        // Add services to the container.
        // Razor Pages + Blazor 组件编译在 WebUI 程序集内；当宿主是桌面外壳（入口程序集 ≠ WebUI）时，
        // 该程序集不会被默认加入 MVC 应用部件，_Host.cshtml 等页将无法被发现（404）。
        // 独立运行 WebUI 时入口程序集就是本程序集，无需重复添加。
        var razorPages = builder.Services.AddRazorPages();
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(WebUiHost).Assembly)
            razorPages.AddApplicationPart(typeof(WebUiHost).Assembly);
        builder.Services.AddServerSideBlazor();

        // 共享配置节注册表：模块先于 Core 登记节
        var registry = new ConfigSectionRegistry();
        builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

        // ── Full 能力集：与 Tui / CLI / Gateway.Server 共用同一注册点 ──
        // 新增能力模块只需改 samples/Seeing.Agent.HostComposition，不再逐宿主补行。
        builder.Services.AddSeeingFullCapabilities(registry, builder.Configuration, includeGatewayServer: true);
        builder.Services.AddGatewayChannelRegistry();

        builder.Services.AddSeeingCore(registry);

        // === Session 管理：由 AddSeeingCore 统一注册 ISessionStore + SessionManager + ISessionManager + ISessionEventPublisher ===
        // 勿再调用 AddSessionManager() / 重复注册 ISessionEventPublisher，避免双实例分裂

        // === Web Host Shape：Circuit + EventStreamPermissionChannel ===
        builder.Services.AddSeeingHostingWeb(options =>
        {
            options.MaxConcurrentExecutions = -1;  // -1 = 无限制
            options.EventBufferSize = 100;
            options.ExecutionHistoryLimit = 100;
            options.SessionIdleTimeout = TimeSpan.FromMinutes(30);
            options.CleanupInterval = TimeSpan.FromMinutes(5);
        });
        BootOverrideSource.ApplyToServices(builder.Services, args);

        // ── UI 扩展信封（必须在 Build() 之前）──
        // 扩展点目录为空时所有贡献都会被判「未声明的扩展点」而拒收 → 侧栏与设置页全空。
        builder.Services.AddSeeingUiPoints([.. WebUiPoints.All]);
        builder.Services.AddSingleton<WebUiViewResolver>();
        builder.Services.AddSingleton<IUiViewKeyCatalog>(sp => sp.GetRequiredService<WebUiViewResolver>());
        builder.Services.AddSingleton<IUiContributionValidator, WebUiUiContributionValidator>();
        builder.Services.AddSingleton<IUiHostContributor, WebUiHostContributor>();

        builder.Services.AddSingleton<AppState>();
        builder.Services.AddScoped<SessionState>();
        builder.Services.AddScoped<UploadedFileService>();
        builder.Services.AddScoped<SpeechTranscriptionService>();
        builder.Services.AddScoped<MessageTimelineStore>();

        // 会话事件流路由（Singleton）：按会话统一订阅 + 按 circuit 关联 Scoped 消费者。
        // CircuitContext（Scoped）：载入 circuit.Id，供页面经 Router.GetOrCreateConsumer 关联消费者。
        // TaskCardAggregator（Scoped）：每父会话一实例，聚合子代理 TaskSteps。
        builder.Services.AddSingleton<SessionEventStreamRouter>();
        builder.Services.AddSingleton<ICircuitResourceCleanup>(sp => sp.GetRequiredService<SessionEventStreamRouter>());
        // 权限全局收件箱（Singleton 投影：GetAllPending → PermissionCardModel + Changed）
        builder.Services.AddSingleton<PermissionInbox>();
        // 当前标签权限收件箱视图（Scoped，circuit 维度；作用域 = SessionWindowRegistry.Windows）
        builder.Services.AddScoped<PermissionInboxView>();
        // WebUI circuit 呈现端（Scoped）：向 IPermissionSurfaceRegistry 声明可呈现会话集合
        builder.Services.AddScoped<WebUiSurfacePresenter>();
        builder.Services.AddScoped<TaskCardAggregator>();
        builder.Services.AddScoped<PermissionInteractionService>();
        // 问题全局收件箱（Singleton 投影）与当前标签视图（Scoped，circuit 维度）
        builder.Services.AddSingleton<QuestionInbox>();
        builder.Services.AddScoped<QuestionInboxView>();
        builder.Services.AddScoped<QuestionInteractionService>();
        builder.Services.AddScoped<IToolCallSlotProvider, QuestionToolCallSlotProvider>();
        builder.Services.AddScoped<TaskSessionResolver>();
        builder.Services.AddScoped<SessionWindowRegistry>();
        // 文件面板：桥接（Scoped，circuit 维度 IStreamConsumer）+ 开合状态（Scoped，circuit 维度）
        builder.Services.AddScoped<Seeing.Agent.WebUI.Services.AgentFileChangeBridge>();
        builder.Services.AddScoped<Seeing.Agent.WebUI.Files.Services.FilePanelState>();
        builder.Services.AddScoped<Seeing.Agent.WebUI.Files.Services.FileTreeState>();

        // 宿主请求（Host Request）：全局收件箱（Singleton 投影）+ 活跃面板登记（Singleton）+ 派发器（Scoped，circuit 维度）
        builder.Services.AddSingleton<Seeing.Agent.WebUI.Services.HostRequestInbox>();
        builder.Services.AddSingleton<Seeing.Agent.WebUI.Services.HostRequestActivePanelRegistry>();
        builder.Services.AddScoped<Seeing.Agent.WebUI.Services.HostRequestDispatcher>();
        // 跨组件 UI 覆盖层请求（Scoped，circuit 维度）：主页卡片 → 审批中心抽屉
        builder.Services.AddScoped<Seeing.Agent.WebUI.Services.UiOverlayService>();

        // EventStreamHandler：页面渲染实例经 SessionEventStreamRouter.GetOrCreateConsumer 按会话创建（Session.razor）。
        // 此处保留的 Scoped 注册作为全局事件处理占位实例（sessionId 为空串）。
        builder.Services.AddScoped<EventStreamHandler>(sp =>
            new EventStreamHandler(string.Empty, sp.GetRequiredService<ISessionManager>()));
        builder.Services.AddScoped<ErrorHandlingService>();
        builder.Services.AddSingleton<McpStateService>();
        builder.Services.AddSingleton<SeeingConfigService>();
        builder.Services.AddSingleton<ISeeingConfigService>(sp => sp.GetRequiredService<SeeingConfigService>());
        builder.Services.AddSingleton<GatewayClientConfigService>();
        builder.Services.AddChannelHostManagement();
        builder.Services.AddSingleton<GatewayClientSupervisor>();
        builder.Services.AddSingleton<WorkspaceSwitchService>();

        // 目录选择：进程内服务，只给 DirectoryPicker 用（列出当前工作区之外的目录）
        builder.Services.AddSingleton<IDirectoryBrowser, DirectoryBrowser>();

        // 工作区切换进度：单例切换器经 Hub 上报到当前 circuit 的覆盖层
        builder.Services.AddSingleton<WorkspaceSwitchProgressHub>();
        builder.Services.AddSingleton<IWorkspaceSwitchProgress>(sp =>
            sp.GetRequiredService<WorkspaceSwitchProgressHub>());
        builder.Services.AddScoped<WorkspaceSwitchProgress>();

        // 首次选定工作区后的补初始化（命令发现 / TokenBudget 钩子 / Gateway 渠道注册）
        builder.Services.AddSingleton<WorkspaceReadyInitializer>();
        builder.Services.AddSingleton<WorkspaceSwitcherReadiness>();

        // 启动决议：宿主在 Build 之前登记。桌面外壳已经改过进程当前目录，
        // 所以启动目录必须由调用方显式传入，不能在这里读 GetCurrentDirectory。
        var launchStartupDirectory = startupDirectory ?? Directory.GetCurrentDirectory();
        builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
            Mode: WorkspaceLaunchMode.Interactive,
            StartupDirectory: launchStartupDirectory,
            ExecutableDirectory: AppContext.BaseDirectory,
            ExplicitPath: ReadWorkspaceArgument(args),
            InheritedEnvironment: Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT"),
            History: WorkspaceHistorySnapshot.Empty)));

        // TokenBudget Notifier (必须在 AddTokenBudgetHooks 之前注册)
        builder.Services.AddSingleton<Seeing.Agent.TokenBudget.IBudgetStatusNotifier, BudgetStatusNotifier>();

        // TokenBudget Hooks (依赖 IBudgetStatusNotifier)
        builder.Services.AddTokenBudgetHooks();

        // === 宿主基线（会话运行时 + 执行引擎）已由 AddSeeingHostingWeb 提供 ===
        // 执行参数必须在 AddSeeingHostingWeb 时经 AddExecutionEngine(configure) 传入：
        // ExecutionOptions 是以**具体实例**注册的单例（AddExecutionEngine 里 new + AddSingleton），
        // 不是 IOptions，因此事后 Configure<T>() 不会被任何消费者读到（Configure 注册的是
        // IOptions<T>，仓内无该类型的 IOptions 消费者）。
        // 见 WebUiHost 中 AddSeeingHostingWeb(...) 调用处的 configureExecution 参数。

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

        // 视图表：必须在校验器工作之前完成登记——校验器会把未登记的视图键判为「视图未装配」并拒收，
        // 结果是侧栏/设置页静默变空
        WebUiViews.RegisterBuiltIns(app.Services.GetRequiredService<WebUiViewResolver>());

        // 宿主壳层 UI 声明：Build 之后、InitializeSeeingAsync 之前收集。
        // 壳层贡献是进程级的（Requires = []），与工作区是否选定无关——否则用户在工作区首页
        // 就看不到侧栏与设置入口，无法自救。
        var collector = app.Services.GetRequiredService<UiContributionCollector>();
        var hostCount = collector.CollectHostContributions(app.Services.GetServices<IUiHostContributor>());
        if (hostCount == 0)
        {
            app.Logger.LogError(
                "未收集到任何宿主 UI 贡献：侧栏与设置页将为空白，请检查 IUiHostContributor 注册");
        }

        // 文件面板插槽：归属模块是能力包 files.workspace，但视图键指向 WebUI 的 SessionFilePanel，
        // 而能力包不能引用宿主组件类型，因此由宿主以该模块的 owner id 代为声明。
        // 停用模块后面板消失由可见性（Requires = ["files.workspace"]）保证，不靠撤销声明。
        var uiRegistry = app.Services.GetRequiredService<IUiContributionRegistry>();
        uiRegistry.AddRange(FilePanelUiContribution.OwnerModuleId, new FilePanelUiContribution().DescribeUi());

        using (var scope = app.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;

            // 初始化核心组件（自动解析工作区）
            await sp.InitializeSeeingAsync();

            // 模块声明在结算后收集（含未启用模块）。诊断非空说明有贡献被拒收——
            // 表现为「某个页面/卡片莫名消失」，必须在启动日志里一次说清。
            if (uiRegistry.Diagnostics.Count > 0)
            {
                app.Logger.LogError(
                    "UI 贡献收集存在 {Count} 条诊断：{Details}",
                    uiRegistry.Diagnostics.Count,
                    string.Join("; ", uiRegistry.Diagnostics.Select(
                        d => $"{d.Kind}:{d.OwnerId}/{d.ContributionId}")));
            }

            // 命令发现 / TokenBudget 钩子 / Gateway 渠道注册。
            // 未选定工作区时它们会构造依赖项目路径的单例（会话存储、调度器），必须先跳过，
            // 等用户在工作区首页选定后由 WorkspaceReadyInitializer 补做（见该类注释）。
            var ready = sp.GetRequiredService<WorkspaceReadyInitializer>();
            if (sp.GetRequiredService<IWorkspaceProvider>().IsResolved)
                ready.RunIfNeeded();
            else
                sp.GetRequiredService<WorkspaceSwitcherReadiness>().Attach(ready);
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
        app.MapUploadEndpoints();
        app.MapTranscriptionEndpoints();

        // 记忆详情路由的路径以 .md 结尾（/memory/detail/daily/xxx.md）。
        // MapFallbackToPage 对「看起来像文件」的请求不生效：ASP.NET Core 的 fallback 会跳过
        // 含扩展名的路径，于是这些深链接在浏览器里直接刷新会得到 404（表现为整页空白）。
        // 先按模板精确映射记忆详情，命中则交给 Blazor 的 _Host 渲染；未命中的仍走通用 fallback。
        app.MapFallbackToPage("/memory/detail/{*FilePath}", "/_Host");
        app.MapFallbackToPage("/_Host");

        return app;
    }

    /// <summary>读取 <c>--workspace &lt;目录&gt;</c>。</summary>
    private static string? ReadWorkspaceArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
