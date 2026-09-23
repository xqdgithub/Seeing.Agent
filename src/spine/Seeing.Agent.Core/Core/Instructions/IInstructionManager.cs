using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令文件（AGENTS.md 等）的发现与按需注入管理器。
/// </summary>
public interface IInstructionManager
{
    /// <summary>
    /// 从工作目录向上逐级发现适用的指令文件。
    /// </summary>
    Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default);

    /// <summary>
    /// 对比会话已记录的指纹，仅在指令文件变化时向会话注入更新内容。
    /// </summary>
    Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default);

    /// <summary>
    /// 读取会话当前的指令文件指纹快照。
    /// </summary>
    InstructionFingerprintSnapshot GetFingerprints(SessionData session);
}
