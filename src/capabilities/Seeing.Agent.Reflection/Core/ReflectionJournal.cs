using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Reflection.Abstractions;

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
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // 中文不转义：日志是给人读的，\uXXXX 会让排查成本陡增。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

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
        ct.ThrowIfCancellationRequested();

        var directory = Path.Combine(
            _baseDirectory,
            "runs",
            record.At.ToLocalTime().ToString("yyyyMMdd"));

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var filePath = Path.Combine(directory, SanitizeFileName(record.ExecutionId) + ".json");
        var json = JsonSerializer.Serialize(record, SerializerOptions);

        // 同一执行 ID 重复复盘视为覆盖（重跑不应留下互相矛盾的重复记录）。
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    /// <summary>
    /// 清洗 executionId 作为文件名：只保留字母数字与 <c>-</c>、<c>_</c>，其余替换为 <c>_</c>。
    /// <para>
    /// executionId 可能来自外部输入，直接拼路径会形成目录穿越（<c>../../evil</c>）。
    /// </para>
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
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
