using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tools.Editor;

/// <summary>
/// 编辑器命令工具 — 将 Monaco 命令透传到用户 WebUI 的编辑器面板执行。
/// <para>经 Host Request 通道与宿主通信；无可用编辑器连接时快速失败。</para>
/// </summary>
public sealed class EditorCommandTool : BuiltInToolBase
{
    /// <summary>创建 EditorCommandTool 实例。</summary>
    public EditorCommandTool(ILogger<EditorCommandTool> logger) : base(logger)
    {
    }

    /// <inheritdoc />
    public override string Id => "editor_command";

    /// <inheritdoc />
    public override string Description =>
        "在用户 WebUI 的 Monaco 编辑器中执行一条命令（透传 commandId）。" +
        "commandId 为 Monaco 命令标识（如 editor.action.formatDocument、editor.foldAll）；" +
        "args 为命令参数（可选）；path 用于在当前面板内切换文件（可选）。" +
        "仅当用户打开了文件面板时可用；命令修改内容后不会自动保存，需用户手动保存。";

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.General;

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            commandId = new { type = "string", description = "Monaco 命令标识（如 editor.action.formatDocument）" },
            args = new { type = "object", description = "命令参数（可选，透传给 Monaco）" },
            path = new { type = "string", description = "要切换到的文件路径（可选，在当前面板内切换）" }
        },
        required = new[] { "commandId" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var commandId = GetStringArgument(arguments, "commandId");
        if (string.IsNullOrWhiteSpace(commandId))
            return Failure("缺少必需参数: commandId");

        var manager = context.Services?.GetService<IHostRequestManager>();
        if (manager is null)
            return Failure("宿主请求通道未接入");

        var surfaces = context.Services?.GetService<IHostSurfaceRegistry>();
        if (surfaces is not null && !surfaces.CanSurface(context.SessionId))
            return Failure("无可用编辑器连接（请在 WebUI 打开文件面板）");

        JsonElement? args = arguments.TryGetProperty("args", out var rawArgs) &&
                            rawArgs.ValueKind == JsonValueKind.Object
            ? rawArgs
            : null;
        var path = GetStringArgument(arguments, "path");

        var request = EditorHostRequests.Command(context.SessionId, context.CallId, commandId, args, path);

        var ct = context.CancellationToken;
        var ticket = await manager.BeginAsync(request, ct).ConfigureAwait(false);
        var response = await manager.WaitAsync(ticket, ct).ConfigureAwait(false);

        return FormatResult(response);
    }

    private static ToolResult FormatResult(HostResponse response)
    {
        switch (response.Status)
        {
            case HostResponseStatus.Completed:
                var output = "命令已执行。";
                if (response.Payload is { } payload)
                    output += Environment.NewLine + payload.GetRawText();
                return new ToolResult
                {
                    Success = true,
                    Output = output,
                    Metadata = new Dictionary<string, object>
                    {
                        ["status"] = "completed",
                        ["request_id"] = response.RequestId
                    }
                };
            case HostResponseStatus.Unavailable:
                return ToolResult.Failed("无可用编辑器连接");
            case HostResponseStatus.Timeout:
                return ToolResult.Failed("编辑器未在限定时间内响应");
            case HostResponseStatus.Cancelled:
                return ToolResult.Failed("编辑器操作已取消");
            default:
                return ToolResult.Failed(response.Reason ?? "编辑器操作失败");
        }
    }
}
