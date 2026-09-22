using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 视图态 → Spectre 渲染体（纯函数）。活动区 = 当前回合块 + 输入行 + 状态栏。
/// </summary>
public sealed class TuiRenderer
{
    private const int SummaryWidth = 80;
    private const int ReasoningTailLines = 3;

    private readonly TuiRenderOptions _options;
    private readonly MarkdownTerminalRenderer _markdown = new();

    public TuiRenderer(TuiRenderOptions options)
    {
        _options = options ?? new TuiRenderOptions();
    }

    public IRenderable BuildActiveView(
        TuiViewState state,
        TuiInputEditorState input,
        int width,
        int pendingApprovals = 0,
        int backgroundExecutions = 0,
        IReadOnlyDictionary<string, int>? committedOffsets = null,
        int maxLines = 0,
        IReadOnlyList<TuiCompletionItem>? completions = null,
        string? hint = null)
    {
        var items = new List<IRenderable>();
        try
        {
            foreach (var block in state.Blocks.Where(b => !b.IsTerminal))
            {
                if (IsFullyCommitted(block, committedOffsets))
                    continue;

                items.Add(BuildBlock(block, width, committedOffsets));
            }

            // 斜杠命令候选：紧贴输入行上方，随输入实时更新（Tab 仍可直接补全/选择）。
            if (completions is { Count: > 0 })
                items.Add(BuildCompletions(completions));

            // 不再插入「运行中」占位行：执行态已由状态栏呈现，重复会出现两个运行中指示。

            items.Add(BuildInputLine(input, width));
            items.Add(StatusBarView.Render(state, width, pendingApprovals, backgroundExecutions, hint));
        }
        catch
        {
            items.Clear();
            items.Add(new Markup("[red]渲染失败[/]"));
            items.Add(StatusBarView.Render(state, width, pendingApprovals, backgroundExecutions, hint));
        }

        IRenderable rows = new Rows(items);

        // maxLines > 0：活动区高度上限交渲染端保证，裁尾保头部（输入行 + 状态栏恒在尾部）。
        return maxLines > 0 ? new TailClipRenderable(rows, maxLines) : rows;
    }

    public IRenderable BuildCommitted(TuiBlock block, int width)
    {
        try
        {
            return BuildBlock(block, width);
        }
        catch
        {
            return new Text(block.Text ?? string.Empty);
        }
    }

    public IRenderable BuildSessionList(IReadOnlyList<SessionData> sessions, int width)
        => SessionListView.Render(sessions, width);

    private IRenderable BuildBlock(TuiBlock block, int width, IReadOnlyDictionary<string, int>? committedOffsets = null)
    {
        switch (block.Kind)
        {
            case TuiBlockKind.User:
                return BuildUser(block);

            case TuiBlockKind.Assistant:
                return BuildAssistant(block, width, committedOffsets);

            case TuiBlockKind.Tool:
                return block.Tool is null ? new Text(block.Text ?? string.Empty) : BuildToolCard(block.Tool);

            case TuiBlockKind.Error:
                return new Panel(new Text(block.Text ?? string.Empty)).Border(BoxBorder.Rounded);

            case TuiBlockKind.System:
                return new Markup($"[grey]· {Markup.Escape(block.Text ?? string.Empty)}[/]");

            case TuiBlockKind.Compaction:
                return BuildCompaction(block, width);

            case TuiBlockKind.Divider:
                return BuildDivider(width);

            default:
                return new Text(block.Text ?? string.Empty);
        }
    }

    /// <summary>斜杠命令候选表（命令名 + 说明），两列对齐、暗淡呈现。</summary>
    private static IRenderable BuildCompletions(IReadOnlyList<TuiCompletionItem> items)
    {
        var nameWidth = Math.Min(items.Max(i => i.Name.Length), 24);
        var rows = new List<IRenderable>(items.Count);
        foreach (var item in items)
        {
            var name = item.Name.Length < nameWidth ? item.Name.PadRight(nameWidth) : item.Name;
            rows.Add(new Markup(
                $"[green]{Markup.Escape(name)}[/] [grey11]{Markup.Escape(item.Description ?? string.Empty)}[/]"));
        }

        return new Rows(rows);
    }

