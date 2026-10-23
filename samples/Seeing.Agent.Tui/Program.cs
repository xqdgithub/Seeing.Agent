using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.HostComposition;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Tui;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Agent.Tui;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Logging;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;

// 任何 Console/AnsiConsole 访问之前统一 UTF-8：系统默认 GBK(936) 会让中文输入乱码。
TuiConsoleEncoding.EnsureUtf8();

var options = TuiCliOptions.Parse(args);

if (options.Workspace is not null)
    Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", options.Workspace);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new TuiFileLoggerProvider(ResolveLogDirectory(), ResolveLogLevel(options.LogLevel)));

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

// ── Full 能力集：与 WebUI / CLI / Gateway.Server 共用同一注册点 ──
// Tui 不监听 Gateway 端口，但仍登记 gateway/comms 模块以保持 Full 一致。
builder.Services.AddSeeingFullCapabilities(registry, builder.Configuration, includeGatewayServer: false);

builder.Services.AddSeeingCore(registry);

builder.Services.AddSeeingHostingTui(o =>
{
    o.MaxConcurrentExecutions = -1;
    o.EventBufferSize = 100;
    o.ExecutionHistoryLimit = 100;
    o.SessionIdleTimeout = TimeSpan.FromMinutes(30);
    o.CleanupInterval = TimeSpan.FromMinutes(5);
});

AddTuiServices(builder.Services);

builder.Services.AddSingleton<Seeing.Agent.TokenBudget.IBudgetStatusNotifier, TuiBudgetStatusNotifier>();
builder.Services.AddTokenBudgetHooks();

BootOverrideSource.ApplyToServices(builder.Services, args);

using var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    await sp.InitializeSeeingAsync();
    sp.InitializeCommands();
    sp.InitializeAcpCommands();
    sp.UseTokenBudgetHooks();
}

await host.StartAsync();
try
{
    var engine = host.Services.GetRequiredService<TuiChatEngine>();
    return await engine.RunAsync(options);
}
finally
{
    // ApplicationStopping 回调仅取消引擎令牌、不同步等待本流程，故此处 StopAsync 不会在停止回调中重入死锁；
    // 引擎收到取消后主循环退出、RunAsync 返回，StopAsync 得以正常完成。
    await host.StopAsync();
    TuiConsoleEncoding.Restore();
}

static void AddTuiServices(IServiceCollection services)
{
    services.AddSingleton<TuiRenderOptions>();
    services.AddSingleton<TuiRenderer>();
    services.AddSingleton<MarkdownTerminalRenderer>();
    // DSR 底锚探针单例：引擎（BeginProbe 代次分配）与 surface（写点）/输入层（Report）共用同一实例。
    services.AddSingleton<ITuiAnchorProbe, TuiAnchorProbe>();
    services.AddSingleton<TuiPromptInputRelay>();
    services.AddSingleton<ChannelAnsiConsoleInput>(sp => sp.GetRequiredService<TuiPromptInputRelay>().Input);
    services.AddSingleton<ITerminalSurface, SpectreTerminalSurface>();
    services.AddSingleton<Func<string, ITuiEventPump>>(sp =>
    {
        var runtime = sp.GetRequiredService<ISessionRuntime>();
        return sessionId => new TuiEventPump(runtime, sessionId);
    });
    services.AddSingleton<TuiSurfaceProvider>();
    services.AddSingleton<TuiSessionController>();
    services.AddSingleton<TuiCommandRouter>();
    services.AddSingleton<TuiPermissionQueue>();
    services.AddSingleton<TuiQuestionQueue>();
    services.AddSingleton<TuiAttachmentResolver>();
    services.AddSingleton<IClipboardReader>(_ => OperatingSystem.IsWindows()
        ? new WindowsClipboardReader()
        : new UnsupportedClipboardReader());
    services.AddSingleton<TuiCompletionProvider>();
    services.AddSingleton<TuiChatEngine>();
}

static string ResolveLogDirectory()
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".seeing", "logs");
}

static LogLevel ResolveLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "trace" => LogLevel.Trace,
    "debug" => LogLevel.Debug,
    "warning" or "warn" => LogLevel.Warning,
    "error" => LogLevel.Error,
    "critical" or "fatal" => LogLevel.Critical,
    "none" or "off" => LogLevel.None,
    _ => LogLevel.Information,
};
