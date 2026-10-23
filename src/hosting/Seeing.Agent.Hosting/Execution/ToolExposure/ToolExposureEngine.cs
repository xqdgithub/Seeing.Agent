using System.Text;
using System.Text.Json;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 暴露结算输入（纯数据，无任何会话对象引用）。
/// </summary>
/// <param name="Tools">本轮候选工具（已按会话结算 + Agent 过滤）。</param>
/// <param name="AvailableIds">会话级可用工具 id 集（未经 Agent 过滤，用于发现集裁剪）。</param>
/// <param name="SettledToolCount">
/// 会话级结算工具数（**未经 Agent 过滤**）。升级阈值必须用它：
/// 否则切换 Agent（如 build→explore）会让工具数掉到阈值下，产生与"会话真实工具规模"无关的误判。
/// </param>
/// <param name="Discovered">本轮已生效发现集（发现顺序）。</param>
/// <param name="Escalated">会话当前升级粘滞位。</param>
/// <param name="Config">四级解析后的有效配置。</param>
internal sealed record ToolExposureInput(
    IReadOnlyList<FunctionToolSchema> Tools,
    IReadOnlySet<string>? AvailableIds,
    int SettledToolCount,
    IReadOnlyList<string> Discovered,
    bool Escalated,
    EffectiveExposureConfig Config);

/// <summary>
/// 暴露结算结果：本轮工具视图与判定结果。纯计算结果，不触碰会话。
/// </summary>
/// <param name="Discovered">裁剪后的发现集——只保留本轮会话仍可用的工具。唯一裁剪点。</param>
internal sealed record ToolExposureResult(
    IReadOnlyList<ExposedTool> Tools,
    ToolExposureLayout Layout,
    string? CatalogText,
    int SchemaTokens,
    int CatalogTokens,
    bool Escalated,
    IReadOnlyList<string> Discovered);

/// <summary>
/// 工具暴露结算引擎：**纯函数**。输入是值，输出是值，无 DI、无 I/O、无会话写入、确定。
/// <para>
/// 职责：分配暴露模式、判定升级、裁剪发现集、物化并排序内联集、按命名空间预算构建目录文本、统计度量。
/// 会话状态的全部写入由执行入口在同一轮开头统一提交（见 <see cref="SessionToolExposureDelta"/>）。
/// </para>
/// </summary>
internal static class ToolExposureEngine
{
    /// <summary>目录每条列表项的前缀开销（"- " 与换行）。</summary>
    private const int LineOverhead = 3;

