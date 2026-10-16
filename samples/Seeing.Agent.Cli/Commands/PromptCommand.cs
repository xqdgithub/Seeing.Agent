using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Cli.Services;
using Seeing.Session.Core;
using System.CommandLine;

namespace Seeing.Agent.Cli.Commands;

/// <summary>一次性提示词执行（`seeing-cli -p "..." [--yolo] [--stream]`）。</summary>
public static class PromptCommand
{
    public static Option<string> CreatePromptOption()
        => new("--prompt", "-p")
        {
            Description = "一次性执行提示词并返回结果；`-` 表示从 stdin 读取。指定即触发一次性模式"
        };

    public static Option<bool> CreateYoloOption()
        => new("--yolo")
        {
            Description = "本次会话自动批准全部审批（危险：允许所有工具/文件/命令操作）"
        };

    public static Option<bool> CreateStreamOption()
        => new("--stream")
        {
            Description = "以 JSONL 事件流写 stdout（每行一个事件对象）"
        };

    public static Option<string?> CreateAgentOption()
        => new("--agent") { Description = "覆盖默认 Agent" };

    public static Option<string?> CreateModelOption()
        => new("--model") { Description = "覆盖默认模型" };

    public static Option<int?> CreateTimeoutOption()
        => new("--timeout") { Description = "整体超时（秒）" };

    public static Option<string?> CreateLogLevelOption()
        => new("--log-level") { Description = "日志级别（Debug/Information/Warning/Error），仅写 stderr" };

    /// <summary>把一次性选项注册到根命令，并绑定根 action。</summary>
    public static void AttachTo(RootCommand root)
    {
        var promptOption = CreatePromptOption();
        var yoloOption = CreateYoloOption();
        var streamOption = CreateStreamOption();
        var agentOption = CreateAgentOption();
        var modelOption = CreateModelOption();
        var timeoutOption = CreateTimeoutOption();
        var logLevelOption = CreateLogLevelOption();

        root.Options.Add(promptOption);
        root.Options.Add(yoloOption);
        root.Options.Add(streamOption);
        root.Options.Add(agentOption);
        root.Options.Add(modelOption);
        root.Options.Add(timeoutOption);
        root.Options.Add(logLevelOption);

        root.SetAction(async parseResult =>
        {
            var prompt = parseResult.GetValue(promptOption);
            var yolo = parseResult.GetValue(yoloOption);
            var stream = parseResult.GetValue(streamOption);

            if (prompt is null)
            {
                if (yolo || stream)
                {
                    Console.Error.WriteLine("错误: --yolo/--stream 需与 -p 同用");
                    Environment.ExitCode = 2;
                }
                return;
            }

            if (prompt.Length == 0)
            {
                Console.Error.WriteLine("错误: 提示词为空");
                Environment.ExitCode = 2;
                return;
            }

            var timeout = parseResult.GetValue(timeoutOption);
            if (timeout is <= 0)
            {
                Console.Error.WriteLine("错误: --timeout 必须为正整数");
                Environment.ExitCode = 2;
                return;
            }

            if (prompt == "-")
            {
                var stdin = await StdinReader.ReadAllAsync(Console.In);
                if (stdin is null)
                {
                    Console.Error.WriteLine("错误: 提示词为空");
                    Environment.ExitCode = 2;
                    return;
                }

                prompt = stdin;
            }

            var options = new OneShotOptions(
                Prompt: prompt,
                Yolo: yolo,
                Stream: stream,
                Agent: parseResult.GetValue(agentOption),
                Model: parseResult.GetValue(modelOption),
                TimeoutSeconds: timeout);

            Environment.ExitCode = await RunWithCancelAsync(options, parseResult.GetValue(logLevelOption));
        });
    }

    internal static async Task<int> ExecuteAsync(OneShotOptions options, string? logLevel, CancellationToken ct = default)
    {
        using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>(), logToStderr: true, logLevel);

        var runner = new OneShotRunner(
            host.Services.GetRequiredService<ISessionRuntime>(),
            host.Services.GetRequiredService<ISessionDirectory>(),
            host.Services.GetRequiredService<ISessionManager>(),
            new OneShotOutput(Console.Out, Console.Error, options.Stream));

        return await runner.RunAsync(options, ct);
    }

    /// <summary>绑定 Ctrl+C：取消在途执行并返回退出码 130（由 action 调用）。</summary>
    internal static async Task<int> RunWithCancelAsync(OneShotOptions options, string? logLevel)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await ExecuteAsync(options, logLevel, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
