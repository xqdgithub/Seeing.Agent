using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem
{
    /// <summary>
    /// 文件写入工具
    /// <para>
    /// 将内容写入文件。如果文件不存在，会创建新文件。
    /// 如果文件存在，会覆盖原有内容。
    /// </para>
    /// </summary>
    public class WriteTool : BuiltInToolBase
    {
        private readonly IFileSystem _fileSystem;
        private readonly IWorkspacePathGate _pathGate;

        /// <summary>
        /// 创建 WriteTool 实例
        /// </summary>
        public WriteTool(
            ILogger<WriteTool> logger,
            IFileSystem fileSystem,
            IWorkspacePathGate pathGate) : base(logger)
        {
            _fileSystem = fileSystem;
            _pathGate = pathGate;
        }

        public override string Id => "write";

        public override string Description =>
            "将内容写入文件。\n\n" +
            "将提供的文本内容写入指定的文件路径。路径必须是绝对路径。\n" +
            "如果文件已存在，会覆盖原有内容。\n" +
            "如果文件不存在，会创建新文件（包括必要的目录）。";

        public override ToolCategory Category => ToolCategory.FileSystem;

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
            var filePath = GetStringArgument(arguments, "filePath");
            if (string.IsNullOrEmpty(filePath))
            {
                return Failure("缺少必需参数: filePath");
            }

            var content = GetStringArgument(arguments, "content");
            if (content == null)
            {
                return Failure("缺少必需参数: content");
            }

            if (!Path.IsPathRooted(filePath))
            {
                filePath = _fileSystem.GetFullPath(filePath);
            }

            var denied = PathGateHelper.RejectIfDenied(_pathGate, context, filePath, Failure);
            if (denied != null)
                return denied;

            _logger.LogInformation("写入文件: {FilePath}", filePath);

            var exists = _fileSystem.Exists(filePath) && !FileSystemHelper.IsDirectory(_fileSystem, filePath);
            var oldContent = exists
                ? await _fileSystem.ReadAllTextAsync(filePath, context.CancellationToken)
                : "";

            var diff = GenerateDiff(filePath, oldContent, content);

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !_fileSystem.Exists(directory))
            {
                _fileSystem.CreateDirectory(directory);
            }

            try
            {
                await _fileSystem.WriteAllTextAsync(filePath, content, context.CancellationToken);
            }
            catch (Exception ex)
            {
                return Failure(ex, "写入文件失败");
            }

            var output = exists ? "文件写入成功。" : "新文件已创建。";

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

        private string GenerateDiff(string filePath, string oldContent, string newContent)
        {
            var oldLines = oldContent.Split('\n');
            var newLines = newContent.Split('\n');

            var diffLines = new List<string>
            {
                $"--- {filePath}",
                $"+++ {filePath}"
            };

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
