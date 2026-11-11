using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>工具产出内容块的类型常量。</summary>
public static class ToolOutputPartKind
{
    /// <summary>文本块。</summary>
    public const string Text = "text";

    /// <summary>图片块。</summary>
    public const string Image = "image";

    /// <summary>通用文件块。</summary>
    public const string File = "file";
}

/// <summary>
/// 工具产出的内容块（文本 / 图片 / 文件）。
/// <para>
/// <b>职责边界</b>：框架只负责传递与在 <c>Session → Chat</c> 转换时把
/// <see cref="LocalPath"/> 物化为可发送载荷；<b>如何出站</b>（内嵌、拆消息、聚合）由各 LLM client 自行决定。
/// </para>
/// <para>
/// <b>为什么要 LocalPath 而不是直接 base64</b>：会话文件是整文件重写，且每次工具状态迁移都会触发；
/// base64 落进会话会造成写放大（与图片大小无关）。工具图片一律先落盘，会话只存引用。
/// </para>
/// </summary>
public sealed record ToolOutputPart
{
    /// <summary>块类型：<see cref="ToolOutputPartKind"/> 中的一个。</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>文本内容（<see cref="ToolOutputPartKind.Text"/> 使用）。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>本地文件绝对路径。由框架在出站前物化为 base64，client 不应直接读盘。</summary>
    [JsonPropertyName("local_path")]
    public string? LocalPath { get; init; }

    /// <summary>内联 Base64（不含 <c>data:</c> 前缀）。与 <see cref="LocalPath"/> 二选一。</summary>
    [JsonPropertyName("data_base64")]
    public string? DataBase64 { get; init; }

    /// <summary>可公开访问的 URL（https 或 data:）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>MIME 类型，如 <c>image/png</c>。</summary>
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    /// <summary>原始文件名（用于展示与调试）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
