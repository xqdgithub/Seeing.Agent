using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 打开文件编辑器工具（只读，无磁盘副作用）。
/// <para>
/// 请求前端文件面板打开指定文件：面板未开则打开；已是当前文件则从磁盘刷新；
/// 已是其他标签则切换；新文件则新建标签（不展开目录树）。仅回写路径元数据，由宿主桥接驱动 UI。
/// </para>
/// </summary>
public class OpenFileTool : BuiltInToolBase
{
    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _pathGate;

    /// <summary>创建 OpenFileTool 实例。</summary>
    public OpenFileTool(
        ILogger<OpenFileTool> logger,
        IFileSystem fileSystem,
        IWorkspacePathGate pathGate) : base(logger)
    {
        _fileSystem = fileSystem;
        _pathGate = pathGate;
    }

    /// <inheritdoc />
    public override string Id => "open_file";

    /// <inheritdoc />
    public override string Description =>
        "在用户编辑器中打开文件并定位；只用于呈现，不读取内容（用 `read`）。\n\n" +
        "- 行为：已打开则切换，当前文件则从磁盘刷新；无磁盘副作用。\n" +
        "- 定位：line 与 column 均从 1 开始，column 需同时给 line。\n" +
        "- 前置：需要宿主提供文件编辑器面板；纯文本读取请用 `read`。";

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.FileSystem;

    /// <inheritdoc />
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
                    description = "要打开的文件：绝对路径，或相对当前工作目录的路径"
                },
                line = new
                {
                    type = "integer",
                    description = "可选：跳转到的行号（从 1 开始）"
                },
                column = new
                {
                    type = "integer",
                    description = "可选：跳转到的列号（从 1 开始；需同时提供 line）"
                }
            },
            required = new[] { "path" }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var filePath = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult(Failure("缺少必需参数: path"));

        if (!Path.IsPathRooted(filePath))
            filePath = _fileSystem.GetFullPath(filePath);
        else
            filePath = ResolvePath(filePath);

        var denied = PathGateHelper.RejectIfDenied(_pathGate, context, filePath, Failure);
        if (denied != null)
            return Task.FromResult(denied);

        _logger.LogInformation("请求打开文件: {FilePath}", filePath);

        // 无磁盘副作用：仅回写 path/line/column，由宿主桥接驱动前端打开/切换/刷新/定位。
        var metadata = new Dictionary<string, object> { ["path"] = filePath };
        var line = GetIntArgument(arguments, "line");
        var column = GetIntArgument(arguments, "column");
        if (line is not null)
        {
            metadata["line"] = line.Value;
            if (column is not null)
                metadata["column"] = column.Value;
        }

        return Task.FromResult(Success($"已请求打开: {filePath}", metadata));
    }
}
