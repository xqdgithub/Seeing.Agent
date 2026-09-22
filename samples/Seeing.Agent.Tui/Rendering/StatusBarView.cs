using System.Globalization;
using Seeing.Agent.Tui.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 状态栏（纯渲染辅助），<b>两行</b>：
/// <list type="bullet">
///   <item>第 1 行：Agent / 模型 / <b>审批模式</b> / 思考档 / 执行态 / 队列 / 待批 / 后台执行 / 提示 / Todo。</item>
///   <item>第 2 行：<b>左右分栏</b> —— 左侧当前工作目录（主目录缩写为 <c>~</c>），右侧上下文用量。</item>
/// </list>
/// <para>
/// 每行恒为<b>单行</b>：活动区高度预算按固定行数计算，一旦折行会把输入行挤出屏幕。
/// 第 1 行超宽时按优先级丢弃低价值段（Todo → 思考档 → 后台执行 → 队列 → 模型），
/// Agent / 执行态 / 审批模式始终保留；第 2 行优先保障右侧用量，左侧路径按需<b>中间省略</b>。
/// </para>
/// <para>
/// 宽度一律按<b>显示格</b>计算（CJK/全角记 2 格），与终端实际占位一致。
/// </para>
/// </summary>
public static class StatusBarView
{
    /// <summary>段间分隔符。</summary>
    private const string Separator = " · ";

    /// <summary>分隔符的显示宽度（由 <see cref="DisplayText.Width"/> 推导，避免与实际占位脱节）。</summary>
    private static readonly int SeparatorWidth = DisplayText.Width(Separator);

    /// <summary>行首/行尾各一个空格。</summary>
    private const int PaddingWidth = 2;

    /// <summary>行尾保留 1 格：写满最后一格会触发终端自动换行（经典边界问题）。</summary>
    private const int WrapGuardWidth = 1;

    /// <summary>第 2 行左侧工作目录的最小宽度；不足则只保留右侧用量。</summary>
    private const int MinWorkspaceWidth = 12;

    // 第 1 行段优先级：数值越小越先被丢弃。审批模式涉及安全，优先级最高。
    private const int PriorityTodo = 20;
    private const int PriorityThinking = 30;
    private const int PriorityBackground = 40;
    private const int PriorityQueue = 50;
    private const int PriorityModel = 60;
    private const int PriorityAutoApprove = 70;
    private const int PriorityPendingApprovals = 80;
    private const int PriorityHint = 90;
    private const int PriorityMandatory = 1000;

    public static IRenderable Render(
        TuiViewState state,
        int width,
        int pendingApprovals = 0,
        int backgroundExecutions = 0,
        string? hint = null)
    {
        var rows = BuildRows(state, width, pendingApprovals, backgroundExecutions, hint);
        return rows.Count == 1 ? rows[0] : new Rows(rows);
    }

    /// <summary>
    /// 状态栏各行（1~2 行）。返回行集合而非整块渲染体：调用方需要行数来计算编辑插入点位置
    /// （插入点之下有几行状态栏），逐行加入活动区与整块加入等价。
    /// </summary>
    internal static IReadOnlyList<IRenderable> BuildRows(
        TuiViewState state,
        int width,
        int pendingApprovals = 0,
        int backgroundExecutions = 0,
        string? hint = null)
    {
        try
        {
            var rows = new List<IRenderable>(2)
            {
                BuildInfoRow(state, width, pendingApprovals, backgroundExecutions, hint),
            };

            var secondary = BuildSecondaryRow(state, width);
            if (secondary is not null)
                rows.Add(secondary);

            return rows;
        }
        catch
        {
            return [new Text(string.Empty)];
        }
    }

    /// <summary>第 1 行：Agent / 模型 / 审批模式 / 思考档 / 执行态 / 队列 / 待批 / 后台 / 提示 / Todo。</summary>
    private static IRenderable BuildInfoRow(
        TuiViewState state,
        int width,
        int pendingApprovals,
        int backgroundExecutions,
        string? hint)
    {
        var segments = new List<Segment>
        {
            new("[grey]", state.AgentId ?? "-", PriorityMandatory, Mandatory: true),
        };

        if (!string.IsNullOrEmpty(state.ModelId))
            segments.Add(new Segment("[blue]", state.ModelId, PriorityModel));

        var approval = AutoApproveText.Label(state.AutoApprove, state.GlobalAutoApprove);
        segments.Add(new Segment(
            AutoApproveText.IsAuto(state.AutoApprove, state.GlobalAutoApprove) ? "[yellow]" : "[grey]",
            $"审批 {approval}",
            PriorityAutoApprove));

        if (!string.IsNullOrEmpty(state.ThinkingEffort))
            segments.Add(new Segment("[grey]", $"think:{state.ThinkingEffort}", PriorityThinking));

        segments.Add(new Segment(
            state.IsExecuting ? "[yellow]" : "[green]",
            state.IsExecuting ? "运行中" : "空闲",
            PriorityMandatory,
            Mandatory: true));

        if (state.QueueLength > 0)
            segments.Add(new Segment("[cyan]", $"队列 {state.QueueLength}", PriorityQueue));

        if (pendingApprovals > 0)
            segments.Add(new Segment("[red]", $"待批 {pendingApprovals}", PriorityPendingApprovals));

        if (backgroundExecutions > 0)
            segments.Add(new Segment("[grey]", $"{backgroundExecutions} 后台执行", PriorityBackground));

        if (!string.IsNullOrEmpty(hint))
            segments.Add(new Segment("[yellow]", hint, PriorityHint));

        var todo = TodoSummary(state.Todos);
        if (todo is not null)
            segments.Add(new Segment("[grey]", todo, PriorityTodo));

        Fit(segments, width - WrapGuardWidth);

        // 分隔符 " · "：首尾空格放在 markup 之外，避免标签影响空白渲染。
        var separatorMarkup = $" [grey]{Separator.Trim()}[/] ";
        var line = string.Join(separatorMarkup, segments.Select(s => s.MarkupText));
        return new Markup($"[grey11] {line} [/]");
    }

