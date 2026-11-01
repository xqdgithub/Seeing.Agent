using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// 记忆召回源：<c>Target = Message</c>、<c>Scope = Project</c>。
/// <para>
/// <b>为什么是 <see cref="ContextSourceTarget.Message"/> 而不是 <see cref="ContextSourceTarget.System"/></b>：
/// 装配器对 System 源只在首次建立时渲染基线，之后只叙述 delta。而本轮用户查询每轮变化 → 召回每轮变化，
/// 若落到 System，系统提示会永久停在第一轮内容，却每轮继续产出增量消息（语义分裂）。
/// Message 落点让内容以消息形式进入历史，与「本轮相关记忆」的时效语义一致。
/// </para>
/// <para>
/// <b>为什么无命中返回 <see cref="ContextValueState.Unavailable"/> 而不是 <see cref="ContextValueState.Removed"/></b>：
/// <c>IMemoryRecallService.RecallAsync</c> 内部 catch 所有异常并返回空列表，源无法区分「确实无命中」与「IO 失败」。
/// 若按 <see cref="ContextValueState.Removed"/> 处理，一次瞬时失败就会向历史叙述「记忆已移除」。
/// </para>
/// <para>
/// <c>Canonical</c> = <c>{"hits":[{"path","score"}]}</c>（按 path ordinal 排序，<b>不含正文</b>）：
/// 必须是 JSON 对象——装配器把非对象形态判为「声明有值但没有可用 Canonical」并跳过整个源。
/// <c>Payload</c> 携带本次召回条目（含标题与正文），仅供本次渲染使用、不落盘。
/// </para>
/// </summary>
public sealed class MemoryContextSource : IContextSource
{
    /// <summary>基线文本字符上限（截断由本类执行，截断结果即存库文本）。</summary>
    /// <remarks>统一取 8000，不与 <c>Retrieval.MaxInjectTokens * 4</c> 并用（后者只约束召回条数）。</remarks>
    public const int BaselineCharLimit = 8_000;

    /// <summary>基线区块标题。</summary>
    public const string Heading = "## Relevant memories";

    /// <summary>单条摘要的字符上限。</summary>
    public const int SummaryCharLimit = 240;

    private const string TruncatedSuffix = "\n…（记忆清单已按上下文基线上限截断）";

    private const string SummaryTruncatedSuffix = "…";

    private readonly IContextExecutionScope _executionScope;
    private readonly IMemoryRecallService _recall;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly MemoryModuleActivity? _activity;
    private readonly ILogger<MemoryContextSource> _logger;

    /// <param name="executionScope">执行级作用域（本轮用户查询的唯一来源）。</param>
    /// <param name="recall">记忆召回端口（内部已有超时与异常兜底）。</param>
    /// <param name="options">记忆配置（热重载，用于检索模式判定）。</param>
    /// <param name="activity">
    /// 模块运行门控；<c>null</c> 表示未注入（测试或独立使用场景），视为活跃，
    /// 避免把「无模块生命周期」误判成停用。
    /// </param>
    /// <param name="logger">可选日志（缺省为空日志，便于测试直接构造）。</param>
    public MemoryContextSource(
        IContextExecutionScope executionScope,
        IMemoryRecallService recall,
        IOptionsMonitor<MemoryOptions> options,
        MemoryModuleActivity? activity = null,
        ILogger<MemoryContextSource>? logger = null)
    {
        _executionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
        _recall = recall ?? throw new ArgumentNullException(nameof(recall));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activity = activity;
        _logger = logger ?? NullLogger<MemoryContextSource>.Instance;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => new(typeof(MemoryContextSource).FullName!);

    /// <inheritdoc />
    public ContextSourceScope Scope => ContextSourceScope.Project;

    /// <inheritdoc />
    public int ValueSchemaVersion => 1;

    /// <inheritdoc />
    public ContextSourceTarget Target => ContextSourceTarget.Message;

    /// <inheritdoc />
    public int MaxBaselineChars => BaselineCharLimit;

    /// <inheritdoc />
    public async Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 1) 模块停用：不召回、不注入，也不叙述（保留上次已叙述内容）
        if (_activity?.IsActive == false)
            return Unavailable();

        // 2) 仅工具检索模式：记忆由 memory_search 工具按需取用，不自动注入
        var retrieval = _options.CurrentValue.Retrieval;
        if (retrieval.Mode == MemoryRetrievalMode.ToolsOnly)
            return Unavailable();

