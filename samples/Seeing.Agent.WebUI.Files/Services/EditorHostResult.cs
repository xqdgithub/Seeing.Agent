using System.Text.Json;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>编辑器命令执行结果。</summary>
public enum CommandOutcome { Ok, NoEditor, Failed }

/// <summary>宿主请求的编辑器侧结果（是否可用 + 已解析的 JSON 载荷，避免二次编码）。</summary>
public readonly record struct HostResponsePayload(bool Available, JsonElement? Payload)
{
    /// <summary>编辑器不可用（未打开/只读/非文本等）。</summary>
    public static HostResponsePayload Unavailable => new(false, null);

    /// <summary>请求成功；可选携带已解析的 JSON 载荷。</summary>
    public static HostResponsePayload Ok(JsonElement? payload = null) => new(true, payload);

    /// <summary>从 JSON 字符串解析载荷（空/解析失败视为无载荷）。</summary>
    public static HostResponsePayload OkFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new(true, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return new(true, doc.RootElement.Clone());
        }
        catch
        {
            return new(true, null);
        }
    }
}
