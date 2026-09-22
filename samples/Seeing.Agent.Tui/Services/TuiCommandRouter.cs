using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Llm;
using Seeing.Agent.Tui.Rendering;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 命令路由结果：本地命令 <see cref="Output"/> 非空；转发命令 <see cref="ForwardText"/> 为原文本。
/// <para><see cref="ToggleReasoning"/> 为 true 时引擎应把 <see cref="TuiCommandRouter.ReasoningEnabled"/>
/// 的当前值应用到渲染选项（本路由不直接触碰渲染器）。</para>
/// </summary>
public sealed record TuiCommandResult(
    bool Handled,
    bool ExitRequested,
    IRenderable? Output,
    string? ForwardText,
    bool ToggleReasoning = false);

/// <summary>命令执行上下文（由引擎注入）。</summary>
public sealed record TuiCommandContext(
    TuiSessionController Sessions,
    TuiPermissionQueue Permissions,
    TuiQuestionQueue Questions,
    ITerminalSurface Surface,
    TuiViewState? State,
    IAgentRegistry Agents,
    IModelConfigManager Models,
    ILlmService Llm,
    IScenarioCatalog Scenarios);

/// <summary>
/// 斜杠命令分流器：本地命令拦截执行，其余（含未知 <c>/xxx</c>）转发为普通输入提交。
/// <para>无参的会话/Agent/模型/场景/思考档命令走 <see cref="ITerminalSurface.PromptAsync"/> 交互选择。</para>
/// </summary>
public sealed class TuiCommandRouter
{
    /// <summary>交互选择中的「清除」哨兵值（避免空字符串作为选择项值）。</summary>
    private const string ClearOption = "__seeing_clear__";

