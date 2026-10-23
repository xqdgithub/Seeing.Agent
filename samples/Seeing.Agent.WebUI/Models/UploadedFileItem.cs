using System.Text.Json.Serialization;

namespace Seeing.Agent.WebUI.Models;

/// <summary>上传端点返回给前端的元数据（不含绝对路径）。</summary>
public sealed record UploadedFileItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sessionRelativePath")] string SessionRelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mimeType")] string MimeType);
