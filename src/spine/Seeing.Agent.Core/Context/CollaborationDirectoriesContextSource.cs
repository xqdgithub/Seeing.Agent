using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Paths;
using Seeing.Session.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 协同目录上下文源：把「本会话有哪些协同目录、是否可用」讲给模型听。
/// <para>
/// <b>读取实时、成员稳定排序、存在性探针</b>：每轮装配时经 <see cref="ICollaborationDirectories"/>
/// 读会话当前配置（与权限判定、文件工作台同源）；执行中发生的变更本轮不追溯，
/// 下一轮装配按增量叙述。条目按 path 排序，初始渲染字节稳定。
/// </para>
/// <para>
/// <b>不渲染 cwd</b>：<c>## Environment</c> 分节已由 <c>EnvironmentPromptSectionContributor</c>
/// 每请求实时渲染 <c>Working directory</c>。系统基线是快照、切换工作区不会重写，
/// 这里再渲染一次会产生两个互相矛盾的 cwd，所以标题只声明"以 Environment 分节为基准"。
/// </para>
/// <para>
/// <b>空列表返回 <see cref="ContextValueState.Value"/> 而不是 Removed</b>：空列表是"这个会话没有协同目录"
/// 这一确定事实，首轮必须建立基线并渲染"（无）"；<c>Removed</c> 会在首次装配被直接跳过。
/// </para>
/// </summary>
public sealed class CollaborationDirectoriesContextSource : IContextSource
{
    /// <summary>基线字符上限（截断在源内按行边界完成，截断结果即存库文本）。</summary>
    public const int BaselineCharLimit = 4_000;

    /// <summary>
    /// 单条存在性探针的短超时：装配是热路径，UNC / 休眠盘不得把它拖住。
    /// </summary>
    private static readonly TimeSpan ExistsProbeTimeout = TimeSpan.FromMilliseconds(250);

    private const string Title =
        "协同工作目录（可读写；相对路径以 Environment 分节的工作目录为基准）:";

    private const string EmptyLine = "- （无）";

    private const string TruncatedSuffix = "\n…（协同目录列表已按上下文基线上限截断）";

    /// <summary>本源稳定键（按类型全名；调用方据此判断本轮是否跳过）。</summary>
    public static ContextSourceKey SourceKey { get; } =
        new(typeof(CollaborationDirectoriesContextSource).FullName!);

    private readonly IContextExecutionScope _executionScope;
    private readonly ICollaborationDirectories _directories;
    private readonly ILogger<CollaborationDirectoriesContextSource> _logger;

    /// <summary>创建源：会话 id 来自执行作用域，目录内容每轮经读端口取当前值。</summary>
    public CollaborationDirectoriesContextSource(
        IContextExecutionScope executionScope,
        ICollaborationDirectories directories,
        ILogger<CollaborationDirectoriesContextSource>? logger = null)
    {
        _executionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _logger = logger ?? NullLogger<CollaborationDirectoriesContextSource>.Instance;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => SourceKey;

    /// <inheritdoc />
    public ContextSourceScope Scope => ContextSourceScope.Session;

    /// <inheritdoc />
    public int ValueSchemaVersion => 1;

    /// <inheritdoc />
    public ContextSourceTarget Target => ContextSourceTarget.System;

    /// <inheritdoc />
    public int MaxBaselineChars => BaselineCharLimit;

    /// <inheritdoc />
    public async Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var directories = await _directories
            .GetAsync(_executionScope.SessionId, ct)
            .ConfigureAwait(false);

        var entries = new List<DirectoryEntry>(directories.Count);
        foreach (var directory in directories)
        {
            if (directory is null || string.IsNullOrWhiteSpace(directory.Path))
                continue;

            var path = directory.Path.Trim();
            if (IndexOf(entries, path) >= 0)
                continue;

            var exists = await ProbeExistsAsync(path, ct).ConfigureAwait(false);
            entries.Add(new DirectoryEntry(path, NormalizeNote(directory.Note), exists));
        }

        // 稳定排序：canonical 与初始渲染都必须与成员登记顺序解耦
        entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        return new ContextSourceValue(
            ContextValueState.Value,
            BuildCanonical(entries),
            entries);
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var entries = EntriesFrom(value);

        var builder = new StringBuilder();
        builder.Append(Title);

        if (entries.Count == 0)
        {
            builder.Append('\n').Append(EmptyLine);
            return Truncate(builder.ToString());
        }

        foreach (var entry in entries)
        {
            builder.Append('\n').Append("- ").Append(entry.Path);
            if (!string.IsNullOrEmpty(entry.Note))
                builder.Append('（').Append(entry.Note).Append('）');
        }

        return Truncate(builder.ToString());
    }

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        if (previous.Canonical is null || current.Canonical is null)
        {
            // 没有上一份基线（首次 / 上次不可用）：只推进基线，不叙述
            return null;
        }

