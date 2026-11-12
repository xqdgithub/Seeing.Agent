using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 基于本地 JSON 文件、按 UTC 日期分桶的用量账本。
/// <para>
/// 文件结构：<c>{ "date": "2026-11-01", "input": 0, "output": 0 }</c>，落盘于
/// <c>{baseDirectory}/usage.json</c>。
/// </para>
/// <para>
/// 只在 <see cref="UsedToday"/> 首次调用时读盘，之后走进程内缓存；
/// <see cref="Add"/> 不再回读，避免高频写路径上的磁盘读放大。
/// </para>
/// </summary>
public sealed class ExecutionUsageLedger : IExecutionUsageLedger
{
    private static readonly JsonSerializerOptions SerializerOptions = SeeingJson.Create(indented: true, o =>
    {
        o.Converters.Add(new JsonStringEnumConverter());
    });

    private readonly Func<string> _resolveFilePath;
    private readonly object _gate = new();

    private bool _loaded;
    private string? _loadedPath;
    private string? _date;
    private long _input;
    private long _output;

    /// <summary>
    /// 创建账本实例（固定目录）。
    /// </summary>
    /// <param name="baseDirectory">账本根目录（通常为 <c>{工作区}/.seeing/reflection</c>）。</param>
    public ExecutionUsageLedger(string baseDirectory)
        : this(ClosedOver(baseDirectory))
    {
    }

    /// <summary>
    /// 创建账本实例（目录随工作区切换）。
    /// <para>
    /// 切换目录时必须丢弃内存累计并重新读盘：预算按<b>项目</b>分别计，
    /// 留着旧工作区的数字会让新工作区「一开机就没有额度」。
    /// </para>
    /// </summary>
    /// <param name="root">产物目录持有者。</param>
    public ExecutionUsageLedger(ReflectionStorageRoot root)
        : this(() => (root ?? throw new ArgumentNullException(nameof(root))).Path)
    {
    }

    private ExecutionUsageLedger(Func<string> resolveBaseDirectory)
    {
        _resolveFilePath = () => Path.Combine(resolveBaseDirectory(), "usage.json");
    }

    private static Func<string> ClosedOver(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("账本根目录不能为空", nameof(baseDirectory));
        }

        return () => Path.Combine(baseDirectory, "usage.json");
    }

    /// <summary>账本文件路径（诊断用）。</summary>
    private string FilePath => _resolveFilePath();

    /// <inheritdoc />
    public long UsedToday()
    {
        lock (_gate)
        {
            EnsureLoaded();

            // 跨天只返回 0，不写盘：读操作不产生副作用。
            if (_date != Today())
            {
                return 0;
            }

            return _input + _output;
        }
    }

    /// <inheritdoc />
    public void Add(TokenUsage usage)
    {
        if (usage is null)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();

            var today = Today();
            if (_date != today)
            {
                // 换天即重置：旧日期的累计不再参与今日预算。
                _date = today;
                _input = 0;
                _output = 0;
            }

            _input += usage.InputTokens;
            _output += usage.OutputTokens;

            Persist();
        }
    }

    /// <summary>
    /// 首次使用时从磁盘加载；文件缺失或损坏一律视为全 0，不让账本故障影响复盘主流程。
    /// <para>
    /// 目录变化（工作区切换）时必须重新加载：预算按项目分别计，
    /// 沿用旧工作区的累计会让新项目一开机就没有额度。
    /// </para>
    /// </summary>
    private void EnsureLoaded()
    {
        var path = FilePath;

        if (_loaded && string.Equals(_loadedPath, path, StringComparison.Ordinal))
        {
            return;
        }

        _loaded = true;
        _loadedPath = path;
        _date = null;
        _input = 0;
        _output = 0;

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String)
            {
                _date = date.GetString();
            }

            if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Number)
            {
                _input = input.GetInt64();
            }

            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number)
            {
                _output = output.GetInt64();
            }
        }
        catch (JsonException)
        {
            // 文件损坏：视为全 0，不抛。下一次 Add 会自然覆盖掉坏文件。
            _date = null;
            _input = 0;
            _output = 0;
        }
        catch (IOException)
        {
            // 磁盘暂时不可读：同样按全 0 处理，保持后台链路可继续。
            _date = null;
            _input = 0;
            _output = 0;
        }
    }

    /// <summary>
    /// 将当前累计写回磁盘。
    /// </summary>
    private void Persist()
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new UsageFile(_date, _input, _output);
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (IOException)
        {
            // 写盘失败不影响内存累计：本次进程内预算判断仍然正确，
            // 只是该次用量不会跨进程保留。
        }
    }

    /// <summary>今日 UTC 日期（<c>yyyy-MM-dd</c>）。</summary>
    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>账本文件结构。</summary>
    private sealed record UsageFile(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("input")] long Input,
        [property: JsonPropertyName("output")] long Output);
}
