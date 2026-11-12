using Seeing.Agent.Paths;
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

    private readonly Func<string> _resolveBaseDirectory;

    /// <summary>
    /// 读取根目录下的 <c>runs/</c>。
    /// </summary>
    /// <param name="baseDirectory">复盘产物根目录。</param>
    public ReflectionRunReader(string baseDirectory)
        : this(ClosedOver(baseDirectory))
    {
    }

    /// <summary>
    /// 读取根目录下的 <c>runs/</c>（目录随工作区切换）。
    /// <para>记录页每次加载都重新解析：切换工作区后必须看到新工作区的记录。</para>
    /// </summary>
    /// <param name="root">产物目录持有者（与 journal 同源）。</param>
    public ReflectionRunReader(ReflectionStorageRoot root)
        : this(() => (root ?? throw new ArgumentNullException(nameof(root))).Path)
    {
    }

    private ReflectionRunReader(Func<string> resolveBaseDirectory)
        => _resolveBaseDirectory = resolveBaseDirectory;

    private static Func<string> ClosedOver(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("根目录不能为空", nameof(baseDirectory));

        return () => baseDirectory;
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
        var runsRoot = Path.Combine(_resolveBaseDirectory(), "runs");
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

    /// <summary>
    /// 按路径读取一条记录（记录页的详情 / 生成技能都要用它定位建议清单）。
    /// <para>
    /// <b>路径来自 UI，必须做边界校验</b>：走 <see cref="PathBoundary.IsStrictlyWithin"/>，
    /// 内联前缀比较会被 <c>runs/evil -&gt; C:\Windows</c> 这类符号链接绕过。
    /// </para>
    /// </summary>
    /// <param name="recordFilePath">记录的绝对或相对路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>越界、不存在或无法解析时返回 <c>null</c>。</returns>
    public async Task<ReflectionRunSummary?> FindAsync(string recordFilePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(recordFilePath))
            return null;

        var runsRoot = Path.Combine(_resolveBaseDirectory(), "runs");
        if (!PathBoundary.IsStrictlyWithin(recordFilePath, runsRoot))
            return null;

        var fullPath = Path.GetFullPath(recordFilePath);
        if (!fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return null;

        return await TryReadAsync(new FileInfo(fullPath), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按稳定标识读取一条记录。
    /// <para>
    /// 与 <see cref="FindAsync"/> 并行存在：路径是文件系统事实，ID 是记录身份。
    /// 页面用 ID（跨分天目录与命名规则稳定），底层再映射成文件。
    /// </para>
    /// </summary>
    /// <param name="runId">记录的稳定标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不存在或标识非法时返回 <c>null</c>。</returns>
    public async Task<ReflectionRunSummary?> FindByRunIdAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var runsRoot = Path.Combine(_resolveBaseDirectory(), "runs");
        if (!Directory.Exists(runsRoot))
            return null;

        // 与 journal 的删除同一套规则：文件名主干 = 消毒后的 ID，逐个日期目录找。
        var stem = ReflectionJournal.SanitizeStem(runId);

        foreach (var dayDirectory in Directory.EnumerateDirectories(runsRoot))
        {
            ct.ThrowIfCancellationRequested();

            // 成功记录优先：同一 RunId 可能同时存在失败快照（重试），列表页以成功记录为准。
            foreach (var suffix in new[] { ".json", ".fail.json" })
            {
                var candidate = Path.Combine(dayDirectory, stem + suffix);
                if (!PathBoundary.IsStrictlyWithin(candidate, runsRoot) || !File.Exists(candidate))
                    continue;

                var summary = await TryReadAsync(new FileInfo(candidate), ct).ConfigureAwait(false);
                if (summary is not null)
                    return summary;
            }
        }

        return null;
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
                    FilePath: file.FullName,
                    RunId: ResolveRunId(failure.RunId, file));
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
                FilePath: file.FullName,
                RunId: ResolveRunId(record.RunId, file));
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

    /// <summary>
    /// 解析记录的身份：优先取记录内字段，旧记录（无该字段）回退到文件名主干。
    /// </summary>
    /// <remarks>
    /// 回退是必需的：升级前的记录里没有 <c>RunId</c>，若返回 null，这些记录将无法删除
    /// （删除按 ID 定位）。文件名主干正是当初写入时用的值，因此回退是准确的。
    /// </remarks>
    private static string ResolveRunId(string? declared, FileInfo file)
    {
        if (!string.IsNullOrWhiteSpace(declared))
            return declared;

        var name = file.Name;

        if (name.EndsWith(".fail.json", StringComparison.OrdinalIgnoreCase))
            return name[..^".fail.json".Length];

        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? name[..^".json".Length]
            : name;
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
/// <param name="RunId">
/// 记录的稳定标识（删除 / 定位用）。旧记录由文件名回退得出，因此恒非空。
/// </param>
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
    string FilePath,
    string RunId)
{
    /// <summary>总 Token（列表展示用）。</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}