        var before = ReadEntries(previous.Canonical);
        var after = ReadEntries(current.Canonical);

        var beforeByPath = ToMap(before);
        var afterByPath = ToMap(after);

        var lines = new List<string>();

        foreach (var entry in after)
        {
            if (!beforeByPath.TryGetValue(entry.Path, out var old))
            {
                lines.Add(DescribeAdded(entry));
                continue;
            }

            if (old.Exists && !entry.Exists)
                lines.Add($"协同目录不可用：{entry.Path}（目录不存在）");
            else if (!old.Exists && entry.Exists)
                lines.Add($"协同目录已恢复：{entry.Path}");

            if (!string.Equals(old.Note, entry.Note, StringComparison.Ordinal))
            {
                lines.Add(string.IsNullOrEmpty(entry.Note)
                    ? $"协同目录备注已清空：{entry.Path}"
                    : $"协同目录备注已更新：{entry.Path}（{entry.Note}）");
            }
        }

        foreach (var entry in before)
        {
            if (!afterByPath.ContainsKey(entry.Path))
                lines.Add($"协同目录已移除：{entry.Path}");
        }

        return lines.Count == 0 ? null : Truncate(string.Join('\n', lines));
    }

    private static string DescribeAdded(DirectoryEntry entry) =>
        string.IsNullOrEmpty(entry.Note)
            ? $"协同目录已添加：{entry.Path}"
            : $"协同目录已添加：{entry.Path}（{entry.Note}）";

    /// <summary>
    /// 带取消与短超时的存在性探针。
    /// <para>超时按"可达"处理（返回 true）：宁可少叙述一次"不可用"，也不要把网络盘/休眠盘的抖动
    /// 说成"目录不存在"——后者会让模型主动放弃一个合法根。</para>
    /// </summary>
    private static async Task<bool> ProbeExistsAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExistsProbeTimeout);

        try
        {
            return await Task.Run(() => Directory.Exists(path), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>值结构：<c>{"directories":[{"path","note","exists"}]}</c>（按 path ordinal 排序）。</summary>
    private static JsonElement BuildCanonical(IReadOnlyList<DirectoryEntry> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["note"] = entry.Note,
                ["exists"] = entry.Exists
            });
        }

        var root = new JsonObject { ["directories"] = array };
        return JsonSerializer.SerializeToElement(root);
    }

    private static IReadOnlyList<DirectoryEntry> EntriesFrom(ContextSourceValue value)
    {
        if (value.State == ContextValueState.Removed)
            return Array.Empty<DirectoryEntry>();

        if (value.Payload is IReadOnlyList<DirectoryEntry> payload)
            return payload;

        return ReadEntries(value.Canonical);
    }

    /// <summary>从 canonical 读条目（previous 值的 payload 通常不可用，diff 只能靠它）。</summary>
    private static IReadOnlyList<DirectoryEntry> ReadEntries(JsonElement? canonical)
    {
        if (canonical is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("directories", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DirectoryEntry>();
        }

        var result = new List<DirectoryEntry>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var note = ReadString(item, "note");
            var exists = item.TryGetProperty("exists", out var existsNode)
                         && existsNode.ValueKind != JsonValueKind.False;

            result.Add(new DirectoryEntry(path, NormalizeNote(note), exists));
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return result;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;

        // 备注是单行叙述：换行会被渲染成假的分节，必须压平
        return note.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static Dictionary<string, DirectoryEntry> ToMap(IReadOnlyList<DirectoryEntry> entries)
    {
        var map = new Dictionary<string, DirectoryEntry>(
            entries.Count,
            StringComparer.FromComparison(PathBoundary.PathComparison));

        foreach (var entry in entries)
            map[entry.Path] = entry;

        return map;
    }

    private static int IndexOf(IReadOnlyList<DirectoryEntry> entries, string path)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (PathBoundary.AreSame(entries[i].Path, path))
                return i;
        }

        return -1;
    }

    /// <summary>按行边界截断到上限（结果确定，重复渲染逐字节相等）。</summary>
    private static string Truncate(string text)
    {
        if (text.Length <= BaselineCharLimit)
            return text;

        var cut = text.LastIndexOf('\n', BaselineCharLimit - 1);
        if (cut <= 0)
            cut = BaselineCharLimit;

        return text[..cut].TrimEnd() + TruncatedSuffix;
    }

    /// <summary>一条协同目录的本次读取事实（exists 是探针结果，不落盘到成员集合里）。</summary>
    private sealed record DirectoryEntry(string Path, string? Note, bool Exists);
}
