using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Paths;
using System.Text;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.FileSystem
{
    /// <summary>
    /// 文件系统辅助类 - 提供文件操作的共享功能
    /// </summary>
    public static class FileSystemHelper
    {
        /// <summary>默认读取行数限制</summary>
        public const int DefaultReadLimit = 2000;

        /// <summary>单行最大长度</summary>
        public const int MaxLineLength = 2000;

        /// <summary>最大字节限制 (50KB)</summary>
        public const int MaxBytes = 50 * 1024;

        /// <summary>用户正则匹配超时（ReDoS 防护）。</summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>单行截断后缀</summary>
        public static string MaxLineSuffix => $"... (行已截断至 {MaxLineLength} 字符)";

        /// <summary>平台相关的路径比较（Windows/macOS 大小写不敏感，Linux 敏感）。委托唯一实现。</summary>
        internal static StringComparison PathComparison => PathBoundary.PathComparison;

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
        /// 判断路径是否为目录：不存在或不可枚举时返回 false。
        /// </summary>
        public static bool IsDirectory(IFileSystem fileSystem, string path)
        {
            if (!fileSystem.Exists(path))
            {
                return false;
            }

            try
            {
                using var enumerator = fileSystem.EnumerateFiles(path, "*", recursive: false).GetEnumerator();
                _ = enumerator.MoveNext();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return false;
            }
        }

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
        public static bool IsBinaryByContent(IFileSystem fileSystem, string filePath, int sampleSize = 4096)
        {
            if (!fileSystem.Exists(filePath)) return false;

            try
            {
                using var stream = fileSystem.OpenRead(filePath);
                if (stream.Length == 0) return false;

                var bytesToRead = (int)Math.Min(sampleSize, stream.Length);
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
                return nonPrintableCount / (double)bytesRead > 0.3;
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
            IFileSystem fileSystem,
            string filePath,
            int offset = 1,
            int limit = DefaultReadLimit)
        {
            var lines = new List<string>();
            var totalLines = 0;
            var bytes = 0;
            var truncated = false;
            var truncatedByBytes = false;

            using var stream = fileSystem.OpenRead(filePath);
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
        public static List<string> GetDirectoryEntries(IFileSystem fileSystem, string directoryPath)
        {
            var entries = new List<string>();

            foreach (var dir in fileSystem.EnumerateDirectories(directoryPath, "*", recursive: false))
            {
                entries.Add(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
            }

            foreach (var file in fileSystem.EnumerateFiles(directoryPath, "*", recursive: false))
            {
                entries.Add(Path.GetFileName(file));
            }

            entries.Sort();
            return entries;
        }

        /// <summary>
        /// 验证路径是否在指定目录内 — 委托给唯一实现 <see cref="PathBoundary"/>（含符号链接真实路径解析）。
        /// <para>不要在此重新实现边界逻辑：该算法一旦分叉，安全语义就会在工具 / Skills / ACP 之间不一致。</para>
        /// </summary>
        public static bool IsPathWithinDirectory(string path, string baseDirectory)
            => PathBoundary.IsWithin(path, baseDirectory);
        /// <summary>
        /// 查找相似的文件名建议
        /// </summary>
        public static List<string> FindSimilarFiles(IFileSystem fileSystem, string filePath, int maxSuggestions = 3)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath);

                if (string.IsNullOrEmpty(dir) || !fileSystem.Exists(dir)) return new List<string>();

                var entries = fileSystem.EnumerateFiles(dir, "*", recursive: false)
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
        /// Glob 模式匹配（语义见 <see cref="GlobMatcher"/>）。
        /// <para>
        /// <paramref name="path"/> 应为「相对搜索根的路径」或「文件名」：
        /// 含 <c>/</c> 的模式匹配相对路径，不含 <c>/</c> 的模式匹配文件名。
        /// 传入绝对路径将不匹配任何相对模式。
        /// </para>
        /// </summary>
        public static bool MatchesGlobPattern(string path, string pattern)
        {
            var caseSensitive = !IsCaseInsensitivePathComparison();
            return GlobMatcher.Matches(path, pattern, caseSensitive);
        }

        /// <summary>按当前平台路径比较语义决定 glob 是否大小写敏感（与 rg 侧 --glob-case-insensitive 同源）。</summary>
        internal static bool IsCaseInsensitivePathComparison()
            => PathComparison == StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// 递归搜索目录。
        /// </summary>
        /// <param name="root">搜索根（用于计算相对路径与判重）。</param>
        /// <param name="directory">当前遍历目录。</param>
        /// <param name="pattern">glob 模式。</param>
        /// <param name="caseSensitive">是否大小写敏感。</param>
        /// <param name="includeHidden">是否包含隐藏项（对齐 rg 的 <c>--hidden</c>）。</param>
        private static void SearchDirectory(
            IFileSystem fileSystem,
            string root,
            string directory,
            string pattern,
            bool caseSensitive,
            bool includeHidden,
            List<string> files,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (files.Count >= limit) return;

            try
            {
                // 搜索文件
                foreach (var file in fileSystem.EnumerateFiles(directory, "*", recursive: false))
                {
                    if (files.Count >= limit) break;
                    cancellationToken.ThrowIfCancellationRequested();

                    // 跳过隐藏文件（对齐 rg 默认：不输出隐藏项）
                    if (!includeHidden && fileSystem.IsHidden(file)) continue;

                    if (GlobMatcher.Matches(ToMatchTarget(root, file, pattern), pattern, caseSensitive))
                        files.Add(file);
                }

                // 递归搜索子目录
                foreach (var subDir in fileSystem.EnumerateDirectories(directory, "*", recursive: false))
                {
                    if (files.Count >= limit) break;
                    cancellationToken.ThrowIfCancellationRequested();

                    // 跳过隐藏目录（对齐 rg 默认）
                    if (!includeHidden && fileSystem.IsHidden(subDir)) continue;

                    SearchDirectory(fileSystem, root, subDir, pattern, caseSensitive, includeHidden, files, limit, cancellationToken);
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
            catch (IOException)
            {
                // 跳过不可枚举目录
            }
        }

        /// <summary>
        /// 计算 glob 匹配对象：含 <c>/</c> 的模式用「相对搜索根的路径」，否则用文件名。
        /// </summary>
        private static string ToMatchTarget(string root, string file, string pattern)
        {
            if (!GlobMatcher.RequiresRelativePath(pattern))
                return Path.GetFileName(file);

            try
            {
                return Path.GetRelativePath(root, file).Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                // 不同驱动器等无法求相对路径的情况：退回文件名
                return Path.GetFileName(file);
            }
        }

        /// <summary>
        /// 递归搜索匹配 Glob 模式的文件。
        /// <para>托管回退可能遍历整棵树（数十万文件、分钟级），因此必须响应
        /// <paramref name="cancellationToken"/>，否则用户取消无法中断搜索。</para>
        /// <para>
        /// 匹配对象为「相对 <paramref name="directory"/> 的路径」（归一为 <c>/</c>），
        /// 不含 <c>/</c> 的模式由 <see cref="GlobMatcher"/> 按 basename 解释。
        /// 返回值恒为绝对路径，与 rg 后端一致。
        /// </para>
        /// </summary>
        public static List<string> GlobSearch(
            IFileSystem fileSystem,
            string directory,
            string pattern,
            int limit = 100,
            CancellationToken cancellationToken = default,
            bool includeHidden = false)
        {
            var files = new List<string>();
            var caseSensitive = !IsCaseInsensitivePathComparison();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                SearchDirectory(fileSystem, directory, directory, pattern, caseSensitive, includeHidden, files, limit, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消必须穿透：托管遍历可能持续数分钟，吞掉取消会让整个会话无法中断
                throw;
            }
            catch (Exception)
            {
                // 搜索失败，返回空列表
            }

            // 按修改时间排序
            return files
                .OrderByDescending(f => fileSystem.GetLastWriteTimeUtc(f))
                .ToList();
        }

        /// <summary>
        /// Grep 搜索 - 在文件内容中搜索正则表达式模式。
        /// <para>取消必须穿透到遍历循环，不能当作"搜索失败"静默返回部分结果。</para>
        /// </summary>
        public static List<GrepMatch> GrepSearch(
            IFileSystem fileSystem,
            string directory,
            string pattern,
            string? includePattern = null,
            int limit = 100,
            CancellationToken cancellationToken = default,
            bool includeHidden = false)
        {
            var matches = new List<GrepMatch>();
            var regex = new Regex(pattern, RegexOptions.Compiled, RegexTimeout);
            var caseSensitive = !IsCaseInsensitivePathComparison();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchDirectoryForContent(
                    fileSystem, directory, directory, regex, includePattern, caseSensitive, includeHidden, matches, limit, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消必须穿透：不能把中断当成"搜索失败"静默返回部分结果
                throw;
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
        /// 在目录中搜索文件内容。
        /// <para><paramref name="includePattern"/> 经 <see cref="GlobMatcher"/> 解释，与 rg 的 <c>-g</c> 语义一致。</para>
        /// </summary>
        private static void SearchDirectoryForContent(
            IFileSystem fileSystem,
            string root,
            string directory,
            Regex regex,
            string? includePattern,
            bool caseSensitive,
            bool includeHidden,
            List<GrepMatch> matches,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (matches.Count >= limit) return;

            try
            {
                var hasInclude = !string.IsNullOrEmpty(includePattern);

                // 搜索文件
                foreach (var file in fileSystem.EnumerateFiles(directory, "*", recursive: false))
                {
                    if (matches.Count >= limit) break;
                    cancellationToken.ThrowIfCancellationRequested();

                    // 跳过隐藏文件（对齐 rg 默认）
                    if (!includeHidden && fileSystem.IsHidden(file)) continue;

                    // 检查 include 模式（对象规则与 glob 工具一致）
                    if (hasInclude && !GlobMatcher.Matches(ToMatchTarget(root, file, includePattern!), includePattern!, caseSensitive))
                        continue;

                    // 跳过二进制文件
                    if (IsBinaryByExtension(file) || IsBinaryByContent(fileSystem, file))
                        continue;

                    // 搜索文件内容
                    SearchFileContent(fileSystem, file, regex, matches, limit, cancellationToken);
                }

                // 递归搜索子目录
                foreach (var subDir in fileSystem.EnumerateDirectories(directory, "*", recursive: false))
                {
                    if (matches.Count >= limit) break;
                    cancellationToken.ThrowIfCancellationRequested();

                    // 跳过隐藏目录（对齐 rg 默认）
                    if (!includeHidden && fileSystem.IsHidden(subDir)) continue;

                    SearchDirectoryForContent(
                        fileSystem, root, subDir, regex, includePattern, caseSensitive, includeHidden, matches, limit, cancellationToken);
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
            catch (IOException)
            {
                // 跳过不可枚举目录
            }
        }

        /// <summary>
        /// 在单个文件中搜索内容
        /// </summary>
        private static void SearchFileContent(
            IFileSystem fileSystem,
            string filePath,
            Regex regex,
            List<GrepMatch> matches,
            int limit,
            CancellationToken cancellationToken)
        {
            try
            {
                var modTime = fileSystem.GetLastWriteTimeUtc(filePath).Ticks;

                using var stream = fileSystem.OpenRead(filePath);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var lineNum = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消不是"读取失败"：必须向上穿透
                throw;
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
    public class GrepMatch
    {
        public string Path { get; set; } = string.Empty;
        public int LineNum { get; set; }
        public string LineText { get; set; } = string.Empty;
        public long ModTime { get; set; }
    }
}