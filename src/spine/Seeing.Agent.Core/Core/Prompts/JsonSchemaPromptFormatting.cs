using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 将 JSON Schema 属性格式化为提示词片段（容忍标准 schema：object 级 required 数组、type 联合数组）。
/// </summary>
internal static class JsonSchemaPromptFormatting
{
    public static HashSet<string> ReadRequiredNames(JsonElement schemaObject)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (schemaObject.ValueKind != JsonValueKind.Object)
            return set;

        if (!schemaObject.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
            return set;

        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name))
                    set.Add(name);
            }
        }

        return set;
    }

    public static string FormatProperty(string propertyName, JsonElement propertySchema, ISet<string> requiredNames)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object)
            return "未知类型";

        var sb = new StringBuilder();
        sb.Append(FormatType(propertySchema));

        if (propertySchema.TryGetProperty("description", out var descElement) &&
            descElement.ValueKind == JsonValueKind.String)
        {
            var desc = descElement.GetString();
            if (!string.IsNullOrEmpty(desc))
                sb.Append($" - {desc}");
        }

        if (IsPropertyRequired(propertyName, propertySchema, requiredNames))
            sb.Append(" (必需)");

        return sb.ToString();
    }

    private static bool IsPropertyRequired(
        string propertyName,
        JsonElement propertySchema,
        ISet<string> requiredNames)
    {
        if (requiredNames.Contains(propertyName))
            return true;

        // 非标准：属性上写 "required": true（历史测试/部分生成器）
        if (propertySchema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.True)
            return true;

        // 属性上的 "required": ["…"] 是嵌套 object 的子字段清单，不表示本属性必需
        return false;
    }

    private static string FormatType(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out var typeElement))
            return "unknown";

        return typeElement.ValueKind switch
        {
            JsonValueKind.String => typeElement.GetString() ?? "unknown",
            JsonValueKind.Array => FormatTypeArray(typeElement),
            _ => "unknown"
        };
    }

    private static string FormatTypeArray(JsonElement typeElement)
    {
        var parts = typeElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToArray();
        return parts.Length == 0 ? "unknown" : string.Join("|", parts);
    }
}
