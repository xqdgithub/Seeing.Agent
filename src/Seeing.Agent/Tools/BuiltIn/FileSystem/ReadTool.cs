using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 文件/目录读取工具
    /// <para>
    /// 读取文件或目录的内容。支持 offset 和 limit 参数分页读取大文件。
    /// 自动检测和处理二进制文件、图片、PDF 文件。
    /// </para>
    /// </summary>
    public class ReadTool : ToolBase
    {
        /// <summary>
        /// 创建 ReadTool 实例
        /// </summary>
        public ReadTool(ILogger<ReadTool> logger) : base(logger)
        {
        }

        public override string Id => "read";

        public override string Description => 
            "读取文件或目录内容。\n\n" +
            "可以读取文件或目录。返回带行号的文件内容，或目录中的条目列表。\n" +
            "使用 offset 和 limit 参数分页读取大文件。\n" +
            "自动检测二进制文件、图片、PDF 文件并返回附件而非文本内容。";

        public ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => BuildParametersSchema();

        private JsonElement BuildParametersSchema()
        {
            var schema = new
            {
                type = "object",
                properties = new
                {
                    filePath = new
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
                required = new[] { "filePath" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取 filePath 参数
            var filePath = GetStringArgument(arguments, "filePath");
            if (string.IsNullOrEmpty(filePath))
            {
                return Failure("缺少必需参数: filePath");
            }

            // 获取 offset 和 limit 参数
            var offset = GetIntArgument(arguments, "offset") ?? 1;
            var limit = GetIntArgument(arguments, "limit") ?? FileSystemHelper.DefaultReadLimit;

            // 验证 offset
            if (offset < 1)
            {
                return Failure("offset 必须大于或等于 1");
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.GetFullPath(filePath);
            }

            _logger.LogInformation("读取文件: {FilePath}, offset={Offset}, limit={Limit}", filePath, offset, limit);

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "read",
                    Patterns = new List<string> { filePath },
                    Metadata = new Dictionary<string, object>
                    {
                        ["filePath"] = filePath,
                        ["offset"] = offset,
                        ["limit"] = limit
                    }
                });
            }

            // 检查路径是否存在
            if (!File.Exists(filePath) && !Directory.Exists(filePath))
            {
                // 尝试查找相似文件
                var suggestions = FileSystemHelper.FindSimilarFiles(filePath);
                if (suggestions.Count > 0)
                {
                    return Failure($"文件不存在: {filePath}\n\n您是否指的是以下文件?\n{string.Join("\n", suggestions)}");
                }
                return Failure($"文件不存在: {filePath}");
            }

            // 处理目录
            if (Directory.Exists(filePath))
            {
                return ReadDirectory(filePath, offset, limit);
            }

            // 处理文件
            return ReadFile(filePath, offset, limit, context);
        }

        /// <summary>
        /// 读取目录内容
        /// </summary>
        private ToolResult ReadDirectory(string filePath, int offset, int limit)
        {
            var entries = FileSystemHelper.GetDirectoryEntries(filePath);

            var start = offset - 1;
            var sliced = entries.Skip(start).Take(limit).ToList();
            var truncated = start + sliced.Count < entries.Count;

            var outputLines = new List<string>
            {
                $"文件不存在: {filePath}", // 使用正确的方法
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

        /// <summary>
        /// 读取文件内容
        /// </summary>
        private ToolResult ReadFile(string filePath, int offset, int limit, ToolContext context)
        {
            // 检查文件类型
            var mime = FileSystemHelper.GetMimeType(filePath);
            var isImage = FileSystemHelper.IsImage(filePath);
            var isPdf = FileSystemHelper.IsPdf(filePath);

            // 处理图片和 PDF
            if (isImage || isPdf)
            {
                return ReadBinaryFile(filePath, mime, isImage ? "图片" : "PDF");
            }

            // 检查是否为二进制文件
            if (FileSystemHelper.IsBinaryByExtension(filePath) || 
                FileSystemHelper.IsBinaryByContent(filePath))
            {
                return Failure($"无法读取二进制文件: {filePath}");
            }

            // 读取文本文件
            var (lines, totalLines, truncated, truncatedByBytes) = 
                FileSystemHelper.ReadFileWithLimit(filePath, offset, limit);

            // 检查 offset 是否超出范围
            if (totalLines < offset && !(totalLines == 0 && offset == 1))
            {
                return Failure($"offset {offset} 超出文件范围（文件共 {totalLines} 行）");
            }

            // 构建带行号的输出
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

        /// <summary>
        /// 读取二进制文件（图片/PDF）并返回附件
        /// </summary>
        private ToolResult ReadBinaryFile(string filePath, string mime, string typeLabel)
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var base64 = Convert.ToBase64String(bytes);
                var dataUrl = $"data:{mime};base64,{base64}";

                var result = new ToolResult
                {
                    Success = true,
                    Output = $"{typeLabel} 读取成功",
                    Metadata = new Dictionary<string, object>
                    {
                        ["type"] = "binary",
                        ["mime"] = mime,
                        ["size"] = bytes.Length
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

                // 将 base64 数据存储在 metadata 中
                result.Metadata["dataUrl"] = dataUrl;

                return result;
            }
            catch (Exception ex)
            {
                return Failure($"读取 {typeLabel} 失败: {ex.Message}");
            }
        }
    }
}