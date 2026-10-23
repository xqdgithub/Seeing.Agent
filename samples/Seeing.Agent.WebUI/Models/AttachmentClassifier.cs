namespace Seeing.Agent.WebUI.Models;

/// <summary>附件类型判定：MIME 优先，扩展名兜底。</summary>
public static class AttachmentClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tif", ".tiff"
    };

    /// <summary>是否图片。</summary>
    public static bool IsImage(string? contentType, string fileName)
        => (!string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
           || ImageExtensions.Contains(Path.GetExtension(fileName));
}
