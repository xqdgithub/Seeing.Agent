using System.Text.Json;

namespace Seeing.Agent.Tools.SystemOne;

/// <summary>
/// systemone_* 工具入参的 JSON 形态归一化。
/// <para>
/// 模型（或上游网关）常把嵌套的对象/数组序列化成字符串，例如把 <c>questions</c>
/// 或 <c>state</c> 以 JSON 文本形式放进字符串。本类是**全工具唯一**处理
/// 「字符串化 JSON」的地方：只要某值的字符串内容能解析为对象/数组，就向内解包，
/// 从而让解析层只面对结构化元素。
/// </para>
/// </summary>
internal static class SystemOneJson
{
    /// <summary>字符串解包的最大层数，防御异常深的嵌套。</summary>
    public const int MaxUnwrapDepth = 8;

    /// <summary>
    /// 递归解包字符串化的 JSON：当前元素是字符串且其内容是合法 JSON（对象/数组/字符串）时
    /// 继续向内解包；普通文本与数字/布尔等标量保持原样。
    /// </summary>
    public static JsonElement Unwrap(JsonElement element)
    {
        var current = element;
        for (var depth = 0; depth < MaxUnwrapDepth; depth++)
        {
            if (current.ValueKind != JsonValueKind.String)
                return current;

            if (!TryParseJson(current.GetString(), out var parsed))
                return current;

            current = parsed;
        }

        return current;
    }

    /// <summary>解包后尝试取得对象。</summary>
    public static bool TryGetObject(JsonElement element, out JsonElement obj)
    {
        obj = Unwrap(element);
        if (obj.ValueKind == JsonValueKind.Object)
            return true;

        obj = default;
        return false;
    }

    /// <summary>解包后尝试取得数组。</summary>
    public static bool TryGetArray(JsonElement element, out JsonElement array)
    {
        array = Unwrap(element);
        if (array.ValueKind == JsonValueKind.Array)
            return true;

        array = default;
        return false;
    }

    /// <summary>
    /// 把 state 归一化为 SystemOne 请求可接受的值：文本字符串保持为 <see cref="string"/>；
    /// 字符串化的对象/数组解包为结构化 <see cref="JsonElement"/>；原生对象/数组原样保留。
    /// 无法解析的字符串按纯文本处理，避免把 "42"、"true" 这类误判为 JSON。
    /// </summary>
    public static object? NormalizeState(JsonElement element)
    {
        var value = Unwrap(element);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.Clone();
    }

    private static bool TryParseJson(string? text, out JsonElement parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        // 仅对象/数组/字符串算「可继续解包」；数字/布尔/null 视为普通文本，避免 "42" 被改写。
        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String))
            return false;

        parsed = root;
        return true;
    }
}
