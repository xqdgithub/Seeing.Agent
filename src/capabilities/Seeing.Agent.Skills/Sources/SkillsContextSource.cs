using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;

namespace Seeing.Agent.Skills.Sources;

/// <summary>
/// 技能清单源：<c>Target = System</c>、<c>Scope = Project</c>。
/// <para>只读执行作用域的 <see cref="IContextExecutionScope.SkillSnapshot"/>（执行级钉住），
/// **不读** <see cref="ISkillCatalog.Current"/>：同一次执行内源与 <c>skill</c> 工具看到的是同一个快照，
/// 不会因安装/删除中途漂移。</para>
/// <para><c>Canonical</c> = <c>{"skills":[{"id","name","description"}]}</c>（按 id ordinal 排序，不含正文）；
/// <c>Payload</c> 携带本次读取的目录条目，仅供本次渲染使用、不落盘。</para>
/// </summary>
public sealed class SkillsContextSource : IContextSource
{
    /// <summary>基线文本字符上限（截断由本类执行，截断结果即存库文本）。</summary>
    public const int BaselineCharLimit = 8_000;

    private const string TruncatedSuffix = "\n…（技能清单已按上下文基线上限截断）";

    private readonly IContextExecutionScope _executionScope;
    private readonly ILogger<SkillsContextSource> _logger;

    /// <summary>
    /// 创建技能清单源。
    /// </summary>
    /// <param name="executionScope">执行级作用域（钉住快照的唯一来源）。</param>
    /// <param name="logger">可选日志（缺省为空日志，便于测试直接构造）。</param>
    public SkillsContextSource(IContextExecutionScope executionScope, ILogger<SkillsContextSource>? logger = null)
    {
        _executionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
        _logger = logger ?? NullLogger<SkillsContextSource>.Instance;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => new(typeof(SkillsContextSource).FullName!);

    /// <inheritdoc />
    public ContextSourceScope Scope => ContextSourceScope.Project;

    /// <inheritdoc />
    public int ValueSchemaVersion => 1;

    /// <inheritdoc />
    public ContextSourceTarget Target => ContextSourceTarget.System;

    /// <inheritdoc />
    public int MaxBaselineChars => BaselineCharLimit;

    /// <inheritdoc />
    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = _executionScope.SkillSnapshot;
        if (snapshot is null)
        {
            // 钉住快照缺失属宿主接线错误：按不可用处理（保留上次已叙述内容），不得当作「技能已删除」
            _logger.LogWarning("技能钉住快照为空，本源按不可用处理");
            return Task.FromResult(new ContextSourceValue(ContextValueState.Unavailable, null, null));
        }

        // 禁用技能不进入清单：禁用即「不可再调用」，走 Removed/移除叙述
        // 可用条目投影只有 SkillCatalogProjection 一份实现（漏一次禁用判断就是一次可见性回归）
        var entries = SkillCatalogProjection.EnabledEntries(snapshot);

        if (entries.Count == 0)
            return Task.FromResult(new ContextSourceValue(ContextValueState.Removed, null, null));

        var canonical = JsonSerializer.SerializeToElement(new
        {
            skills = entries
                .OrderBy(entry => entry.Info.Name, StringComparer.Ordinal)
                .Select(entry => new
                {
                    id = entry.Info.Name,
                    name = entry.Info.Name,
                    description = entry.Info.Description
                })
                .ToList()
        });

        return Task.FromResult(new ContextSourceValue(ContextValueState.Value, canonical, entries));
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var entries = value.Payload is IReadOnlyList<SkillCatalogEntry> payload
            ? payload
            : [];

        var text = entries.Count > 0
            ? SkillCatalogFormatter.RenderBaseline(entries)
            : SkillCatalogFormatter.RenderBaseline(ListEntriesFromCanonical(value.Canonical));

        return Truncate(text);
    }

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        var previousEntries = ListEntriesFromCanonical(previous.Canonical);
        var currentEntries = current.State == ContextValueState.Removed
            ? []
            : ListEntriesFromValue(current);

        var text = SkillCatalogFormatter.RenderDelta(previousEntries, currentEntries);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ===== 内部 =====

    /// <summary>从 payload（本次读取的目录条目）重建清单条目；payload 缺失时退回 canonical。</summary>
    private IReadOnlyList<SkillListEntry> ListEntriesFromValue(ContextSourceValue value)
    {
        if (value.Payload is IReadOnlyList<SkillCatalogEntry> payload)
            return SkillCatalogFormatter.ToListEntries(payload);

        if (value.Payload is not null)
        {
            // Payload 形状异常：记录可诊断事实，退回 canonical（绝不静默）
            _logger.LogWarning(
                "技能源 Payload 形状不是目录条目列表，已退回 canonical：{PayloadType}",
                value.Payload.GetType().FullName);
        }

        return ListEntriesFromCanonical(value.Canonical);
    }

    /// <summary>从 canonical 的 <c>skills</c> 数组重建清单条目（previous 值的唯一来源）。</summary>
    private IReadOnlyList<SkillListEntry> ListEntriesFromCanonical(JsonElement? canonical)
    {
        if (canonical is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("skills", out var skills) ||
            skills.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SkillListEntry>();
        foreach (var item in skills.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id");
            var name = ReadString(item, "name");
            if (string.IsNullOrEmpty(id))
                continue;

            result.Add(new SkillListEntry(
                id,
                string.IsNullOrEmpty(name) ? id : name,
                ReadString(item, "description")));
        }

        return result;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

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
}
