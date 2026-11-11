using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Tools.Browser.Tools;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 浏览器能力模块（模块 id <c>browser</c>）：12 个 <c>browser_*</c> 工具 + 「浏览器」工作台声明。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么拆成 12 个工具而不是一个带 action 的工具</b>：权限判定的键是
/// <c>(PermissionKind.Tool, toolName)</c>，<b>不含参数</b>。合成一个工具后无法单独放行/禁止
/// <c>browser_eval</c>（等价于任意脚本执行）。拆开才有粒度。
/// </para>
/// <para>
/// <b>为什么工作台声明也在这里</b>：谁拥有能力谁声明（与「工具在 Activate 挂载」同一条纪律）。
/// 工作台视图组件在宿主 <c>WebUiWorkbenchViews</c> 里按 <c>ViewKey</c> 登记，能力包不认识 Blazor。
/// </para>
/// </remarks>
public sealed class BrowserModule : ISeeingModule, IUiContributor
{
    /// <summary>
    /// 模块 id，同时也是「浏览器」工作台的 id。
    /// </summary>
    /// <remarks>
    /// 提成常量是因为它有三个消费点：模块声明、工作台 id（<c>browser_open</c> 用它请求打开面板）、
    /// 以及宿主端点的模块门控。三处字符串一旦漂移，症状是「模块启用了但工作台打不开」这种静默失败。
    /// </remarks>
    public const string IdValue = "browser";