    /// <summary>
    /// 用户消息：<c>&gt; </c> 前缀 + 蓝色（不加粗），与无前缀的助手 Markdown 正文区分，且与输入行 `&gt; …▌` 视觉同源。
    /// 不再单独渲染「你」标签行。
    /// </summary>
    private static IRenderable BuildUser(TuiBlock block)
    {
        var text = block.Text ?? string.Empty;
        if (text.Length == 0)
            return new Text(string.Empty);

        return new Markup($"[blue]{TuiGlyphs.Prompt} {Markup.Escape(text)}[/]");
    }

    /// <summary>暗淡短分隔：半宽（上限 40 列）而非整宽，降低每帧噪声。</summary>
    private static IRenderable BuildDivider(int width)
    {
        try
        {
            var length = Math.Min(Math.Max(width, 0) / 2, 40);
            return new Markup($"[grey11]{new string('─', length)}[/]");
        }
        catch
        {
            return new Text(string.Empty);
        }
    }

    private IRenderable BuildAssistant(TuiBlock block, int width, IReadOnlyDictionary<string, int>? committedOffsets)
    {
        var text = block.Text ?? string.Empty;
        var offset = committedOffsets is not null && committedOffsets.TryGetValue(block.Key, out var value)
            ? Math.Clamp(value, 0, text.Length)
            : 0;

        var rows = new List<IRenderable>();

        // 推理只在「正文尚未固化任何前缀」（offset == 0）时渲染：
        // 首个固化片已把推理随正文写入滚动历史，offset > 0 再画会导致思考插到已固化正文下方。
        if (offset == 0)
            AddReasoning(rows, block);

        var tail = offset > 0 ? text[offset..] : text;

        if (!string.IsNullOrEmpty(tail))
        {
            rows.Add(_markdown.Render(tail, width));
        }
        else if (block.IsStreaming && offset == 0)
        {
            rows.Add(new Text("…"));
        }

        if (block.IsCancelled)
            rows.Add(new Markup("[grey](已取消)[/]"));

        if (rows.Count == 0)
            rows.Add(new Text(string.Empty));

        return rows.Count == 1 ? rows[0] : new Rows(rows);
    }

    /// <summary>非终态 assistant 块的文本已被完全固化时，活动区不再重复呈现该块。</summary>
    private static bool IsFullyCommitted(TuiBlock block, IReadOnlyDictionary<string, int>? committedOffsets)
    {
        if (committedOffsets is null || block.Kind != TuiBlockKind.Assistant || string.IsNullOrEmpty(block.Text))
            return false;

        return committedOffsets.TryGetValue(block.Key, out var offset) && offset >= block.Text.Length;
    }

