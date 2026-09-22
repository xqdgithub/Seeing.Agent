using Seeing.Agent.Abstractions.Questions;
using Spectre.Console;

namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>
/// 问答内联提示：按题型渲染（单选 <see cref="SelectionPrompt{T}"/>、多选 <see cref="MultiSelectionPrompt{T}"/>、
/// 文本 <see cref="TextPrompt{T}"/>），支持「其他」自由输入与单答案截断。
/// <para>经 <see cref="ITerminalSurface.PromptAsync{T}"/> 在渲染线程执行；取消返回 <c>null</c>（不决策）。</para>
/// </summary>
public static class QuestionPrompt
{
    private const string OtherChoice = "其他（自定义）";
    private const string CancelSentinel = "\u0000__tui_question_cancel__";
    private const int MaxAnswerLength = 2000;
    private const int MaxPageSize = 10;

    /// <summary>逐个呈现问题并返回完整作答；用户取消时返回 <c>null</c>。</summary>
    public static Task<QuestionResult?> ShowAsync(
        ITerminalSurface surface,
        QuestionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(request);

        return surface.PromptAsync(console => RunAsync(console, request, ct), ct);
    }

    private static async Task<QuestionResult?> RunAsync(
        IAnsiConsole console,
        QuestionRequest request,
        CancellationToken ct)
    {
        var answers = new List<QuestionAnswer>();
        try
        {
            foreach (var question in request.Questions)
            {
                var answer = await AskAsync(console, question, ct).ConfigureAwait(false);
                if (answer is null)
                    return null;
                answers.Add(answer);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
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
        IAnsiConsole console,
        Question question,
        CancellationToken ct)
        => question.Kind switch
        {
            QuestionKind.Multiple => AskMultipleAsync(console, question, ct),
            QuestionKind.Text => AskTextAsync(console, question, ct),
            _ => AskSingleAsync(console, question, ct),
        };

    private static async Task<QuestionAnswer?> AskSingleAsync(
        IAnsiConsole console,
        Question question,
        CancellationToken ct)
    {
        var labels = GetOptionLabels(question);
        if (labels.Count == 0)
            return await AskTextAsync(console, question, ct).ConfigureAwait(false);

        var choices = new List<string>(labels);
        if (question.AllowCustom)
            choices.Add(OtherChoice);

        var prompt = new SelectionPrompt<string>()
            .Title(BuildTitle(question))
            .PageSize(Math.Clamp(choices.Count, 1, MaxPageSize))
            .AddChoices(choices)
            .AddCancelResult(CancelSentinel);

        var selected = await prompt.ShowAsync(console, ct).ConfigureAwait(false);
        if (string.Equals(selected, CancelSentinel, StringComparison.Ordinal))
            return null;
        if (string.Equals(selected, OtherChoice, StringComparison.Ordinal))
            return await AskCustomAsync(console, question, ct).ConfigureAwait(false);

        return new QuestionAnswer
        {
            QuestionId = question.Id,
            SelectedLabels = new List<string> { selected },
        };
    }

    private static async Task<QuestionAnswer?> AskMultipleAsync(
        IAnsiConsole console,
        Question question,
        CancellationToken ct)
    {
        var labels = GetOptionLabels(question);
        if (labels.Count == 0)
            return await AskTextAsync(console, question, ct).ConfigureAwait(false);

        var choices = new List<string>(labels);
        if (question.AllowCustom)
            choices.Add(OtherChoice);

        var prompt = new MultiSelectionPrompt<string>()
            .Title(BuildTitle(question))
            .PageSize(Math.Clamp(choices.Count, 1, MaxPageSize))
            .AddChoices(choices)
            .AddCancelResult(new List<string> { CancelSentinel });

        if (!question.Required)
            prompt = prompt.NotRequired();

        var selected = await prompt.ShowAsync(console, ct).ConfigureAwait(false);
        if (selected is null || selected.Contains(CancelSentinel, StringComparer.Ordinal))
            return null;

        var answer = new QuestionAnswer
        {
            QuestionId = question.Id,
            SelectedLabels = selected
                .Where(label => !string.Equals(label, OtherChoice, StringComparison.Ordinal))
                .ToList(),
        };

        if (selected.Contains(OtherChoice, StringComparer.Ordinal))
        {
            var custom = await AskCustomAsync(console, question, ct).ConfigureAwait(false);
            if (custom is null)
                return null;
            answer.CustomAnswer = custom.CustomAnswer;
        }

        return answer;
    }

    private static async Task<QuestionAnswer?> AskTextAsync(
        IAnsiConsole console,
        Question question,
        CancellationToken ct,
        string? title = null)
    {
        var prompt = new TextPrompt<string>(title ?? BuildTitle(question));
        var defaultValue = ResolveDefaultText(question);
        if (!string.IsNullOrEmpty(defaultValue))
            prompt.DefaultValue(defaultValue).ShowDefaultValue(true);
        else if (!question.Required)
            prompt.AllowEmpty();

        string text;
        try
        {
            text = await prompt.ShowAsync(console, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        return new QuestionAnswer
        {
            QuestionId = question.Id,
            CustomAnswer = Truncate(text),
        };
    }

    private static async Task<QuestionAnswer?> AskCustomAsync(
        IAnsiConsole console,
        Question question,
        CancellationToken ct)
    {
        var prompt = new TextPrompt<string>($"{BuildTitle(question)} — 自定义回答");
        if (!question.Required)
            prompt.AllowEmpty();

        string text;
        try
        {
            text = await prompt.ShowAsync(console, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        return new QuestionAnswer
        {
            QuestionId = question.Id,
            CustomAnswer = Truncate(text),
        };
    }

    private static List<string> GetOptionLabels(Question question)
    {
        if (question.Options is null)
            return new List<string>();

        return question.Options
            .Where(option => option is not null && !string.IsNullOrWhiteSpace(option.Label))
            .Select(option => option.Label)
            .ToList();
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