    /// <summary>
    /// 静态提供的 12 个工具。
    /// </summary>
    /// <remarks>
    /// <b>为什么全在这里、没有「可选工具」</b>：<c>ProvidedTools</c> 是<b>结算输入</b>（`settledToolIds`），
    /// 必须与 <c>ActivateAsync</c> 实际挂载的集合一致。曾有一个 <c>Browser.EnableEval</c> 配置项
    /// 让 <c>browser_eval</c> 可能不挂载——那会破坏「ProvidedTools ⇔ 实际注册」的不变量
    /// （模块声称 12 个、实际 11 个，且没有任何地方报错）。
    /// 要禁止任意 JS 执行，用权限系统（`(PermissionKind.Tool, "browser_eval")` 设为 deny），
    /// 或者直接停用整个 `browser` 模块——不需要新增一种「配置决定工具是否存在」的机制。
    /// </remarks>
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "browser_open",
        "browser_snapshot",
        "browser_click",
        "browser_type",
        "browser_press",
        "browser_scroll",
        "browser_wait_for",
        "browser_screenshot",
        "browser_eval",
        "browser_back",
        "browser_forward",
        "browser_close"
    ];

    /// <inheritdoc />
    public string Id => IdValue;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    /// <remarks>
    /// <b>空依赖</b>：本模块探测浏览器可执行文件用的是静态 <c>File.Exists</c>（在进程启动前就确定的本机事实），
    /// 不读 <c>IFileSystem</c>，也不经执行世界；因此<b>不</b>声明 <c>io.local</c> 依赖。
    /// 曾误声明过 <c>["io.local"]</c>（注释写「需要 IFileSystem 探测」），但全包并无该引用——
    /// 假依赖会让最小宿主里的激活顺序与失败模式失真。
    /// </remarks>
    public IReadOnlyList<string> DependsOn { get; } = [];

    /// <summary>
    /// <see cref="ConfigureServices"/> 是否成功登记了配置节。
    /// </summary>
    /// <remarks>
    /// <b>为什么要暴露这个状态</b>：登记要求服务集合里已存在 <see cref="IConfigSectionRegistry"/> 实例。
    /// 若宿主先注册模块、后加 registry，节就登记不上，而 <see cref="BrowserOptions"/> 的
    /// options monitor 会永远返回默认值——「配了 <c>Headless=false</c> 却始终无头」这类
    /// 静默失效极难排查。宿主与测试可据此断言注册顺序正确。
    /// </remarks>
    public bool ConfigSectionRegistered { get; private set; }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 登记失败（服务集合里还没有 IConfigSectionRegistry）会让本模块所有配置项静默失效，
        // 因此把结果暴露在实例上供宿主/测试断言：注册顺序错了能被发现，
        // 而不是表现为「配了 Headless=false 但永远无头」。
        ConfigSectionRegistered = TryRegisterConfigSection(services);

        // 配置桥接三件套：把 seeing.json 的 Browser 节真正接到 IOptionsMonitor。
        // 只有 AddOptions<>() 是不够的——那只登记 options 基础设施，没有任何 configure 委托，
        // CurrentValue 永远返回 new BrowserOptions() 的默认值（Headless / ExecutablePath /
        // AllowPrivateHosts 全部读不到，等于配置项是文档谎言）。
        services.AddOptions<BrowserOptions>();
        services.TryAddSingleton(sp =>
            new ConfigSectionOptionsMonitor<BrowserOptions>(
                sp.GetRequiredService<IConfigSectionStore>(),
                BrowserOptions.SectionName));
        services.TryAddSingleton<IOptionsMonitor<BrowserOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<BrowserOptions>>());
        services.TryAddSingleton<IOptions<BrowserOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<BrowserOptions>>());

        services.TryAddSingleton<BrowserEngine>();
        services.TryAddSingleton<BrowserSessionHost>();
        services.TryAddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());
        services.TryAddSingleton<BrowserScreencastHub>();

        // 页面创建 → 画面流建立的通知总线（agent 用工具开页时端点不会自己知道）。
        // 与 FileWatcherHostedService 同形：IHostedService + IModuleHostedService，
        // 随模块启停订阅/退订 PageCreated 事件。
        services.AddModuleHostedService<BrowserPageNotifier>();

        // 进程级接线：会话页面关闭 → 释放对应画面流。
        // 与 FileWatcherHostedService 同形：同时满足 IHostedService（Generic Host）
        // 与 IModuleHostedService（模块生命周期管理器），三个服务位都登记。
        services.AddModuleHostedService<BrowserLifetimeService>();

        // 工具
        services.TryAddSingleton<BrowserOpenTool>();
        services.TryAddSingleton<BrowserSnapshotTool>();
        services.TryAddSingleton<BrowserClickTool>();
        services.TryAddSingleton<BrowserTypeTool>();
        services.TryAddSingleton<BrowserPressTool>();
        services.TryAddSingleton<BrowserScrollTool>();
        services.TryAddSingleton<BrowserWaitForTool>();
        services.TryAddSingleton<BrowserScreenshotTool>();
        services.TryAddSingleton<BrowserEvalTool>();
        services.TryAddSingleton<BrowserBackTool>();
        services.TryAddSingleton<BrowserForwardTool>();
        services.TryAddSingleton<BrowserCloseTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 接线由 Generic Host / 模块生命周期管理器经 IModuleHostedService 驱动，
        // 这里显式 Start 一次是为了覆盖「Activate 早于 HostedService 启动」的顺序
        // （StartAsync 幂等，不会重复订阅）。
        if (services.GetService<BrowserLifetimeService>() is { } lifetime)
            await lifetime.StartAsync(cancellationToken).ConfigureAwait(false);

        var tools = services.GetService<IToolManager>();
        if (tools is null)
            return;

        await RegisterAsync(tools, services.GetService<BrowserOpenTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserSnapshotTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserClickTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserTypeTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserPressTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserScrollTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserWaitForTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserScreenshotTool>(), cancellationToken).ConfigureAwait(false);

        // 12 个工具全部挂载：ProvidedTools 与实际注册必须一致（见 s_providedTools 注释）。
        // 禁止 browser_eval 用权限系统（PermissionKind.Tool），不用「配置决定工具是否存在」。
        await RegisterAsync(tools, services.GetService<BrowserEvalTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserBackTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserForwardTool>(), cancellationToken).ConfigureAwait(false);
        await RegisterAsync(tools, services.GetService<BrowserCloseTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tools = services.GetService<IToolManager>();
        if (tools is not null)
        {
            foreach (var id in s_providedTools)
                await tools.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        // 停用必须释放资源：否则「停用浏览器模块」只停了工具，Chromium 还在跑。
        // 但**不能 Dispose 这两个 DI 单例**：模块可能被重新启用（热重载），
        // 而单例一旦 Dispose，内部的信号量就永久失效，下次调用抛 ObjectDisposedException
        // （症状：停用再启用后浏览器永远起不来）。因此走「关闭资源但保持实例可用」的关停方法。
        if (services.GetService<BrowserLifetimeService>() is { } lifetime)
            await lifetime.StopAsync(cancellationToken).ConfigureAwait(false);

        if (services.GetService<BrowserScreencastHub>() is { } hub)
            await hub.ShutdownAsync(cancellationToken).ConfigureAwait(false);

        if (services.GetService<BrowserSessionHost>() is { } host)
            await host.ShutdownAsync(cancellationToken).ConfigureAwait(false);

        if (services.GetService<BrowserEngine>() is { } engine)
            await engine.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 声明「浏览器」工作台。
    /// </summary>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new WorkbenchContribution
        {
            Point = WorkbenchPoints.DockId,
            Id = "browser",
            Title = "浏览器",
            ViewKey = WorkbenchPoints.ViewKeyFor("browser"),
            Icon = "globe",
            Requires = [Id],
            Order = 20,
            DefaultDock = WorkbenchDock.Right,
            // 比文件工作台宽一点：网页基本都要横向空间
            Sizing = new WorkbenchSizing(DefaultPercent: 55, MinPx: 420),
            OpenPayloadSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    url = new
                    {
                        type = "string",
                        description = "初始化打开的地址（可选；不给则显示空态，等 agent 调 browser_open）"
                    }
                }
            }),
            ProvidedTools = s_providedTools,
            // 「用户正在浏览哪个网页」对 agent 有用，且摘要由 UI 截断到 200 字
            ContributesContext = true
        }
    ];

    private static async Task RegisterAsync(IToolManager tools, ITool? tool, CancellationToken cancellationToken)
    {
        if (tool is not null)
            await tools.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 登记配置节 <c>Browser</c>（节名 + 文件名 + 作用域 + 选项类型）。
    /// <para>与 Mcp/Skills 同样走「从服务集合里找已注册的 registry 实例」的做法，避免改宿主签名。</para>
    /// </summary>
    /// <remarks>
    /// <b>作用域必须是 UserOnly，不能是 Both</b>：<c>ExecutablePath</c> 决定「启动哪个可执行文件」，
    /// <c>AllowPrivateHosts</c> 决定能否访问内网与云元数据地址。项目级配置随仓库分发、内容由第三方控制，
    /// 若允许项目级覆盖这两个键，<c>git clone</c> + 一次 <c>browser_open</c> 就是任意程序执行 / SSRF。
    /// 机器与进程级事实（浏览器装在哪、是否开窗、视口多大）本就是用户级信息。
    /// 对照：<c>Providers</c> / <c>ModelOverrides</c> 也是 UserOnly。
    /// </remarks>
    /// <returns>
    /// 是否成功登记。<see langword="false"/> 表示服务集合里还没有 <see cref="IConfigSectionRegistry"/>——
    /// 此时本模块的所有配置项都会**静默失效**（options monitor 永远返回默认值）。
    /// 返回它而不是静默结束，是为了让调用方能断言/记录这件事：
    /// 「模块装上了但配置读不到」是最难察觉的一类缺陷。
    /// </returns>
    private static bool TryRegisterConfigSection(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry registry)
            {
                registry.Register(new ConfigSectionMeta(
                    BrowserOptions.SectionName,
                    "seeing.json",
                    ConfigScope.UserOnly,
                    typeof(BrowserOptions)));
                return true;
            }
        }

        return false;
    }
}
