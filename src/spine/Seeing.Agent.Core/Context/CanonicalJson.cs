using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 规范化 JSON（diff 与持久化的字节稳定形式）。
/// <para>规范化规则：对象键按 ordinal 升序递归输出；数组保持顺序；不含任何空白；
/// 字符串使用标准转义（"、\ 与控制字符转义，非 ASCII 原样 UTF-8）；数字保留原始表示
/// （<see cref="JsonElement.GetRawText"/>，避免精度改写）；未定义类型抛
/// <see cref="NotSupportedException"/>。</para>
/// <para>只做序列化：值的哈希由装配器按<b>文本</b>计算（同值重渲染比对用的就是渲染文本的哈希）。</para>
/// </summary>
internal static class CanonicalJson
{
    // 非 ASCII 原样输出；仍转义引号、反斜杠与控制字符。
    private static readonly JavaScriptEncoder s_encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// 输出规范化 JSON 文本。
    /// </summary>
    public static string Serialize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => SerializeObject(value),
        JsonValueKind.Array => SerializeArray(value),
        JsonValueKind.String => EncodeString(value.GetString() ?? string.Empty),
        // 保留原始数字表示（如 1.0 / 1e3），不做精度或格式改写。
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new NotSupportedException(
            $"无法规范化未定义的 JSON 类型：{value.ValueKind}")
    };

    private static string SerializeObject(JsonElement value)
    {
        // 稳定排序：按 ordinal 升序；重名键（非标准 JSON）保持输入相对顺序。
        var properties = value.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append('{');
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(EncodeString(properties[i].Name));
            builder.Append(':');
            builder.Append(Serialize(properties[i].Value));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static string SerializeArray(JsonElement value)
    {
        var builder = new StringBuilder();
        builder.Append('[');

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (index++ > 0)
                builder.Append(',');

            builder.Append(Serialize(item));
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static string EncodeString(string value) =>
        // EncodedUtf8Bytes 只是转义后的正文，两侧引号需自行补齐。
        $"\"{Encoding.UTF8.GetString(JsonEncodedText.Encode(value, s_encoder).EncodedUtf8Bytes)}\"";
}
