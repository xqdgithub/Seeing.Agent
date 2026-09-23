using System.Text;
using Seeing.Agent.Tui.Input;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>
/// 自绘模态「可选列表」控件：键盘（↑/↓ 游标、Enter 确认、Space 多选切换、Esc 取消哨兵）+
/// 鼠标（hover 高亮、点击确认/切换），复用 <see cref="SelectableList"/> 内核与 <see cref="ITuiAnchorProbe"/> 命中。
/// <para>
/// 擦画口径（spec §7.1 / M-8）：控件只拥有「标题 + 列表项」这些行；多行头部（如权限 Panel 概要）
/// 由调用方在调本控件<b>之前</b>自行 <c>Console.Write</c> 一次。每帧重画前把光标 <c>CUU</c> 回本控件首行
/// （绝不越过 header 边界）、<c>ESC[J</c> 擦到屏尾再画，帧末行留光标原位并写 <c>ESC[6n</c>（仅鼠标开启时）做 DSR 底锚。
/// </para>
/// <para>
/// 两种版式：<b>富样式</b>（<c>header/question/items</c> 重载，问答用）——徽标头 + 题目 + 序号
/// <c>[√]/[ ]</c> + 灰色描述副行 + 底部键位提示；<b>简样式</b>（<c>title/labels</c> 重载，权限用）——单行标题 + 选项行。
/// 富样式每项可占 2 行（有描述时），命中表按实际行号反算，故支持可变行高。
/// </para>
/// <para>标题与项一律 <see cref="Markup.Escape"/>；每项按显示宽截断保证单物理行，杜绝折行破坏逐行几何。</para>
/// <para>未校准/跨帧代次 → 鼠标命中安全失效（键盘全功能兜底）；无按键通道 → 不阻塞，直接按取消收敛。</para>
/// </summary>
public static class TuiListPrompt
{
    /// <summary>富样式选中行底色（深灰，深色终端上可辨）与前景亮蓝。</summary>
    private const string SelectedStyle = "bold deepskyblue1 on grey30";

    /// <summary>单选；返回选中下标，取消返回 <paramref name="cancelIndex"/>（或 null）。</summary>
    public static async Task<int?> SelectAsync(
        TuiPromptContext ctx,
        string title,
        IReadOnlyList<string> labels,
        int pageSize,
        int? cancelIndex,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var outcome = await RunLoopAsync(
            ctx, header: null, question: title ?? string.Empty, ToItems(labels), pageSize,
            multi: false, preChecked: null, notRequired: false, rich: false, ct).ConfigureAwait(false);

        return outcome.Submitted ? outcome.SelectedIndex : cancelIndex;
    }

    /// <summary>多选；返回勾选下标集（升序），取消返回 null。</summary>
    public static async Task<IReadOnlyList<int>?> MultipleAsync(
        TuiPromptContext ctx,
        string title,
        IReadOnlyList<string> labels,
        int pageSize,
        IReadOnlyList<int>? preChecked,
        bool notRequired,
        int? cancelIndex,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var outcome = await RunLoopAsync(
            ctx, header: null, question: title ?? string.Empty, ToItems(labels), pageSize,
            multi: true, preChecked, notRequired, rich: false, ct).ConfigureAwait(false);

        return outcome.Submitted ? outcome.Checks : null;
    }

    /// <summary>富样式单选（问答用）：徽标头 + 题目 + 序号选项 + 描述副行 + 键位提示。</summary>
    public static async Task<int?> SelectAsync(
        TuiPromptContext ctx,
        string? header,
        string question,
        IReadOnlyList<TuiListItem> items,
        int pageSize,
        int? cancelIndex,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var outcome = await RunLoopAsync(
            ctx, header, question ?? string.Empty, items ?? [], pageSize,
            multi: false, preChecked: null, notRequired: false, rich: true, ct).ConfigureAwait(false);

        return outcome.Submitted ? outcome.SelectedIndex : cancelIndex;
    }

    /// <summary>富样式多选（问答用）。</summary>
    public static async Task<IReadOnlyList<int>?> MultipleAsync(
        TuiPromptContext ctx,
        string? header,
        string question,
        IReadOnlyList<TuiListItem> items,
        int pageSize,
        IReadOnlyList<int>? preChecked,
        bool notRequired,
        int? cancelIndex,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var outcome = await RunLoopAsync(
            ctx, header, question ?? string.Empty, items ?? [], pageSize,
            multi: true, preChecked, notRequired, rich: true, ct).ConfigureAwait(false);

        return outcome.Submitted ? outcome.Checks : null;
    }

    private static IReadOnlyList<TuiListItem> ToItems(IReadOnlyList<string> labels)
        => labels is null ? [] : labels.Select(label => new TuiListItem(label)).ToList();

