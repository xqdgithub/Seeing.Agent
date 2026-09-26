using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem
{
    /// <summary>
    /// 读取文件/目录工具
    /// <para>
    /// 读取文件或目录的内容。支持 offset 和 limit 参数分页读取大文件。
    /// 自动检测和处理二进制文件、图片、PDF 文件。
    /// </para>
    /// <para>自身已实现 2000 行/50KB 限制与 offset/limit 分页，声明 output.skip=true
    /// 豁免全局输出限制，避免行号前缀导致的边界冗余落盘。</para>
    /// </summary>
    [ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
    public class ReadTool : BuiltInToolBase
    {
        private readonly IFileSystem _fileSystem;
        private readonly IWorkspacePathGate _pathGate;

        /// <summary>
        /// 创建 ReadTool 实例
        /// </summary>
        public ReadTool(
            ILogger<ReadTool> logger,
            IFileSystem fileSystem,
            IWorkspacePathGate pathGate) : base(logger)
        {
            _fileSystem = fileSystem;
            _pathGate = pathGate;
        }

        public override string Id => "read";

        public override string Description =>
            "读取文件或目录内容。\n\n" +
            "可以读取文件或目录。返回带行号的文件内容，或目录中的条目列表。\n" +
            "使用 offset 和 limit 参数分页读取大文件。\n" +
            "自动检测二进制文件、图片、PDF 文件并返回附件而非文本内容。";

        public override ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => BuildParametersSchema();

        private JsonElement BuildParametersSchema()
        {
            var schema = new
            {
                type = "object",
                properties = new
                {
                    path = new
                    {
                        type = "string",
                        description = "要读取的文件或目录的绝对路径"
                    },
                    offset = new
                    {
                        type = "integer",
                        description = "开始读取的行号（从 1 开始索引）",
                        minimum = 1
                    },
                    limit = new
                    {
                        type = "integer",
                        description = $"最大读取行数（默认 {FileSystemHelper.DefaultReadLimit}）"
                    }
                },
                required = new[] { "path" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var filePath = GetStringArgument(arguments, "path");
            if (string.IsNullOrEmpty(filePath))
            {
                return Failure("缺少必需参数: path");
            }

            var offset = GetIntArgument(arguments, "offset") ?? 1;
            var limit = GetIntArgument(arguments, "limit") ?? FileSystemHelper.DefaultReadLimit;

            if (offset < 1)
            {
                return Failure("offset 必须大于或等于 1");
            }

            if (!Path.IsPathRooted(filePath))
            {
                filePath = _fileSystem.GetFullPath(filePath);
            }

            var denied = PathGateHelper.RejectIfDenied(_pathGate, context, filePath, Failure);
            if (denied != null)
                return denied;

            _logger.LogInformation("读取文件: {FilePath}, offset={Offset}, limit={Limit}", filePath, offset, limit);

            if (!_fileSystem.Exists(filePath))
            {
                var suggestions = FileSystemHelper.FindSimilarFiles(_fileSystem, filePath);
                if (suggestions.Count > 0)
                {
                    return Failure($"文件不存在: {filePath}\n\n您是否指的是以下文件?\n{string.Join("\n", suggestions)}");
                }
                return Failure($"文件不存在: {filePath}");
            }

            if (FileSystemHelper.IsDirectory(_fileSystem, filePath))
            {
                return ReadDirectory(filePath, offset, limit);
            }

            return await ReadFileAsync(filePath, offset, limit, context);
        }

        private ToolResult ReadDirectory(string filePath, int offset, int limit)
        {
            var entries = new List<string>();

            foreach (var dir in _fileSystem.EnumerateDirectories(filePath, "*", recursive: false))
            {
                entries.Add(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
            }

            foreach (var file in _fileSystem.EnumerateFiles(filePath, "*", recursive: false))
            {
                entries.Add(Path.GetFileName(file));
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);

            var start = offset - 1;
            var sliced = entries.Skip(start).Take(limit).ToList();
            var truncated = start + sliced.Count < entries.Count;

            var outputLines = new List<string>
            {
                $"目录: {filePath}",
                $"类型: directory",
                $"总条目数: {entries.Count}",
                "",
                "条目列表:"
            };

            foreach (var entry in sliced)
            {
                outputLines.Add($"  {entry}");
            }

            if (truncated)
            {
                outputLines.Add("");
                outputLines.Add($"(显示 {sliced.Count} 条，共 {entries.Count} 条。使用 offset 参数继续读取)");
            }
            else
            {
                outputLines.Add("");
                outputLines.Add($"(共 {entries.Count} 条)");
            }

            return Success(
                string.Join("\n", outputLines),
                new Dictionary<string, object>
                {
                    ["type"] = "directory",
                    ["count"] = entries.Count,
                    ["truncated"] = truncated
                }
            );
        }

        private async Task<ToolResult> ReadFileAsync(string filePath, int offset, int limit, ToolContext context)
        {
            var mime = FileSystemHelper.GetMimeType(filePath);
            var isImage = FileSystemHelper.IsImage(filePath);
            var isPdf = FileSystemHelper.IsPdf(filePath);

            if (isImage || isPdf)
            {
                return ReadBinaryFile(filePath, mime, isImage ? "图片" : "PDF");
            }

            if (FileSystemHelper.IsBinaryByExtension(filePath) ||
                FileSystemHelper.IsBinaryByContent(_fileSystem, filePath))
            {
                return Failure($"无法读取二进制文件: {filePath}");
            }

            var (lines, totalLines, truncated, truncatedByBytes) =
                await ReadTextFileWithLimitAsync(filePath, offset, limit, context.CancellationToken);

            if (totalLines < offset && !(totalLines == 0 && offset == 1))
            {
                return Failure($"offset {offset} 超出文件范围（文件共 {totalLines} 行）");
            }

            var outputLines = new List<string>
            {
                $"路径: {filePath}",
                $"类型: file",
                "",
                "内容:"
            };

            foreach (var line in lines.Select((line, index) => (line, index)))
            {
                outputLines.Add($"{offset + line.index}: {line.line}");
            }

            var lastReadLine = offset + lines.Count - 1;
            var nextOffset = lastReadLine + 1;

            outputLines.Add("");
            if (truncatedByBytes)
            {
                outputLines.Add($"(输出限制在 {FileSystemHelper.MaxBytes / 1024}KB。显示行 {offset}-{lastReadLine}。使用 offset={nextOffset} 继续。)");
            }
            else if (truncated)
            {
                outputLines.Add($"(显示行 {offset}-{lastReadLine}，共 {totalLines} 行。使用 offset={nextOffset} 继续。)");
            }
            else
            {
                outputLines.Add($"(文件结尾 - 共 {totalLines} 行)");
            }

            var preview = string.Join("\n", lines.Take(20));

            return Success(
                string.Join("\n", outputLines),
                new Dictionary<string, object>
                {
                    ["type"] = "file",
                    ["totalLines"] = totalLines,
                    ["offset"] = offset,
                    ["readLines"] = lines.Count,
                    ["truncated"] = truncated,
                    ["preview"] = preview
                }
            );
        }

        private static async Task<(List<string> Lines, int TotalLines, bool Truncated, bool TruncatedByBytes)> ReadTextFileWithLimitAsync(
            IFileSystem fileSystem,
            string filePath,
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            var totalLines = 0;
            var bytes = 0;
            var truncated = false;
            var truncatedByBytes = false;
            var startLine = offset - 1;

            await foreach (var line in fileSystem.ReadLinesAsync(filePath, cancellationToken))
            {
                totalLines++;

                if (totalLines <= startLine)
                {
                    continue;
                }

                if (lines.Count >= limit)
                {
                    truncated = true;
                    continue;
                }

                var truncatedLine = FileSystemHelper.TruncateLine(line);
                var lineSize = Encoding.UTF8.GetByteCount(truncatedLine) + (lines.Count > 0 ? 1 : 0);

                if (bytes + lineSize > FileSystemHelper.MaxBytes)
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

        private Task<(List<string> Lines, int TotalLines, bool Truncated, bool TruncatedByBytes)> ReadTextFileWithLimitAsync(
            string filePath,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            ReadTextFileWithLimitAsync(_fileSystem, filePath, offset, limit, cancellationToken);

        private ToolResult ReadBinaryFile(string filePath, string mime, string typeLabel)
        {
            try
            {
                var size = ReadBinarySize(filePath);

                return new ToolResult
                {
                    Success = true,
                    Output = $"{typeLabel} 读取成功",
                    Metadata = new Dictionary<string, object>
                    {
                        ["type"] = "binary",
                        ["mime"] = mime,
                        ["size"] = size
                    },
                    Attachments = new List<FileAttachment>
                    {
                        new FileAttachment
                        {
                            Name = Path.GetFileName(filePath) ?? "file",
                            Path = filePath,
                            MimeType = mime
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return Failure($"读取 {typeLabel} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取二进制文件真实大小：可寻址流直接取长度；非可寻址流（远程/虚拟文件系统）
        /// 完整排空计数——只计数不缓存，内存占用恒定，避免把 <c>size</c> 误报为截断到
        /// <see cref="FileSystemHelper.MaxBytes"/> 后的值（如 100MB 文件报 50KB）。
        /// </summary>
        private long ReadBinarySize(string filePath)
        {
            using var stream = _fileSystem.OpenRead(filePath);

            if (stream.CanSeek)
                return stream.Length;

            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                total += read;

            return total;
        }
    }
}