        // 3) 无本轮查询（纯系统批次 / 命令短路）：没有检索依据
        var query = _executionScope.UserQuery;
        if (string.IsNullOrWhiteSpace(query))
            return Unavailable();

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await _recall.RecallAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 召回失败不得外抛（契约要求可恢复失败按不可用处理）
            _logger.LogWarning(ex, "记忆召回失败，本源按不可用处理");
            return Unavailable();
        }

        // 4) 无命中也按不可用：RecallAsync 内部吞掉异常返回空列表，无法区分失败与无命中。
        //    若按 Removed 处理，一次瞬时失败就会叙述「记忆已移除」。
        if (hits.Count == 0)
            return Unavailable();

        var ordered = hits
            .OrderBy(hit => hit.Node.Path, StringComparer.Ordinal)
            .ThenByDescending(hit => hit.Score)
            .ToList();

        // Canonical 必须是 JSON 对象（数组形态会被装配器判为无效并跳过整个源）
        var canonical = JsonSerializer.SerializeToElement(new
        {
            hits = ordered.Select(hit => new
            {
                path = hit.Node.Path,
                score = hit.Score
            }).ToList()
        });

        return new ContextSourceValue(ContextValueState.Value, canonical, ordered);
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var hits = HitsFromValue(value);
        if (hits.Count == 0)
            return string.Empty;

        var lines = new List<string>
        {
            Heading,
            string.Empty,
            "以下记忆与本次请求相关（按相关度排序，仅作参考，以当前代码与文件为准）："
        };

        foreach (var hit in hits)
        {
            lines.Add(string.Empty);
            lines.Add(RenderHitLine(hit));
        }

        // 显式使用 \n：提示词文本不随平台变化（字节稳定的实现细节）
        return Truncate(string.Join('\n', lines));
    }

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        // previous.Payload 恒为 null（装配器只从落盘的 CurrentValues 重建 previous），
        // 故增量叙述只能依赖 Canonical 的 path/score，绝不读标题或正文。
        var previousHits = EntriesFromCanonical(previous.Canonical);
        var currentHits = current.State == ContextValueState.Removed
            ? []
            : EntriesFromCanonical(current.Canonical);

        if (SequenceEqual(previousHits, currentHits))
            return null;

        var previousPaths = previousHits.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var currentPaths = currentHits.ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        var lines = new List<string>();
        foreach (var entry in currentHits)
        {
            if (!previousPaths.ContainsKey(entry.Path))
            {
                lines.Add($"- 新增相关记忆：{entry.Path}（score {FormatScore(entry.Score)}）");
            }
        }

        foreach (var entry in previousHits)
        {
            if (!currentPaths.ContainsKey(entry.Path))
                lines.Add($"- 不再相关：{entry.Path}");
        }

        if (lines.Count == 0)
        {
            // 同一批路径但相关度变化：只叙述排序变化，不逐条列举
            return "记忆召回的相关度已更新（条目未变）。";
        }

        return "记忆召回已变化：\n\n" + string.Join('\n', lines);
    }

    // ===== 内部 =====

    private static ContextSourceValue Unavailable() =>
        new(ContextValueState.Unavailable, null, null);

    /// <summary>从 payload（本次召回条目）重建渲染用命中；payload 缺失时退回 canonical。</summary>
    private IReadOnlyList<SearchHit> HitsFromValue(ContextSourceValue value)
    {
        if (value.Payload is IReadOnlyList<SearchHit> payload)
            return payload;

        if (value.Payload is not null)
        {
            // Payload 形状异常：记录可诊断事实，退回 canonical（绝不静默）
            _logger.LogWarning(
                "记忆源 Payload 形状不是召回命中列表，已退回 canonical：{PayloadType}",
                value.Payload.GetType().FullName);
        }

        return EntriesFromCanonical(value.Canonical)
            .Select(entry => new SearchHit(
                FileNode.Create(entry.Path, entry.Path),
                entry.Score,
                entry.Score,
                0))
            .ToList();
    }

    /// <summary>从 canonical 的 <c>hits</c> 数组重建条目（previous 值的唯一来源）。</summary>
    private static IReadOnlyList<MemoryHitEntry> EntriesFromCanonical(JsonElement? canonical)
    {
        if (canonical is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("hits", out var hits) ||
            hits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<MemoryHitEntry>();
        foreach (var item in hits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var path = ReadString(item, "path");
            if (string.IsNullOrEmpty(path))
                continue;

            result.Add(new MemoryHitEntry(path, ReadScore(item, "score")));
        }

        return result;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadScore(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var score)
            ? score
            : 0;

    /// <summary>比较两组条目的路径与相关度（canonical 语义相等，与 JSON 排版无关）。</summary>
    private static bool SequenceEqual(
        IReadOnlyList<MemoryHitEntry> left,
        IReadOnlyList<MemoryHitEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Path, right[i].Path, StringComparison.Ordinal))
                return false;

            if (!left[i].Score.Equals(right[i].Score))
                return false;
        }

        return true;
    }

    /// <summary>渲染单条命中：<c>- (score) 标题: 摘要</c>（摘要压成单行并截断到 240 字符）。</summary>
    private static string RenderHitLine(SearchHit hit)
    {
        var title = string.IsNullOrWhiteSpace(hit.Node.Metadata.Title)
            ? hit.Node.Path
            : hit.Node.Metadata.Title!;

        var summary = Summarize(hit.Node.Content);
        var prefix = $"- ({FormatScore(hit.Score)}) {CollapseWhitespace(title)}";

        return summary.Length == 0 ? prefix : $"{prefix}: {summary}";
    }

    private static string Summarize(string? content)
    {
        var collapsed = CollapseWhitespace(content);
        if (collapsed.Length <= SummaryCharLimit)
            return collapsed;

        return collapsed[..SummaryCharLimit].TrimEnd() + SummaryTruncatedSuffix;
    }

    private static string FormatScore(double score) =>
        score.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>正文压成单行：多行内容会破坏「一行一条目」的清单结构。</summary>
    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    sb.Append(' ');

                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString();
    }

    /// <summary>按行边界截断到上限（截断结果确定，重复渲染逐字节相等）。</summary>
    private static string Truncate(string text)
    {
        if (text.Length <= BaselineCharLimit)
            return text;

        var cut = text.LastIndexOf('\n', BaselineCharLimit - 1);
        if (cut <= 0)
            cut = BaselineCharLimit;

        return text[..cut].TrimEnd() + TruncatedSuffix;
    }

    /// <summary>canonical 中的一条命中（只有路径与相关度，不含正文）。</summary>
    private readonly record struct MemoryHitEntry(string Path, double Score);
}
