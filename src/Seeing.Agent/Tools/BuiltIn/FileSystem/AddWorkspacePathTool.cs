using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
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

    private readonly IWorkspaceWhitelist _whitelist;

    public AddWorkspacePathTool(ILogger<AddWorkspacePathTool> logger, IWorkspaceWhitelist whitelist)
        : base(logger)
    {
        _whitelist = whitelist;
    }

    public override string Id => "add_workspace_path";

    public override string Description => "请求将指定目录加入当前会话的工作区白名单，获批准后该目录下所有文件操作自动免询问";

    public override ToolCategory Category => ToolCategory.FileSystem;

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

        if (string.IsNullOrEmpty(context.SessionId))
            return Failure("当前上下文缺少会话 ID，无法扩展工作区白名单");

        _whitelist.Add(context.SessionId, path);

        return Success("路径已加入当前会话的工作区白名单", path);
    }
}
