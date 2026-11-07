using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 工具目录上下文源：<c>Target = System</c>、<c>Scope = Session</c>。
/// <para>
/// <b>初始渲染进系统提示词</b>（与 <c>SkillsContextSource</c> 同构）：目录是环境事实，第 1 轮就该在位、
/// 位置固定。它只在基线建立时渲染一次，之后不再参与每请求拼装，因此不会因「工具数/展示数」变化
/// 而击穿前缀缓存。
/// </para>
/// <para>
/// 运行时变化（工具增减 / 升级）由装配器统一转成一条 <c>context-update</c> 增量叙述消息追加到
/// 消息尾部，不重写系统提示词。
/// </para>
/// <para>
/// 只读执行作用域钉住的 <see cref="IContextExecutionScope.ToolSettlement"/>，
/// 与工具看到同一份结算；不自行结算，避免第二条 schema 路径。
/// </para>
/// <para>
/// canonical 只含「命名空间 + 工具 id」，<b>不含描述</b>：描述可能被 <c>tool.definition</c> Hook 改写，
/// 放进 canonical 会让无关的措辞变化触发叙述。目录正文（Payload）承载描述，只在初始渲染用一次。
/// </para>
/// </summary>
public sealed class ToolCatalogContextSource : IContextSource, IContextSourceMessageMetadata
{
    /// <summary>增量消息元数据键：供 UI 折叠与标题计数。</summary>
    public const string MessageMetadataKey = "tool_catalog";

    private const string GeneralNamespace = "通用";

    private readonly IContextExecutionScope _executionScope;
    private readonly ILogger<ToolCatalogContextSource> _logger;

    /// <summary>创建工具目录源。</summary>
    /// <param name="executionScope">执行级作用域（钉住结算视图的唯一来源）。</param>
    /// <param name="logger">可选日志。</param>
    public ToolCatalogContextSource(
        IContextExecutionScope executionScope,
        ILogger<ToolCatalogContextSource>? logger = null)
    {
        _executionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
        _logger = logger ?? NullLogger<ToolCatalogContextSource>.Instance;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => new(typeof(ToolCatalogContextSource).FullName!);

    /// <inheritdoc />
    /// <remarks>目录内容随会话结算变化，不跨会话复用。</remarks>
    public ContextSourceScope Scope => ContextSourceScope.Session;

    /// <inheritdoc />
    public int ValueSchemaVersion => 1;

    /// <inheritdoc />
    /// <remarks>
    /// 初始渲染进系统提示词（与 <c>SkillsContextSource</c> 同构）：目录是环境事实，
    /// 第 1 轮就应在位、位置固定。之后的运行时变化由装配器统一转成增量叙述消息，
    /// 因此系统提示词在基线建立后字节稳定（<c>BuildSystemBaseline</c> 只读存库基线）。
    /// </remarks>
    public ContextSourceTarget Target => ContextSourceTarget.System;

    /// <inheritdoc />
    /// <remarks>截断由 <see cref="ToolExposureEngine"/> 的目录预算完成，框架不二次截断。</remarks>
    public int MaxBaselineChars => 0;

    /// <inheritdoc />
    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var settlement = _executionScope.ToolSettlement;
        if (settlement is null)
        {
            // 结算缺失属宿主接线问题：按不可用处理（保留上次已叙述内容），不得当作「目录已删除」。
            _logger.LogDebug("工具结算视图为空，工具目录源按不可用处理");
            return Task.FromResult(new ContextSourceValue(ContextValueState.Unavailable, null, null));
        }

        if (settlement.DeferredToolCount == 0)
        {
            // 两种成因必须区分：布局变 Flat（工具全进 tools[]，仍可调）与工具真消失。
            // 前者叙述「不得再调用」会写入与环境相反的持久事实，模型据此拒绝使用已内联的工具。
            if (settlement.Layout != ToolExposureLayout.Tiered)
            {
                _logger.LogDebug("布局非分层（{Layout}），工具已全部内联；目录按不可用处理", settlement.Layout);
                return Task.FromResult(new ContextSourceValue(ContextValueState.Unavailable, null, null));
            }

            return Task.FromResult(new ContextSourceValue(ContextValueState.Removed, null, null));
        }

        var canonical = BuildCanonical(settlement);

        // CatalogText 是目录正文的唯一渲染点产物；无正文时退回不可用（不让空文本覆盖旧叙述）。
        var catalogText = settlement.CatalogText;
        if (string.IsNullOrWhiteSpace(catalogText))
        {
            _logger.LogDebug("工具结算未产出目录正文，工具目录源按不可用处理");
            return Task.FromResult(new ContextSourceValue(ContextValueState.Unavailable, null, null));
        }

        return Task.FromResult(new ContextSourceValue(ContextValueState.Value, canonical, catalogText));
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value) =>
        value.Payload as string ?? string.Empty;

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        var currentCanonical = current.State == ContextValueState.Removed
            ? (JsonElement?)null
            : current.Canonical;

        var text = ToolCatalogDeltaRenderer.Render(previous.Canonical, currentCanonical);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 仅用于增量叙述消息（System 落点的初始渲染不进消息）。带 <c>context.update</c> 标记
    /// 与 <c>ContextUpdateSources</c>，形状与记忆源一致，供 UI 折叠与标题计数。
    /// </remarks>
    public IReadOnlyDictionary<string, object>? GetMessageMetadata(
        string renderedText, ContextSourceValue value) =>
        new Dictionary<string, object>
        {
            [SessionMetadataKeys.ContextUpdate] = true,
            [SessionMetadataKeys.ContextUpdateSources] = new[] { Key.Value },
            [MessageMetadataKey] = true
        };

    private static JsonElement BuildCanonical(SessionToolSchemaResolution settlement)
    {
        var namespaces = settlement.Tools
            .Where(t => t.Mode == ToolExposureMode.Deferred)
            .GroupBy(
                t => string.IsNullOrEmpty(t.Namespace) ? GeneralNamespace : t.Namespace,
                StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                name = g.Key,
                tools = g.Select(t => t.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList()
            })
            .ToList();

        return JsonSerializer.SerializeToElement(new { namespaces });
    }
}
