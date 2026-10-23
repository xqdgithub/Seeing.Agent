using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.HostComposition;
using Seeing.Agent.Hosting.Headless;
using Seeing.Agent.TokenBudget.Extensions;

namespace Seeing.Agent.Cli.Infrastructure;

public static class CliServiceBootstrap
{
    /// <summary>
    /// 构建 DI 容器，复用 WebUI 的核心注册链。
    /// 不使用 Blazor/AntDesign/Circuit/MessageRendering。
    /// </summary>
    public static async Task<IHost> BuildHostAsync(string[] args)
        => await BuildHostAsync(args, logToStderr: false);

    /// <summary>
    /// 构建 DI 容器。
    /// <paramref name="logToStderr"/>=true 时把所有 Console 日志写到 stderr，保证 stdout 只承载结果/JSONL
    /// （一次性提示词执行 <c>seeing-cli -p ...</c> 依赖此保证）。此时日志默认<b>完全静默</b>（None），
    /// 可用 <paramref name="logLevel"/> 显式放开。
    /// </summary>
    public static async Task<IHost> BuildHostAsync(string[] args, bool logToStderr, string? logLevel = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // 一次性模式：日志默认完全静默（stdout 只承载结果），--log-level 可显式放开。
        // 注意：appsettings.json 从「当前目录」加载，脚本在其它目录运行时不会命中 CLI 自带的默认值，
        // 故此处必须显式设置，不能依赖配置文件。
        if (logToStderr)
        {
            var minimum = Microsoft.Extensions.Logging.LogLevel.None;
            if (!string.IsNullOrWhiteSpace(logLevel) &&
                Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(logLevel, ignoreCase: true, out var parsed))
            {
                minimum = parsed;
            }

            builder.Logging.SetMinimumLevel(minimum);
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
        }

        var registry = new ConfigSectionRegistry();
        builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

        // ── Full 能力集：与 WebUI / Tui / Gateway.Server 共用同一注册点 ──
        builder.Services.AddSeeingFullCapabilities(registry, builder.Configuration, includeGatewayServer: true);
        builder.Services.AddTokenBudgetHooks();

        builder.Services.AddSeeingCore(registry);
        builder.Services.AddSeeingHostingHeadless();
        BootOverrideSource.ApplyToServices(builder.Services, args);

        var host = builder.Build();

        await host.Services.InitializeSeeingAsync();
        host.Services.UseTokenBudgetHooks();

        return host;
    }
}
