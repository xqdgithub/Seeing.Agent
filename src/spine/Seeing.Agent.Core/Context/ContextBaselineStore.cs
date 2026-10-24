using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 上下文基线状态的读写端口（<see cref="SessionMetadataKeys.ContextBaseline"/>）。
/// <para>状态损坏或版本不匹配时返回空状态并记录 Debug 日志，不返回 null、不静默吞掉。</para>
/// <para>实例化以便注入 <see cref="ILogger{TCategoryName}"/>；装配器通过构造函数接收（见 <c>ContextAssembler</c>）。</para>
/// </summary>
internal sealed class ContextBaselineStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 省略 null 字段，控制会话文件体积；缺失字段由模型默认值承接。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举与 [Flags] 组合值以名称（可读）落盘。
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<ContextBaselineStore> _logger;

    public ContextBaselineStore(ILogger<ContextBaselineStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 读取会话基线状态；无状态、损坏或版本不匹配时返回空状态。
    /// </summary>
    public ContextBaselineState Load(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Metadata is null ||
            !session.Metadata.TryGetValue(SessionMetadataKeys.ContextBaseline, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return new ContextBaselineState();
        }

        ContextBaselineState? state;
        try
        {
            state = JsonSerializer.Deserialize<ContextBaselineState>(json, s_options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogDebug(
                ex,
                "上下文基线状态解析失败，按空状态重建：{SessionId}",
                session.Id);
            return new ContextBaselineState();
        }

        if (state is null)
        {
            _logger.LogDebug("上下文基线状态为 null，按空状态重建：{SessionId}", session.Id);
            return new ContextBaselineState();
        }

        if (state.StateVersion != ContextBaselineState.CurrentStateVersion)
        {
            _logger.LogDebug(
                "上下文基线状态版本不匹配，按空状态重建：{SessionId} 期望={Expected} 实际={Actual}",
                session.Id,
                ContextBaselineState.CurrentStateVersion,
                state.StateVersion);
            return new ContextBaselineState();
        }

        return Normalize(session, state);
    }

    /// <summary>
    /// 写入会话基线状态。
    /// </summary>
    public void Save(SessionData session, ContextBaselineState state)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);

        session.Metadata ??= new Dictionary<string, string>();
        session.Metadata[SessionMetadataKeys.ContextBaseline] = JsonSerializer.Serialize(state, s_options);
    }

    /// <summary>
    /// 修正反序列化可绕过的非空约束（JSON 显式 null）并丢弃无法使用的损坏条目。
    /// </summary>
    private ContextBaselineState Normalize(SessionData session, ContextBaselineState state)
    {
        state.Sources ??= new Dictionary<string, ContextSourceState>(StringComparer.Ordinal);

        var sources = new Dictionary<string, ContextSourceState>(state.Sources.Count, StringComparer.Ordinal);
        var droppedSources = 0;
        var droppedLedgerEntries = 0;

        foreach (var (key, source) in state.Sources)
        {
            if (source is null)
            {
                droppedSources++;
                continue;
            }

            source.BaselineText ??= string.Empty;
            source.BaselineHash ??= string.Empty;
            source.DeltaLedger ??= new List<DeltaLedgerEntry>();

            var before = source.DeltaLedger.Count;
            source.DeltaLedger.RemoveAll(
                entry => entry is null || string.IsNullOrEmpty(entry.MessageId) || entry.Values is null);
            droppedLedgerEntries += before - source.DeltaLedger.Count;

            sources[key] = source;
        }

        if (droppedSources > 0 || droppedLedgerEntries > 0)
        {
            _logger.LogDebug(
                "丢弃损坏的上下文基线条目：{SessionId} 源={DroppedSources} 增量账本={DroppedLedgerEntries}",
                session.Id,
                droppedSources,
                droppedLedgerEntries);
        }

        state.Sources = sources;
        return state;
    }
}
