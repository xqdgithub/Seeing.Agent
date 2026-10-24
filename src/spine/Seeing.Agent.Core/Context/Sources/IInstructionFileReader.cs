namespace Seeing.Agent.Core.Context.Sources;

/// <summary>
/// 单个候选指令文件的读取结果状态。
/// </summary>
public enum InstructionFileReadStatus
{
    /// <summary>读取成功，正文可用。</summary>
    Loaded,

    /// <summary>确认不存在（文件或目录缺失）——参与 diff，可叙述删除。</summary>
    Missing,

    /// <summary>暂时不可读（被占用 / 无权限 / 其它 IO 失败）——不参与 diff，保留上次已叙述内容。</summary>
    Unreadable
}

/// <summary>
/// 逐路径的指令文件读取结果。
/// </summary>
/// <param name="Path">绝对路径。</param>
/// <param name="Status">读取状态；三态区分是「缺失」与「不可读」不混淆的唯一依据。</param>
/// <param name="Content">正文（仅 <see cref="InstructionFileReadStatus.Loaded"/> 时非空）。</param>
/// <param name="Error">失败描述（仅 <see cref="InstructionFileReadStatus.Unreadable"/> 时非空，供上层记录）。</param>
public sealed record InstructionFileReadResult(
    string Path,
    InstructionFileReadStatus Status,
    string? Content,
    string? Error);

/// <summary>
/// 指令文件的逐路径读取端口：按候选路径返回三态结果。
/// <para>与 <c>InstructionDiscovery.TryReadAsync</c> 的关键差异：不得把 IO 失败吞成「不存在」，
/// 否则「文件被占用」会被渲染成「指令已删除」。</para>
/// </summary>
public interface IInstructionFileReader
{
    /// <summary>
    /// 读取 <paramref name="cwd"/> 到 <paramref name="workspaceRoot"/> 之间适用的全部候选指令文件。
    /// 候选集合的搜索根规则由发现器统一决定，本端口只负责逐路径分类。
    /// </summary>
    Task<IReadOnlyList<InstructionFileReadResult>> ReadAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default);
}