    public static ToolExposureResult Compute(ToolExposureInput input, ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(counter);
        var cfg = input.Config;

        // 发现集裁剪：只保留会话级仍可用的工具（会话级集合未知时不裁剪）。
        var discovered = input.AvailableIds is null
            ? input.Discovered.ToList()
            : input.Discovered.Where(input.AvailableIds.Contains).ToList();

        // 候选工具元信息（单遍历：估算 token、推导命名空间与签名）。
        var pinnedIds = new HashSet<string>(cfg.PinnedTools, StringComparer.OrdinalIgnoreCase);
        var discoveredIds = new HashSet<string>(discovered, StringComparer.OrdinalIgnoreCase);
        var metas = input.Tools
            .Select((schema, index) => BuildMeta(schema, index, counter, cfg.CatalogLineMaxChars))
            .ToList();

        // 升级判定：显式 Tiered 恒定分层；Auto 依阈值与会话粘滞；All 永不升级。
        // 计数与 token 阈值都基于**会话级结算规模**（SettledToolCount / 未过滤候选 token），
        // 而非 Agent 过滤后的候选集——否则切换 Agent 会造成误升级或误降级。
        var allInlineTokens = metas.Sum(m => m.EstimatedTokens);
        var thresholdCount = Math.Max(input.SettledToolCount, input.Tools.Count);
        var escalated = input.Escalated;
        if (cfg.Mode == ToolExposurePolicyMode.Auto)
            escalated = escalated || ExceedsThreshold(thresholdCount, allInlineTokens, cfg);

        var layout = cfg.Mode == ToolExposurePolicyMode.Tiered
            ? ToolExposureLayout.Tiered
            : cfg.Mode == ToolExposurePolicyMode.Auto
                ? (escalated ? ToolExposureLayout.Tiered : ToolExposureLayout.Flat)
                : ToolExposureLayout.Flat;

        // 安全钳制：分层需要检索入口。本轮没有 tool_search 时不藏工具。
        var searchAvailable = metas.Any(m => IsSearchTool(m.Id));
        if (layout == ToolExposureLayout.Tiered && !searchAvailable)
            layout = ToolExposureLayout.Flat;

        // 分配暴露模式。
        var assigned = new List<(ToolMeta Meta, ToolExposureMode Mode)>(metas.Count);
        foreach (var meta in metas)
            assigned.Add((meta, ResolveMode(meta.Id, layout, pinnedIds, discoveredIds)));

        // 物化顺序：pinned（声明序）→ 已发现（发现序）→ 其余内联（id 序）。
        var ordered = new List<ExposedTool>(assigned.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void EmitInline(string id)
        {
            var hit = assigned.FirstOrDefault(a =>
                string.Equals(a.Meta.Id, id, StringComparison.OrdinalIgnoreCase)
                && a.Mode == ToolExposureMode.Inline);
            if (hit.Meta is null || !emitted.Add(hit.Meta.Id))
                return;
            ordered.Add(ToExposed(hit.Meta, hit.Mode));
        }

        void EmitDeferredInIdOrder()
        {
            foreach (var (meta, mode) in assigned
                         .Where(a => a.Mode == ToolExposureMode.Deferred)
                         .OrderBy(a => a.Meta.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (emitted.Add(meta.Id))
                    ordered.Add(ToExposed(meta, mode));
            }
        }

        foreach (var pinned in cfg.PinnedTools)
            EmitInline(pinned);
        foreach (var id in discovered)
            EmitInline(id);

        foreach (var (meta, mode) in assigned
                     .Where(a => a.Mode == ToolExposureMode.Inline)
                     .OrderBy(a => a.Meta.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (emitted.Add(meta.Id))
                ordered.Add(ToExposed(meta, mode));
        }

        EmitDeferredInIdOrder();

        // 隐藏的工具（如全量模式下的检索元工具）不进入本轮视图。
        var exposed = ordered.Where(t => t.Mode != ToolExposureMode.Hidden).ToList();

        // 目录：仅分层布局、且有延迟工具时生成。
        string? catalogText = null;
        var catalogTokens = 0;
        if (layout == ToolExposureLayout.Tiered)
        {
            var deferred = exposed.Where(t => t.Mode == ToolExposureMode.Deferred).ToList();
            catalogText = BuildCatalog(deferred, cfg.CatalogBudgetChars, cfg.CatalogLineMaxChars, searchAvailable, counter);
            if (!string.IsNullOrEmpty(catalogText))
                catalogTokens = counter.Estimate(catalogText);
        }

        var schemaTokens = exposed
            .Where(t => t.Mode == ToolExposureMode.Inline)
            .Sum(t => t.EstimatedTokens);

        return new ToolExposureResult(
            exposed,
            layout,
            catalogText,
            schemaTokens,
            catalogTokens,
            escalated,
            discovered);
    }

    private static bool ExceedsThreshold(int toolCount, int schemaTokens, EffectiveExposureConfig cfg) =>
        (cfg.EscalateToolCount > 0 && toolCount >= cfg.EscalateToolCount)
        || (cfg.EscalateSchemaTokens > 0 && schemaTokens >= cfg.EscalateSchemaTokens);

    private static ToolExposureMode ResolveMode(
        string id, ToolExposureLayout layout, IReadOnlySet<string> pinned, IReadOnlySet<string> discovered)
    {
        if (layout == ToolExposureLayout.Flat)
            return IsSearchTool(id) ? ToolExposureMode.Hidden : ToolExposureMode.Inline;

        if (IsSearchTool(id) || discovered.Contains(id) || pinned.Contains(id))
            return ToolExposureMode.Inline;

        return ToolExposureMode.Deferred;
    }

    private static bool IsSearchTool(string id) =>
        string.Equals(id, ToolIdConventions.SearchToolId, StringComparison.OrdinalIgnoreCase);

    private static ToolMeta BuildMeta(
        FunctionToolSchema schema, int index, ITokenCounter counter, int lineMaxChars)
    {
        var id = schema.Function?.Name ?? string.Empty;
        var description = NormalizeWhitespace(schema.Function?.Description);
        var signature = $"{id}({DescribeParameters(schema.Function?.Parameters)}): {Truncate(description, lineMaxChars)}";
        return new ToolMeta(id, DeriveNamespace(id), Truncate(signature, lineMaxChars), schema,
            counter.Estimate(JsonSerializer.Serialize(schema)), index);
    }

    private static ExposedTool ToExposed(ToolMeta meta, ToolExposureMode mode) =>
        new(meta.Id, meta.Namespace, meta.Signature, meta.Schema, meta.EstimatedTokens, mode);

    /// <summary>
    /// 命名空间：MCP 工具为 <c>mcp__{server}</c>（id 是唯一真相源），内置工具为空串。
    /// </summary>
    private static string DeriveNamespace(string id)
    {
        if (!id.StartsWith(ToolIdConventions.McpPrefix, StringComparison.Ordinal))
            return string.Empty;

        var rest = id[ToolIdConventions.McpPrefix.Length..];
        var separator = rest.IndexOf(ToolIdConventions.McpSeparator, StringComparison.Ordinal);
        return separator >= 0 ? ToolIdConventions.McpPrefix + rest[..separator] : id;
    }

    /// <summary>单行参数摘要，形如 <c>path, offset?, limit?</c>。</summary>
    private static string DescribeParameters(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                    required.Add(name);
            }
        }

        var names = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            names.Add(required.Contains(property.Name) ? property.Name : property.Name + "?");
            if (names.Count == 4)
                break;
        }

        return names.Count == 4 ? string.Join(", ", names) + ", …" : string.Join(", ", names);
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        return maxChars == 1 ? "…" : text[..(maxChars - 1)] + "…";
    }

