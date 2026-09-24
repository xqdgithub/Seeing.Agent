using System.Text.Json;
using Seeing.Agent.Abstractions.Questions;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// question 工具结果态展示投影：从通用 <see cref="ToolCallViewModel"/> 现算，只读、非权威。
/// 成功态完整呈现题面与全部选项（选中标记）；失败态仅呈现状态与说明。
/// </summary>
public sealed class QuestionResultCardModel
{
    private const string AnswersBegin = "<<<USER_ANSWERS_BEGIN>>>";
    private const string AnswersEnd = "<<<USER_ANSWERS_END>>>";

    /// <summary>
    /// 失败态文案 → 状态映射；文案来源见
    /// <c>src/capabilities/Seeing.Agent.Tools.Question/QuestionTool.cs</c>（FormatResult / ExecuteAsync）。
    /// 未匹配一律 <see cref="QuestionCardState.Failed"/>。
    /// </summary>
    private static readonly (string Text, QuestionCardState State)[] FailureTextMap =
    [
        ("用户取消了作答", QuestionCardState.Cancelled),
        ("用户未在限定时间内作答", QuestionCardState.Timeout),
        ("问题交互通道不可用", QuestionCardState.Unavailable),
        ("当前环境不支持交互提问", QuestionCardState.Unavailable),
        ("问题交互通道未接入", QuestionCardState.Unavailable)
    ];

    public required ToolCallViewModel ToolCall { get; init; }
    public QuestionCardState State { get; init; }
    public IReadOnlyList<QuestionResultItem> Items { get; init; } = Array.Empty<QuestionResultItem>();
    public string DisplayTitle { get; init; } = "提问";
    public string StateText { get; init; } = "";
    public string? FailureMessage { get; init; }

    public bool IsRunning => State == QuestionCardState.Running;

    /// <summary>状态徽标颜色单一来源：按结果态判定，避免原始 Status 与实际态不一致（如 success 无哨兵→Failed）。</summary>
    public string TagColor => State switch
    {
        QuestionCardState.Completed => "success",
        QuestionCardState.Running => "processing",
        QuestionCardState.Cancelled => "default",
        QuestionCardState.Timeout => "warning",
        QuestionCardState.Unavailable => "default",
        QuestionCardState.Failed => "error",
        _ => "default"
    };

    public static QuestionResultCardModel From(ToolCallViewModel toolCall)
    {
        var state = ResolveState(toolCall, out var failureMessage);
        var items = state == QuestionCardState.Completed
            ? BuildItems(toolCall)
            : Array.Empty<QuestionResultItem>();

        return new QuestionResultCardModel
        {
            ToolCall = toolCall,
            State = state,
            Items = items,
            DisplayTitle = state switch
            {
                QuestionCardState.Running => "等待你的回答",
                QuestionCardState.Completed => "已作答",
                _ => "未完成作答"
            },
            StateText = state switch
            {
                QuestionCardState.Running => "提问中",
                QuestionCardState.Completed => "完成",
                QuestionCardState.Cancelled => "已取消",
                QuestionCardState.Timeout => "超时",
                QuestionCardState.Unavailable => "不可用",
                _ => "失败"
            },
            FailureMessage = failureMessage
        };
    }

    private static QuestionCardState ResolveState(ToolCallViewModel toolCall, out string? failureMessage)
    {
        failureMessage = null;
        var status = toolCall.Status?.ToLowerInvariant() ?? "";

        if (status is "pending" or "running")
            return QuestionCardState.Running;

        if (status == "success")
        {
            if (HasAnswers(toolCall.Result))
                return QuestionCardState.Completed;
            failureMessage = toolCall.Error;
            return QuestionCardState.Failed;
        }

        failureMessage = toolCall.Error;

        if (status is "cancelled" or "rejected")
            return QuestionCardState.Cancelled;

        if (status == "failed")
        {
            var error = toolCall.Error ?? "";
            foreach (var (text, state) in FailureTextMap)
            {
                if (error.Contains(text, StringComparison.Ordinal))
                    return state;
            }
            return QuestionCardState.Failed;
        }

        return QuestionCardState.Failed;
    }

