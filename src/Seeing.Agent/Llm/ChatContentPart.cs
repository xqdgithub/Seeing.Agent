using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm;

/// <summary>
/// 单条消息中的内容段，用于多模态（文本、图片、文件、音频等）传输。
/// 与各家 Provider 的块结构对齐：OpenAI Chat Completions、Anthropic Messages 等。
/// </summary>
public class ChatContentPart
{
    public const string KindText = "text";
    public const string KindImage = "image";
    /// <summary>通用二进制文件（如 PDF），映射到 OpenAI file、Anthropic document。</summary>
    public const string KindFile = "file";
    /// <summary>显式文档块（Anthropic document）；若未使用可仅用 <see cref="KindFile"/> + application/pdf。</summary>
    public const string KindDocument = "document";
    public const string KindInputAudio = "input_audio";

    /// <summary>段类型：<see cref="KindText"/>、<see cref="KindImage"/>、<see cref="KindFile"/> 等。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = KindText;

    /// <summary>文本段内容。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>可公开访问的图片 URL（https）或 data: URL。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>内联 Base64 载荷（不含 data: 前缀）。</summary>
    [JsonPropertyName("data_base64")]
    public string? DataBase64 { get; set; }

    /// <summary>MIME，如 image/png、application/pdf、audio/wav。</summary>
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    /// <summary>原始文件名（文件段建议填写）。</summary>
    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    /// <summary>已上传文件的 Provider id（OpenAI <c>file-id</c>）。与 <see cref="DataBase64"/> 二选一。</summary>
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    /// <summary>图片细节：auto、low、high（主要供 OpenAI）。</summary>
    [JsonPropertyName("image_detail")]
    public string? ImageDetail { get; set; }

    public static ChatContentPart CreateText(string text) =>
        new() { Type = KindText, Text = text };

    public static ChatContentPart CreateImageFromUrl(string url, string? imageDetail = null) =>
        new() { Type = KindImage, Url = url, ImageDetail = imageDetail };

    public static ChatContentPart CreateImageFromBase64(string base64, string mimeType, string? imageDetail = null) =>
        new() { Type = KindImage, DataBase64 = base64, MimeType = mimeType, ImageDetail = imageDetail };

    public static ChatContentPart CreateFileFromBase64(string base64, string mimeType, string? fileName = null) =>
        new() { Type = KindFile, DataBase64 = base64, MimeType = mimeType, FileName = fileName };

    public static ChatContentPart CreateFileFromProviderId(string fileId) =>
        new() { Type = KindFile, FileId = fileId };

    public static ChatContentPart CreateInputAudioFromBase64(string base64, string mimeType) =>
        new() { Type = KindInputAudio, DataBase64 = base64, MimeType = mimeType };
}
