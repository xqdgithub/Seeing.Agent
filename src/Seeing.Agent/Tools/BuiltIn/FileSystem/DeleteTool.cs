using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem;

/// <summary>
/// 文件/目录删除工具。目录默认递归删除。
/// </summary>
public class DeleteTool : BuiltInToolBase
{
    public DeleteTool(ILogger<DeleteTool> logger) : base(logger)
    {
    }

    public override string Id => "delete";

    public override string Description =>
        "删除文件或目录。\n\n" +
        "删除指定路径的文件，或递归删除整个目录（含所有子项）。";

    public ToolCategory Category => ToolCategory.FileSystem;

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

        path = ResolvePath(path);

        _logger.LogInformation("删除路径: {Path}", path);

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return Success($"目录已删除: {path}");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                return Success($"文件已删除: {path}");
            }
            return Failure($"路径不存在: {path}");
        }
        catch (Exception ex)
        {
            return Failure(ex, "删除失败");
        }
    }
}
