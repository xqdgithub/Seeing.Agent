using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 文件/目录删除工具。目录默认递归删除。
/// </summary>
public class DeleteTool : BuiltInToolBase
{
    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _pathGate;
    private readonly IPathWriteLock _writeLock;

    public DeleteTool(
        ILogger<DeleteTool> logger,
        IFileSystem fileSystem,
        IWorkspacePathGate pathGate,
        IPathWriteLock writeLock) : base(logger)
    {
        _fileSystem = fileSystem;
        _pathGate = pathGate;
        _writeLock = writeLock;
    }

    public override string Id => "delete";

    public override string Description =>
        "删除文件或目录。\n\n" +
        "删除指定路径的文件，或递归删除整个目录（含所有子项）。";

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
                    description = "要删除的文件或目录的绝对路径"
                }
            },
            required = new[] { "path" }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
            return Failure("缺少必需参数: path");

        if (!Path.IsPathRooted(path))
            path = _fileSystem.GetFullPath(path);
        else
            path = ResolvePath(path);

        var denied = PathGateHelper.RejectIfDenied(_pathGate, context, path, Failure);
        if (denied != null)
            return denied;

        _logger.LogInformation("删除路径: {Path}", path);

        try
        {
            using var _ = await _writeLock.AcquireAsync(path, context.CancellationToken);
            if (!_fileSystem.Exists(path))
                return Failure($"路径不存在: {path}");

            var isDirectory = FileSystemHelper.IsDirectory(_fileSystem, path);
            _fileSystem.Delete(path);
            return Success(isDirectory
                ? $"目录已删除: {path}"
                : $"文件已删除: {path}");
        }
        catch (Exception ex)
        {
            return Failure(ex, "删除失败");
        }
    }
}
