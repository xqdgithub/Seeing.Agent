namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 工作区路径硬边界门闸：只校验允许集，不发起 Ask。
/// </summary>
public interface IWorkspacePathGate
{
    /// <summary>
    /// 若路径允许则返回 <see langword="null"/>；否则返回失败原因（供工具 Failure）。
    /// <para>
    /// 条件判定：受 <c>Workspace.RestrictToWorkspace</c> 开关影响，且额外接受会话权限扩权白名单。
    /// 需要无条件的工作区边界时用 <see cref="EnsureWithinWorkspace"/>。
    /// </para>
    /// </summary>
    string? EnsureAllowed(string sessionId, string path);

    /// <summary>
    /// 严格工作区包含判定（会话 cwd ∪ 项目根）：在界内返回 <see langword="null"/>，否则返回失败原因。
    /// <para>
    /// 无条件判定：不读 <c>Workspace.RestrictToWorkspace</c>，不查 <see cref="IPermissionGrantStore"/> 扩权白名单。
    /// </para>
    /// </summary>
    string? EnsureWithinWorkspace(string sessionId, string path);

    /// <summary>
    /// 把路径按 <paramref name="baseDirectory"/> 绝对化后做允许判定（解析 + 授权一个入口）。
    /// <para>
    /// 允许时返回 <see langword="null"/> 并给出 <paramref name="fullPath"/>；
    /// 相对路径缺少基准、或越界时返回失败原因。工具不得绕过本方法自行解析相对路径。
    /// </para>
    /// </summary>
    string? ResolveAllowed(
        string sessionId,
        string path,
        string? baseDirectory,
        out string fullPath);
}
