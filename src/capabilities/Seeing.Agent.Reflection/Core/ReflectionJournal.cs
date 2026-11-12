using Seeing.Agent.Paths;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 基于本地文件的复盘日志。
/// <para>
/// 落盘路径为 <c>{baseDirectory}/runs/{yyyyMMdd}/{executionId}.json</c>，按天分目录，
/// 便于人工按日期翻阅，也避免单目录文件无限膨胀。
/// </para>
/// </summary>
public sealed class ReflectionJournal : IReflectionJournal
{
    /// <summary>
    /// 文件名（不含 <c>.json</c>）的最大长度。
    /// <para>
    /// 取 100 而非贴近 Windows 的 255：目录段 + 扩展名 + 短哈希仍留有余量，
    /// 也远低于多数文件系统的单段限制。
    /// </para>
    /// </summary>
    public const int MaxFileNameLength = 100;

    private static readonly JsonSerializerOptions SerializerOptions = SeeingJson.Create(indented: true, o =>
    {
        // 中文不转义：日志是给人读的，\uXXXX 会让排查成本陡增。
        o.Converters.Add(new JsonStringEnumConverter());
    });

    private readonly Func<string> _resolveBaseDirectory;

    /// <summary>
    /// 创建日志实例（固定目录）。
    /// </summary>
    /// <param name="baseDirectory">日志根目录（通常为 <c>{工作区}/.seeing/reflection</c>）。</param>
    public ReflectionJournal(string baseDirectory)
        : this(ClosedOver(baseDirectory))
    {
    }

    /// <summary>
    /// 创建日志实例（目录随工作区切换）。
    /// <para>每次写入都重新解析目录：切换工作区后新记录必须落到新工作区，而不是继续写旧根。</para>
    /// </summary>
    /// <param name="root">产物目录持有者。</param>
    public ReflectionJournal(ReflectionStorageRoot root)
        : this(() => (root ?? throw new ArgumentNullException(nameof(root))).Path)
    {
    }

    private ReflectionJournal(Func<string> resolveBaseDirectory)
    {
        _resolveBaseDirectory = resolveBaseDirectory;
    }

    private static Func<string> ClosedOver(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("日志根目录不能为空", nameof(baseDirectory));
        }

        return () => baseDirectory;
    }

    /// <inheritdoc />
    public async Task AppendAsync(ReflectionRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        var filePath = Path.Combine(
            ResolveDayDirectory(record.At),
            ResolveFileStem(record.RunId, record.ExecutionId) + ".json");

        var json = JsonSerializer.Serialize(record, SerializerOptions);

        // 同一 RunId 重复写入视为覆盖：重跑同一次复盘不应留下互相矛盾的重复记录。
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    /// <inheritdoc />
    public async Task AppendFailureAsync(ReflectionFailureRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        // 失败快照用独立后缀：同一次复盘可能既有成功记录又有失败重试，
        // 后缀不同才不会互相覆盖（覆盖会丢掉一个方向的事实）。
        var filePath = Path.Combine(
            ResolveDayDirectory(record.At),
            ResolveFileStem(record.RunId, record.ExecutionId) + ".fail.json");

        var json = JsonSerializer.Serialize(record, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public Task<int> DeleteAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(runId))
            return Task.FromResult(0);

        var runsRoot = Path.Combine(_resolveBaseDirectory(), "runs");
        if (!Directory.Exists(runsRoot))
            return Task.FromResult(0);

        // 文件名由 RunId 消毒而来；按同一规则反推候选名，逐个日期目录找。
        // 不按「当前日期」猜：记录可能产生于很久以前，按天分目录后 ID 与日期无绑定关系。
        var stem = SanitizeStem(runId);
        var deleted = 0;

        foreach (var dayDirectory in Directory.EnumerateDirectories(runsRoot))
        {
            // 目录也可能被外部塞进符号链接：每层文件都做一次边界校验。
            deleted += TryDeleteRecordPair(Path.Combine(dayDirectory, stem), runsRoot);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// 删除 <paramref name="stem"/> 对应的成功记录与失败快照，返回删除文件数。
    /// </summary>
    /// <remarks>
    /// 两个文件都必须先过 <see cref="PathBoundary.IsStrictlyWithin"/>：
    /// <paramref name="stem"/> 已消毒，但「消毒永远正确」不该是边界安全的唯一依据。
    /// </remarks>
    private static int TryDeleteRecordPair(string pathWithoutSuffix, string runsRoot)
    {
        var deleted = 0;
        string[] candidates = [pathWithoutSuffix + ".json", pathWithoutSuffix + ".fail.json"];

        foreach (var candidate in candidates)
        {
            if (!PathBoundary.IsStrictlyWithin(candidate, runsRoot))
                continue;

            try
            {
                if (!File.Exists(candidate))
                    continue;

                File.Delete(candidate);
                deleted++;
            }
            catch (IOException)
            {
                // 文件被占用：跳过即可，调用方按返回的文件数判定，不抛。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }

        return deleted;
    }

    /// <summary>
    /// 解析记录的文件名主干：优先 <paramref name="runId"/>，缺失时回退 <paramref name="executionId"/>。
    /// <para>
    /// 回退只为兼容旧记录与未赋值调用方。<b>回退是危险的</b>：空闲/手动触发的
    /// <c>ExecutionId</c> 是空串，消毒后变成 <c>unknown</c>，同一天的多条记录会写进同一文件互相覆盖。
    /// 因此新写入必须给 <c>RunId</c>；这里保留回退只是为了让老调用点不静默失败。
    /// </para>
    /// </summary>
    private static string ResolveFileStem(string? runId, string? executionId)
        => SanitizeStem(string.IsNullOrWhiteSpace(runId) ? executionId : runId);

    /// <summary>按记录时刻解析（并创建）当天的目录。</summary>
    private string ResolveDayDirectory(DateTimeOffset at)
    {
        var directory = Path.Combine(
            _resolveBaseDirectory(),
            "runs",
            at.ToLocalTime().ToString("yyyyMMdd"));

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>
    /// 把标识清洗成文件名主干：只保留字母数字与 <c>-</c>、<c>_</c>，其余替换为 <c>_</c>；
    /// 超过 <see cref="MaxFileNameLength"/> 时截断并追加短哈希。
    /// <para>
    /// <b>公开</b>是为了让读取侧（<c>ReflectionRunReader.FindByRunIdAsync</c>）与写入侧
    /// 用同一套命名规则：两边各写一份消毒逻辑，改名规则一改就会「写进去读不出来」。
    /// </para>
    /// <para>
    /// 清洗的两个理由：
    /// </para>
    /// <list type="number">
    /// <item>标识可能来自外部输入，直接拼路径会形成目录穿越（<c>../../evil</c>）。</item>
    /// <item>不同标识消毒后可能撞名（<c>a/b</c> 与 <c>a:b</c> 都变 <c>a_b</c>），
    /// 因此截断后要补短哈希——否则两次不同的复盘会互相覆盖，且看不出来。</item>
    /// </list>
    /// </summary>
    public static string SanitizeStem(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "unknown";
        }

        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        var sanitized = builder.ToString();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "unknown";
        }

        if (sanitized.Length <= MaxFileNameLength)
        {
            return sanitized;
        }

        // 截断后必须补哈希：只截断会让「前缀相同」的不同 ID 指向同一文件，静默覆盖。
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)))[..8];

        return sanitized[..MaxFileNameLength] + "-" + hash;
    }
}
