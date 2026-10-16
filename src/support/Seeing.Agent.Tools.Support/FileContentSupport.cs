namespace Seeing.Agent.Tools.Support;

/// <summary>文件内容纯函数支持：读取限制、MIME、二进制判定。从 Tools.FileSystem.FileSystemHelper 上提。</summary>
public static class FileContentSupport
{
    /// <summary>默认读取行数限制。</summary>
    public const int DefaultReadLimit = 2000;

    /// <summary>单行最大长度。</summary>
    public const int MaxLineLength = 2000;

    /// <summary>最大字节限制 (50KB)。</summary>
    public const int MaxBytes = 50 * 1024;

    /// <summary>二进制文件扩展名列表。</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".gz", ".exe", ".dll", ".so", ".class", ".jar", ".war", ".7z",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".bin", ".dat", ".obj", ".o", ".a", ".lib", ".wasm", ".pyc", ".pyo",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
        ".pdf", ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flv", ".mkv"
    };

    /// <summary>MIME 类型映射。</summary>
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图片类型
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
        // PDF
        [".pdf"] = "application/pdf",
        // 文本类型
        [".txt"] = "text/plain",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".ts"] = "application/javascript",
        [".md"] = "text/markdown",
        [".cs"] = "text/x-csharp",
        [".py"] = "text/x-python",
        [".java"] = "text/x-java",
        [".go"] = "text/x-go",
        [".rs"] = "text/x-rust",
        [".c"] = "text/x-c",
        [".cpp"] = "text/x-c++",
        [".h"] = "text/x-c",
        [".hpp"] = "text/x-c++",
        [".sh"] = "text/x-shellscript",
        [".bash"] = "text/x-shellscript",
        [".yaml"] = "text/yaml",
        [".yml"] = "text/yaml",
        [".toml"] = "text/x-toml",
        [".ini"] = "text/x-ini",
        [".cfg"] = "text/x-ini",
        [".env"] = "text/plain"
    };

    /// <summary>按扩展名获取 MIME 类型。</summary>
    public static string GetMimeType(string filePath)
        => MimeTypes.TryGetValue(Path.GetExtension(filePath), out var mime) ? mime : "application/octet-stream";

    /// <summary>按扩展名判定是否二进制。</summary>
    public static bool IsBinaryByExtension(string filePath)
        => BinaryExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>归一化换行符为 '\n'。</summary>
    public static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