    private static async Task<Outcome> RunLoopAsync(
        TuiPromptContext ctx,
        string? header,
        string question,
        IReadOnlyList<TuiListItem> items,
        int pageSize,
        bool multi,
        IReadOnlyList<int>? preChecked,
        bool notRequired,
        bool rich,
        CancellationToken ct)
    {
        var keys = ctx.Keys;
        if (keys is null)
            return Outcome.Cancelled;

        var labels = items.Select(item => item.Label).ToList();
        var list = new SelectableList(labels, pageSize, multi);
        if (preChecked is { Count: > 0 })
        {
            foreach (var index in preChecked)
                list.Toggle(index);

            list.Reset();
        }

        var prevLines = 0;
        IReadOnlyList<TuiHitRegion> hits = [];
        var frameGen = 0L;

        try
        {
            (prevLines, hits, frameGen) = RenderFrame(ctx, list, header, question, items, multi, rich, prevLines);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool available;
                try
                {
                    available = await keys.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (!available)
                    return Outcome.Cancelled;

                if (!keys.TryRead(out var key))
                    continue;

                var changed = false;
                Outcome? decision = null;

                switch (key.Action)
                {
                    case TuiInputAction.HistoryPrev:
                        list.MoveUp();
                        changed = true;
                        break;

                    case TuiInputAction.HistoryNext:
                        list.MoveDown();
                        changed = true;
                        break;

                    case TuiInputAction.Submit:
                        if (multi)
                        {
                            var checks = list.ConfirmChecked();
                            if (checks.Count > 0 || notRequired)
                                decision = Outcome.OfChecked(checks);
                        }
                        else
                        {
                            var index = list.ConfirmIndex();
                            if (index >= 0)
                                decision = Outcome.OfSelected(index);
                        }

                        break;

                    case TuiInputAction.InsertText when multi && key.Text == " ":
                        list.Toggle(list.SelectedIndex);
                        changed = true;
                        break;

                    case TuiInputAction.Cancel:
                        decision = Outcome.Cancelled;
                        break;

                    case TuiInputAction.Mouse when ctx.MouseEnabled && key.Mouse is { } mouse:
                        if (mouse.Phase != TuiMousePhase.Release)
                        {
                            var hit = TuiHitTest.TryResolve(ctx.Anchor, hits, frameGen, mouse.Row);
                            if (hit is { } item)
                            {
                                changed = list.SetByHit(item);

                                // 点击确认只认 Left+Press（忽略 Release 防双触发）；滚轮为 P3 不消费。
                                if (mouse.Phase == TuiMousePhase.Press && mouse.Button == TuiMouseButton.Left)
                                {
                                    if (multi)
                                    {
                                        list.Toggle(item);
                                        changed = true;
                                    }
                                    else
                                    {
                                        decision = Outcome.OfSelected(item);
                                    }
                                }
                            }
                        }

                        break;
                }

                if (decision is not null)
                    return decision.Value;

                if (changed)
                    (prevLines, hits, frameGen) = RenderFrame(ctx, list, header, question, items, multi, rich, prevLines);
            }
        }
        finally
        {
            // 结束（确认/取消/异常）统一擦掉本控件帧，把光标留在列表区首行，供调用方续写。
            EraseFrame(ctx, prevLines);
        }
    }

    /// <summary>
    /// 画一帧（可选先擦旧帧）；返回（本帧行数, 命中表, 帧代次）。
    /// 帧末光标停在列表末行（无尾换行），DSR 底锚即在「列表末行」发出，与命中表 RowsFromBottom 同基准。
    /// </summary>
    private static (int Lines, IReadOnlyList<TuiHitRegion> Hits, long Gen) RenderFrame(
        TuiPromptContext ctx,
        SelectableList list,
        string? header,
        string question,
        IReadOnlyList<TuiListItem> items,
        bool multi,
        bool rich,
        int prevLines)
    {
        EraseFrame(ctx, prevLines);

        var width = ctx.Width > 0 ? ctx.Width : 80;
        // 留 1 列右余量：满宽行会触发终端自动换行，破坏本帧擦画/DSR 的行基准（同内联 BuildCompletions 口径）。
        var usable = Math.Max(1, width - 1);
        var page = list.PageIndices();
        var lines = new List<IRenderable>();
        var hitRows = new List<(int Item, int Line)>();

        if (rich)
        {
            if (!string.IsNullOrWhiteSpace(header))
                lines.Add(new Markup($"[black on hotpink] {Markup.Escape(TruncateToWidth(header!, usable - 2))} [/]"));

            var suffix = multi ? "（可多选）" : string.Empty;
            lines.Add(new Markup(Markup.Escape(TruncateToWidth(question + suffix, usable))));
            lines.Add(new Markup(string.Empty));
        }
        else
        {
            lines.Add(new Markup(Markup.Escape(TruncateToWidth(question, usable))));
        }

        for (var k = 0; k < page.Count; k++)
        {
            var index = page[k];
            var selected = index == list.SelectedIndex;
            var label = items[index].Label ?? string.Empty;

            if (rich)
            {
                var number = $"{k + 1}. ";
                var check = multi ? (list.IsChecked(index) ? "[√] " : "[ ] ") : string.Empty;
                var prefix = number + check;
                var text = Markup.Escape(prefix + TruncateToWidth(label, usable - DisplayText.Width(prefix)));
                lines.Add(selected ? new Markup($"[{SelectedStyle}]{text}[/]") : new Markup(text));
                hitRows.Add((index, lines.Count - 1));

                var description = items[index].Description;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    var indent = new string(' ', DisplayText.Width(prefix));
                    lines.Add(new Markup($"[grey]{Markup.Escape(TruncateToWidth(indent + description, usable))}[/]"));
                    hitRows.Add((index, lines.Count - 1));
                }
            }
            else
            {
                var marker = selected ? "▸ " : "  ";
                var check = multi ? (list.IsChecked(index) ? "[☑] " : "[☐] ") : string.Empty;
                var prefix = marker + check;
                var text = Markup.Escape(prefix + TruncateToWidth(label, usable - DisplayText.Width(prefix)));
                lines.Add(selected ? new Markup($"[bold deepskyblue1 on grey11]{text}[/]") : new Markup(text));
                hitRows.Add((index, lines.Count - 1));
            }
        }

