using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Serialization;

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

    private readonly string _baseDirectory;

    /// <summary>
    /// 创建日志实例。
    /// </summary>
    /// <param name="baseDirectory">日志根目录（通常为 <c>~/.seeing/reflection</c>）。</param>
    public ReflectionJournal(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("日志根目录不能为空", nameof(baseDirectory));
        }

        _baseDirectory = baseDirectory;
    }

    /// <inheritdoc />
    public async Task AppendAsync(ReflectionRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        var filePath = Path.Combine(
            ResolveDayDirectory(record.At),
            SanitizeFileName(record.ExecutionId) + ".json");

        var json = JsonSerializer.Serialize(record, SerializerOptions);

        // 同一执行 ID 重复复盘视为覆盖（重跑不应留下互相矛盾的重复记录）。
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    /// <inheritdoc />
    public async Task AppendFailureAsync(ReflectionFailureRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        // 失败快照用独立后缀：同一次执行可能既有一次成功记录、又有一次失败重试，
        // 后缀不同才不会互相覆盖（覆盖会丢掉一个方向的事实）。
        var filePath = Path.Combine(
            ResolveDayDirectory(record.At),
            SanitizeFileName(record.ExecutionId) + ".fail.json");

        var json = JsonSerializer.Serialize(record, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    /// <summary>按记录时刻解析（并创建）当天的目录。</summary>
    private string ResolveDayDirectory(DateTimeOffset at)
    {
        var directory = Path.Combine(
            _baseDirectory,
            "runs",
            at.ToLocalTime().ToString("yyyyMMdd"));

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>
    /// 清洗 executionId 作为文件名：只保留字母数字与 <c>-</c>、<c>_</c>，其余替换为 <c>_</c>；
    /// 超过 <see cref="MaxFileNameLength"/> 时截断并追加短哈希。
    /// <para>
    /// 清洗的两个理由：
    /// </para>
    /// <list type="number">
    /// <item>executionId 可能来自外部输入，直接拼路径会形成目录穿越（<c>../../evil</c>）。</item>
    /// <item>不同 ID 消毒后可能撞名（<c>a/b</c> 与 <c>a:b</c> 都变 <c>a_b</c>），
    /// 因此截断后要补短哈希——否则两次不同的复盘会互相覆盖，且看不出来。</item>
    /// </list>
    /// </summary>
    private static string SanitizeFileName(string? executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            return "unknown";
        }

        var builder = new StringBuilder(executionId.Length);
        foreach (var ch in executionId)
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