    /// <summary>
    /// 构建目录正文：按命名空间分组，恒有组标题（含总数与已展示数），
    /// 在字符预算内按行长度升序轮转补全；0 展示的分组也保留标题。
    /// </summary>
    private static string? BuildCatalog(
        IReadOnlyList<ExposedTool> deferred,
        int budgetChars,
        int lineMaxChars,
        bool searchAvailable,
        ITokenCounter counter)
    {
        if (deferred.Count == 0)
            return null;

        var groups = deferred
            .GroupBy(t => t.Namespace, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        var intro = searchAvailable
            ? "以下工具当前未内联进本次请求的 schema，未加载前不能直接调用。先调用 tool_search 检索（可按 namespace、关键词与 detail 收窄）；命中后本次执行内即可直接调用，下一次执行起自动进入 tools[]。目录只展示部分条目，未展示的工具同样存在、同样可检索，不代表缺失。"
            : "以下工具当前未内联进本次请求的 schema，未加载前不能直接调用；本次未启用工具检索入口，如需调用请由宿主启用工具检索能力。";

        // 预算从开场白与各分组标题开始累计，标题恒保留。
        var headers = groups
            .Select(g => $"### {DisplayName(g[0].Namespace)} ({g.Count} tools)")
            .ToList();
        var used = intro.Length + headers.Sum(h => h.Length + 1);

        var shownCounts = new int[groups.Count];
        var remaining = budgetChars - used;
        var pending = new List<(int Group, ExposedTool Tool)>();
        for (var i = 0; i < groups.Count; i++)
        {
            // 每组保底 1 行（最短者优先），放不下则该组 0 展示。
            var shortest = groups[i].OrderBy(t => t.Signature.Length).ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
            if (shortest.Count > 0 && remaining >= shortest[0].Signature.Length + LineOverhead)
            {
                shownCounts[i] = 1;
                remaining -= shortest[0].Signature.Length + LineOverhead;
                foreach (var tool in shortest.Skip(1))
                    pending.Add((i, tool));
            }
            else
            {
                foreach (var tool in shortest)
                    pending.Add((i, tool));
            }
        }

        foreach (var (group, tool) in pending
                     .OrderBy(p => p.Tool.Signature.Length)
                     .ThenBy(p => p.Tool.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (remaining < tool.Signature.Length + LineOverhead)
                break;
            shownCounts[group]++;
            remaining -= tool.Signature.Length + LineOverhead;
        }

        var builder = new StringBuilder();
        builder.Append(intro).AppendLine().AppendLine();
        for (var i = 0; i < groups.Count; i++)
        {
            builder.Append($"### {DisplayName(groups[i][0].Namespace)} ({groups[i].Count} tools, {shownCounts[i]} shown)").AppendLine();
            foreach (var tool in groups[i].Take(shownCounts[i]))
                builder.Append("- ").AppendLine(tool.Signature);
            builder.AppendLine();
        }

        // 单行长度超限的条目已按 lineMaxChars 截断在建 meta 时完成。
        _ = lineMaxChars;
        _ = counter;
        return builder.ToString().TrimEnd();
    }

    private static string DisplayName(string ns) => string.IsNullOrEmpty(ns) ? "通用" : ns;

    private sealed record ToolMeta(
        string Id,
        string Namespace,
        string Signature,
        FunctionToolSchema Schema,
        int EstimatedTokens,
        int Index);
}
