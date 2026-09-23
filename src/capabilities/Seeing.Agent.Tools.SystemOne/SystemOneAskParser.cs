using System.Text.Json;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.Core.Tools.SystemOne;

/// <summary>systemone_ask 工具入参（解析结果）。</summary>
internal sealed class SystemOneAskInput
{
    public string State { get; init; } = string.Empty;
    public string? Model { get; init; }
    public IReadOnlyList<SystemOneQuestionSpec> Questions { get; init; } = Array.Empty<SystemOneQuestionSpec>();
}

/// <summary>单个考察题的题面。</summary>
internal sealed class SystemOneQuestionSpec
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public IReadOnlyList<SystemOneOptionSpec> Options { get; init; } = Array.Empty<SystemOneOptionSpec>();
    public IReadOnlyList<string> Levels { get; init; } = Array.Empty<string>();
    public string? CriteriaTrue { get; init; }
    public string? CriteriaFalse { get; init; }
}

/// <summary>choice 题的一个候选选项。</summary>
internal sealed class SystemOneOptionSpec
{
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
}

/// <summary>
/// 解析并校验 <c>systemone_ask</c> 的 JSON 入参。
/// </summary>
internal static class SystemOneAskParser
{
    public const int MaxQuestions = 20;
    public const int MinChoiceOptions = 2;
    public const int MaxChoiceOptions = 50;
    public const int MinScoreLevels = 2;
    public const int MaxScoreLevels = 10;

    private const string TypeFieldUsageHint = "（正确用法：choice 只给 options；score 只给 levels；noul 两者都不给）";

    /// <summary>解析入参；失败时返回 false 并给出错误原因。</summary>
    public static bool TryParse(JsonElement arguments, out SystemOneAskInput input, out string? error)
    {
        input = null!;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "参数必须是 JSON 对象";
            return false;
        }

        var state = GetString(arguments, "state");
        if (string.IsNullOrWhiteSpace(state))
        {
            error = "参数 state 缺失或为空";
            return false;
        }

        if (!arguments.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            error = "参数 questions 缺失或不是数组";
            return false;
        }

        var count = array.GetArrayLength();
        if (count < 1)
        {
            error = "至少需要提供 1 个问题";
            return false;
        }

        if (count > MaxQuestions)
        {
            error = $"问题数量超限（{count}），最多 {MaxQuestions} 个";
            return false;
        }

        var questions = new List<SystemOneQuestionSpec>(count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "questions 中每个问题必须是对象";
                return false;
            }

            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "问题 id 不能为空";
                return false;
            }

            if (!seenIds.Add(id))
            {
                error = $"问题 id 重复：{id}";
                return false;
            }

            var rawType = GetString(item, "type");
            var type = rawType?.Trim().ToLowerInvariant();
            if (type is not (SystemOneQuestionTypes.Noul or SystemOneQuestionTypes.Choice or SystemOneQuestionTypes.Score))
            {
                error = $"问题 {id} 的 type 非法：{rawType}（可选 noul/choice/score）";
                return false;
            }

            var instructions = GetString(item, "instructions");
            if (string.IsNullOrWhiteSpace(instructions))
            {
                error = $"问题 {id} 的 instructions 不能为空";
                return false;
            }

            if (!TryParseOptions(item, id, out var options, out error))
                return false;

            var levels = GetStringList(item, "levels", out var levelsAllStrings);

            if (type == SystemOneQuestionTypes.Choice)
            {
                if (options.Count < MinChoiceOptions || options.Count > MaxChoiceOptions)
                {
                    error = $"choice 题 {id} 需要 {MinChoiceOptions}-{MaxChoiceOptions} 个 options（当前 {options.Count}）";
                    return false;
                }

                var seenLabels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in options)
                {
                    if (!seenLabels.Add(option.Label))
                    {
                        error = $"choice 题 {id} 的 options 存在重复 label：{option.Label}";
                        return false;
                    }
                }

                if (levels.Count > 0)
                {
                    error = $"choice 题 {id} 不得提供 levels{TypeFieldUsageHint}";
                    return false;
                }
            }
            else if (type == SystemOneQuestionTypes.Score)
            {
                if (!levelsAllStrings)
                {
                    error = $"score 题 {id} 的 levels 必须全部为字符串";
                    return false;
                }

                if (levels.Count < MinScoreLevels || levels.Count > MaxScoreLevels)
                {
                    error = $"score 题 {id} 需要 {MinScoreLevels}-{MaxScoreLevels} 个 levels（当前 {levels.Count}）";
                    return false;
                }

                if (options.Count > 0)
                {
                    error = $"score 题 {id} 不得提供 options{TypeFieldUsageHint}";
                    return false;
                }
            }
            else
            {
                if (options.Count > 0)
                {
                    error = $"noul 题 {id} 不得提供 options{TypeFieldUsageHint}";
                    return false;
                }

                if (levels.Count > 0)
                {
                    error = $"noul 题 {id} 不得提供 levels{TypeFieldUsageHint}";
                    return false;
                }
            }

            questions.Add(new SystemOneQuestionSpec
            {
                Id = id,
                Type = type,
                Instructions = instructions,
                Options = options,
                Levels = levels,
                CriteriaTrue = GetString(item, "criteriaTrue"),
                CriteriaFalse = GetString(item, "criteriaFalse")
            });
        }

        input = new SystemOneAskInput
        {
            State = state,
            Model = GetString(arguments, "model"),
            Questions = questions
        };
        return true;
    }

    private static bool TryParseOptions(
        JsonElement item, string questionId, out List<SystemOneOptionSpec> options, out string? error)
    {
        options = new List<SystemOneOptionSpec>();
        error = null;

        if (!item.TryGetProperty("options", out var raw) || raw.ValueKind == JsonValueKind.Null)
            return true;

        if (raw.ValueKind != JsonValueKind.Array)
        {
            error = $"问题 {questionId} 的 options 必须是数组";
            return false;
        }

        foreach (var option in raw.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                error = $"问题 {questionId} 的选项必须是对象";
                return false;
            }

            var label = GetString(option, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                error = $"问题 {questionId} 存在缺少 label 的选项";
                return false;
            }

            options.Add(new SystemOneOptionSpec
            {
                Label = label,
                Description = GetString(option, "description")
            });
        }

        return true;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> GetStringList(JsonElement element, string property, out bool allStrings)
    {
        var list = new List<string>();
        allStrings = true;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                list.Add(text);
            else
                allStrings = false;
        }

        return list;
    }
}
