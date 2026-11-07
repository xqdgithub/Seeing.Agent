using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Reflection.Ui;

/// <summary>
/// 复盘运行记录的只读查询服务（供 WebUI 列出历史复盘）。
/// <para>
/// <b>为什么读取不放 <c>IReflectionJournal</c></b>：journal 是写入端口，
/// 让日志同时承担状态源会诱使运行期逻辑依赖历史记录。查询是纯展示需求，
/// 单独一个只读服务即可，也便于 WebUI 按需注入。
/// </para>
/// <para>
/// 读取的是 <c>runs/{yyyyMMdd}/*.json</c> 与 <c>*.fail.json</c>，
/// 按文件时间倒序返回最近若干条。
/// </para>
/// </summary>
public sealed class ReflectionRunReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _baseDirectory;

    /// <summary>读取根目录下的 <c>runs/</c>。</summary>
    /// <param name="baseDirectory">复盘产物根目录（与 journal 同源）。</param>
    public ReflectionRunReader(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("根目录不能为空", nameof(baseDirectory));

        _baseDirectory = baseDirectory;
    }

    /// <summary>
    /// 列出最近的复盘记录（成功 + 失败），按时间倒序。
    /// </summary>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<ReflectionRunSummary>> ListAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        var runsRoot = Path.Combine(_baseDirectory, "runs");
        if (!Directory.Exists(runsRoot))
            return Array.Empty<ReflectionRunSummary>();

        // 只扫一级日期目录：runs/{yyyyMMdd}/{file}.json。更深层结构不存在，避免递归扫全盘。
        var files = Directory
            .EnumerateFiles(runsRoot, "*.json", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(Math.Max(1, limit))
            .ToList();

        var result = new List<ReflectionRunSummary>(files.Count);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var summary = await TryReadAsync(file, ct).ConfigureAwait(false);
            if (summary is not null)
                result.Add(summary);
        }

        return result;
    }

    /// <summary>读取单条记录；文件损坏或结构不识别时返回 <c>null</c>（不抛）。</summary>
    private static async Task<ReflectionRunSummary?> TryReadAsync(FileInfo file, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
            var isFailure = file.Name.EndsWith(".fail.json", StringComparison.OrdinalIgnoreCase);

            if (isFailure)
            {
                var failure = JsonSerializer.Deserialize<ReflectionFailureRecord>(json, SerializerOptions);
                if (failure is null)
                    return null;

                return new ReflectionRunSummary(
                    SessionId: failure.SessionId,
                    ExecutionId: failure.ExecutionId,
                    Kind: failure.Kind,
                    At: failure.At,
                    // 失败记录的 Outcome 由 SkipReason 派生，与成功记录共用展示形态。
                    Outcome: "failed:" + failure.SkipReason,
                    CreatedSkills: Array.Empty<string>(),
                    Suggestions: Array.Empty<ReflectionSuggestion>(),
                    InputTokens: 0,
                    OutputTokens: 0,
                    ModelId: failure.ModelId,
                    IsFailure: true,
                    Detail: string.IsNullOrWhiteSpace(failure.Detail)
                        ? failure.Transcript
                        : failure.Detail,
                    FilePath: file.FullName);
            }

            var record = JsonSerializer.Deserialize<ReflectionRunRecord>(json, SerializerOptions);
            if (record is null)
                return null;

            return new ReflectionRunSummary(
                SessionId: record.SessionId,
                ExecutionId: record.ExecutionId,
                Kind: record.Kind,
                At: record.At,
                Outcome: record.Outcome,
                CreatedSkills: record.CreatedSkills,
                Suggestions: record.Suggestions,
                InputTokens: record.InputTokens,
                OutputTokens: record.OutputTokens,
                ModelId: record.ModelId,
                IsFailure: false,
                Detail: null,
                FilePath: file.FullName);
        }
        catch (JsonException)
        {
            // 历史文件格式演进后可能读不了：跳过而不是让整页失败。
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>列表展示用的复盘记录摘要（成功与失败统一形态）。</summary>
/// <param name="SessionId">被复盘的会话 ID。</param>
/// <param name="ExecutionId">触发本次复盘的执行 ID。</param>
/// <param name="Kind">触发原因。</param>
/// <param name="At">记录产生时刻。</param>
/// <param name="Outcome">结果描述；失败记录形如 <c>failed:timeout</c>。</param>
/// <param name="CreatedSkills">实际创建的技能名。</param>
/// <param name="Suggestions">产出的建议清单。</param>
/// <param name="InputTokens">输入 Token。</param>
/// <param name="OutputTokens">输出 Token。</param>
/// <param name="ModelId">实际使用的模型。</param>
/// <param name="IsFailure">是否为失败快照。</param>
/// <param name="Detail">失败详情（仅失败记录）。</param>
/// <param name="FilePath">原始文件路径（便于用户直接查看）。</param>
public sealed record ReflectionRunSummary(
    string SessionId,
    string ExecutionId,
    Core.ReflectionTriggerKind Kind,
    DateTimeOffset At,
    string Outcome,
    IReadOnlyList<string> CreatedSkills,
    IReadOnlyList<ReflectionSuggestion> Suggestions,
    int InputTokens,
    int OutputTokens,
    string? ModelId,
    bool IsFailure,
    string? Detail,
    string FilePath)
{
    /// <summary>总 Token（列表展示用）。</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}
