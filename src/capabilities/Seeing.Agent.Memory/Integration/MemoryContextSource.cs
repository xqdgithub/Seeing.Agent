using System.Globalization;
using System.Security.Cryptography;
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
using Seeing.Session.Core;

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
/// <c>Canonical</c> = <c>{"hits":[{"path","score","contentHash"}]}</c>（按 path ordinal 排序，<b>不含正文</b>）：
/// 必须是 JSON 对象——装配器把非对象形态判为「声明有值但没有可用 Canonical」并跳过整个源。
/// <c>contentHash</c> 是正文的 SHA256 前 16 位：<c>score</c> 是 RRF 分数、**由排名决定**，
/// 记忆文件正文更新后排名常常不变 → 分数逐位相同；只有内容指纹才能把「正文变了」体现为值变化。
/// 缺了它，正文更新永久不可见（增量叙述无从判断），还会每轮触发装配器的「同值不同文本」告警。
/// </para>
/// <para>
/// <c>Payload</c> 携带本次召回条目（含标题与正文），仅供本次渲染与增量叙述使用、不落盘。
/// </para>
/// </summary>
public sealed class MemoryContextSource : IContextSource, IContextSourceMessageMetadata
{
    /// <summary>基线文本字符上限（截断由本类执行，截断结果即存库文本）。</summary>
    /// <remarks>
    /// 统一取 8000。<c>MemoryRetrievalOptions.MaxInjectTokens</c> 在本路径上**没有任何消费者**：
    /// 召回条数由 <c>MemoryRetrievalOptions.TopK</c> 在 <c>MemoryRecallService.RecallAsync</c> 内决定
    /// （<c>Take(opts.TopK)</c>），本源的截断是字符上限、与 token 无关。
    /// 该配置对「记忆注入」已失效，见 <c>docs/architecture/24</c> §已知空窗。
    /// </remarks>
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

    /// <summary>
    /// 值结构版本；<c>hits</c> 元素新增 <c>contentHash</c> 时递增，
    /// 旧基线（无指纹）随之整体重建。
    /// </summary>
    public int ValueSchemaVersion => 2;

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

        IReadOnlyList<SearchHit>? hits;
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
        if (hits is null || hits.Count == 0)
            return Unavailable();

        var entries = BuildEntries(hits);
        if (entries.Count == 0)
        {
            // 命中全部不可用（Node / Path 缺失）：等同无命中
            _logger.LogDebug("记忆召回命中均不可用（缺少节点或路径），本源按不可用处理");
            return Unavailable();
        }

        // Canonical 必须是 JSON 对象（数组形态会被装配器判为无效并跳过整个源）
        var canonical = JsonSerializer.SerializeToElement(new
        {
            hits = entries.Select(entry => new
            {
                path = entry.Path,
                score = entry.Score,
                contentHash = entry.ContentHash
            }).ToList()
        });

        var payload = entries.Select(entry => entry.Hit).ToList();

        return new ContextSourceValue(ContextValueState.Value, canonical, payload);
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var entries = EntriesOf(value);
        if (entries.Count == 0)
            return string.Empty;

        var lines = new List<string>
        {
            Heading,
            string.Empty,
            "以下记忆与本次请求相关（按相关度排序，仅作参考，以当前代码与文件为准）："
        };

        foreach (var (entry, hit) in entries)
        {
            lines.Add(string.Empty);
            lines.Add(RenderHitLine(entry, hit));
        }

