using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Ui;

/// <summary>
/// 复盘记录页的写操作入口：删除记录、把建议落成技能。
/// <para>
/// <b>为什么不让页面直接碰 journal / writer</b>：
/// </para>
/// <list type="bullet">
/// <item>删除需要「路径边界校验 + 配对文件一起删」两个不变量，散在 UI 里必然有人漏掉；</item>
/// <item>生成技能需要「按层级写盘 + 失败原因可展示」，<see cref="ReflectionSuggestionsWriter"/>
/// 返回的是内部元组，不适合直接给 Razor 用；</item>
/// <item>两个动作都要在事后刷新列表——调用方只想知道「成了没有、为什么没成」。</item>
/// </list>
/// </summary>
public sealed class ReflectionRunOperations
{
    private readonly IReflectionJournal _journal;
    private readonly ReflectionRunReader _reader;
    private readonly ReflectionSuggestionsWriter _writer;
    private readonly ILogger<ReflectionRunOperations>? _logger;

    public ReflectionRunOperations(
        IReflectionJournal journal,
        ReflectionRunReader reader,
        ReflectionSuggestionsWriter writer,
        ILogger<ReflectionRunOperations>? logger = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger;
    }

    /// <summary>读取一条记录的完整内容（含建议清单）。</summary>
    /// <param name="recordFilePath">列表页给出的记录路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读取失败或路径越界时返回 <c>null</c>。</returns>
    public Task<ReflectionRunSummary?> FindAsync(string recordFilePath, CancellationToken ct = default)
        => _reader.FindAsync(recordFilePath, ct);

    /// <summary>按稳定标识读取一条记录（含建议清单）。</summary>
    /// <param name="runId">记录的稳定标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不存在时返回 <c>null</c>。</returns>
    public Task<ReflectionRunSummary?> FindByRunIdAsync(string runId, CancellationToken ct = default)
        => _reader.FindByRunIdAsync(runId, ct);

    /// <summary>删除一次复盘的全部记录（按稳定标识，成功记录与失败快照一起删）。</summary>
    /// <param name="runId">记录的稳定标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>删除结果；<c>DeletedFiles</c> 为实际删除的文件数。</returns>
    public async Task<ReflectionRecordDeletionResult> DeleteAsync(
        string runId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return ReflectionRecordDeletionResult.Failed("缺少记录标识。");

        var deleted = await _journal.DeleteAsync(runId, ct).ConfigureAwait(false);
        if (deleted == 0)
        {
            // 「0 个文件」有两种可能：标识非法（越界/空）或文件已不存在。
            // 不区分原因对用户无意义，但必须如实说「没删掉」，不能假装成功。
            return ReflectionRecordDeletionResult.Failed("记录不存在或已被删除。");
        }

        _logger?.LogInformation("复盘记录已删除：RunId={RunId}（{Count} 个文件）", runId, deleted);
        return new ReflectionRecordDeletionResult(true, deleted, null);
    }

    /// <summary>把一条建议落成技能。</summary>
    /// <param name="runId">记录的稳定标识（用于定位建议清单）。</param>
    /// <param name="suggestionIndex">建议在清单中的序号（从 0 开始）。</param>
    /// <param name="level">目标层级。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ReflectionSkillCreationResult> CreateSkillAsync(
        string runId,
        int suggestionIndex,
        ConfigLevel level,
        CancellationToken ct = default)
    {
        var record = await _reader.FindByRunIdAsync(runId, ct).ConfigureAwait(false);
        if (record is null)
            return ReflectionSkillCreationResult.Failed("记录不存在或无法读取。");

        if (suggestionIndex < 0 || suggestionIndex >= record.Suggestions.Count)
            return ReflectionSkillCreationResult.Failed("建议序号越界（记录可能已被修改）。");

        var suggestion = record.Suggestions[suggestionIndex];

        var result = await _writer
            .CreateFromSuggestionAsync(suggestion, record.SessionId, level, ct)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _logger?.LogInformation(
                "复盘建议已手动落成技能：Skill={Skill}, Level={Level}, RunId={RunId}",
                result.SkillName,
                level,
                runId);
        }

        return result;
    }
}

/// <summary>删除一条复盘记录的结果。</summary>
/// <param name="Success">是否真的删掉了。</param>
/// <param name="DeletedFiles">实际删除的文件数（记录 + 配对失败快照）。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
public sealed record ReflectionRecordDeletionResult(bool Success, int DeletedFiles, string? Error)
{
    /// <summary>构造失败结果。</summary>
    /// <param name="error">可展示的原因。</param>
    public static ReflectionRecordDeletionResult Failed(string error)
        => new(false, 0, error);
}
