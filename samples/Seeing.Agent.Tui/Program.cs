using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Decorators;
using Seeing.Agent.Extensions;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.MCP;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tui.Infrastructure;
using Seeing.Agent.Tui.Services;
using Spectre.Console;
using ChatMessage = Seeing.Agent.Llm.ChatMessage;

namespace Seeing.Agent.Tui;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            PrintHelp();
            return 0;
        }

        var pathArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        var walkUp = !args.Contains("--no-walk-up", StringComparer.Ordinal);

        string workspace;
        try
        {
            workspace = WorkspaceResolver.Resolve(pathArg, walkUp);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        Directory.SetCurrentDirectory(workspace);

        var userSeeingJson = SeeingLayout.UserSeeingJsonPath;
        try
        {
            if (SeeingUserProfileInitializer.EnsureCreated())
                AnsiConsole.MarkupLine($"[grey]已初始化用户目录:[/] [cyan]{MarkupEscape(SeeingLayout.UserSeeingDirectory)}[/] [grey]（默认 seeing.json / mcp.json 与 skills、rules 目录）[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]无法创建用户目录 ~/.seeing: {MarkupEscape(ex.Message)}[/]");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(userSeeingJson, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSeeingAgent(configuration);
        services.PostConfigure<SeeingAgentOptions>(ApplyApiKeysFromEnvironment);

        services.AddSingleton(_ => new TuiHostState { WorkspaceRoot = workspace });
        services.AddSingleton<IPermissionChannel, ConsolePermissionChannel>();
        services.AddSingleton<ChatOrchestrator>();
        // ToolInvoker 已在 AddSeeingAgent 中注册，会自动注册所有 ITool（包括内置工具）

        using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Tui");

        try
        {
            await TuiWorkspace.InitializeAsync(provider, logger);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]初始化失败: {MarkupEscape(ex.Message)}[/]");
            return 1;
        }

        var host = provider.GetRequiredService<TuiHostState>();
        var orchestrator = provider.GetRequiredService<ChatOrchestrator>();
        var skillManager = provider.GetRequiredService<SkillManager>();
        var mcp = provider.GetRequiredService<McpClientManager>();
        var options = provider.GetRequiredService<IOptions<SeeingAgentOptions>>();

        AnsiConsole.Write(new FigletText("Seeing TUI").Color(Color.Cyan1));
        AnsiConsole.MarkupLine($"[grey]工作区:[/] [green]{MarkupEscape(host.WorkspaceRoot)}[/]");
        AnsiConsole.MarkupLine($"[grey]用户配置:[/] [grey]{MarkupEscape(userSeeingJson)}[/]");
        AnsiConsole.MarkupLine($"[grey]Agent:[/] [yellow]{MarkupEscape(host.CurrentAgentKey)}[/]  ·  [grey]输入 /help 查看命令，空行退出[/]");
        AnsiConsole.WriteLine();

        var history = new List<ChatMessage>();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            var line = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold cyan]〉[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(line))
                break;

            var input = line.Trim();
            if (input.StartsWith("/", StringComparison.Ordinal))
            {
                var done = await HandleSlashCommandAsync(
                    input,
                    provider,
                    host,
                    skillManager,
                    mcp,
                    options,
                    history,
                    logger,
                    cts.Token);
                if (done)
                    break;
                continue;
            }

            try
            {
                // 流式输出不要用 Status：Spinner 与逐块写控制台会互相抢占光标，出现「思考中…」碎片和错乱换行。
                // 也不要对每块 MarkupLine，否则模型每个 token 一段都会变成单独一行。
                await orchestrator.RunTurnAsync(
                    history,
                    input,
                    static chunk =>
                    {
                        if (string.IsNullOrEmpty(chunk))
                            return;
                        AnsiConsole.Markup(MarkupEscape(chunk));
                    },
                    cts.Token);
                AnsiConsole.WriteLine();
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]已取消[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{MarkupEscape(ex.Message)}[/]");
            }

            AnsiConsole.WriteLine();
        }

        try
        {
            await provider.GetRequiredService<McpClientManager>().DisconnectAllAsync();
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    /// <summary>处理斜杠命令；返回 true 表示应退出主循环。</summary>
    private static async Task<bool> HandleSlashCommandAsync(
        string input,
        IServiceProvider provider,
        TuiHostState host,
        SkillManager skillManager,
        McpClientManager mcp,
        IOptions<SeeingAgentOptions> options,
        List<ChatMessage> history,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case "/exit":
            case "/quit":
                return true;

            case "/help":
                AnsiConsole.MarkupLine("""
                    [bold]/help[/] 帮助  [bold]/exit[/] 退出
                    [bold]/cd[/] [grey]路径[/] 切换工作区并重连 MCP、重扫 Skill
                    [bold]/skills[/] 列出 SKILL.md 技能  [bold]/mcp[/] 列出 MCP 工具
                    [bold]/agents[/] 列出 Agent 配置  [bold]/agent[/] [grey]名称[/] 切换当前 Agent
                    [bold]/model[/] 当前模型  [bold]/rules[/] 规则来源与长度
                    [bold]/clear[/] 清空对话历史
                    """);
                break;

            case "/clear":
                history.Clear();
                AnsiConsole.MarkupLine("[grey]对话历史已清空[/]");
                break;

            case "/cd":
                if (string.IsNullOrEmpty(arg))
                {
                    AnsiConsole.MarkupLine("[red]用法: /cd <绝对或相对路径>[/]");
                    break;
                }

                var target = Path.GetFullPath(Path.Combine(host.WorkspaceRoot, arg));
                if (!Directory.Exists(target))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]目录不存在: {MarkupEscape(target)}[/]");
                    break;
                }

                try
                {
                    await AnsiConsole.Status().StartAsync("切换工作区…", async _ =>
                        await TuiWorkspace.ChangeWorkspaceAsync(provider, target, logger, cancellationToken));
                    AnsiConsole.MarkupLineInterpolated($"[green]已切换到[/] {MarkupEscape(target)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]{MarkupEscape(ex.Message)}[/]");
                }

                break;

            case "/skills":
            {
                var table = new Table().AddColumn("名称").AddColumn("描述");
                foreach (var kv in skillManager.GetAllSkillInfos().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    table.AddRow(MarkupEscape(kv.Key), MarkupEscape(kv.Value.Description ?? ""));
                AnsiConsole.Write(table);
                break;
            }

            case "/mcp":
            {
                var table = new Table().AddColumn("工具 Id").AddColumn("说明");
                foreach (var t in mcp.GetTools().OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
                    table.AddRow(MarkupEscape(t.Id), MarkupEscape(t.Description ?? ""));
                if (mcp.GetTools().Count == 0)
                    AnsiConsole.MarkupLine("[grey]当前无已连接的 MCP 工具[/]");
                else
                    AnsiConsole.Write(table);
                break;
            }

            case "/agents":
            {
                foreach (var name in options.Value.Agents.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    var mark = string.Equals(name, host.CurrentAgentKey, StringComparison.Ordinal) ? "[green]*[/] " : "  ";
                    AnsiConsole.MarkupLine($"{mark}[bold]{MarkupEscape(name)}[/]");
                }

                break;
            }

            case "/agent":
                if (string.IsNullOrEmpty(arg))
                {
                    AnsiConsole.MarkupLine("[red]用法: /agent <名称>[/]");
                    break;
                }

                if (!options.Value.Agents.ContainsKey(arg))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]未知 Agent: {MarkupEscape(arg)}[/]");
                    break;
                }

                host.CurrentAgentKey = arg;
                AnsiConsole.MarkupLineInterpolated($"[green]已切换 Agent:[/] {MarkupEscape(arg)}");
                break;

            case "/model":
            {
                var agents = options.Value.Agents;
                if (!agents.TryGetValue(host.CurrentAgentKey, out var ac))
                    ac = agents.Values.FirstOrDefault();
                var model = ac?.Model ?? options.Value.DefaultModel ?? "(未配置)";
                AnsiConsole.MarkupLineInterpolated($"[grey]当前模型:[/] [bold]{MarkupEscape(model)}[/]");
                break;
            }

            case "/rules":
                AnsiConsole.MarkupLineInterpolated($"[grey]规则字符数:[/] {host.RulesMarkdown.Length}");
                if (host.RulesSources.Count == 0)
                    AnsiConsole.MarkupLine("[grey]无 ~/.seeing/rules 与 项目 rules / .seeing/rules / .agent/rules 下的 .md[/]");
                else
                    foreach (var s in host.RulesSources)
                        AnsiConsole.MarkupLineInterpolated($"[grey]-[/] {MarkupEscape(s)}");
                break;

            default:
                AnsiConsole.MarkupLineInterpolated($"[red]未知命令:[/] {MarkupEscape(cmd)}  （/help）");
                break;
        }

        return false;
    }

    private static void ApplyApiKeysFromEnvironment(SeeingAgentOptions o)
    {
        foreach (var p in o.Providers.Values)
        {
            if (!string.IsNullOrWhiteSpace(p.ApiKey))
                continue;
            if (p.Type == ProviderType.OpenAI)
                p.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            else if (p.Type == ProviderType.Anthropic)
                p.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            seeing-tui — Seeing.Agent 控制台交互

            用法:
              seeing-tui [工作区路径] [--no-walk-up]

            选项:
              --no-walk-up   不向上一级查找 .git / .seeing / .cursor
              -h, --help     显示此帮助

            配置:
              Provider/Model/Plugin：固定为 ~/.seeing/seeing.json（SeeingAgent 节）
              MCP：~/.seeing/mcp.json 与 项目 .seeing/mcp.json（项目覆盖同名服务）
              Skill：~/.seeing/skills；项目 skills/、.seeing/skills、.agent/skills（后者覆盖同名）
              Rule：~/.seeing/rules；项目 rules/、.seeing/rules、.agent/rules（后者覆盖同名）
              另：appsettings.json（程序目录，可选）、环境变量 OPENAI_API_KEY 等。
            """);
    }

    private static string MarkupEscape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
