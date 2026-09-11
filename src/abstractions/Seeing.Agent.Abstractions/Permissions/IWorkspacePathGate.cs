namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 工作区路径硬边界门闸：只校验允许集，不发起 Ask。
/// </summary>
public interface IWorkspacePathGate
{
    /// <summary>
    /// 若路径允许则返回 <see langword="null"/>；否则返回失败原因（供工具 Failure）。
    /// </summary>
    string? EnsureAllowed(string sessionId, string path);
}
