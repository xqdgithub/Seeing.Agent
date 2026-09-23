using Seeing.Agent.Abstractions.Questions;
using Spectre.Console;

namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>
/// 问答内联提示：单选/多选走自绘模态列表控件 <see cref="TuiListPrompt"/>（键盘 + 鼠标 hover/点击，
/// 经 <see cref="ITerminalSurface.PromptListAsync{T}"/> 在渲染线程执行、直读 <c>TuiKeyInput</c>），
/// 文本走 Spectre <see cref="TextPrompt{T}"/>（经 <see cref="ITerminalSurface.PromptAsync{T}"/>），
/// 支持「其他」自由输入与单答案截断。
/// <para>取消（Esc）返回 <c>null</c>（不决策）：列表控件消费 Esc 键直接以取消收敛（<c>cancelIndex: null</c>），
/// 与旧 <c>SelectionPrompt.AddCancelResult</c> 语义等价；文本提示见下方 Esc 说明。</para>
/// <para>
/// 标题与选项<b>转义纪律</b>：模型给出的问题/选项可能含 <c>[ ]</c>，未转义会触发 markup 解析异常。
/// <see cref="TuiListPrompt"/> 内部对标题/项统一 <see cref="Markup.Escape"/> 且返回原始下标，
/// 故本类传给列表控件的是<b>原始文本</b>（勿双重转义）；<see cref="TextPrompt{T}"/> 路径仍在本类显式转义。
/// 业务返回值一律为原始标签。
/// </para>
/// <para>
/// Esc 语义：列表路径由控件直接消费 <c>TuiInputAction.Cancel</c> 键收敛为 <c>null</c>；
/// <see cref="TextPrompt{T}"/> 会忽略 Esc，由渲染端（<c>EscapeCancellationScope</c>）把 Esc 链入提示取消令牌，
/// 从而同样收敛为取消而不是永久阻塞。
/// </para>
/// </summary>
public static class QuestionPrompt
{
    private const string OtherChoice = "其他（自定义）";
    private const int MaxAnswerLength = 2000;

    /// <summary>富样式页窗上限：每项可含 1 行描述，控制总帧高避免超出终端可用行。</summary>
    private const int RichPageSize = 6;

    /// <summary>逐个呈现问题并返回完整作答；用户取消时返回 <c>null</c>。</summary>
    public static Task<QuestionResult?> ShowAsync(
        ITerminalSurface surface,
        QuestionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(request);

        // 每个问题独立经 surface 起一个模态（列表走 PromptListAsync、文本走 PromptAsync），
        // 取消令牌由 surface 在提示内部链接调用方 ct 与 Esc 信号。
        return RunAsync(surface, request, callerCt: ct);
    }

    private static async Task<QuestionResult?> RunAsync(
        ITerminalSurface surface,
        QuestionRequest request,
        CancellationToken callerCt)
    {
        var answers = new List<QuestionAnswer>();
        try
        {
            foreach (var question in request.Questions)
            {
                var answer = await AskAsync(surface, question, callerCt).ConfigureAwait(false);
                if (answer is null)
                    return null;
                answers.Add(answer);
            }
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            // 提示被 Esc 取消（调用方令牌未取消）：按「用户放弃作答」处理。
            return null;
        }

        return new QuestionResult
        {
            RequestId = request.Id,
            Status = QuestionResultStatus.Completed,
            Answers = answers,
        };
    }

    private static Task<QuestionAnswer?> AskAsync(
        ITerminalSurface surface,
        Question question,
        CancellationToken ct)
        => question.Kind switch
        {
            QuestionKind.Multiple => AskMultipleAsync(surface, question, ct),
            QuestionKind.Text => AskTextAsync(surface, question, ct),
            _ => AskSingleAsync(surface, question, ct),
        };

    private static async Task<QuestionAnswer?> AskSingleAsync(
        ITerminalSurface surface,
        Question question,
        CancellationToken ct)
    {
        var items = BuildItems(question);
        if (items.Count == 0)
            return await AskTextAsync(surface, question, ct).ConfigureAwait(false);

        var optionCount = items.Count;
        if (question.AllowCustom)
            items.Add(new TuiListItem(OtherChoice));

        // 标题/项传原始文本：TuiListPrompt 内部统一 Markup.Escape，避免双重转义。
        // cancelIndex: null → Esc 由控件收敛为 null（不决策），等价旧 AddCancelResult。
        var selected = await surface.PromptListAsync(
            (ctx, promptCt) => TuiListPrompt.SelectAsync(
                ctx,
                ResolveHeader(question),
                ResolveQuestionText(question),
                items,
                RichPageSizeFor(items.Count),
                cancelIndex: null,
                promptCt),
            ct).ConfigureAwait(false);

        if (selected is null)
            return null;

        var otherIndex = question.AllowCustom ? optionCount : -1;
        if (selected.Value == otherIndex)
            return await AskCustomAsync(surface, question, ct).ConfigureAwait(false);

        return new QuestionAnswer
        {
            QuestionId = question.Id,
            SelectedLabels = new List<string> { items[selected.Value].Label },
        };
    }

