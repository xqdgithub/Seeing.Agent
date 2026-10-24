using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 上下文基线状态的读写端口（<see cref="SessionMetadataKeys.ContextBaseline"/>）。
/// <para>状态损坏或版本不匹配时返回空状态并记录 Debug 日志，不返回 null、不静默吞掉。</para>
/// </summary>
internal static class ContextBaselineStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 省略 null 字段，控制会话文件体积；缺失字段由模型默认值承接。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举与 [Flags] 组合值以名称（可读）落盘。
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 读取会话基线状态；无状态、损坏或版本不匹配时返回空状态。
    /// </summary>
    public static ContextBaselineState Load(SessionData session)
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
            Debug.WriteLine(
                $"[ContextBaselineStore] 上下文基线状态解析失败，按空状态重建：{ex.GetType().Name}: {ex.Message}");
            return new ContextBaselineState();
        }

        if (state is null)
        {
            Debug.WriteLine("[ContextBaselineStore] 上下文基线状态为 null，按空状态重建。");
            return new ContextBaselineState();
        }

        if (state.StateVersion != ContextBaselineState.CurrentStateVersion)
        {
            Debug.WriteLine(
                $"[ContextBaselineStore] 上下文基线状态版本不匹配（{state.StateVersion} != {ContextBaselineState.CurrentStateVersion}），按空状态重建。");
            return new ContextBaselineState();
        }

        return Normalize(state);
    }

    /// <summary>
    /// 写入会话基线状态。
    /// </summary>
    public static void Save(SessionData session, ContextBaselineState state)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);

        session.Metadata ??= new Dictionary<string, string>();
        session.Metadata[SessionMetadataKeys.ContextBaseline] = JsonSerializer.Serialize(state, s_options);
    }

    /// <summary>
    /// 移除会话基线状态（清空消息时使用）。
    /// </summary>
    public static void Clear(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Metadata?.Remove(SessionMetadataKeys.ContextBaseline);
    }

    /// <summary>
    /// 修正反序列化可绕过的非空约束（JSON 显式 null）并丢弃无法使用的损坏条目。
    /// </summary>
    private static ContextBaselineState Normalize(ContextBaselineState state)
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
            Debug.WriteLine(
                $"[ContextBaselineStore] 丢弃损坏的基线条目：源 {droppedSources} 个，增量账本 {droppedLedgerEntries} 条。");
        }

        state.Sources = sources;
        return state;
    }
}