        // 显式使用 \n：提示词文本不随平台变化（字节稳定的实现细节）
        return Truncate(string.Join('\n', lines));
    }

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        // previous.Payload 恒为 null（装配器只从落盘的 CurrentValues 重建 previous），
        // 故「哪些条目变了」只能由 Canonical 的 path/score/contentHash 判定；
        // 但 current.Payload 是可用的，正文更新时可以把新正文一并叙述出来。
        var previousEntries = EntriesFromCanonical(previous.Canonical);
        var currentEntries = current.State == ContextValueState.Removed
            ? []
            : EntriesFromCanonical(current.Canonical);

        if (SequenceEqual(previousEntries, currentEntries))
            return null;

        var previousByPath = ToPathMap(previousEntries);
        var currentByPath = ToPathMap(currentEntries);
        var currentHits = HitsByPath(current.Payload);

        var lines = new List<string>();
        var relevanceChanged = false;

        foreach (var entry in currentEntries)
        {
            if (!previousByPath.TryGetValue(entry.Path, out var before))
            {
                lines.Add(RenderChangedLine("新增相关记忆", entry, currentHits));
                continue;
            }

            if (!string.Equals(before.ContentHash, entry.ContentHash, StringComparison.Ordinal))
            {
                lines.Add(RenderChangedLine("记忆内容已更新", entry, currentHits));
                continue;
            }

            if (!before.Score.Equals(entry.Score))
                relevanceChanged = true;
        }

        foreach (var entry in previousEntries)
        {
            if (!currentByPath.ContainsKey(entry.Path))
                lines.Add($"- 不再相关：{entry.Path}");
        }

        if (lines.Count == 0)
        {
            // 路径与正文都未变、只有 RRF 分数漂移：只叙述相关度变化，不逐条列举
            return relevanceChanged ? "记忆召回的相关度已更新（条目未变）。" : null;
        }

        return "记忆召回已变化：\n\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// 为落库的初始渲染消息提供「机器注入」元数据。
    /// <para>
    /// <b>使用的键</b>：<see cref="SessionMetadataKeys.ContextUpdate"/>（<c>bool</c>）与
    /// <see cref="SessionMetadataKeys.ContextUpdateSources"/>（<c>string[]</c>，本次为源键）。
    /// 二者是仓内**既有**的「机器注入 user 消息」判据，已有消费方直接生效：
    /// <c>SessionTitleService.IsIntentionalUserMessage</c>（不计入意图用户消息，不喂给标题模型）
    /// 与 WebUI 的 <c>MessageViewModelFactory</c>（呈现为上下文注入而非用户气泡）。
    /// 不新建键的理由：单独新增一个 <c>memoryContext</c> 键若不同时改 Core 的标题服务，
    /// 标题模型仍会把记忆块当用户发言消费——那等于没有修复问题。
    /// </para>
    /// <para>
    /// 键值只依赖源键，不依赖正文：重放路径（<c>Payload</c> 不可用）下同样成立。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, object>? GetMessageMetadata(
        string renderedText,
        ContextSourceValue value)
    {
        if (string.IsNullOrWhiteSpace(renderedText))
            return null;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SessionMetadataKeys.ContextUpdate] = true,
            [SessionMetadataKeys.ContextUpdateSources] = new[] { Key.Value }
        };
    }

    // ===== 内部 =====

    private static ContextSourceValue Unavailable() =>
        new(ContextValueState.Unavailable, null, null);

    /// <summary>
    /// 把召回命中投影为规范化条目（按 path ordinal 排序、同 path 去重）。
    /// <para>
    /// 防御三件事：<c>Node</c> 为 null、<c>Path</c> 为空、<c>Score</c> 为 NaN/Infinity。
    /// 后者会直接让 <c>JsonSerializer</c> 抛异常；只跳过坏条目，不连坐整个源。
    /// </para>
    /// </summary>
    private List<MemoryHitEntry> BuildEntries(IReadOnlyList<SearchHit> hits)
    {
        var result = new List<MemoryHitEntry>(hits.Count);
        var skipped = 0;

        foreach (var hit in hits)
        {
            var node = hit?.Node;
            var path = node?.Path;
            if (node is null || string.IsNullOrWhiteSpace(path))
            {
                skipped++;
                continue;
            }

            result.Add(new MemoryHitEntry(
                path,
                SanitizeScore(hit.Score),
                ContentHashOf(node.Content),
                hit));
        }

        if (skipped > 0)
        {
            _logger.LogDebug(
                "记忆召回命中缺少节点或路径，已跳过：{SkippedCount}/{TotalCount}",
                skipped,
                hits.Count);
        }

        // 同 path 只保留分数最高的一条（不确定的重复会在 Render 的按 path 聚合处引发歧义）
        return result
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(entry => entry.Score).First())
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>NaN / Infinity 不能进 JSON；一并归零并记录（不静默）。</summary>
    private double SanitizeScore(double score)
    {
        if (double.IsFinite(score))
            return score;

        _logger.LogDebug("记忆召回命中分数非有限值，已归零：{Score}", score);
        return 0;
    }

    /// <summary>正文的 SHA256 前 16 位（十六进制小写）；正文为空时也给稳定值。</summary>
    private static string ContentHashOf(string? content) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)))[..16];

    /// <summary>渲染用条目：canonical 事实 + 本次可用的原始命中（可能为 null）。</summary>
    private IReadOnlyList<(MemoryHitEntry Entry, SearchHit? Hit)> EntriesOf(ContextSourceValue value)
    {
        var hits = HitsByPath(value.Payload);
        var entries = EntriesFromCanonical(value.Canonical);

        if (entries.Count == 0)
            return [];

        var result = new List<(MemoryHitEntry, SearchHit?)>(entries.Count);
        foreach (var entry in entries)
            result.Add((entry, hits.TryGetValue(entry.Path, out var hit) ? hit : null));

        return result;
    }

    /// <summary>
    /// 把 <c>Payload</c> 投影成 path → 命中的映射。
    /// <para>形状异常时返回空映射（渲染退化为「只有路径」），记 Debug 而非 Warning：
    /// 这不是故障，只是本次没有正文可用。</para>
    /// </summary>
    private IReadOnlyDictionary<string, SearchHit> HitsByPath(object? payload)
    {
        if (payload is not IEnumerable<SearchHit> hits)
        {
            if (payload is not null)
            {
                _logger.LogDebug(
                    "记忆源 Payload 形状不是召回命中列表，渲染退化为仅路径：{PayloadType}",
                    payload.GetType().FullName);
            }

            return new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var path = hit?.Node?.Path;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            // 同 path 取分数最高的一条：与 BuildEntries 的去重口径一致
            if (!map.TryGetValue(path, out var existing) || hit.Score > existing.Score)
                map[path] = hit;
        }

        return map;
    }

    /// <summary>从 canonical 的 <c>hits</c> 数组重建条目（previous 值的唯一来源）。</summary>
    private static List<MemoryHitEntry> EntriesFromCanonical(JsonElement? canonical)
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

            result.Add(new MemoryHitEntry(
                path,
                ReadScore(item, "score"),
                ReadString(item, "contentHash"),
                null));
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
        value.TryGetDouble(out var score) &&
        double.IsFinite(score)
            ? score
            : 0;

    /// <summary>按 path 聚合（重复 path 取首条，绝不抛 <c>ArgumentException</c>）。</summary>
    private static Dictionary<string, MemoryHitEntry> ToPathMap(IEnumerable<MemoryHitEntry> entries)
    {
        var map = new Dictionary<string, MemoryHitEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            map.TryAdd(entry.Path, entry);

        return map;
    }

    /// <summary>比较两组条目的路径、相关度与内容指纹（canonical 语义相等，与 JSON 排版无关）。</summary>
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

            if (!string.Equals(left[i].ContentHash, right[i].ContentHash, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>渲染单条命中：<c>- (score) 标题: 摘要</c>（摘要压成单行并截断到 240 字符）。</summary>
    private static string RenderHitLine(MemoryHitEntry entry, SearchHit? hit)
    {
        var title = ResolveTitle(entry.Path, hit);
        var summary = Summarize(hit?.Node?.Content);
        var prefix = $"- ({FormatScore(entry.Score)}) {title}";

        return summary.Length == 0 ? prefix : $"{prefix}: {summary}";
    }

    /// <summary>
    /// 渲染「新增 / 内容更新」叙述行；有当前正文时连同新正文一起叙述，
    /// 使模型不必再调工具就能看到更新后的内容。
    /// </summary>
    private static string RenderChangedLine(
        string reason,
        MemoryHitEntry entry,
        IReadOnlyDictionary<string, SearchHit> currentHits)
    {
        var hit = currentHits.TryGetValue(entry.Path, out var found) ? found : null;
        var title = ResolveTitle(entry.Path, hit);
        var summary = Summarize(hit?.Node?.Content);

        return summary.Length == 0
            ? $"- {reason}：{entry.Path}"
            : $"- {reason}：{title}：{summary}";
    }

    private static string ResolveTitle(string path, SearchHit? hit)
    {
        var title = hit?.Node?.Metadata?.Title;
        return string.IsNullOrWhiteSpace(title) ? path : CollapseWhitespace(title);
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

    /// <summary>canonical 中的一条命中（路径、相关度、内容指纹；不含正文）。</summary>
    private readonly record struct MemoryHitEntry(
        string Path,
        double Score,
        string ContentHash,
        SearchHit? Hit);
}
