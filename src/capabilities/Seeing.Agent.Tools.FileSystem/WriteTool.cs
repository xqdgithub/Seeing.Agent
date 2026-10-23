using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
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
        private readonly IPathWriteLock _writeLock;

        /// <summary>
        /// 创建 WriteTool 实例
        /// </summary>
        public WriteTool(
            ILogger<WriteTool> logger,
            IFileSystem fileSystem,
            IWorkspacePathGate pathGate,
            IPathWriteLock writeLock) : base(logger)
        {
            _fileSystem = fileSystem;
            _pathGate = pathGate;
            _writeLock = writeLock;
        }

        public override string Id => "write";

        public override string Description =>
            "新建文件或整体重写；只改现有文件的一小段请用 `edit`。\n\n" +
            "- 路径：绝对路径，或相对当前工作目录的路径；父目录不存在会自动创建。\n" +
            "- 覆盖：目标已存在时整文件替换，原有内容不保留。\n" +
            "- 不用于：读取内容（用 `read`）；把文件呈现给用户（用 `open_file`）。";

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
                        description = "要写入的文件：绝对路径，或相对当前工作目录的路径（父目录会自动创建）"
                    },
                    content = new
                    {
                        type = "string",
                        description = "要写入文件的内容"
                    }
                },
                required = new[] { "path", "content" }
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

            // 与文件工作区（UI）共享同一进程内路径写锁，避免同进程并发写竞态
            using var _ = await _writeLock.AcquireAsync(filePath, context.CancellationToken);

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

            var metadata = FileDiffMetadata.Build(filePath, oldContent, content, diff);
            metadata["exists"] = exists;
            metadata["size"] = content.Length;

            return Success(
                Path.GetFileName(filePath) ?? filePath,
                output,
                metadata
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
