using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 文件工具共用的工作区路径门闸检查。
/// </summary>
internal static class PathGateHelper
{
    public static ToolResult? RejectIfDenied(
        IWorkspacePathGate gate,
        ToolContext context,
        string path,
        Func<string, ToolResult> failure)
    {
        var reason = gate.EnsureAllowed(context.SessionId ?? string.Empty, path);
        return reason == null ? null : failure(reason);
    }
}
