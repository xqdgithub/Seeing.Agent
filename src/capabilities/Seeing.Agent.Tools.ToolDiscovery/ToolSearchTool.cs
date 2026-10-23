using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tools.ToolDiscovery;

/// <summary>
/// 工具检索 — 在"可发现但未内联"的目录（deferred 集合）中检索工具，
/// 按 name/summary/full 三档返回；full 命中项登记为本执行内可调用并返回权威全量定义，
/// 下一次执行起自动内联。
/// <para>只依赖 Abstractions + Seeing.Session + Tools.Support；经注入端口获取能力，不引用 Core。</para>
/// <para>默认值（limit/offset/分页上限/full 硬上限）为工具内常量：能力包不读 Core options。</para>
/// </summary>
[ToolCapability(ToolCapabilityKeys.ExposureLoad, "true")]
public sealed class ToolSearchTool : ToolBase
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 30;
    private const int FullPageHardCap = 3;

    private const int ScoreExactId = 20;
    private const int ScoreIdWord = 12;
    private const int ScoreIdSubstring = 8;
    private const int ScoreSummarySubstring = 4;
    private const int ScoreTextSubstring = 2;

    private readonly ISessionToolSchemaResolver _resolver;
    private readonly IToolDiscoveryStore _discoveryStore;

    /// <summary>创建 ToolSearchTool 实例。</summary>
    public ToolSearchTool(
        ISessionToolSchemaResolver resolver,
        IToolDiscoveryStore discoveryStore,
        ILogger<ToolSearchTool>? logger = null)
        : base(logger ?? NullLogger<ToolSearchTool>.Instance)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _discoveryStore = discoveryStore ?? throw new ArgumentNullException(nameof(discoveryStore));
    }

    /// <inheritdoc />
    public override string Id => Seeing.Agent.Abstractions.Tools.ToolIdConventions.SearchToolId;

    /// <inheritdoc />
    public override string Description =>
        "检索当前会话可发现但尚未内联的工具目录（延迟暴露集合）。" +
        "query 省略时浏览全部；可经 namespace 限定范围；" +
        "detail=name/summary/full（默认 summary）：full 将命中项登记为本执行内可调用并返回完整定义，" +
        "命中后本次执行内即可直接调用，下一次执行起自动内联，每页最多 3 条。" +
        $"支持 limit（默认 {DefaultLimit}，上限 {MaxLimit}）与 offset 分页。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => ["discovery", "tools"];

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "检索关键词（驼峰/下划线自动分词，支持单复数还原）；省略则浏览全部目录"
            },
            @namespace = new
            {
                type = "string",
                description = "命名空间限定（前缀/等值，大小写不敏感）"
            },
            detail = new
            {
                type = "string",
                @enum = new[] { "name", "summary", "full" },
                description = "详情档位：name=仅 id；summary=id+签名/摘要（默认）；full=写入发现态+完整定义（下一轮生效，每页最多 3 条）"
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLimit,
                description = $"返回条数上限（默认 {DefaultLimit}，上限 {MaxLimit}）"
            },
            offset = new
            {
                type = "integer",
                minimum = 0,
                description = "分页起始偏移（默认 0）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;
        var query = GetStringArgument(arguments, "query");
        var nsFilter = GetStringArgument(arguments, "namespace");
        var detail = (GetStringArgument(arguments, "detail") ?? "summary").Trim().ToLowerInvariant();

        if (detail is not ("name" or "summary" or "full"))
            return Failure($"未知的 detail: {detail}。可选值：name、summary、full");

        var limit = Math.Clamp(GetIntArgument(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        var offset = Math.Max(GetIntArgument(arguments, "offset") ?? 0, 0);

        if (context.Agent is null)
            return Failure("需要 Agent 上下文才能确定本会话的工具视图");

        // 工具视图唯一来源：优先执行级冻结快照（能搜到 == 能调），
        // 仅非执行器宿主（未透传作用域）才回退现场结算。
        SessionToolSchemaResolution? settlement = null;
        if (context.ToolScope is null)
        {
            settlement = await _resolver
                .ResolveAsync(context.SessionId, context.Agent, ct).ConfigureAwait(false);
        }

        var corpus = context.ToolScope?.DeferredTools ?? settlement!.DeferredTools;

        if (corpus is null || corpus.Count == 0)
        {
            var emptyMeta = BuildMetadata(detail, 0, 0, 0, null);
            return Success("当前无额外可发现工具，所有可用工具已内联。", emptyMeta);
        }

        IReadOnlyList<ExposedTool> scoped = string.IsNullOrWhiteSpace(nsFilter)
            ? corpus
            : corpus
                .Where(e => !string.IsNullOrEmpty(e.Namespace) &&
                            e.Namespace.StartsWith(nsFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (scoped.Count == 0)
        {
            var text = $"未命中：命名空间 '{nsFilter}' 下没有可发现工具。\n" +
                       $"可用命名空间:\n{FormatNamespaceCounts(corpus)}\n" +
                       "建议：检查命名空间拼写，或去掉 namespace 参数重新检索。";
            var meta = BuildMetadata(detail, 0, 0, 0, null);
            return Success(text, meta);
        }

        var ranked = Rank(scoped, query);

        if (ranked.Count == 0)
        {
            var text = $"未命中：关键词 '{query}' 没有匹配到可发现工具。\n" +
                       $"可用命名空间:\n{FormatNamespaceCounts(scoped)}\n" +
                       "建议：换用更宽泛或不同措辞的关键词，或去掉 namespace 限定重新检索。";
            var meta = BuildMetadata(detail, 0, 0, 0, null);
            return Success(text, meta);
        }

        var total = ranked.Count;

        if (offset >= total)
        {
            var text = $"offset 越界：offset={offset} 不在有效区间 [0, {total})，返回 0 条。";
            var meta = BuildMetadata(detail, total, 0, 0, null);
            return Success(text, meta);
        }

        var effectiveLimit = detail == "full" ? Math.Min(limit, FullPageHardCap) : limit;
        var page = ranked.Skip(offset).Take(effectiveLimit).ToList();
        var remaining = total - offset - page.Count;
        var nextOffset = offset + page.Count;
        var hitIds = page.Select(p => p.Entry.Id).ToList();

        var discovered = detail != "full" || hitIds.Count == 0 || _discoveryStore.Add(context.SessionId, hitIds);

        // 只声明"本执行内已加载"，由执行器统一应用（能力键门控 + 审计），能力包不直接写作用域。
        IReadOnlyList<string>? loadedToolIds = null;
        if (detail == "full" && discovered && hitIds.Count > 0)
            loadedToolIds = hitIds;

        var sb = new StringBuilder();
        sb.Append($"工具检索结果（detail={detail}, total={total}, returned={page.Count}, remaining={remaining}）:");
        if (detail == "full" && limit > FullPageHardCap)
        {
            sb.Append($"\n(detail=full 每页最多加载 {FullPageHardCap} 条全量定义，已按硬上限截断)");
        }

        for (var i = 0; i < page.Count; i++)
        {
            var entry = page[i].Entry;
            sb.Append('\n').Append(i + 1).Append(". ").Append(entry.Id);
            if (detail == "name")
                continue;

            if (!string.IsNullOrWhiteSpace(entry.Signature))
                sb.Append(" | ").Append(entry.Signature);

            if (detail != "full")
                continue;

            sb.Append(discovered
                ? context.ToolScope is not null
                    ? "\n   已加载：本次执行内可直接调用；下一次执行起自动内联:"
                    : "\n   已登记：下一次执行起可直接调用（本次执行内暂不可调用）:"
                : "\n   警告：发现态登记失败（会话不可用），该工具本次及后续请求均不可直接调用:");
            var schemaText = GetFullDefinition(entry);
            sb.Append('\n').Append(schemaText);
        }

        sb.Append('\n');
        if (remaining > 0)
        {
            sb.Append($"提示：优先使用更精确关键词或指定 namespace 参数收窄结果；如需继续翻页可用 offset={nextOffset}。");
            if (detail == "full")
            {
                sb.Append($"\n注意：继续以 detail=full 加载会写入过多 schema，建议先用 detail=summary 浏览候选。");
            }
        }
        else if (total > 0)
        {
            sb.Append("已到末页；若未找到合适工具，可换用更宽泛或不同措辞的关键词。");
        }

        var resultMeta = BuildMetadata(detail, total, page.Count, remaining, remaining > 0 ? nextOffset : null);
        var result = Success(sb.ToString(), resultMeta);
        result.LoadedToolIds = loadedToolIds;
        return result;
    }

    private static string GetFullDefinition(ExposedTool entry)
    {
        // full 定义直接取自本执行快照，不再回头查注册表或重新结算（避免第二条 schema 路径）。
        return "   " + JsonSerializer.Serialize(entry.Schema);
    }

    private static Dictionary<string, object> BuildMetadata(
        string detail, int total, int returned, int remaining, int? nextOffset)
    {
        var meta = new Dictionary<string, object>
        {
            ["detail"] = detail,
            ["total"] = total,
            ["returned"] = returned,
            ["remaining"] = remaining,
        };
        if (nextOffset is not null)
            meta["next_offset"] = nextOffset.Value;
        return meta;
    }

    private static List<(ExposedTool Entry, int Score)> Rank(
        IReadOnlyList<ExposedTool> entries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries
                .Select(e => (Entry: e, Score: 0))
                .OrderBy(x => x.Entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var tokens = Tokenize(query).ToList();
        var fullQueryLower = query.Trim().ToLowerInvariant();
        var scored = new List<(ExposedTool, int)>();

        foreach (var entry in entries)
        {
            var idLower = entry.Id.ToLowerInvariant();
            var summaryLower = entry.Signature.ToLowerInvariant();
            var nsLower = (entry.Namespace ?? string.Empty).ToLowerInvariant();
            var fullText = string.Join(' ', entry.Id, entry.Namespace ?? "", entry.Signature)
                .ToLowerInvariant();
            var idWords = SplitWords(entry.Id).Select(Stem).ToHashSet(StringComparer.Ordinal);

            var score = 0;
            if (fullQueryLower == idLower)
                score += ScoreExactId;

            foreach (var token in tokens)
            {
                if (idWords.Contains(token))
                    score += ScoreIdWord;
                else if (idLower.Contains(token))
                    score += ScoreIdSubstring;
                else if (summaryLower.Contains(token))
                    score += ScoreSummarySubstring;
                else if (nsLower.Contains(token) || fullText.Contains(token))
                    score += ScoreTextSubstring;
            }

            if (score > 0)
                scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(x => x.Item2)
            .ThenBy(x => x.Item1.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string input) =>
        SplitWords(input).Select(Stem).Where(w => w.Length > 0);

    /// <summary>按分隔符与驼峰边界拆词并小写。</summary>
    private static List<string> SplitWords(string input)
    {
        var result = new List<string>();
        foreach (var part in input.Split(
                     ['_', '-', '.', '/', '\\', ':', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new StringBuilder();
            for (var i = 0; i < part.Length; i++)
            {
                var c = part[i];
                if (char.IsUpper(c) && sb.Length > 0)
                {
                    var prev = sb[^1];
                    var nextIsLower = i + 1 < part.Length && char.IsLower(part[i + 1]);
                    if (!char.IsUpper(prev) || nextIsLower)
                    {
                        result.Add(sb.ToString().ToLowerInvariant());
                        sb.Clear();
                    }
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
                result.Add(sb.ToString().ToLowerInvariant());
        }

        return result;
    }

    /// <summary>末尾 s/es 词形还原去复数。</summary>
    private static string Stem(string word)
    {
        if (word.Length > 2 && word.EndsWith("es", StringComparison.Ordinal))
            return word[..^2];
        if (word.Length > 2 && word.EndsWith("s", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    private static string FormatNamespaceCounts(IReadOnlyList<ExposedTool> entries) =>
        string.Join("\n", entries
            .GroupBy(e => e.Namespace ?? "(无命名空间)")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"  {g.Key}: {g.Count()} 个"));
}
