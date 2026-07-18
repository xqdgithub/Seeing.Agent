using System.Text;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Helpers;

/// <summary>
/// 工具调用参数/结果展示格式化（供 ToolCallMessageComponent 使用）
/// </summary>
public sealed record ToolCallParameterEntry(string Name, string Value, bool IsLong);

public static class ToolCallDisplayFormatter
{
    private const int LongValueCharThreshold = 400;
    private const int LongValueLineThreshold = 8;

    private static readonly JsonSerializerOptions s_prettyOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions s_compactOptions = new()
    {
        WriteIndented = false
    };

    public static bool TryParseParameters(string? parametersJson, out IReadOnlyList<ToolCallParameterEntry> entries)
    {
        entries = Array.Empty<ToolCallParameterEntry>();
        if (string.IsNullOrWhiteSpace(parametersJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var list = new List<ToolCallParameterEntry>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = FormatPropertyValue(prop.Value);
                list.Add(new ToolCallParameterEntry(prop.Name, value, IsLongValue(value)));
            }

            if (list.Count == 0)
                return false;

            entries = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsLongValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.Length > LongValueCharThreshold)
            return true;

        var lines = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
            {
                lines++;
                if (lines > LongValueLineThreshold)
                    return true;
            }
        }

        return false;
    }

    public static string FormatJsonPretty(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, s_prettyOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static string FormatReadableContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return JsonSerializer.Serialize(doc.RootElement, s_prettyOptions);
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        return content;
    }

    public static string FormatKeyValueCopyText(IEnumerable<ToolCallParameterEntry> entries)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
                sb.Append('\n');
            first = false;
            sb.Append(entry.Name).Append(": ").Append(entry.Value);
        }

        return sb.ToString();
    }

    private static string FormatPropertyValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object or JsonValueKind.Array =>
            JsonSerializer.Serialize(element, s_compactOptions),
        _ => element.GetRawText()
    };
}
