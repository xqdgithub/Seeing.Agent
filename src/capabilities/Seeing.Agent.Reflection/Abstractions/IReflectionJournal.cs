using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 复盘运行日志的写入端口。
/// <para>
/// 只写不读：日志用于事后人工排查「这次复盘为什么没产出」，不参与运行期决策，
/// 因此故意不提供查询接口，避免日志被当成状态源使用。
/// </para>
/// </summary>
public interface IReflectionJournal
{
    /// <summary>追加一条复盘运行记录。</summary>
    Task AppendAsync(ReflectionRunRecord record, CancellationToken ct = default);
}

/// <summary>
/// 一次复盘运行的结果记录。
/// </summary>
/// <param name="SessionId">被复盘的会话 ID。</param>
/// <param name="ExecutionId">触发本次复盘的执行 ID。</param>
/// <param name="Kind">触发原因。</param>
/// <param name="At">记录产生时刻。</param>
/// <param name="Outcome">结果描述（如 skipped / suggested / created / failed）。</param>
/// <param name="CreatedSkills">本次实际创建的技能名。</param>
/// <param name="Suggestions">本次产出的建议清单。</param>
/// <param name="InputTokens">本次复盘消耗的输入 Token。</param>
/// <param name="OutputTokens">本次复盘消耗的输出 Token。</param>
public sealed record ReflectionRunRecord(
    string SessionId,
    string ExecutionId,
    ReflectionTriggerKind Kind,
    DateTimeOffset At,
    string Outcome,
    IReadOnlyList<string> CreatedSkills,
    IReadOnlyList<ReflectionSuggestion> Suggestions,
    int InputTokens,
    int OutputTokens);