    private static async Task<QuestionAnswer?> AskMultipleAsync(
        ITerminalSurface surface,
        Question question,
        CancellationToken ct)
    {
        var items = BuildItems(question);
        if (items.Count == 0)
            return await AskTextAsync(surface, question, ct).ConfigureAwait(false);

        var optionCount = items.Count;
        if (question.AllowCustom)
            items.Add(new TuiListItem(OtherChoice));

        var otherIndex = question.AllowCustom ? optionCount : -1;
        var preChecked = MapDefaultIndices(question, items, otherIndex);

        // Required 语义保持：必填时空勾选在控件内拦截（notRequired: !Required）。
        var selected = await surface.PromptListAsync(
            (ctx, promptCt) => TuiListPrompt.MultipleAsync(
                ctx,
                ResolveHeader(question),
                ResolveQuestionText(question),
                items,
                RichPageSizeFor(items.Count),
                preChecked,
                notRequired: !question.Required,
                cancelIndex: null,
                promptCt),
            ct).ConfigureAwait(false);

        if (selected is null)
            return null;

        var answer = new QuestionAnswer
        {
            QuestionId = question.Id,
            SelectedLabels = selected
                .Where(index => index != otherIndex)
                .Select(index => items[index].Label)
                .ToList(),
        };

        if (otherIndex >= 0 && selected.Contains(otherIndex))
        {
            var custom = await AskCustomAsync(surface, question, ct).ConfigureAwait(false);
            if (custom is null)
                return null;
            answer.CustomAnswer = custom.CustomAnswer;
        }

        return answer;
    }

    private static Task<QuestionAnswer?> AskTextAsync(
        ITerminalSurface surface,
        Question question,
        CancellationToken ct,
        string? title = null)
    {
        var defaultValue = ResolveDefaultText(question);
        var effectiveTitle = title ?? BuildTitle(question);
        if (defaultValue is not null)
            effectiveTitle = $"{effectiveTitle} （默认：{defaultValue}）";

        return surface.PromptAsync(async (console, promptCt) =>
        {
            // 不用 TextPrompt.DefaultValue：Spectre 会把默认值按 markup 解析，
            // 默认值含 "[" 时会抛「Could not find color or style」并终止引擎。
            // 改为「标题里转义展示默认值 + 允许空提交 → 空值回退默认值」。
            var prompt = new TextPrompt<string>(Markup.Escape(effectiveTitle));
            if (defaultValue is not null || !question.Required)
                prompt.AllowEmpty();

            string text;
            try
            {
                text = await prompt.ShowAsync(console, promptCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!promptCt.IsCancellationRequested)
            {
                return null;
            }

            if (string.IsNullOrEmpty(text) && defaultValue is not null)
                text = defaultValue;

            return new QuestionAnswer
            {
                QuestionId = question.Id,
                CustomAnswer = Truncate(text),
            };
        }, ct);
    }

    private static Task<QuestionAnswer?> AskCustomAsync(
        ITerminalSurface surface,
        Question question,
        CancellationToken ct)
    {
        return surface.PromptAsync(async (console, promptCt) =>
        {
            var prompt = new TextPrompt<string>(Markup.Escape($"{BuildTitle(question)} — 自定义回答"));
            if (!question.Required)
                prompt.AllowEmpty();

            string text;
            try
            {
                text = await prompt.ShowAsync(console, promptCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!promptCt.IsCancellationRequested)
            {
                return null;
            }

            return new QuestionAnswer
            {
                QuestionId = question.Id,
                CustomAnswer = Truncate(text),
            };
        }, ct);
    }

    /// <summary>构造富样式列表项：过滤空标签，带出选项说明（可空）。</summary>
    private static List<TuiListItem> BuildItems(Question question)
    {
        if (question.Options is null)
            return [];

        return question.Options
            .Where(option => option is not null && !string.IsNullOrWhiteSpace(option.Label))
            .Select(option => new TuiListItem(option.Label, option.Description))
            .ToList();
    }

    private static string? ResolveHeader(Question question)
        => string.IsNullOrWhiteSpace(question.Header) ? null : question.Header.Trim();

    private static string ResolveQuestionText(Question question)
        => string.IsNullOrWhiteSpace(question.QuestionText) ? "请回答" : question.QuestionText.Trim();

    private static int RichPageSizeFor(int count)
        => Math.Clamp(count, 1, RichPageSize);

    private static List<int>? MapDefaultIndices(Question question, List<TuiListItem> items, int otherIndex)
    {
        if (question.DefaultSelectedLabels is not { Count: > 0 })
            return null;

        var indices = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (i == otherIndex)
                continue;

            if (question.DefaultSelectedLabels.Contains(items[i].Label, StringComparer.Ordinal))
                indices.Add(i);
        }

        return indices.Count > 0 ? indices : null;
    }

    private static string BuildTitle(Question question)
    {
        var header = string.IsNullOrWhiteSpace(question.Header) ? null : question.Header.Trim();
        var text = string.IsNullOrWhiteSpace(question.QuestionText) ? "请回答" : question.QuestionText.Trim();
        return header is null ? text : $"{header} · {text}";
    }

    private static string? ResolveDefaultText(Question question)
    {
        if (!string.IsNullOrWhiteSpace(question.DefaultCustomAnswer))
            return question.DefaultCustomAnswer;
        if (question.DefaultSelectedLabels is { Count: > 0 })
            return question.DefaultSelectedLabels[0];
        return null;
    }

    private static string Truncate(string text)
        => text.Length <= MaxAnswerLength ? text : text[..MaxAnswerLength];
}
