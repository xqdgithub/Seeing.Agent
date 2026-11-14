using System.Text.Json;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 一条复盘建议。
/// </summary>
/// <param name="Kind">建议类型（如 <c>skill</c>）。</param>
/// <param name="Target">建议目标（技能名；须为英文 kebab-case）。</param>
/// <param name="SuggestedContent">建议写入的技能正文；可为空（例如仅提示改名）。</param>
/// <param name="Description">
/// 技能 frontmatter 描述：Agent 据此决定是否加载该技能。
/// 须写成「何时用 / 做什么」，不得写成会话回顾。
/// </param>
/// <param name="Reason">
/// 备注：为何值得沉淀、本会话里出现了什么信号等介绍性说明。
/// 只给人看，不写入技能描述。
/// </param>
/// <param name="Evidence">来自执行记录的具体依据。</param>
public sealed record ReflectionSuggestion(
    string Kind,
    string Target,
    string? SuggestedContent,
    string? Description,
    string? Reason,
    IReadOnlyList<string> Evidence);

/// <summary>
/// 复盘建议的解析器。
/// <para>
/// 刻意手写 <see cref="JsonDocument"/> 解析而不走 <c>JsonSerializer.Deserialize&lt;T&gt;</c>：
/// 模型的输出经常带围栏、带未知字段、个别条目畸形。反序列化会因一处畸形整批失败，
/// 而这里希望「好的条目留下来，坏的条目丢掉」。
/// </para>
/// <para>
/// 解析永不抛异常：调用方是后台链路，一条坏输出不应该让复盘任务崩掉。
/// </para>
/// </summary>
public static class Suggestions
{
    /// <summary>
    /// 解析模型输出为建议清单。任何无法解析的输入都返回空列表。
    /// </summary>
    public static IReadOnlyList<ReflectionSuggestion> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<ReflectionSuggestion>();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return Array.Empty<ReflectionSuggestion>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.Substring(start, end - start + 1));
        }
        catch (JsonException)
        {
            return Array.Empty<ReflectionSuggestion>();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<ReflectionSuggestion>();
            }

            // 先判 skip：这是模型表达「本轮没有建议」的唯一合法形态。
            if (root.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                && string.Equals(action.GetString(), "skip", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<ReflectionSuggestion>();
            }

            if (!root.TryGetProperty("suggestions", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ReflectionSuggestion>();
            }

            var result = new List<ReflectionSuggestion>();
            foreach (var item in items.EnumerateArray())
            {
                var suggestion = ParseOne(item);
                if (suggestion is not null)
                {
                    result.Add(suggestion);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 解析单条建议；缺字段或无依据时返回 <c>null</c>（整条丢弃）。
    /// <para>
    /// 必填：kind / target / description / evidence。
    /// reason（备注）可选——只给人读，不参与落盘描述。
    /// </para>
    /// </summary>
    private static ReflectionSuggestion? ParseOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var kind = ReadString(item, "kind");
        var target = ReadString(item, "target");
        var description = ReadString(item, "description");

        // 无类型、无目标、无技能描述的建议不可采纳，直接丢弃。
        // reason 是备注，缺了不拦；description 会进 SKILL.md，必须有。
        if (string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var evidence = ReadEvidence(item);
        if (evidence.Count == 0)
        {
            return null;
        }

        return new ReflectionSuggestion(
            Kind: kind!,
            Target: target!,
            SuggestedContent: ReadString(item, "suggestedContent"),
            Description: description!.Trim(),
            Reason: ReadString(item, "reason")?.Trim(),
            Evidence: evidence);
    }

    /// <summary>
    /// 读取字符串字段；不存在或类型不符时返回 <c>null</c>。
    /// </summary>
    private static string? ReadString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    /// <summary>
    /// 读取并清洗 evidence：跳过非字符串元素，去重且保留首次出现的顺序。
    /// </summary>
    private static List<string> ReadEvidence(JsonElement item)
    {
        var evidence = new List<string>();
        if (!item.TryGetProperty("evidence", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return evidence;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = entry.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (seen.Add(text))
            {
                evidence.Add(text);
            }
        }

        return evidence;
    }
}
