using System.Text.Json;
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

    /// <summary>question 工具输出中包裹结构化作答 JSON 的定界哨兵（与 <c>QuestionTool</c> 一致）。</summary>
    private const string AnswersBeginSentinel = "<<<USER_ANSWERS_BEGIN>>>";
    private const string AnswersEndSentinel = "<<<USER_ANSWERS_END>>>";

    /// <summary>输入行前缀 <c>&gt; </c> 的显示宽度（<see cref="TuiGlyphs.Prompt"/> + 一个空格）。</summary>
    private static readonly int InputPrefixWidth = DisplayText.Width(TuiGlyphs.Prompt) + 1;

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
        => BuildActiveViewWithCaret(
            state,
            input,
            width,
            pendingApprovals,
            backgroundExecutions,
            committedOffsets,
            maxLines,
            completions,
            hint).View;

    /// <summary>
    /// 同 <see cref="BuildActiveView"/>，额外返回该帧的编辑插入点：终端输入法组合串跟随物理光标，
    /// 调用方据此把光标移到插入点（详见 <see cref="TuiCaret"/>）。
    /// <para>
    /// <paramref name="completionSelectedIndex"/> 为内联下拉游标项（0 基，-1 无高亮）；
    /// <paramref name="frameGen"/> 为本帧代次（引擎经 <c>ITuiAnchorProbe</c> 分配，DSR 底锚与命中表同源配对）。
    /// 返回帧携带 <c>CompletionHits</c>：仅含本帧实画且未被 <paramref name="maxLines"/> 裁掉的候选行（spec §6.4）。
    /// </para>
    /// </summary>
    public TuiActiveView BuildActiveViewWithCaret(
        TuiViewState state,
        TuiInputEditorState input,
        int width,
        int pendingApprovals = 0,
        int backgroundExecutions = 0,
        IReadOnlyDictionary<string, int>? committedOffsets = null,
        int maxLines = 0,
        IReadOnlyList<TuiCompletionItem>? completions = null,
        string? hint = null,
        int completionSelectedIndex = -1,
        long frameGen = 0)
    {
        var items = new List<IRenderable>();
        TuiCaret? caret = null;
        IReadOnlyList<TuiHitRegion>? hits = null;
        try
        {
            // 起始页：Logo + 常用操作，仅在尚无对话内容且空闲时出现，首次提交后自然消失。
            if (TuiWelcome.IsStartPage(state))
                items.Add(TuiWelcome.Render(width));

            foreach (var block in state.Blocks.Where(b => !b.IsTerminal))
            {
                if (IsFullyCommitted(block, committedOffsets))
                    continue;

                items.Add(BuildBlock(block, width, committedOffsets));
            }

            var inputBlock = BuildInputLine(input, width);

            // 斜杠命令候选：紧贴输入行上方，随输入实时更新；游标项高亮（内联不翻页，全画 ≤MaxCompletionRows）。
            if (completions is { Count: > 0 })
                items.Add(BuildCompletions(completions, width, completionSelectedIndex));

            // 不再插入「运行中」占位行：执行态已由状态栏呈现，重复会出现两个运行中指示。

            // 输入区上边界：分隔「滚动历史/消息」与「输入区（候选 + 输入行 + 状态栏）」。
            // 恒常显示，避免输入行贴着最新消息难以定位。
            items.Add(BuildInputSeparator(width));

            items.Add(inputBlock.Renderable);

            var statusRows = StatusBarView.BuildRows(state, width, pendingApprovals, backgroundExecutions, hint);
            foreach (var row in statusRows)
                items.Add(row);

            caret = ResolveCaret(inputBlock, statusRows.Count, maxLines);
            hits = BuildCompletionHits(completions, inputBlock, statusRows.Count, maxLines, frameGen);
        }
        catch
        {
            caret = null;
            hits = null;
            items.Clear();
            items.Add(new Markup("[red]渲染失败[/]"));
            items.Add(StatusBarView.Render(state, width, pendingApprovals, backgroundExecutions, hint));
        }

        IRenderable rows = new Rows(items);

        // maxLines > 0：活动区高度上限交渲染端保证，裁尾保头部（输入行 + 状态栏恒在尾部）。
        var view = maxLines > 0 ? new TailClipRenderable(rows, maxLines) : rows;
        return new TuiActiveView(view, caret, hits, frameGen);
    }

    /// <summary>
    /// 输入块几何 → 相对活动区末行的插入点；输入行被高度裁剪或无法定位时返回 null
    /// （宁可不动光标，也不能把光标移到活动区之外）。
    /// </summary>
    private static TuiCaret? ResolveCaret(InputLineBlock input, int statusRows, int maxLines)
    {
        if (input.RowsBelowCaret < 0 || input.CaretColumn <= 0)
            return null;

        var rowsBelow = input.RowsBelowCaret + Math.Max(0, statusRows);
        if (maxLines > 0 && rowsBelow + 1 > maxLines)
            return null;

        return new TuiCaret(rowsBelow, input.CaretColumn);
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

            default:
                return new Text(block.Text ?? string.Empty);
        }
    }

    /// <summary>
    /// 斜杠命令候选表（命令名 + 说明）：说明用终端默认前景色，避免 grey11(#1c1c1c) 在黑底不可见。
    /// <para>
    /// 每候选<b>单物理行</b>（M-5）：name/desc 各按 <see cref="DisplayText"/> 显示宽预算截断补 <c>…</c>，
    /// 杜绝折行破坏逐行命中几何；游标项（<paramref name="selectedIndex"/>，0 基，-1 无）以 <c>▸ </c> + blue-on-grey 高亮，
    /// <c>▸ </c> 计入行首显示宽。name 列宽取各候选显示宽最大值（上限 24，且为高亮前缀与换行余量留空）。
    /// </para>
    /// </summary>
    private static IRenderable BuildCompletions(IReadOnlyList<TuiCompletionItem> items, int width, int selectedIndex)
    {
        var cursorPrefixWidth = DisplayText.Width(CursorPrefix);
        // 高亮行最挤：前缀 3 + name + 分隔 1 + desc ≥ 1 且整行 ≤ width-1（末列余量防自动换行）→ name 上限。
        var nameCap = Math.Min(24, Math.Max(0, width - 2 - cursorPrefixWidth));
        var nameWidth = Math.Min(items.Max(i => DisplayText.Width(i.Name)), nameCap);
        var rows = new List<IRenderable>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var highlighted = i == selectedIndex;
            var prefixWidth = highlighted ? cursorPrefixWidth : 0;
            var name = PadToWidth(FitToWidth(items[i].Name ?? string.Empty, nameWidth), nameWidth);
            var descBudget = Math.Max(0, width - 1 - prefixWidth - nameWidth - 1);
            var desc = FitToWidth(items[i].Description ?? string.Empty, descBudget);

            rows.Add(highlighted
                ? new Markup($"[bold deepskyblue1 on grey11]{CursorPrefix}{Markup.Escape(name)} {Markup.Escape(desc)}[/]")
                : new Markup($"[green]{Markup.Escape(name)}[/] [default]{Markup.Escape(desc)}[/]"));
        }

        return new Rows(rows);
    }

    /// <summary>高亮游标前缀（计入行首显示宽）。</summary>
    private const string CursorPrefix = "\u25b8 ";

    /// <summary>
    /// 补全候选命中表（spec §6.4，M-6：只含实画行）：候选块位于分隔线之上，第 k 项（0 基）之下依次为
    /// 块内剩余候选行、分隔线 1 行、输入块（附件 + 输入窗口行）、状态栏行；
    /// <c>maxLines &gt; 0</c> 时仅收 <c>RowsFromBottom ≤ maxLines-1</c> 的项（与 <see cref="ResolveCaret"/> 同裁剪口径）。
    /// </summary>
    private static IReadOnlyList<TuiHitRegion>? BuildCompletionHits(
        IReadOnlyList<TuiCompletionItem>? completions,
        InputLineBlock inputBlock,
        int statusRows,
        int maxLines,
        long frameGen)
    {
        if (completions is not { Count: > 0 })
            return null;

        var rowsBelowBlock = 1 + inputBlock.TotalRows + statusRows;
        var hits = new List<TuiHitRegion>(completions.Count);
        for (var i = 0; i < completions.Count; i++)
        {
            var rowsFromBottom = completions.Count - 1 - i + rowsBelowBlock;
            if (maxLines > 0 && rowsFromBottom > maxLines - 1)
                continue;

            hits.Add(new TuiHitRegion(i, rowsFromBottom, frameGen));
        }

        return hits;
    }

    /// <summary>按显示宽截断：超宽则保留前缀并补 <c>…</c>（结果显示宽 ≤ maxWidth；maxWidth 连省略号都放不下时为空）。</summary>
    internal static string FitToWidth(string value, int maxWidth)
    {
        if (maxWidth <= 0)
            return string.Empty;

        if (DisplayText.Width(value) <= maxWidth)
            return value;

        var budget = maxWidth - TuiGlyphs.EllipsisWidth;
        if (budget < 0)
            return string.Empty;

        var taken = 0;
        var length = 0;
        foreach (var ch in value)
        {
            var cells = DisplayText.Width(ch.ToString());
            if (taken + cells > budget)
                break;

            taken += cells;
            length++;
        }

        return value[..length] + TuiGlyphs.Ellipsis;
    }

    /// <summary>以空格右补齐到目标显示宽（已截断输入，超宽不补）。</summary>
    private static string PadToWidth(string value, int targetWidth)
    {
        var missing = targetWidth - DisplayText.Width(value);
        return missing > 0 ? value + new string(' ', missing) : value;
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

    /// <summary>
    /// 输入区上边界分隔线：整宽、<c>dim</c>（默认前景色 + 弱化，不挑终端背景明暗）。
    /// 回合之间不再另画分隔线：此线已恒常标示「消息区 / 输入区」的边界。
    /// </summary>
    internal static IRenderable BuildInputSeparator(int width)
    {
        try
        {
            // 取 width-1 抑制「写满最后一行触发自动换行」的经典边界问题。
            var length = Math.Max(0, width - 1);
            return new Markup($"[dim]{new string('─', length)}[/]");
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

        // question 输出的结构化 JSON（含哨兵 + \uXXXX 转义）是给模型的，直显不可读；
        // 解析为「√ 选项 / 自定义」答案卡片，解析失败再退回原样展示。
        if (string.Equals(tool.Name, "question", StringComparison.OrdinalIgnoreCase)
            && TryBuildQuestionAnswerCard(tool.Output, tool.Arguments, out var answerCard))
        {
            rows.Add(answerCard);
            return;
        }

        var lines = NormalizeLines(tool.Output);
        var preview = Math.Max(1, _options.ToolPreviewLines);
        var shown = string.Join('\n', lines.Take(preview));

        rows.Add(string.Equals(tool.Name, "bash", StringComparison.OrdinalIgnoreCase)
            ? new Panel(new Text(shown)).Border(BoxBorder.Rounded)
            : new Text(shown));

        if (lines.Length > preview)
            rows.Add(new Markup($"[grey]… 共 {lines.Length} 行，输入 /expand {Markup.Escape(tool.CallId)} 查看完整输出[/]"));
    }

    /// <summary>
    /// 输入行渲染体 + 插入点几何（行/列均相对输入块自身；<c>CaretColumn</c> 为 0 表示无法定位）；
    /// <c>TotalRows</c> 为输入块实占物理行数（附件行 + 窗口化输入行），供候选命中表推算其下方行数。
    /// </summary>
    private readonly record struct InputLineBlock(IRenderable Renderable, int RowsBelowCaret, int CaretColumn, int TotalRows);

    private InputLineBlock BuildInputLine(TuiInputEditorState input, int width)
    {
        var rows = new List<IRenderable>();

        if (input.Attachments.Count > 0)
            rows.Add(new Markup($"[grey]附件: {Markup.Escape(string.Join(", ", input.Attachments))}[/]"));

        var raw = input.Text ?? string.Empty;
        var cursor = Math.Clamp(input.Cursor, 0, raw.Length);

        // 光标按真实插入位渲染：文本分居光标两侧，而非一律追加在末尾。
        var marked = raw[..cursor] + TuiGlyphs.Cursor + raw[cursor..];
        var lines = NormalizeLines(marked);

        // 插入点所在行（换行数与加标记无关，故可直接用光标前的文本推算）。
        var caretLine = NormalizeLines(raw[..cursor]).Length - 1;

        var max = Math.Max(1, _options.MaxInputLines);
        if (lines.Length > max)
        {
            // 保证光标所在行可见：以光标行为窗口下界。
            var start = Math.Clamp(caretLine - max + 1, 0, lines.Length - max);
            lines = lines[start..(start + max)];
            caretLine -= start;
        }

        rows.Add(new Markup($"[green]{TuiGlyphs.Prompt}[/] " + Markup.Escape(string.Join('\n', lines))));

        // 输入块实占行数 = 附件行（0/1）+ 窗口化后的输入文本行；命中表以此推算候选行之下的行数。
        var totalRows = lines.Length + rows.Count - 1;

        // 插入点行内前缀：走显示格宽度，与终端实际占位一致（CJK 记 2 格）。
        // 直接用「光标前的文本」而非搜索标记字形——用户粘贴的文本里也可能出现该字形。
        var caretColumn = InputPrefixWidth + DisplayText.Width(NormalizeLines(raw[..cursor])[^1]) + 1;

        return new InputLineBlock(
            rows.Count == 1 ? rows[0] : new Rows(rows),
            lines.Length - 1 - caretLine,
            caretColumn,
            totalRows);
    }

    private static string ToolSummary(TuiToolState tool)
    {
        if (string.Equals(tool.Name, "todowrite", StringComparison.OrdinalIgnoreCase))
            return tool.Title ?? "更新待办";

        // question 的参数是 questions JSON：直接显示原始 JSON 会因 \uXXXX 转义而不可读，
        // 故解析后展示「标题 · 问题」。
        if (string.Equals(tool.Name, "question", StringComparison.OrdinalIgnoreCase))
            return FirstLine(tool.Title) ?? FirstLine(SummarizeQuestions(tool.Arguments)) ?? "提问";

        return FirstLine(tool.Title)
            ?? FirstLine(tool.Arguments)
            ?? string.Empty;
    }

    /// <summary>解析 question 工具参数，返回「标题 · 问题」摘要（多题用 | 连接）；无法解析时返回 null。</summary>
    internal static string? SummarizeQuestions(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (!document.RootElement.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var question in questions.EnumerateArray())
            {
                var header = question.TryGetProperty("header", out var h) ? h.GetString() : null;
                var text = question.TryGetProperty("question", out var t) ? t.GetString() : null;

                var part = string.IsNullOrWhiteSpace(header)
                    ? text
                    : string.IsNullOrWhiteSpace(text) ? header : $"{header} · {text}";
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part!);
            }

            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }
        catch
        {
            // 参数非合法 JSON：退回原始展示。
            return null;
        }
    }

    /// <summary>
    /// 把 question 工具的结构化输出解析成可读答案卡片：每条答案的选中项以 <c>√</c> 罗列，
    /// 自定义回答标为「自定义」；多选题逐题带标题。无法解析（无哨兵/非数组/空）时返回 false 走原样展示。
    /// </summary>
    private static bool TryBuildQuestionAnswerCard(
        string output,
        string? arguments,
        out IRenderable card)
    {
        card = null!;

        var begin = output.IndexOf(AnswersBeginSentinel, StringComparison.Ordinal);
        var end = output.IndexOf(AnswersEndSentinel, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
            return false;

        var json = output[(begin + AnswersBeginSentinel.Length)..end].Trim();
        if (json.Length == 0)
            return false;

        List<(string? Question, List<string> Selected, string? Custom)> answers;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var labels = ParseQuestionLabels(arguments);
            answers = [];
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("questionId", out var idElement) ? idElement.GetString() : null;
                var selected = new List<string>();
                if (item.TryGetProperty("selectedLabels", out var selectedElement) &&
                    selectedElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var label in selectedElement.EnumerateArray())
                    {
                        if (label.GetString() is { Length: > 0 } value)
                            selected.Add(value);
                    }
                }

                var custom = item.TryGetProperty("customAnswer", out var customElement)
                    ? customElement.GetString()
                    : null;

                var question = id is not null && labels.TryGetValue(id, out var label2) ? label2 : null;
                answers.Add((question, selected, string.IsNullOrWhiteSpace(custom) ? null : custom));
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (answers.Count == 0)
            return false;

        var rows = new List<IRenderable>();
        var multiple = answers.Count > 1;
        foreach (var (question, selected, custom) in answers)
        {
            if (multiple && !string.IsNullOrEmpty(question))
                rows.Add(new Markup($"[grey]{Markup.Escape(question!)}[/]"));

            var indent = multiple ? "    " : "  ";
            foreach (var label in selected)
                rows.Add(new Markup($"{indent}[green]{TuiGlyphs.Success}[/] {Markup.Escape(label)}"));

            if (custom is not null)
                rows.Add(new Markup($"{indent}[green]{TuiGlyphs.Success}[/] [grey]自定义：[/]{Markup.Escape(custom)}"));
        }

        if (rows.Count == 0)
            return false;

        card = new Rows(rows);
        return true;
    }

    /// <summary>解析 question 工具参数的 <c>questions[]</c>，返回 id → 「标题 · 问题」标签；无法解析返回空表。</summary>
    private static Dictionary<string, string> ParseQuestionLabels(string? arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (!document.RootElement.TryGetProperty("questions", out var questions) ||
                questions.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var question in questions.EnumerateArray())
            {
                var id = question.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrEmpty(id))
                    continue;

                var header = question.TryGetProperty("header", out var headerElement) ? headerElement.GetString() : null;
                var text = question.TryGetProperty("question", out var textElement) ? textElement.GetString() : null;

                var label = string.IsNullOrWhiteSpace(header)
                    ? text
                    : string.IsNullOrWhiteSpace(text) ? header : $"{header} · {text}";
                if (!string.IsNullOrWhiteSpace(label))
                    result[id] = label!;
            }
        }
        catch (JsonException)
        {
            // 参数非合法 JSON：不做标签映射，卡片退化为仅列答案。
        }

        return result;
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
