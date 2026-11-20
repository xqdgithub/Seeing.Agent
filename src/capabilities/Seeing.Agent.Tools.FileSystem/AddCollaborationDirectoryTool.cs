using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 请求把目录加入**会话协同目录**（持久化）。
/// <para>
/// 与 <c>add_workspace_path</c> 的区别：后者是临时白名单（内存、切工作区清空、不审批），
/// 本工具写入会话配置并随会话持久化，因此**必须经审批**（RequireInteraction）。
/// </para>
/// </summary>
public class AddCollaborationDirectoryTool : BuiltInToolBase
{
    private const string PermissionKind = "filesystem.collaboration_directory";

    private static readonly JsonElement s_schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "要加入协同目录的目录：绝对路径，或相对当前工作目录的路径"
            },
            ["note"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "可选说明（用于上下文展示，帮助理解该目录的用途）"
            }
        },
        ["required"] = new[] { "path" }
    });

    private readonly IFileSystem _fileSystem;
    private readonly ICollaborationDirectories _collaboration;

    public AddCollaborationDirectoryTool(
        ILogger<AddCollaborationDirectoryTool> logger,
        IFileSystem fileSystem,
        ICollaborationDirectories collaboration)
        : base(logger)
    {
        _fileSystem = fileSystem;
        _collaboration = collaboration;
    }

    public override string Id => "add_collaboration_directory";

    public override string Description =>
        "请求把目录加入当前会话的协同工作目录（持久化，可读写、可在文件工作台浏览）。\n\n" +
        "- 审批：必须由用户确认；一次性批准不会持久化。\n" +
        "- 区别：临时扩权请用 `add_workspace_path`。";

    public override ToolCategory Category => ToolCategory.FileSystem;

    public override JsonElement ParametersSchema => s_schema;

    public override IReadOnlyList<string> Tags => new[] { "built-in", "filesystem" };

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Failure("缺少必需参数: path");

        var note = GetStringArgument(arguments, "note");

        var authorizer = context.PermissionAuthorizer;
        if (authorizer is null)
            return Failure("当前执行没有审批通道，不能添加协同目录。");

        if (string.IsNullOrWhiteSpace(context.WorkingDirectory))
            return Failure("缺少执行工作目录，无法解析相对路径。");

        string absolutePath;
        try
        {
            absolutePath = Path.IsPathRooted(path)
                ? path
                : _fileSystem.GetFullPath(path, context.WorkingDirectory);
        }
        catch (Exception ex)
        {
            return Failure($"无法解析路径: {ex.Message}");
        }

        if (!_fileSystem.Exists(absolutePath) || !FileSystemHelper.IsDirectory(_fileSystem, absolutePath))
            return Failure($"目录不存在: {absolutePath}");

        var resolution = await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = context.SessionId,
            CallId = context.CallId,
            AgentName = context.Agent?.Name,
            PermissionKind = PermissionKind,
            Resource = absolutePath,
            RequireInteraction = true,
            WorkingDirectory = context.WorkingDirectory
        }, context.CancellationToken);

        if (resolution.Decision != PermissionEffect.Allow)
            return Failure(resolution.Reason ?? "未获批准");

        if (resolution.Scope == PermissionGrantScope.Once)
            return Failure("一次性批准不会持久化协同目录；请选择本会话或目录级授权。");

        await _collaboration.AddAsync(context.SessionId, absolutePath, note, context.CancellationToken);
        return Success(
            "协同目录已添加",
            absolutePath + "\n本轮后续工具调用已可读写；上下文列表将在下一轮同步。");
    }
}
