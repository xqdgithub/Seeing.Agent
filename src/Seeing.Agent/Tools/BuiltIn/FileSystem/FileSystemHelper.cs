using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 文件系统辅助类 - 提供文件操作的共享功能
    /// </summary>
    internal static class FileSystemHelper
    {
        /// <summary>默认读取行数限制</summary>
        public const int DefaultReadLimit = 2000;

        /// <summary>单行最大长度</summary>
        public const int MaxLineLength = 2000;

        /// <summary>最大字节限制 (50KB)</summary>
        public const int MaxBytes = 50 * 1024;

        /// <summary>单行截断后缀</summary>
        public static string MaxLineSuffix => $"... (行已截断至 {MaxLineLength} 字符)";

        /// <summary>二进制文件扩展名列表</summary>
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".tar", ".gz", ".exe", ".dll", ".so", ".class", ".jar", ".war", ".7z",
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
            ".bin", ".dat", ".obj", ".o", ".a", ".lib", ".wasm", ".pyc", ".pyo",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
            ".pdf", ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flv", ".mkv"
        };

        /// <summary>MIME 类型映射</summary>
        private static readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        /// <summary>
        /// 获取文件的 MIME 类型
        /// </summary>
        public static string GetMimeType(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
        }

        /// <summary>
        /// 检查是否为二进制文件（通过扩展名）
        /// </summary>
        public static bool IsBinaryByExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return BinaryExtensions.Contains(ext);
        }

        /// <summary>
        /// 检查是否为二进制文件（通过内容）
        /// </summary>
        public static bool IsBinaryByContent(string filePath, int sampleSize = 4096)
        {
            if (!File.Exists(filePath)) return false;

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0) return false;

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var bytesToRead = Math.Min(sampleSize, (int)fileInfo.Length);
                var buffer = new byte[bytesToRead];
                var bytesRead = stream.Read(buffer, 0, bytesToRead);

                if (bytesRead == 0) return false;

                // 检查是否有 null 字节
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0) return true;
                }

                // 计算非打印字符比例
                var nonPrintableCount = 0;
                for (var i = 0; i < bytesRead; i++)
                {
                    // ASCII: 9=tab, 10=lf, 13=cr, 其他控制字符（<32）为非打印
                    if (buffer[i] < 9 || (buffer[i] > 13 && buffer[i] < 32))
                    {
                        nonPrintableCount++;
                    }
                }

                // 如果超过30%为非打印字符，认为是二进制
                return nonPrintableCount / bytesRead > 0.3;
            }
            catch
            {
                return true; // 无法读取时假设为二进制
            }
        }

        /// <summary>
        /// 检查是否为图片文件
        /// </summary>
        public static bool IsImage(string filePath)
        {
            var mime = GetMimeType(filePath);
            return mime.StartsWith("image/") && mime != "image/svg+xml" && mime != "image/vnd.fastbidsheet";
        }

        /// <summary>
        /// 检查是否为 PDF 文件
        /// </summary>
        public static bool IsPdf(string filePath)
        {
            return GetMimeType(filePath) == "application/pdf";
        }

        /// <summary>
        /// 截断过长行
        /// </summary>
        public static string TruncateLine(string line)
        {
            return line.Length > MaxLineLength
                ? line.Substring(0, MaxLineLength) + MaxLineSuffix
                : line;
        }

        /// <summary>
        /// XML 转义
        /// </summary>
        public static string EscapeXml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>
        /// 规范化行尾（统一为 LF）
        /// </summary>
        public static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// 读取文件内容（带限制）
        /// </summary>
        public static (List<string> Lines, int TotalLines, bool Truncated, bool TruncatedByBytes) ReadFileWithLimit(
            string filePath,
            int offset = 1,
            int limit = DefaultReadLimit)
        {
            var lines = new List<string>();
            var totalLines = 0;
            var bytes = 0;
            var truncated = false;
            var truncatedByBytes = false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var startLine = offset - 1;

            while (true)
            {
                var line = reader.ReadLine();
                if (line == null) break;

                totalLines++;

                // 跳过 offset 前的行
                if (totalLines <= startLine) continue;

                // 已达到 limit
                if (lines.Count >= limit)
                {
                    truncated = true;
                    continue;
                }

                // 截断过长行
                var truncatedLine = TruncateLine(line);
                var lineSize = Encoding.UTF8.GetByteCount(truncatedLine) + (lines.Count > 0 ? 1 : 0);

                // 检查字节限制
                if (bytes + lineSize > MaxBytes)
                {
                    truncatedByBytes = true;
                    truncated = true;
                    break;
                }

                lines.Add(truncatedLine);
                bytes += lineSize;
            }

            return (lines, totalLines, truncated, truncatedByBytes);
        }

        /// <summary>
        /// 获取目录内容
        /// </summary>
        public static List<string> GetDirectoryEntries(string directoryPath)
        {
            var entries = new List<string>();
            var dirInfo = new DirectoryInfo(directoryPath);

            foreach (var item in dirInfo.GetFileSystemInfos())
            {
                if (item is DirectoryInfo)
                {
                    entries.Add(item.Name + "/");
                }
                else if (item is FileInfo fileInfo)
                {
                    // 检查是否为符号链接且指向目录
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        try
                        {
                            var target = ResolveSymbolicLink(fileInfo.FullName);
                            if (target != null && Directory.Exists(target))
                            {
                                entries.Add(item.Name + "/");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            // ✅ 符号链接解析失败时记录，不再静默吞掉
                            // 失败时当作普通文件处理是合理的业务逻辑
                            Debug.WriteLine($"[FileSystemHelper] 无法解析符号链接 '{item.Name}': {ex.Message}");
                        }
                    }
                    entries.Add(item.Name);
                }
            }

            entries.Sort();
            return entries;
        }

        /// <summary>
        /// 解析符号链接目标
        /// </summary>
        private static string? ResolveSymbolicLink(string path)
        {
            try
            {
                // Windows 上需要特殊处理
                if (OperatingSystem.IsWindows())
                {
                    using var handle = File.OpenRead(path);
                    // 无法直接获取目标，返回 null
                    return null;
                }
                else
                {
                    return UnixResolveSymbolicLink(path);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Unix 系统解析符号链接
        /// </summary>
        private static string? UnixResolveSymbolicLink(string path)
        {
            // 在非 Unix 系统上返回 null
            return null;
        }

        /// <summary>
        /// 验证路径是否在工作目录内（安全检查）
        /// </summary>
        public static bool IsPathWithinDirectory(string path, string baseDirectory)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullBase = Path.GetFullPath(baseDirectory);
                return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 查找相似的文件名建议
        /// </summary>
        public static List<string> FindSimilarFiles(string filePath, int maxSuggestions = 3)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath);

                if (!Directory.Exists(dir)) return new List<string>();

                var entries = Directory.GetFiles(dir)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name.ToLowerInvariant().Contains(fileName.ToLowerInvariant()) ||
                               fileName.ToLowerInvariant().Contains(name.ToLowerInvariant());
                    })
                    .Take(maxSuggestions)
                    .ToList();

                return entries;
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Glob 模式匹配（简化版，支持 * 和 **）
        /// </summary>
        public static bool MatchesGlobPattern(string path, string pattern)
        {
            // 将 glob 模式转换为正则表达式
            var regexPattern = ConvertGlobToRegex(pattern);
            return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 将 Glob 模式转换为正则表达式
        /// </summary>
        private static string ConvertGlobToRegex(string glob)
        {
            var regex = "^";
            foreach (var c in glob)
            {
                switch (c)
                {
                    case '*':
                        // ** 匹配任意深度目录
                        if (regex.EndsWith(".*"))
                        {
                            regex = regex.Substring(0, regex.Length - 1) + ".*";
                        }
                        else
                        {
                            regex += "[^/\\\\]*";
                        }
                        break;
                    case '?':
                        regex += ".";
                        break;
                    case '.':
                        regex += "\\.";
                        break;
                    case '/':
                    case '\\':
                        regex += "[/\\\\]";
                        break;
                    default:
                        if (char.IsLetterOrDigit(c))
                        {
                            regex += c;
                        }
                        else
                        {
                            regex += "\\" + c;
                        }
                        break;
                }
            }
            regex += "$";
            return regex;
        }

        /// <summary>
        /// 递归搜索匹配 Glob 模式的文件
        /// </summary>
        public static List<string> GlobSearch(string directory, string pattern, int limit = 100)
        {
            var files = new List<string>();

            try
            {
                // 处理不同类型的 glob 模式
                var regexPattern = ConvertGlobToRegex(pattern);

                SearchDirectory(directory, regexPattern, files, limit);
            }
            catch (Exception)
            {
                // 搜索失败，返回空列表
            }

            // 按修改时间排序
            return files
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();
        }

        /// <summary>
        /// 递归搜索目录
        /// </summary>
        private static void SearchDirectory(string directory, string regexPattern, List<string> files, int limit)
        {
            if (files.Count >= limit) return;

            try
            {
                var dirInfo = new DirectoryInfo(directory);

                // 搜索文件
                foreach (var file in dirInfo.GetFiles())
                {
                    if (files.Count >= limit) break;

                    if (Regex.IsMatch(file.FullName, regexPattern, RegexOptions.IgnoreCase))
                    {
                        files.Add(file.FullName);
                    }
                }

                // 递归搜索子目录
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    if (files.Count >= limit) break;

                    // 跳过隐藏目录（可选）
                    if (subDir.Name.StartsWith(".")) continue;

                    SearchDirectory(subDir.FullName, regexPattern, files, limit);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 跳过无权限目录
            }
            catch (DirectoryNotFoundException)
            {
                // 跳过不存在的目录
            }
        }

        /// <summary>
        /// Grep 搜索 - 在文件内容中搜索正则表达式模式
        /// </summary>
        public static List<GrepMatch> GrepSearch(
            string directory,
            string pattern,
            string? includePattern = null,
            int limit = 100)
        {
            var matches = new List<GrepMatch>();
            var regex = new Regex(pattern, RegexOptions.Compiled);

            try
            {
                SearchDirectoryForContent(directory, regex, includePattern, matches, limit);
            }
            catch (Exception)
            {
                // 搜索失败，返回已收集的结果
            }

            // 按文件修改时间排序
            return matches
                .OrderByDescending(m => m.ModTime)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// 在目录中搜索文件内容
        /// </summary>
        private static void SearchDirectoryForContent(
            string directory,
            Regex regex,
            string? includePattern,
            List<GrepMatch> matches,
            int limit)
        {
            if (matches.Count >= limit) return;

            try
            {
                var dirInfo = new DirectoryInfo(directory);
                var includeRegex = includePattern != null
                    ? new Regex(ConvertGlobToRegex(includePattern), RegexOptions.IgnoreCase | RegexOptions.Compiled)
                    : null;

                // 搜索文件
                foreach (var file in dirInfo.GetFiles())
                {
                    if (matches.Count >= limit) break;

                    // 检查 include 模式
                    if (includeRegex != null && !includeRegex.IsMatch(file.FullName))
                        continue;

                    // 跳过二进制文件
                    if (IsBinaryByExtension(file.FullName) || IsBinaryByContent(file.FullName))
                        continue;

                    // 搜索文件内容
                    SearchFileContent(file.FullName, regex, matches, limit);
                }

                // 递归搜索子目录
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    if (matches.Count >= limit) break;

                    // 跳过隐藏目录（可选）
                    if (subDir.Name.StartsWith(".")) continue;

                    SearchDirectoryForContent(subDir.FullName, regex, includePattern, matches, limit);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 跳过无权限目录
            }
            catch (DirectoryNotFoundException)
            {
                // 跳过不存在的目录
            }
        }

        /// <summary>
        /// 在单个文件中搜索内容
        /// </summary>
        private static void SearchFileContent(
            string filePath,
            Regex regex,
            List<GrepMatch> matches,
            int limit)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var modTime = fileInfo.LastWriteTimeUtc.Ticks;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var lineNum = 0;
                while (reader.ReadLine() != null)
                {
                    lineNum++;
                    if (matches.Count >= limit) break;
                }

                // 重置流
                stream.Position = 0;
                reader.DiscardBufferedData();

                lineNum = 0;
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;

                    lineNum++;

                    // 检查是否匹配
                    if (regex.IsMatch(line))
                    {
                        matches.Add(new GrepMatch
                        {
                            Path = filePath,
                            LineNum = lineNum,
                            LineText = TruncateLine(line),
                            ModTime = modTime
                        });

                        if (matches.Count >= limit) break;
                    }
                }
            }
            catch
            {
                // 文件读取失败，跳过
            }
        }

        /// <summary>
        /// 计算两字符串的 Levenshtein 距离
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            var matrix = new int[a.Length + 1, b.Length + 1];

            for (var i = 0; i <= a.Length; i++) matrix[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) matrix[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost
                    );
                }
            }

            return matrix[a.Length, b.Length];
        }
    }

    /// <summary>
    /// Grep 搜索匹配结果
    /// </summary>
    internal class GrepMatch
    {
        public string Path { get; set; } = string.Empty;
        public int LineNum { get; set; }
        public string LineText { get; set; } = string.Empty;
        public long ModTime { get; set; }
    }
}