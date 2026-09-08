using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.Support;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Seeing.Agent.Core.Tools.FileSystem;

/// <summary>
/// 文件/目录删除工具。目录默认递归删除。
/// </summary>
public class DeleteTool : BuiltInToolBase
{
    private readonly IFileSystem _fileSystem;

    public DeleteTool(ILogger<DeleteTool> logger, IFileSystem fileSystem) : base(logger)
    {
        _fileSystem = fileSystem;
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

    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(Failure("缺少必需参数: path"));

        if (!Path.IsPathRooted(path))
            path = _fileSystem.GetFullPath(path);
        else
            path = ResolvePath(path);

        _logger.LogInformation("删除路径: {Path}", path);

        try
        {
            if (!_fileSystem.Exists(path))
                return Task.FromResult(Failure($"路径不存在: {path}"));

            var isDirectory = IsDirectory(path);
            _fileSystem.Delete(path);
            return Task.FromResult(Success(isDirectory
                ? $"目录已删除: {path}"
                : $"文件已删除: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Failure(ex, "删除失败"));
        }
    }

    private bool IsDirectory(string path)
    {
        try
        {
            using var enumerator = _fileSystem.EnumerateFiles(path, "*", recursive: false).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
