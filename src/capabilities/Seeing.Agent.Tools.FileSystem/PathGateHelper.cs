using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 文件工具共用的路径解析 + 授权入口。
/// <para>
/// 解析与授权必须一次完成：先按执行级 cwd 绝对化，再做边界判定，
/// 避免"工具各自解析相对路径、各用一套基准"的旧问题。
/// </para>
/// </summary>
internal static class PathGateHelper
{
    public static ToolResult? Resolve(
        IWorkspacePathGate gate,
        ToolContext context,
        string path,
        Func<string, ToolResult> failure,
        out string fullPath)
    {
        var reason = gate.ResolveAllowed(
            context.SessionId ?? string.Empty,
            path,
            context.WorkingDirectory,
            out fullPath);

        return reason == null ? null : failure(reason);
    }
}
