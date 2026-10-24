using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令注入端口：注入由上下文管道完成，本接口只保留投影结果。
/// </summary>
public interface IInstructionManager
{
    /// <summary>
    /// 触发一次注入装配：仅在指令文件变化时向会话注入更新内容。
    /// <para><paramref name="cwd"/> / <paramref name="workspaceRoot"/> 为本轮执行根，会真正传给源
    /// （不得被静默忽略）。<paramref name="insertBeforeMessageId"/> 非空且命中时，指令消息插入到该消息之前
    /// （恢复「指令在用户命令之前」的历史顺序）；未命中则追加到末尾。</para>
    /// </summary>
    Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default,
        string? insertBeforeMessageId = null);

    /// <summary>
    /// 读取会话当前的指令文件指纹快照（迁移后恒为空快照，Task 13 删除）。
    /// </summary>
    InstructionFingerprintSnapshot GetFingerprints(SessionData session);
}
