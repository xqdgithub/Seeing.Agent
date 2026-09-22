using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Tools.Basic;
using Seeing.Agent.Core.Tools.FileSystem;
using Seeing.Agent.Core.Tools.Git;
using Seeing.Agent.Core.Tools.Question;
using Seeing.Agent.Core.Tools.Session;
using Seeing.Agent.Core.Tools.Shell;
using Seeing.Agent.Core.Tools.Web;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Tui;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.ModelCapabilities;
using Seeing.Agent.Llm.ModelCatalog.Builtin;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Skills;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Agent.Tui;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Logging;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.IO.Local;
using Seeing.Provider.DeepSeek;
using Seeing.Provider.OpenCodeZen;

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

builder.Services.AddSeeingModule<LocalExecutionWorldModule>(registry);
builder.Services.AddSeeingModule<FileSystemModule>(registry);
builder.Services.AddSeeingModule<WebModule>(registry);
builder.Services.AddSeeingModule<ShellModule>(registry);
builder.Services.AddSeeingModule<BasicModule>(registry);
builder.Services.AddSeeingModule<GitModule>(registry);
builder.Services.AddSeeingModule<SessionToolsModule>(registry);
builder.Services.AddSeeingModule<SkillsModule>(registry);
builder.Services.AddSeeingModule<McpModule>(registry);
builder.Services.AddSeeingModule<OpenAiLlmModule>(registry);
builder.Services.AddSeeingModule<AnthropicLlmModule>(registry);
builder.Services.AddSeeingModule<ModelCapabilitiesModule>(registry);
builder.Services.AddSeeingModule<BuiltinCatalogModule>(registry);
builder.Services.AddSeeingModule<DeepSeekLlmModule>(registry);
builder.Services.AddSeeingModule<OpenCodeZenLlmModule>(registry);
builder.Services.AddSeeingModule<AgentsBuiltInModule>(registry);
builder.Services.AddSeeingModule<QuestionToolsModule>(registry);
builder.Services.AddSeeingAcp(registry);
builder.Services.AddSeeingScheduler(registry);
builder.Services.AddTokenBudgetIntegration(registry, builder.Configuration);
builder.Services.AddMemoryServices(registry);

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
    services.AddSingleton<TuiPromptInputRelay>();
    services.AddSingleton<ChannelAnsiConsoleInput>(sp => sp.GetRequiredService<TuiPromptInputRelay>().Input);
    services.AddSingleton<ITerminalSurface, SpectreTerminalSurface>();
    services.AddSingleton<Func<string, ITuiEventPump>>(sp =>
    {
        var orchestrator = sp.GetRequiredService<IChatOrchestrator>();
        return sessionId => new TuiEventPump(orchestrator, sessionId);
    });
    services.AddSingleton<TuiSurfaceProvider>();
    services.AddSingleton<TuiSessionController>();
    services.AddSingleton<TuiCommandRouter>();
    services.AddSingleton<TuiPermissionQueue>();
    services.AddSingleton<TuiQuestionQueue>();
    services.AddSingleton<TuiAttachmentResolver>();
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