        if (rich)
        {
            lines.Add(new Markup(string.Empty));
            var hint = multi
                ? "↑↓ 移动   空格 勾选   Enter 确认   Esc 取消"
                : "↑↓ 移动   Enter 确认   Esc 取消";
            lines.Add(new Markup($"[grey]{Markup.Escape(TruncateToWidth(hint, usable))}[/]"));
        }

        // 逐行直写、行间 CRLF、末行不换行：保证帧末光标严格停在本控件末行，
        // 使 EraseFrame 的 CUU(prevLines-1) 与 DSR 底锚/命中几何同基准。
        // 若改用 ctx.Console.Write(new Rows(...))，Spectre 会在末行后附尾换行，
        // 光标落到末行下一行 → 每帧少擦一行，标题逐帧累积（鼠标 hover 重绘即显形）。
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                WriteRaw(ctx, "\r\n");

            ctx.Console.Write(lines[i]);
        }

        if (!ctx.MouseEnabled || page.Count == 0)
            return (lines.Count, [], 0);

        // 每帧（光标在列表末行时）发起 DSR；命中表用同帧代次，跨代次/未校准由 TryResolve 失效。
        var totalLines = lines.Count;
        var gen = ctx.Anchor.BeginProbe(() => WriteRaw(ctx, "\u001b[6n"));
        var hits = new TuiHitRegion[hitRows.Count];
        for (var i = 0; i < hitRows.Count; i++)
            hits[i] = new TuiHitRegion(hitRows[i].Item, totalLines - 1 - hitRows[i].Line, gen);

        return (totalLines, hits, gen);
    }

    /// <summary>擦除上一帧：回到本控件首行（不越过 header），擦到屏幕尾。</summary>
    private static void EraseFrame(TuiPromptContext ctx, int prevLines)
    {
        if (prevLines <= 0)
            return;

        var builder = new StringBuilder("\r\u001b[0m");
        if (prevLines > 1)
            builder.Append(FormattableString.Invariant($"\u001b[{prevLines - 1}A"));

        builder.Append("\u001b[J");
        WriteRaw(ctx, builder.ToString());
    }

    /// <summary>直写终端（不经 Spectre 管线，保留原始转义序列）；终端不可写时忽略，不中断交互。</summary>
    private static void WriteRaw(TuiPromptContext ctx, string sequence)
    {
        try
        {
            var writer = ctx.Console.Profile.Out.Writer;
            writer.Write(sequence);
            writer.Flush();
        }
        catch (IOException)
        {
            // 终端不可写：擦画/探测失败无需中断选择流程。
        }
        catch (ObjectDisposedException)
        {
            // 输出已释放（停机竞态）：忽略。
        }
    }

    /// <summary>按显示宽截断为单物理行（超出以省略号收尾），防折行破坏逐行命中几何。</summary>
    private static string TruncateToWidth(string value, int maxWidth)
    {
        if (string.IsNullOrEmpty(value) || maxWidth <= 0)
            return string.Empty;

        if (DisplayText.Width(value) <= maxWidth)
            return value;

        var budget = maxWidth - TuiGlyphs.EllipsisWidth;
        if (budget <= 0)
            return TuiGlyphs.Ellipsis;

        var builder = new StringBuilder();
        var width = 0;
        foreach (var ch in value)
        {
            var charWidth = DisplayText.Width(ch.ToString());
            if (width + charWidth > budget)
                break;

            builder.Append(ch);
            width += charWidth;
        }

        return builder.Append(TuiGlyphs.Ellipsis).ToString();
    }

    private readonly record struct Outcome(bool Submitted, int SelectedIndex, IReadOnlyList<int> Checks)
    {
        public static Outcome Cancelled => new(false, -1, []);

        public static Outcome OfSelected(int index) => new(true, index, []);

        public static Outcome OfChecked(IReadOnlyList<int> checks) => new(true, -1, checks);
    }
}