    /// <summary>第 2 行：左侧工作目录、右侧上下文用量（左右分栏，各占两端）；两者皆无时返回 null。</summary>
    private static IRenderable? BuildSecondaryRow(TuiViewState state, int width)
    {
        var available = width - PaddingWidth;
        if (available <= 0)
            return null;

        var left = WorkspaceLabel(state);
        var right = state.Budget is null ? null : BudgetText(state.Budget);
        if (left is null && right is null)
            return null;

        if (right is not null && DisplayText.Width(right) > available)
            right = DisplayText.TruncateMiddle(right, available);

        var rightWidth = right is null ? 0 : DisplayText.Width(right);

        // 只有用量，或路径放不下：用量右对齐。
        var maxLeft = available - rightWidth - 1;
        if (left is null || maxLeft < MinWorkspaceWidth)
        {
            // 末尾 [/] 闭合外层 [grey11]（Spectre 要求标签配对，否则抛 Unbalanced markup stack）。
            return right is null
                ? null
                : new Markup(
                    $"[grey11] {new string(' ', available - rightWidth)}[grey]{Markup.Escape(right)}[/][/]");
        }

        var fittedLeft = DisplayText.Width(left) > maxLeft
            ? DisplayText.TruncateMiddle(left, maxLeft)
            : left;

        var gap = Math.Max(1, available - DisplayText.Width(fittedLeft) - rightWidth);

        var body = new System.Text.StringBuilder()
            .Append("[cyan]").Append(Markup.Escape(fittedLeft)).Append("[/]")
            .Append(new string(' ', gap));

        if (right is not null)
            body.Append("[grey]").Append(Markup.Escape(right)).Append("[/]");

        return new Markup($"[grey11] {body}[/]");
    }

    /// <summary>工作目录标签（主目录缩写为 <c>~</c>）；无工作区时为 null。</summary>
    private static string? WorkspaceLabel(TuiViewState state)
        => string.IsNullOrWhiteSpace(state.WorkspaceRoot)
            ? null
            : DisplayText.AbbreviateHome(
                state.WorkspaceRoot!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));


    /// <summary>
    /// 状态栏片段：<see cref="Color"/> + <see cref="Plain"/> + <see cref="Priority"/>。
    /// 明细只有单一前景色，故按需重建 markup（供裁剪时改写文本）。
    /// </summary>
    private sealed record Segment(string Color, string Plain, int Priority, bool Mandatory = false)
    {
        public string MarkupText => $"{Color}{Spectre.Console.Markup.Escape(Plain)}[/]";
    }

    /// <summary>收窄第 2 行：按优先级丢段，极窄时保底裁剪，确保恒为单行。</summary>
    private static void Fit(List<Segment> segments, int width)
    {
        while (LineWidth(segments) > width && DropLowest(segments))
        {
        }

        // 保底：仅剩必需段仍超宽时裁剪最宽段文本（异常窄终端，正常宽度不会走到）。
        while (segments.Count > 0 && LineWidth(segments) > width)
        {
            var widest = segments.OrderByDescending(s => DisplayText.Width(s.Plain)).First();
            var budget = Math.Max(1, DisplayText.Width(widest.Plain) - (LineWidth(segments) - width));
            var fitted = DisplayText.TruncateMiddle(widest.Plain, budget);
            if (string.Equals(fitted, widest.Plain, StringComparison.Ordinal))
                break;

            segments[segments.IndexOf(widest)] = widest with { Plain = fitted };
        }
    }

    /// <summary>丢弃最低优先级的非必需段；返回是否丢掉了内容。</summary>
    private static bool DropLowest(List<Segment> segments)
    {
        var candidate = segments
            .Where(s => !s.Mandatory)
            .OrderBy(s => s.Priority)
            .FirstOrDefault();

        return candidate is not null && segments.Remove(candidate);
    }

    /// <summary>整行显示宽度（含分隔符与行首尾留白）。</summary>
    private static int LineWidth(List<Segment> segments)
        => segments.Sum(s => DisplayText.Width(s.Plain))
            + SeparatorWidth * Math.Max(0, segments.Count - 1)
            + PaddingWidth;

    private static string? TodoSummary(IReadOnlyList<TuiTodo> todos)
    {
        if (todos is null || todos.Count == 0)
            return null;

        var done = todos.Count(t => string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
        return $"Todo {done}/{todos.Count}";
    }

    /// <summary>上下文用量紧凑文案：<c>12.3k/200k (6%)</c>；上限未知时仅 <c>987</c>。</summary>
    private static string BudgetText(TuiBudget budget)
    {
        var current = FormatTokens(budget.CurrentTokens);
        if (budget.MaxTokens is not > 0)
            return current;

        var max = budget.MaxTokens.Value;
        var percent = Math.Min(999, budget.CurrentTokens * 100 / max);
        return $"{current}/{FormatTokens(max)} ({percent}%)";
    }

    /// <summary>Token 数紧凑显示：12345 → 12.3k。</summary>
    private static string FormatTokens(long value)
        => value >= 1000
            ? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : value.ToString(CultureInfo.InvariantCulture);
}
