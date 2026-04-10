using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 文件写入工具
    /// <para>
    /// 将内容写入文件。如果文件不存在，会创建新文件。
    /// 如果文件存在，会覆盖原有内容。
    /// </para>
    /// </summary>
    public class WriteTool : ToolBase
    {
        /// <summary>
        /// 创建 WriteTool 实例
        /// </summary>
        public WriteTool(ILogger<WriteTool> logger) : base(logger)
        {
        }

        public override string Id => "write";

        public override string Description =>
            "将内容写入文件。\n\n" +
            "将提供的文本内容写入指定的文件路径。路径必须是绝对路径。\n" +
            "如果文件已存在，会覆盖原有内容。\n" +
            "如果文件不存在，会创建新文件（包括必要的目录）。";

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
                        description = "要写入的文件的绝对路径（必须是绝对路径，不能是相对路径）"
                    },
                    content = new
                    {
                        type = "string",
                        description = "要写入文件的内容"
                    }
                },
                required = new[] { "filePath", "content" }
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

            // 获取 content 参数
            var content = GetStringArgument(arguments, "content");
            if (content == null)
            {
                return Failure("缺少必需参数: content");
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.GetFullPath(filePath);
            }

            _logger.LogInformation("写入文件: {FilePath}", filePath);

            // 检查文件是否存在
            var exists = File.Exists(filePath);
            var oldContent = exists ? await ReadFileContentAsync(filePath) : "";

            // 生成 diff
            var diff = GenerateDiff(filePath, oldContent, content);

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "edit",
                    Patterns = new List<string> { filePath },
                    Metadata = new Dictionary<string, object>
                    {
                        ["filePath"] = filePath,
                        ["diff"] = diff,
                        ["exists"] = exists
                    }
                });
            }

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 写入文件
            try
            {
                await WriteFileContentAsync(filePath, content);
            }
            catch (Exception ex)
            {
                return Failure(ex, "写入文件失败");
            }

            var output = "文件写入成功。";
            if (!exists)
            {
                output = "新文件已创建。";
            }

            return Success(
                Path.GetFileName(filePath) ?? filePath,
                output,
                new Dictionary<string, object>
                {
                    ["filePath"] = filePath,
                    ["exists"] = exists,
                    ["size"] = content.Length,
                    ["diff"] = diff
                }
            );
        }

        /// <summary>
        /// 异步读取文件内容
        /// </summary>
        private async Task<string> ReadFileContentAsync(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 异步写入文件内容
        /// </summary>
        private async Task WriteFileContentAsync(string filePath, string content)
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            await writer.WriteAsync(content);
        }

        /// <summary>
        /// 生成简单的 diff 输出
        /// </summary>
        private string GenerateDiff(string filePath, string oldContent, string newContent)
        {
            var oldLines = oldContent.Split('\n');
            var newLines = newContent.Split('\n');

            var diffLines = new List<string>
            {
                $"--- {filePath}",
                $"+++ {filePath}"
            };

            // 简化的 diff：显示所有行的变化
            var maxLines = Math.Max(oldLines.Length, newLines.Length);

            for (var i = 0; i < maxLines; i++)
            {
                var oldLine = i < oldLines.Length ? oldLines[i] : null;
                var newLine = i < newLines.Length ? newLines[i] : null;

                if (oldLine != newLine)
                {
                    if (oldLine != null)
                    {
                        diffLines.Add($"-{oldLine}");
                    }
                    if (newLine != null)
                    {
                        diffLines.Add($"+{newLine}");
                    }
                }
                else if (oldLine != null)
                {
                    diffLines.Add($" {oldLine}");
                }
            }

            return string.Join("\n", diffLines);
        }
    }
}
