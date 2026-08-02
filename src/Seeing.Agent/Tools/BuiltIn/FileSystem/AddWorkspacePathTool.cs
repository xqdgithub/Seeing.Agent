using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem;

/// <summary>
/// 请求将路径加入当前会话的工作区白名单。
/// Agent 可主动调用此工具来预先申请路径权限，避免后续逐个文件询问。
/// 权限仅当前会话有效。
/// </summary>
public class AddWorkspacePathTool : BuiltInToolBase
{
    private static readonly JsonElement s_schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "要添加到工作区白名单的目录路径"
            }
        },
        ["required"] = new[] { "path" }
    });

    public AddWorkspacePathTool(ILogger<AddWorkspacePathTool> logger)
        : base(logger)
    {
    }

    public override string Id => "add_workspace_path";

    public override string Description => "请求将指定目录加入当前会话的工作区白名单，获批准后该目录下所有文件操作自动免询问";

    public ToolCategory Category => ToolCategory.FileSystem;

    public override JsonElement ParametersSchema => s_schema;

    public override IReadOnlyList<string> Tags => new[] { "built-in", "filesystem" };

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
            return Failure("参数 path 不能为空");

        path = ResolvePath(path);

        if (!Directory.Exists(path))
            return Failure($"目录不存在: {path}");

        var workspace = context.Services?.GetService<IWorkspaceProvider>();
        if (workspace != null && FileSystemHelper.IsPathWithinDirectory(path, workspace.WorkspaceRoot))
            return Success("路径已在工作区内，无需额外权限", path);

        var channel = context.PermissionChannel;
        if (channel == null)
            return Failure("未配置权限通道，无法申请路径权限");

        // 确保路径以分隔符结尾，以便记忆的目录前缀匹配生效
        var dirPath = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

        var result = await channel.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.workspace_extend",
            Resource = dirPath,
            Patterns = new List<string> { path },
            SessionId = context.SessionId,
            Metadata = new Dictionary<string, object>
            {
                ["path"] = path,
                ["reason"] = "Agent 请求扩展工作区路径"
            }
        });

        if (result.Action == PermissionChannelAction.Deny)
            return Failure(result.Reason ?? "用户拒绝了工作区路径扩展请求");

        return Success("路径已加入当前会话的工作区白名单", path);
    }
}
