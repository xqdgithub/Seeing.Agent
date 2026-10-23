using System.Globalization;
using System.Text;

namespace Seeing.Agent.Tools.Support;

/// <summary>
/// 粘贴/拖拽/选择器文件的统一上传布局与 prompt 路径规则。
/// 存储根按会话有效根；prompt 路径按项目根相对化，项目根外使用绝对路径。
/// </summary>
public static class PastedFileLayout
{
    /// <summary>上传目录（相对会话有效根）。</summary>
    public const string RootDirectory = ".seeing/uploads";

    /// <summary>图片附件上限。</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>普通文件上传上限。</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>单次请求最多文件数。</summary>
    public const int MaxFilesPerRequest = 10;

    /// <summary>单次 HTTP 请求体上限。</summary>
    public const int MaxUploadRequestBytes = 100 * 1024 * 1024;

    private static readonly char[] ExtraInvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tif", ".tiff"
    };

    /// <summary>会话 id 只保留 ASCII 字母数字、下划线和连字符；空值回落 default。</summary>
    public static string SafeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "default";

        var builder = new StringBuilder(sessionId.Length);
        foreach (var ch in sessionId)
        {
            var safe = ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '-';
            builder.Append(safe ? ch : '_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }

    /// <summary>会话上传目录：<c>&lt;sessionRoot&gt;/.seeing/uploads/&lt;safeSessionId&gt;</c>。</summary>
    public static string BuildSessionDirectory(string sessionRoot, string sessionId)
        => Path.Combine(sessionRoot, ".seeing", "uploads", SafeSessionId(sessionId));

    /// <summary>生成统一存储文件名：原名 + 时间戳 + 短后缀。</summary>
    public static string BuildStoredFileName(string originalName, DateTimeOffset now, string suffix)
    {
        var name = Path.GetFileName(originalName ?? string.Empty);
        var extension = SanitizeExtension(Path.GetExtension(name));
        var baseName = SanitizeBaseName(Path.GetFileNameWithoutExtension(name));
        var safeSuffix = SanitizeSuffix(suffix);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseName}-{now:yyyyMMdd-HHmmss}-{safeSuffix}{extension}");
    }

    /// <summary>拼接会话上传目录下的绝对路径。</summary>
    public static string BuildAbsolutePath(string sessionRoot, string sessionId, string storedFileName)
        => Path.Combine(BuildSessionDirectory(sessionRoot, sessionId), storedFileName);

    /// <summary>判断路径是否在项目根内；是则返回正斜杠相对路径。</summary>
    public static bool TryGetProjectRelativePath(string projectRoot, string absolutePath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(absolutePath))
            return false;

        var root = TrimEndSeparators(Path.GetFullPath(projectRoot));
        var full = Path.GetFullPath(absolutePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (full.Equals(root, comparison))
        {
            relativePath = ".";
            return true;
        }

        var inside = full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        if (!inside)
            return false;

        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            return false;

        relativePath = relative;
        return true;
    }

    /// <summary>项目根内返回正斜杠相对路径；项目根外返回正斜杠绝对路径。</summary>
    public static string ToPromptPath(string projectRoot, string absolutePath)
        => TryGetProjectRelativePath(projectRoot, absolutePath, out var relative)
            ? relative
            : Path.GetFullPath(absolutePath).Replace('\\', '/');

    /// <summary>多个路径 → 每行一个反引号代码段。</summary>
    public static string FormatPromptBlock(IReadOnlyList<string> promptPaths)
        => string.Join('\n', promptPaths.Select(path => $"`{path}`"));

    /// <summary>把路径块追加到正文；与已有文本之间保证一个换行。</summary>
    public static string AppendPromptPaths(string text, IReadOnlyList<string> promptPaths)
    {
        if (promptPaths is not { Count: > 0 })
            return text;

        var block = FormatPromptBlock(promptPaths);
        return string.IsNullOrWhiteSpace(text) ? block : text.TrimEnd() + "\n" + block;
    }

    /// <summary>按扩展名判断是否图片。</summary>
    public static bool IsImageFile(string fileName)
        => ImageExtensions.Contains(Path.GetExtension(fileName));

    private static string SanitizeBaseName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return "pasted";

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var ch in ExtraInvalidFileNameChars)
            invalid.Add(ch);

        var builder = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);

        var sanitized = builder.ToString().Trim(' ', '.');
        if (sanitized.Length == 0)
            sanitized = "pasted";
        if (sanitized.Length > 80)
            sanitized = sanitized[..80];

        return sanitized;
    }

    private static string SanitizeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return "0000";

        var builder = new StringBuilder(8);
        foreach (var ch in suffix)
        {
            if (builder.Length == 8)
                break;
            if (char.IsAsciiLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.Length == 0 ? "0000" : builder.ToString();
    }

    private static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return string.Empty;

        var builder = new StringBuilder(extension.Length);
        foreach (var ch in extension)
        {
            if (builder.Length == 16)
                break;
            if (ch == '.' || char.IsAsciiLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.Length <= 1 ? string.Empty : builder.ToString();
    }

    private static string TrimEndSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
