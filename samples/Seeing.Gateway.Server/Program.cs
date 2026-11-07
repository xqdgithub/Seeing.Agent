using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.HostComposition;
using Seeing.Agent.Hosting.Gateway;
using Seeing.Agent.TokenBudget.Extensions;

var builder = Host.CreateApplicationBuilder(args);

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

// ── Full 能力集：与 WebUI / Tui / CLI 共用同一注册点 ──
builder.Services.AddSeeingFullCapabilities(registry, builder.Configuration, includeGatewayServer: true);
builder.Services.AddTokenBudgetHooks();

// Host Shape 基线已含会话运行时与执行引擎（AddSeeingHostingGateway → AddHostShapeBaseline），
// 不再需要手动补 AddSessionHosting/AddExecutionEngine。
builder.Services.AddSeeingHostingGateway();

BootOverrideSource.ApplyToServices(builder.Services, args);
builder.Services.AddGatewayChannelRegistry();
builder.Services.AddChannelHostManagement();
builder.Services.AddSeeingCore(registry);

// 单独运行的服务：没有首页。工作区按 --workspace → 继承的环境变量 → 启动目录
builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
    Mode: WorkspaceLaunchMode.Service,
    StartupDirectory: Directory.GetCurrentDirectory(),
    ExecutableDirectory: null,
    ExplicitPath: ReadWorkspaceArgument(args),
    InheritedEnvironment: Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT"),
    History: WorkspaceHistorySnapshot.Empty)));

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Gateway.Server");

// 初始化（自动解析工作区）— 必须在 Host.StartAsync 之前
await host.Services.InitializeSeeingAsync();
host.Services.UseTokenBudgetHooks();

// 初始化 ChannelHost 注册表
host.Services.ReloadGatewayChannelRegistry();

logger.LogInformation("Gateway Server 就绪，按 Ctrl+C 退出。");

await host.RunAsync();

/// <summary>读取 <c>--workspace &lt;目录&gt;</c>。</summary>
static string? ReadWorkspaceArgument(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}