    private IRenderable BuildCompaction(TuiBlock block, int width)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[grey]{TuiGlyphs.Reasoning} {Markup.Escape(block.Title ?? "已压缩")}[/]"),
        };

        if (!string.IsNullOrWhiteSpace(block.Text))
            rows.Add(_markdown.Render(block.Text!, width));

        return rows.Count == 1 ? rows[0] : new Rows(rows);
    }

    private void AddReasoning(List<IRenderable> rows, TuiBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.Reasoning))
            return;

        var lines = NormalizeLines(block.Reasoning);
        if (!_options.ShowReasoning && lines.Length > ReasoningTailLines)
        {
            rows.Add(new Markup($"[grey]{TuiGlyphs.Reasoning} 思考（{lines.Length} 行，/reasoning 展开）[/]"));
            rows.Add(new Markup($"[grey]{Markup.Escape(string.Join('\n', lines[^ReasoningTailLines..]))}[/]"));
            return;
        }

        rows.Add(new Markup($"[grey]{TuiGlyphs.Reasoning} 思考[/]"));
        rows.Add(new Markup($"[grey]{Markup.Escape(string.Join('\n', lines))}[/]"));
    }

    private IRenderable BuildToolCard(TuiToolState tool)
    {
        var rows = new List<IRenderable>
        {
            new Markup(
                $"[{StatusColor(tool.Status)}]{StatusIcon(tool.Status)}[/] " +
                $"[bold]{Markup.Escape(tool.Name)}[/] {Markup.Escape(ToolSummary(tool))}" +
                (tool.Status == TuiToolStatus.Running ? $" [yellow]{TuiGlyphs.Spinner}[/]" : string.Empty)),
        };

        if (!string.IsNullOrEmpty(tool.TaskDescription))
            rows.Add(new Markup($"[grey]{Markup.Escape(tool.TaskDescription)}[/]"));

        foreach (var step in tool.Steps)
        {
            rows.Add(new Markup(
                $"[grey]  {StatusIcon(step.Status)} {Markup.Escape(step.ToolName)} {Markup.Escape(step.Summary)}[/]"));
        }

        if (tool.Status is TuiToolStatus.Success or TuiToolStatus.Failed or TuiToolStatus.Rejected or TuiToolStatus.Cancelled)
            AddToolBody(rows, tool);

        return new Rows(rows);
    }

    private void AddToolBody(List<IRenderable> rows, TuiToolState tool)
    {
        if (string.Equals(tool.Name, "bash", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(tool.Arguments))
        {
            rows.Add(new Markup($"[grey]$ {Markup.Escape(FirstLine(tool.Arguments)!)}[/]"));
        }

        if (!string.IsNullOrEmpty(tool.Error))
            rows.Add(new Markup($"[red]{Markup.Escape(FirstLine(tool.Error)!)}[/]"));

        if (string.IsNullOrEmpty(tool.Output))
            return;

        var lines = NormalizeLines(tool.Output);
        var preview = Math.Max(1, _options.ToolPreviewLines);
        var shown = string.Join('\n', lines.Take(preview));

        rows.Add(string.Equals(tool.Name, "bash", StringComparison.OrdinalIgnoreCase)
            ? new Panel(new Text(shown)).Border(BoxBorder.Rounded)
            : new Text(shown));

        if (lines.Length > preview)
            rows.Add(new Markup($"[grey]… 共 {lines.Length} 行，输入 /expand {Markup.Escape(tool.CallId)} 查看完整输出[/]"));
    }

    private IRenderable BuildInputLine(TuiInputEditorState input, int width)
    {
        var rows = new List<IRenderable>();

        if (input.Attachments.Count > 0)
            rows.Add(new Markup($"[grey]附件: {Markup.Escape(string.Join(", ", input.Attachments))}[/]"));

        var raw = input.Text ?? string.Empty;
        var cursor = Math.Clamp(input.Cursor, 0, raw.Length);

        // 光标按真实插入位渲染：文本分居光标两侧，而非一律追加在末尾。
        var marked = raw[..cursor] + TuiGlyphs.Cursor + raw[cursor..];
        var lines = NormalizeLines(marked);
        var max = Math.Max(1, _options.MaxInputLines);
        if (lines.Length > max)
        {
            // 保证光标所在行可见：以光标行为窗口下界。
            var caretLine = NormalizeLines(raw[..cursor]).Length - 1;
            var start = Math.Clamp(caretLine - max + 1, 0, lines.Length - max);
            lines = lines[start..(start + max)];
        }

        rows.Add(new Markup($"[green]{TuiGlyphs.Prompt}[/] " + Markup.Escape(string.Join('\n', lines))));
        return rows.Count == 1 ? rows[0] : new Rows(rows);
    }

    private static string ToolSummary(TuiToolState tool)
    {
        if (string.Equals(tool.Name, "todowrite", StringComparison.OrdinalIgnoreCase))
            return tool.Title ?? "更新待办";

        return FirstLine(tool.Title)
            ?? FirstLine(tool.Arguments)
            ?? string.Empty;
    }

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var line = value.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length <= SummaryWidth ? line : line[..SummaryWidth] + "…";
    }

    private static string[] NormalizeLines(string value)
        => value.Replace("\r\n", "\n").Split('\n');

    private static string StatusIcon(TuiToolStatus status) => status switch
    {
        TuiToolStatus.Pending => TuiGlyphs.Tool,
        TuiToolStatus.Running => TuiGlyphs.Tool,
        TuiToolStatus.Success => TuiGlyphs.Success,
        TuiToolStatus.Failed => TuiGlyphs.Failure,
        TuiToolStatus.Rejected => TuiGlyphs.Rejected,
        TuiToolStatus.Cancelled => TuiGlyphs.Cancelled,
        _ => TuiGlyphs.Tool,
    };

    private static string StatusColor(TuiToolStatus status) => status switch
    {
        TuiToolStatus.Success => "green",
        TuiToolStatus.Failed or TuiToolStatus.Rejected => "red",
        TuiToolStatus.Cancelled => "grey",
        _ => "yellow",
    };
}
