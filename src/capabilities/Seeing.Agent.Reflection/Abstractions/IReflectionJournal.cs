using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 复盘运行日志的写入端口。
/// <para>
/// 写入侧只追加两条通道：正常运行记录（<c>*.json</c>）与失败快照（<c>*.fail.json</c>）。
/// 读取不进本接口——WebUI 直接读目录，避免日志被当成运行期状态源。
/// </para>
/// </summary>
public interface IReflectionJournal
{
    /// <summary>追加一条复盘运行记录。</summary>
    Task AppendAsync(ReflectionRunRecord record, CancellationToken ct = default);

    /// <summary>
    /// 追加一条<b>失败快照</b>（<c>{executionId}.fail.json</c>）。
    /// <para>
    /// 失败时子会话会被清理，「诊断信息」因此必须落到这里：失败原因 + 复盘子会话的
    /// transcript。不落的话失败就只剩一条 Warning 日志，无法回答「模型到底输出了什么」。
    /// </para>
    /// </summary>
    Task AppendFailureAsync(ReflectionFailureRecord record, CancellationToken ct = default);

    /// <summary>
    /// 删除一次复盘的全部记录（按 <paramref name="runId"/> 定位，成功记录与失败快照一起删）。
    /// <para>
    /// <b>按 ID 而不是按路径</b>：路径是「文件系统事实」，随分天目录与文件名规则变化；
    /// 而 <c>RunId</c> 是记录的稳定身份。UI 拿 ID 回来，由 journal 自己决定文件在哪——
    /// 这样「删哪个」与「文件怎么命名」解耦，也避免把用户输入当路径用。
    /// </para>
    /// <para>
    /// 实现仍需做边界校验（<see cref="Paths.PathBoundary.IsStrictlyWithin"/>），
    /// 因为 <c>RunId</c> 会被拼进文件名：即使经过消毒，也不该假设消毒永远正确。
    /// </para>
    /// </summary>
    /// <param name="runId">记录的稳定标识。</param>
    /// <returns>实际删除的文件数（0 表示不存在或标识非法）。</returns>
    Task<int> DeleteAsync(string runId, CancellationToken ct = default);
}

/// <summary>一次复盘运行的结果记录。</summary>
/// <param name="SessionId">被复盘的会话 ID。</param>
/// <param name="ExecutionId">触发本次复盘的执行 ID（审计用；空闲/手动触发时可能为空）。</param>
/// <param name="Kind">触发原因。</param>
/// <param name="At">记录产生时刻。</param>
/// <param name="Outcome">结果描述（如 skipped / suggested / created / failed）。</param>
/// <param name="CreatedSkills">本次实际创建的技能名。</param>
/// <param name="Suggestions">本次产出的建议清单。</param>
/// <param name="InputTokens">本次复盘消耗的输入 Token。</param>
/// <param name="OutputTokens">本次复盘消耗的输出 Token。</param>
/// <param name="ModelId">本次复盘实际使用的模型；解析不出时为 null。</param>
/// <param name="RunId">
/// 本次复盘的稳定标识（同时用作文件名）。缺省 <c>null</c> 只为兼容旧记录：
/// 升级前的记录没有该字段，反序列化后为 <c>null</c>，因此读取侧必须能从文件名回退。
/// <b>新写入必须显式赋值</b>——否则文件名会退回 <c>ExecutionId</c>，而空闲/手动触发那里是空串，
/// 同一天的所有记录会写进同一个 <c>unknown.json</c> 互相覆盖。
/// </param>
public sealed record ReflectionRunRecord(
    string SessionId,
    string ExecutionId,
    ReflectionTriggerKind Kind,
    DateTimeOffset At,
    string Outcome,
    IReadOnlyList<string> CreatedSkills,
    IReadOnlyList<ReflectionSuggestion> Suggestions,
    int InputTokens,
    int OutputTokens,
    string? ModelId = null,
    string? RunId = null);

/// <summary>
/// 一次复盘失败快照。
/// <para>
/// 存在的理由：失败 / 超时的复盘子会话会被清理，而「模型输出了什么」是
/// 唯一能定位根因的证据。快照把它留在 journal 里，磁盘上不留孤儿会话。
/// </para>
/// </summary>
/// <param name="SessionId">被复盘的会话 ID。</param>
/// <param name="ExecutionId">触发本次复盘的执行 ID（审计用；空闲/手动触发时可能为空）。</param>
/// <param name="Kind">触发原因。</param>
/// <param name="At">记录产生时刻。</param>
/// <param name="SkipReason">失败分类（见 <see cref="ReflectionSkipReasons"/>）。</param>
/// <param name="ChildSessionId">复盘子会话 ID（已清理，仅供追溯）。</param>
/// <param name="Transcript">复盘子会话的 assistant 文本（截断后）。</param>
/// <param name="Detail">补充说明（异常消息等）。</param>
/// <param name="ModelId">本次复盘实际使用的模型；解析不出时为 null。</param>
/// <param name="RunId">与 <see cref="ReflectionRunRecord.RunId"/> 同一语义（失败与成功共享同一段身份）。</param>
public sealed record ReflectionFailureRecord(
    string SessionId,
    string ExecutionId,
    ReflectionTriggerKind Kind,
    DateTimeOffset At,
    string SkipReason,
    string? ChildSessionId,
    string? Transcript,
    string? Detail,
    string? ModelId = null,
    string? RunId = null);