    private static bool HasAnswers(string? result)
    {
        if (string.IsNullOrEmpty(result)) return false;
        var begin = result.IndexOf(AnswersBegin, StringComparison.Ordinal);
        var end = result.IndexOf(AnswersEnd, StringComparison.Ordinal);
        if (begin < 0 || end <= begin) return false;
        var json = result[(begin + AnswersBegin.Length)..end].Trim();
        if (json.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<QuestionResultItem> BuildItems(ToolCallViewModel toolCall)
    {
        var questionMeta = ParseQuestions(toolCall.Parameters);
        if (questionMeta.Count == 0)
            return Array.Empty<QuestionResultItem>();

        var answers = ParseAnswers(toolCall.Result);
        var items = new List<QuestionResultItem>(questionMeta.Count);
        foreach (var q in questionMeta)
        {
            answers.TryGetValue(q.Id, out var answer);
            var selected = answer?.SelectedLabels ?? new List<string>();
            var custom = string.IsNullOrWhiteSpace(answer?.CustomAnswer) ? null : answer!.CustomAnswer;

            var options = q.Options
                .Select(o => new QuestionResultOption
                {
                    Label = o.Label,
                    Description = o.Description,
                    IsSelected = selected.Contains(o.Label, StringComparer.Ordinal)
                })
                .ToList();

            items.Add(new QuestionResultItem
            {
                Id = q.Id,
                Header = q.Header,
                QuestionText = q.QuestionText,
                Kind = q.Kind,
                Options = options,
                CustomAnswer = custom,
                IsAnswered = selected.Count > 0 || custom is not null
            });
        }

        return items;
    }

    private static List<QuestionMeta> ParseQuestions(string? parameters)
    {
        var result = new List<QuestionMeta>();
        if (string.IsNullOrWhiteSpace(parameters))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(parameters);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("questions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var id = GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                result.Add(new QuestionMeta
                {
                    Id = id!,
                    Header = GetString(item, "header") ?? "",
                    QuestionText = GetString(item, "question") ?? "",
                    Kind = ParseKind(GetString(item, "kind")),
                    Options = ParseOptions(item)
                });
            }
        }
        catch (JsonException)
        {
            // 参数非合法 JSON：退化为无题面
        }

        return result;
    }

    private static List<QuestionOptionMeta> ParseOptions(JsonElement question)
    {
        var options = new List<QuestionOptionMeta>();
        if (!question.TryGetProperty("options", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var opt in arr.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object)
                continue;
            var label = GetString(opt, "label");
            if (string.IsNullOrEmpty(label))
                continue;
            options.Add(new QuestionOptionMeta
            {
                Label = label!,
                Description = GetString(opt, "description")
            });
        }

        return options;
    }

    private static Dictionary<string, AnswerMeta> ParseAnswers(string? result)
    {
        var map = new Dictionary<string, AnswerMeta>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(result))
            return map;

        var begin = result.IndexOf(AnswersBegin, StringComparison.Ordinal);
        var end = result.IndexOf(AnswersEnd, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
            return map;

        var json = result[(begin + AnswersBegin.Length)..end].Trim();
        if (json.Length == 0)
            return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var id = GetString(item, "questionId");
                if (string.IsNullOrEmpty(id))
                    continue;

                var selected = new List<string>();
                if (item.TryGetProperty("selectedLabels", out var sel) && sel.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sel.EnumerateArray())
                    {
                        if (s.GetString() is { Length: > 0 } value)
                            selected.Add(value);
                    }
                }

                map[id!] = new AnswerMeta
                {
                    SelectedLabels = selected,
                    CustomAnswer = GetString(item, "customAnswer")
                };
            }
        }
        catch (JsonException)
        {
            // 忽略：无有效答案
        }

        return map;
    }

    private static QuestionKind ParseKind(string? kind) => kind switch
    {
        "multiple" => QuestionKind.Multiple,
        "text" => QuestionKind.Text,
        _ => QuestionKind.Single
    };

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class QuestionMeta
    {
        public required string Id { get; init; }
        public string Header { get; init; } = "";
        public string QuestionText { get; init; } = "";
        public QuestionKind Kind { get; init; }
        public List<QuestionOptionMeta> Options { get; init; } = new();
    }

    private sealed class QuestionOptionMeta
    {
        public required string Label { get; init; }
        public string? Description { get; init; }
    }

    private sealed class AnswerMeta
    {
        public List<string> SelectedLabels { get; init; } = new();
        public string? CustomAnswer { get; init; }
    }
}

/// <summary>结果态卡片状态。</summary>
public enum QuestionCardState { Completed, Running, Cancelled, Timeout, Unavailable, Failed }

/// <summary>单题结果投影：题面 + 全部选项（选中标记）+ 自定义答案。</summary>
public sealed class QuestionResultItem
{
    public required string Id { get; init; }
    public string Header { get; init; } = "";
    public string QuestionText { get; init; } = "";
    public QuestionKind Kind { get; init; }
    public IReadOnlyList<QuestionResultOption> Options { get; init; } = Array.Empty<QuestionResultOption>();
    public string? CustomAnswer { get; init; }
    public bool IsAnswered { get; init; }
}

/// <summary>选项投影。</summary>
public sealed class QuestionResultOption
{
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool IsSelected { get; init; }
}