    private static readonly HashSet<string> LocalCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "new", "sessions", "resume", "rename", "delete", "fork", "open",
        "agent", "model", "thinking", "scenario", "auto-approve", "reasoning",
        "cancel", "expand", "todo", "exit",
    };

    private static readonly (string Usage, string Description)[] LocalHelp =
    [
        ("/help, /h, /?", "显示本帮助（含服务端命令）"),
        ("/new [title]", "新建会话并切换"),
        ("/sessions [id]", "无参交互选择会话；带参切换到指定会话"),
        ("/resume [id]", "无参交互选择会话；带参切换到指定会话"),
        ("/rename [title]", "重命名活跃会话"),
        ("/delete", "删除活跃会话"),
        ("/fork", "分支活跃会话并切换"),
        ("/open <id>", "打开指定会话/子会话"),
        ("/agent [name]", "无参交互选择 Agent；带参直接切换"),
        ("/model [id]", "无参交互选择模型；带参直接切换"),
        ("/thinking [level]", "无参交互选择思考档；带参直接设置（clear 清除）"),
        ("/scenario [name]", "无参交互选择场景；带参直接设置（clear 清除）"),
        ("/auto-approve [follow|on|off]", "会话级审批模式三态（无参数＝循环切换）"),
        ("/reasoning [on|off]", "推理显示开关（无参切换）"),
        ("/cancel [all]", "取消当前/级联执行"),
        ("/expand <callId>", "展开工具完整输出"),
        ("/todo", "查看 Todo 面板"),
        ("/exit, /quit, /q", "退出"),
    ];

    private readonly ICommandRegistry? _commandRegistry;

    public TuiCommandRouter(ICommandRegistry? commandRegistry = null)
    {
        _commandRegistry = commandRegistry;
    }

    /// <summary>推理显示开关（本地 UI 偏好，权威值）。</summary>
    public bool ReasoningEnabled { get; private set; }

    /// <summary>是否为本模块拦截的本地命令。</summary>
    public bool IsLocal(string input)
        => TryParse(input, out var canonical, out _) && LocalCommands.Contains(canonical);

    public async Task<TuiCommandResult> ExecuteAsync(string input, TuiCommandContext ctx, CancellationToken ct = default)
    {
        if (!TryParse(input, out var canonical, out var args))
            return new TuiCommandResult(false, false, null, input);

        switch (canonical)
        {
            case "help":
                return Local(BuildHelp());

            case "exit":
                return new TuiCommandResult(true, true, null, null);

            case "new":
            {
                var state = await ctx.Sessions.NewAsync(NullIfEmpty(args), ct);
                return Local(new Text($"已新建并切换到会话 {state.SessionId}"));
            }

            case "sessions":
            {
                var sessionId = NullIfEmpty(args);
                if (sessionId is not null)
                    return Local(BuildSessionTable(await ctx.Sessions.ListAsync(ct)));

                var sessions = await ctx.Sessions.ListAsync(ct);
                if (sessions.Count == 0)
                    return Local(new Text("(无会话)"));

                var options = sessions
                    .OrderByDescending(s => s.UpdatedAt)
                    .Select(s => (Display: $"{s.Title ?? "(无标题)"} · {s.UpdatedAt:yyyy-MM-dd HH:mm} · {s.Id}", Value: s.Id))
                    .ToList();

                var chosen = await PromptSelectionAsync(ctx, "选择会话", options, ct);
                if (chosen is null)
                    return Local(new Text("(未选择会话)"));

                var switched = await ctx.Sessions.SwitchAsync(chosen, ct);
                return Local(new Text(switched ? $"已切换到会话 {chosen}" : $"会话不存在：{chosen}"));
            }

            case "resume":
            {
                var sessionId = NullIfEmpty(args);
                if (sessionId is null)
                {
                    var sessions = await ctx.Sessions.ListAsync(ct);
                    if (sessions.Count == 0)
                        return Local(new Text("(无会话)"));

                    var options = sessions
                        .OrderByDescending(s => s.UpdatedAt)
                        .Select(s => (Display: $"{s.Title ?? "(无标题)"} · {s.UpdatedAt:yyyy-MM-dd HH:mm} · {s.Id}", Value: s.Id))
                        .ToList();

                    sessionId = await PromptSelectionAsync(ctx, "选择会话", options, ct);
                    if (sessionId is null)
                        return Local(new Text("(未选择会话)"));
                }

                var switched = await ctx.Sessions.SwitchAsync(sessionId, ct);
                return Local(new Text(switched ? $"已切换到会话 {sessionId}" : $"会话不存在：{sessionId}"));
            }

            case "open":
            {
                var id = NullIfEmpty(args);
                if (id is null)
                    return Local(new Text("用法：/open <sessionId>"));

                var switched = await ctx.Sessions.SwitchAsync(id, ct);
                return Local(new Text(switched ? $"已切换到会话 {id}" : $"会话不存在：{id}"));
            }

            case "rename":
            {
                var title = NullIfEmpty(args);
                if (title is null)
                    return Local(new Text("用法：/rename <title>"));
                await ctx.Sessions.RenameAsync(title, ct);
                return Local(new Text($"已重命名为 {title}"));
            }

            case "delete":
                await ctx.Sessions.DeleteAsync(ct);
                return Local(new Text("已删除活跃会话"));

            case "fork":
            {
                var state = await ctx.Sessions.ForkAsync(ct);
                return Local(new Text($"已分支并切换到会话 {state.SessionId}"));
            }

            case "agent":
            {
                var agent = NullIfEmpty(args);
                if (agent is null)
                {
                    var agents = await ctx.Agents.GetPrimaryAgentsAsync();
                    if (agents.Count == 0)
                        return Local(new Text("(无可用 Agent)"));

                    var options = agents
                        .Select(a => (Display: string.IsNullOrWhiteSpace(a.Description) ? a.Name : $"{a.Name} · {a.Description}", Value: a.Name))
                        .ToList();

                    agent = await PromptSelectionAsync(ctx, "选择 Agent", options, ct);
                    if (agent is null)
                        return Local(new Text("(未选择 Agent)"));
                }

                await ctx.Sessions.SetAgentAsync(agent, ct);
                return Local(new Text($"已切换 Agent：{agent}"));
            }

            case "model":
            {
                var model = NullIfEmpty(args);
                if (model is null)
                {
                    var models = ctx.Models.GetModelsByType(ModelType.Text);
                    if (models.Count == 0)
                        return Local(new Text("(无可用模型)"));

                    var options = models
                        .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(m =>
                        {
                            var display = string.IsNullOrWhiteSpace(m.Value.Name) ? m.Key : m.Value.Name!;
                            return (Display: $"{display} · {m.Key}", Value: m.Key);
                        })
                        .ToList();

                    model = await PromptSelectionAsync(ctx, "选择模型", options, ct);
                    if (model is null)
                        return Local(new Text("(未选择模型)"));
                }

                await ctx.Sessions.SetModelAsync(model, ct);
                return Local(new Text($"已切换模型：{model}"));
            }

            case "thinking":
            {
                var level = NullIfEmpty(args);
                if (level is not null)
                {
                    if (string.Equals(level, "clear", StringComparison.OrdinalIgnoreCase))
                    {
                        await ctx.Sessions.SetThinkingAsync(null, ct);
                        return Local(new Text("思考档已清除（跟随模型默认）"));
                    }

                    await ctx.Sessions.SetThinkingAsync(level, ct);
                    return Local(new Text($"思考档：{level}"));
                }

                var thinking = ctx.Llm?.GetModelConfig(ctx.State?.ModelId ?? string.Empty)?.Options?.Thinking;
                if (!ThinkingEffortKeys.IsSupported(thinking) || thinking!.Levels is null)
                    return Local(new Text("当前模型未提供思考档，用法：/thinking <level>"));

                var options = new List<(string Display, string Value)> { ("（清除，跟随默认）", ClearOption) };
                options.AddRange(thinking.Levels!.Select(l =>
                    (Display: string.IsNullOrWhiteSpace(l.Label) ? l.Key : $"{l.Label} · {l.Key}", Value: l.Key)));

                var chosen = await PromptSelectionAsync(ctx, "选择思考档", options, ct);
                if (chosen is null)
                    return Local(new Text("(未选择思考档)"));

                var selectedLevel = string.Equals(chosen, ClearOption, StringComparison.Ordinal) ? null : chosen;
                await ctx.Sessions.SetThinkingAsync(selectedLevel, ct);
                return Local(new Text(selectedLevel is null ? "思考档已清除（跟随模型默认）" : $"思考档：{selectedLevel}"));
            }

            case "scenario":
            {
                var scenario = NullIfEmpty(args);
                if (scenario is not null)
                {
                    if (string.Equals(scenario, "clear", StringComparison.OrdinalIgnoreCase))
                    {
                        await ctx.Sessions.SetScenarioAsync(null, ct);
                        return Local(new Text("场景已清除（跟随进程默认）"));
                    }

                    await ctx.Sessions.SetScenarioAsync(scenario, ct);
                    return Local(new Text($"场景：{scenario}"));
                }

                var scenarios = ctx.Scenarios.ListAll();
                if (scenarios.Count == 0)
                    return Local(new Text("(无可用场景)"));

                var options = new List<(string Display, string Value)> { ("（清除，跟随默认）", ClearOption) };
                options.AddRange(scenarios.Select(s => (Display: s.Name, Value: s.Name)));

                var chosen = await PromptSelectionAsync(ctx, "选择场景", options, ct);
                if (chosen is null)
                    return Local(new Text("(未选择场景)"));

                var selectedScenario = string.Equals(chosen, ClearOption, StringComparison.Ordinal) ? null : chosen;
                await ctx.Sessions.SetScenarioAsync(selectedScenario, ct);
                return Local(new Text(selectedScenario is null ? "场景已清除（跟随进程默认）" : $"场景：{selectedScenario}"));
            }

            case "auto-approve":
            {
                var globalAuto = ctx.State?.GlobalAutoApprove ?? false;

                SessionAutoApprove mode;
                if (string.IsNullOrWhiteSpace(args))
                {
                    // 无参数：三态循环（对齐 WebUI 分段控件的点击切换）。
                    var current = ctx.State?.AutoApprove
                        ?? ctx.Sessions.Current?.AutoApprove
                        ?? SessionAutoApprove.FollowGlobal;
                    mode = AutoApproveText.Next(current);
                }
                else if (!TryParseAutoApprove(args, out mode))
                {
                    return Local(new Text("用法：/auto-approve [follow|on|off]（无参数＝在三态间循环）"));
                }

                await ctx.Sessions.SetAutoApproveAsync(mode, ct);
                return Local(new Text($"审批模式：{AutoApproveText.Label(mode, globalAuto)}（三态：{AutoApproveText.Mode(mode)}）"));
            }

            case "reasoning":
            {
                if (string.IsNullOrWhiteSpace(args))
                {
                    ReasoningEnabled = !ReasoningEnabled;
                }
                else if (TryParseBool(args, out var enabled))
                {
                    ReasoningEnabled = enabled;
                }
                else
                {
                    return Local(new Text("用法：/reasoning [on|off]"));
                }

                // ToggleReasoning=true：引擎据 ReasoningEnabled 应用渲染选项。
                return new TuiCommandResult(
                    true,
                    false,
                    new Text($"推理显示：{(ReasoningEnabled ? "开" : "关")}"),
                    null,
                    ToggleReasoning: true);
            }

            case "cancel":
            {
                if (string.Equals(args.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                {
                    var count = await ctx.Sessions.CancelAllAsync(ct);
                    return Local(new Text($"已级联取消 {count} 个执行"));
                }

                await ctx.Sessions.CancelAsync(ct);
                return Local(new Text("已请求取消当前执行"));
            }

            case "expand":
            {
                var callId = NullIfEmpty(args);
                if (callId is null)
                    return Local(new Text("用法：/expand <callId>"));

                var block = FindToolBlock(ctx.State, callId);
                if (block?.Tool is null)
                    return Local(new Text($"未找到工具调用：{callId}"));

                block.Tool.IsExpanded = true;
                return Local(BuildExpandedTool(block.Tool));
            }

            case "todo":
            {
                var todos = ctx.State?.Todos ?? [];
                if (todos.Count == 0)
                    return Local(new Text("(无待办)"));

                var table = new Table().AddColumn("状态").AddColumn("内容");
                foreach (var todo in todos)
                    table.AddRow(new Text(todo.Status), new Text(todo.Content));
                return Local(table);
            }

            default:
                return Forward(input);
        }
    }

    private IRenderable BuildHelp()
    {
        var table = new Table().AddColumn("命令").AddColumn("说明");
        foreach (var (usage, description) in LocalHelp)
            table.AddRow(new Text(usage), new Text(description));

        if (_commandRegistry is not null)
        {
            var metadata = _commandRegistry.GetAllMetadata()
                .Where(m => !m.IsHidden)
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Name);

            foreach (var command in metadata)
            {
                var name = command.Aliases.Length > 0
                    ? $"/{command.Name} ({string.Join(", ", command.Aliases.Select(a => "/" + a))})"
                    : $"/{command.Name}";
                table.AddRow(new Text(name), new Text(command.Description));
            }
        }

        return table;
    }

    private static IRenderable BuildSessionTable(IReadOnlyList<SessionData> sessions)
    {
        if (sessions.Count == 0)
            return new Text("(无会话)");

        var table = new Table().AddColumn("会话 Id").AddColumn("标题").AddColumn("Agent").AddColumn("更新时间");
        foreach (var session in sessions.OrderByDescending(s => s.UpdatedAt))
            table.AddRow(
                new Text(session.Id),
                new Text(session.Title),
                new Text(session.SelectedAgent),
                new Text(session.UpdatedAt.ToString("yyyy-MM-dd HH:mm")));

        return table;
    }

    /// <summary>展开工具完整输出（标题 + 参数/错误/输出面板）。</summary>
    private static IRenderable BuildExpandedTool(TuiToolState tool)
    {
        var rows = new List<IRenderable>
        {
            new Text($"{tool.Name} ({tool.CallId})"),
        };

        if (!string.IsNullOrWhiteSpace(tool.Title))
            rows.Add(new Text(tool.Title!));

        if (!string.IsNullOrWhiteSpace(tool.Arguments))
        {
            rows.Add(new Text("参数："));
            rows.Add(new Panel(new Text(tool.Arguments!)).Header("Arguments").Border(BoxBorder.Rounded));
        }

        if (!string.IsNullOrWhiteSpace(tool.Error))
        {
            rows.Add(new Text("错误："));
            rows.Add(new Panel(new Text(tool.Error!)).Header("Error").Border(BoxBorder.Rounded));
        }

        if (!string.IsNullOrWhiteSpace(tool.Output))
        {
            rows.Add(new Text("输出："));
            rows.Add(new Panel(new Text(tool.Output!)).Header("Output").Border(BoxBorder.Rounded));
        }

        if (rows.Count == 1)
            rows.Add(new Text("(无输出)"));

        return new Rows(rows);
    }

    private static TuiBlock? FindToolBlock(TuiViewState? state, string callId)
    {
        if (state is null)
            return null;

        var direct = state.Find($"tool:{callId}");
        if (direct?.Tool is not null)
            return direct;

        return state.Tools.FirstOrDefault(
            b => b.Tool is not null && string.Equals(b.Tool.CallId, callId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>选择器 Esc 取消的哨兵值（不会与真实选项值冲突）。</summary>
    internal const string CancelSentinel = "\u0000__tui_selection_cancel__";

    /// <summary>
    /// 经渲染线程发起交互选择；空选项、无终端或取消（Esc / Ctrl+C）均返回 null。
    /// <para>选项以「值」为选择项、以 <see cref="UseConverter{T}"/> 呈现实文本，避免含
    /// <c>[ ]</c> 的标题触发 markup 解析异常。</para>
    /// </summary>
    private static async Task<string?> PromptSelectionAsync(
        TuiCommandContext ctx,
        string title,
        IReadOnlyList<(string Display, string Value)> options,
        CancellationToken ct)
    {
        if (ctx.Surface is null || options.Count == 0)
            return null;

        var values = options.Select(o => o.Value).ToList();
        var displays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (display, value) in options)
            displays[value] = Markup.Escape(display);

        try
        {
            var chosen = await ctx.Surface.PromptAsync(
                (console, promptCt) => console.PromptAsync(
                    new SelectionPrompt<string>()
                        .Title(Markup.Escape(title))
                        .PageSize(15)
                        .AddChoices(values)
                        .UseConverter(value => displays.TryGetValue(value, out var display) ? display : Markup.Escape(value))
                        // Esc 取消：返回哨兵值，调用方据此放弃选择（否则选择器无法退出）。
                        .AddCancelResult(CancelSentinel),
                    promptCt),
                ct);

            return string.Equals(chosen, CancelSentinel, StringComparison.Ordinal) ? null : chosen;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static TuiCommandResult Local(IRenderable? output) => new(true, false, output, null);

    private static TuiCommandResult Forward(string text) => new(true, false, null, text);

    private static bool TryParseBool(string args, out bool value)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1":
                value = true;
                return true;
            case "off" or "false" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseAutoApprove(string args, out SessionAutoApprove mode)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "follow" or "followglobal" or "default":
                mode = SessionAutoApprove.FollowGlobal;
                return true;
            case "on" or "enabled" or "auto":
                mode = SessionAutoApprove.Enabled;
                return true;
            case "off" or "disabled" or "confirm":
                mode = SessionAutoApprove.Disabled;
                return true;
            default:
                mode = SessionAutoApprove.FollowGlobal;
                return false;
        }
    }

    private static bool TryParse(string input, out string canonical, out string args)
    {
        canonical = string.Empty;
        args = string.Empty;

        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/'))
            return false;

        var body = trimmed[1..];
        var space = body.IndexOf(' ');
        var name = (space < 0 ? body : body[..space]).ToLowerInvariant();
        if (name.Length == 0)
            return false;

        canonical = name switch
        {
            "h" or "?" => "help",
            "q" or "quit" => "exit",
            _ => name,
        };
        args = space < 0 ? string.Empty : body[(space + 1)..].Trim();
        return true;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
