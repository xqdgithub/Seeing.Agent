using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 上下文装配器：唯一的「读源 + 规范化 + diff + 增量叙述」编排点。
/// <para>职责边界：只做编排与落库，<b>不</b>为源渲染任何业务文本（信封之外不添加任何措辞）。</para>
/// <para>状态落在 <see cref="SessionData.Metadata"/>（<see cref="SessionMetadataKeys.ContextBaseline"/>），
/// 与消息写入同一个 <see cref="SessionData"/> 实例，由调用方本轮既有的 <c>SaveAsync</c> 一并落盘。</para>
/// </summary>
public sealed class ContextAssembler : IContextAssembler
{
    /// <summary>增量消息的元数据键：本次叙述涉及的源键（稳定顺序的字符串数组）。</summary>
    internal const string DeltaSourcesMetadataKey = "context.update.sources";

    /// <summary>增量消息信封标签；信封内只放源自己渲染的文本。</summary>
    private const string DeltaEnvelopeTag = "context-update";

    /// <summary>
    /// 会话级单飞闸（固定条纹，避免按会话建表导致内存无限增长）。
    /// 同一条纹上的不同会话会互相等待，仅影响吞吐、不影响正确性。
    /// </summary>
    private static readonly SemaphoreSlim[] s_sessionGates = CreateGates(64);

    private static readonly string[] s_emptyKeys = [];

    private readonly IReadOnlyList<IContextSource> _sources;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ContextAssembler> _logger;
    private readonly ContextBaselineStore _store;

    /// <summary>
    /// 创建装配器。
    /// </summary>
    /// <param name="sources">全部已注册的上下文源（内部按 Scope → Key 排序）。</param>
    /// <param name="sessionManager">会话管理器：仅用于乐观并发守卫（取权威实例）。</param>
    /// <param name="logger">装配日志。</param>
    /// <param name="loggerFactory">可选：用于状态存储的日志；缺省使用空日志（测试 / 极简宿主）。</param>
    public ContextAssembler(
        IEnumerable<IContextSource> sources,
        ISessionManager sessionManager,
        ILogger<ContextAssembler> logger,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = OrderSources(sources);
        _sessionManager = sessionManager;
        _logger = logger;
        _store = new ContextBaselineStore(
            loggerFactory?.CreateLogger<ContextBaselineStore>()
            ?? NullLogger<ContextBaselineStore>.Instance);
    }

    /// <inheritdoc />
    public async Task<ContextAssemblyResult> EnsureFreshAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();

        // 单飞兜底：同一会话的并发装配串行化。
        // Task 12 会把调用点移到 ExecutionRunner 并接入主链 CAS；本闸保留作为兜底，避免重复叙述。
        var gate = s_sessionGates[GetGateIndex(session.Id)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await AssembleWithCasGuardAsync(session, mode, insertBeforeMessageId, skip, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public string BuildSystemBaseline(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 只读已存状态：不读源、不渲染（字节稳定的唯一路径）。
        return JoinSystemBaseline(_store.Load(session));
    }

    // ===== 装配主体 =====

    /// <summary>
    /// 乐观并发守卫：调用方持有的实例可能与缓存中的权威实例不同（例如并发轮次）。
    /// 仅当时间戳 / 消息数不一致时按权威实例重跑一次；不调用 <c>UpdateSessionAsync</c>
    /// —— 装配的所有写入都在同一 <see cref="SessionData"/> 上，由调用方统一落盘。
    /// </summary>
    private async Task<ContextAssemblyResult> AssembleWithCasGuardAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(session.Id))
        {
            SessionData? fresh = null;
            try
            {
                fresh = await _sessionManager.GetOrLoadAsync(session.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 管理器不可用不致命：仍按调用方实例装配，但不得静默
                _logger.LogWarning(
                    ex,
                    "读取会话权威实例失败，按调用方实例装配：{SessionId}",
                    session.Id);
            }

            if (fresh is not null &&
                !ReferenceEquals(fresh, session) &&
                (fresh.UpdatedAt != session.UpdatedAt || fresh.MessageCount != session.MessageCount))
            {
                _logger.LogDebug(
                    "会话实例已更新，按权威实例重跑一次装配：{SessionId} {CallerMessages} → {FreshMessages}",
                    session.Id,
                    session.MessageCount,
                    fresh.MessageCount);

                // 只重跑一次：不递归，避免实例持续变化导致的自旋
                return await AssembleCoreAsync(fresh, mode, insertBeforeMessageId, skip, ct)
                    .ConfigureAwait(false);
            }
        }

        return await AssembleCoreAsync(session, mode, insertBeforeMessageId, skip, ct).ConfigureAwait(false);
    }

    private async Task<ContextAssemblyResult> AssembleCoreAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct)
    {
        var persist = mode == ContextAssemblyMode.Persist;
        var issues = new List<string>();
        var changedKeys = new List<string>();
        var skippedKeys = new List<string>();

        // 源键校验：空键 / 重复键会互相覆盖状态，必须显式记录而不是静默取其一
        var sources = ValidateSources(issues);

        var state = _store.Load(session);
        var stateDirty = false;

        // 1) 并行读取全部源（单源失败不影响其它源）
        var values = await ReadAllAsync(sources, session, issues, skippedKeys, ct).ConfigureAwait(false);

        // 2) 先消费生命周期重置位（压缩 / 切根），再 diff
        var pendingConsumed = false;
        if (persist && state.Pending != PendingBaselineReset.None)
        {
            _logger.LogDebug(
                "消费上下文基线重置位：{SessionId} {Pending}",
                session.Id,
                state.Pending);
            ApplyPendingReset(state);
            state.Pending = PendingBaselineReset.None;
            pendingConsumed = true;
            stateDirty = true;
        }

        var initialMessages = new List<(string SourceKey, string Text)>();
        var narrations = new List<(string SourceKey, string Text)>();
        var narratedStates = new List<ContextSourceState>();

        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var source = sources[i];
            var key = source.Key.Value;

            if (skip is not null && skip.Contains(source.Key))
            {
                skippedKeys.Add(key);
                continue;
            }

            var current = values[i] ?? new ContextSourceValue(ContextValueState.Unavailable, null, null);
            if (current.State == ContextValueState.Unavailable)
            {
                // 保留上一次已叙述内容，不参与 diff（Review Focus #1）
                skippedKeys.Add(key);
                continue;
            }

            var currentState = current.State == ContextValueState.Initial
                ? ContextValueState.Value
                : current.State;

            JsonObject? currentValues;
            if (currentState == ContextValueState.Removed)
            {
                currentValues = new JsonObject();
            }
            else if (current.Canonical is { } canonical &&
                     canonical.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                     JsonNode.Parse(canonical.GetRawText()) is JsonObject parsedValues)
            {
                // 状态模型的 BaselineValues / CurrentValues 为 JsonObject：
                // 源须把集合型值包一层对象（如 {"files":[...]}），使 previous.Canonical 与 current.Canonical 同形。
                currentValues = parsedValues;
            }
            else
            {
                issues.Add($"源 {key} 声明有值但未提供可用的 JSON 对象 Canonical，已按不可用处理。");
                _logger.LogWarning("上下文源缺少可用的 Canonical，已跳过：{SourceKey} {SessionId}", key, session.Id);
                skippedKeys.Add(key);
                continue;
            }

            if (!state.Sources.TryGetValue(key, out var sourceState))
            {
                if (currentState == ContextValueState.Removed)
                {
                    // 从未建立基线：没有可删除的内容，不需要叙述
                    continue;
                }

                sourceState = EstablishBaseline(
                    session, source, current, currentValues, state, initialMessages, issues);
                if (sourceState is not null)
                {
                    changedKeys.Add(key);
                    stateDirty = true;
                }
                else
                {
                    skippedKeys.Add(key);
                }

                continue;
            }

            // 3) 回退路径：裁剪已失效账本，并按存活的最晚条目折叠已叙述值
            if (FoldLedgerToLive(session, sourceState))
                stateDirty = true;

            // 4) 版本二分：不匹配 → 丢弃该源状态并按新形状重建
            if (sourceState.ValueSchemaVersion != source.ValueSchemaVersion)
            {
                // 旧形状的值不能再用于 diff：对源而言等同于「在此之前没有基线」
                var previous = new ContextSourceValue(ContextValueState.Initial, null, null);
                var established = RebuildBaseline(
                    session, source, current, currentValues, sourceState, initialMessages, issues);

                if (!established)
                {
                    skippedKeys.Add(key);
                    continue;
                }

                changedKeys.Add(key);
                stateDirty = true;

                if (source.Target != ContextSourceTarget.Message && persist)
                {
                    // 系统落点没有「初始消息」这一叙述载体，按 Initial 语义全量叙述
                    var full = SafeRender(source, previous, current, issues);
                    if (!string.IsNullOrWhiteSpace(full))
                        narrations.Add((key, full));
                }

                continue;
            }

            // 5) Message 落点：初始渲染消息被回退掉 → 重放（压缩后同样经 pending 清空 BaselineMessageId）
            if (source.Target == ContextSourceTarget.Message &&
                (string.IsNullOrEmpty(sourceState.BaselineMessageId) ||
                 session.IndexOfMessage(sourceState.BaselineMessageId) < 0))
            {
                var baselineText = SafeRenderInitial(source, current, issues);
                if (baselineText is null)
                {
                    skippedKeys.Add(key);
                    continue;
                }

                initialMessages.Add((key, baselineText));
                sourceState.BaselineText = baselineText;
                sourceState.BaselineHash = HashText(baselineText);
                sourceState.BaselineValues = CloneObject(currentValues);
                // 重放的初始渲染已包含当前全量内容，不再重复叙述差异
                sourceState.CurrentValues = CloneObject(currentValues);
                changedKeys.Add(key);
                stateDirty = true;
                continue;
            }

            // 5.1) 重置位已消费且落点为系统提示词：按当前值重新锚定基线文本。
            //      压缩后旧增量已并入摘要，系统基线必须承载当前内容，否则模型会看到过期清单；
            //      文本仍满足「同值重渲染逐字节相同」，不引入逐请求抖动。
            if (pendingConsumed && source.Target == ContextSourceTarget.System)
            {
                var refreshed = SafeRenderInitial(source, current, issues);
                if (refreshed is not null)
                {
                    WarnIfBaselineTooLong(session, source, refreshed, issues);
                    sourceState.BaselineText = refreshed;
                    sourceState.BaselineHash = HashText(refreshed);
                    sourceState.BaselineValues = CloneObject(currentValues);
                    sourceState.CurrentValues = CloneObject(currentValues);
                    changedKeys.Add(key);
                    stateDirty = true;
                }
            }

            // 6) 同版本渲染确定性：值未变却重渲染出不同文本 → 保留已存文本 + 告警
            if (currentState == ContextValueState.Value &&
                sourceState.BaselineText.Length > 0 &&
                JsonEquals(sourceState.BaselineValues, currentValues))
            {
                var rerendered = SafeRenderInitial(source, current, issues);
                if (rerendered is not null &&
                    !string.Equals(HashText(rerendered), sourceState.BaselineHash, StringComparison.Ordinal))
                {
                    issues.Add($"源 {key} 的初始渲染不确定（同值不同文本），已沿用已存基线文本。");
                    _logger.LogWarning(
                        "上下文源初始渲染不确定，沿用已存基线：{SourceKey} {SessionId}",
                        key,
                        session.Id);
                }
            }

            // 7) diff：以「已叙述值」而非基线值为基准
            if (JsonEquals(sourceState.CurrentValues, currentValues))
                continue;

            changedKeys.Add(key);

            // 只读模式：只计算基线 / 变化集，不叙述、不推进
            if (!persist)
                continue;

            var previousValue = BuildPreviousValue(sourceState);
            var text = SafeRender(source, previousValue, current, issues);
            if (string.IsNullOrWhiteSpace(text))
            {
                // Render 返回 null / 空：不推进 currentValues，下一次仍会重试（禁止静默丢更新）
                _logger.LogDebug(
                    "上下文源变化未被叙述（Render 返回空），保留旧值待下次重试：{SourceKey} {SessionId}",
                    key,
                    session.Id);
                continue;
            }

            narrations.Add((key, text));
            sourceState.CurrentValues = CloneObject(currentValues);
            narratedStates.Add(sourceState);
            stateDirty = true;
        }

        // 8) 提交：初始渲染消息 → 增量消息 → 状态
        var historyBaseline = initialMessages.Count == 0
            ? null
            : string.Join("\n\n", initialMessages.Select(m => m.Text));

        string? deltaText = null;

        if (persist)
        {
            foreach (var (sourceKey, text) in initialMessages)
            {
                var message = SessionMessage.UserMessage(text);
                InsertBeforeAnchor(session, insertBeforeMessageId, message);

                if (state.Sources.TryGetValue(sourceKey, out var sourceState))
                {
                    sourceState.BaselineMessageId = message.Id;
                    sourceState.DeltaLedger.Add(new DeltaLedgerEntry(
                        message.Id!,
                        CloneObject(sourceState.CurrentValues) ?? new JsonObject()));
                }

                stateDirty = true;
            }

            if (narrations.Count > 0)
            {
                deltaText = string.Join(
                    "\n\n",
                    narrations.Select(narration => FormatDelta(narration.SourceKey, narration.Text)));

                var message = SessionMessage.UserMessage(deltaText)
                    .WithMetadata(DeltaSourcesMetadataKey, narrations.Select(n => n.SourceKey).ToArray());
                InsertBeforeAnchor(session, insertBeforeMessageId, message);

                foreach (var sourceState in narratedStates)
                {
                    sourceState.DeltaLedger.Add(new DeltaLedgerEntry(
                        message.Id!,
                        CloneObject(sourceState.CurrentValues) ?? new JsonObject()));
                }

                stateDirty = true;
            }

            if (stateDirty)
                _store.Save(session, state);
        }

        var systemBaseline = JoinSystemBaseline(state);

        _logger.LogDebug(
            "上下文装配完成：{SessionId} 模式={Mode} 变化={ChangedCount} 跳过={SkippedCount} 叙述={NarrationCount} 问题={IssueCount}",
            session.Id,
            mode,
            changedKeys.Count,
            skippedKeys.Count,
            narrations.Count,
            issues.Count);

        return new ContextAssemblyResult(
            systemBaseline,
            historyBaseline,
            deltaText,
            changedKeys.Count == 0 ? s_emptyKeys : changedKeys.ToArray(),
            skippedKeys.Count == 0 ? s_emptyKeys : skippedKeys.ToArray(),
            issues.Count == 0 ? s_emptyKeys : issues.ToArray());
    }

    // ===== 建立 / 重建基线 =====

    /// <summary>
    /// 首次建立某源基线。System 落点直接进入提示词；Message 落点插入初始渲染消息（即首次叙述）。
    /// </summary>
    private ContextSourceState? EstablishBaseline(
        SessionData session,
        IContextSource source,
        ContextSourceValue current,
        JsonObject currentValues,
        ContextBaselineState state,
        List<(string SourceKey, string Text)> initialMessages,
        List<string> issues)
    {
        var key = source.Key.Value;
        var baselineText = SafeRenderInitial(source, current, issues);
        if (baselineText is null)
            return null;

        WarnIfBaselineTooLong(session, source, baselineText, issues);

        var sourceState = new ContextSourceState
        {
            ValueSchemaVersion = source.ValueSchemaVersion,
            BaselineText = baselineText,
            BaselineHash = HashText(baselineText),
            BaselineValues = CloneObject(currentValues),
            CurrentValues = CloneObject(currentValues)
        };

        state.Sources[key] = sourceState;

        if (source.Target == ContextSourceTarget.Message)
            initialMessages.Add((key, baselineText));

        return sourceState;
    }

    /// <summary>
    /// 值结构版本变化：丢弃该源旧状态并按新形状重建基线。
    /// <para>Message 落点重放初始渲染消息；System 落点的全量叙述由调用方按 Initial 语义补发。</para>
    /// </summary>
    private bool RebuildBaseline(
        SessionData session,
        IContextSource source,
        ContextSourceValue current,
        JsonObject currentValues,
        ContextSourceState sourceState,
        List<(string SourceKey, string Text)> initialMessages,
        List<string> issues)
    {
        var baselineText = SafeRenderInitial(source, current, issues);
        if (baselineText is null)
            return false;

        WarnIfBaselineTooLong(session, source, baselineText, issues);

        sourceState.ValueSchemaVersion = source.ValueSchemaVersion;
        sourceState.BaselineText = baselineText;
        sourceState.BaselineHash = HashText(baselineText);
        sourceState.BaselineValues = CloneObject(currentValues);
        sourceState.CurrentValues = CloneObject(currentValues);
        sourceState.BaselineMessageId = null;
        sourceState.DeltaLedger.Clear();

        if (source.Target == ContextSourceTarget.Message)
            initialMessages.Add((source.Key.Value, baselineText));

        return true;
    }

    // ===== 生命周期 / 回退 =====

    /// <summary>
    /// 消费 pending 位：已叙述值成为新基线，账本作废（防止旧增量被折叠回来）。
    /// <para>清空 <see cref="ContextSourceState.BaselineMessageId"/> 以触发 Message 落点的初始渲染重放
    /// （压缩后摘要取代了原消息）。</para>
    /// </summary>
    private static void ApplyPendingReset(ContextBaselineState state)
    {
        foreach (var sourceState in state.Sources.Values)
        {
            sourceState.BaselineValues = CloneObject(sourceState.CurrentValues);
            sourceState.CurrentValues = CloneObject(sourceState.BaselineValues);
            sourceState.DeltaLedger.Clear();
            sourceState.BaselineMessageId = null;
        }
    }

    /// <summary>
    /// 回退折叠：丢弃消息已不存在的账本条目；若存活的最晚条目值与已叙述值不同，
    /// 说明会话被回退，按该条目折叠 currentValues（只补叙之后的差异，不重复叙述）。
    /// </summary>
    private static bool FoldLedgerToLive(SessionData session, ContextSourceState sourceState)
    {
        if (sourceState.DeltaLedger.Count == 0)
            return false;

        var before = sourceState.DeltaLedger.Count;
        sourceState.DeltaLedger.RemoveAll(entry => session.IndexOfMessage(entry.MessageId) < 0);

        var changed = before != sourceState.DeltaLedger.Count;

        if (sourceState.DeltaLedger.Count > 0)
        {
            var newest = sourceState.DeltaLedger[^1];
            if (!JsonEquals(sourceState.CurrentValues, newest.Values))
            {
                sourceState.CurrentValues = CloneObject(newest.Values);
                changed = true;
            }
        }

        return changed;
    }

    // ===== 读取 / 渲染 =====

    private async Task<ContextSourceValue?[]> ReadAllAsync(
        IReadOnlyList<IContextSource> sources,
        SessionData session,
        List<string> issues,
        List<string> skippedKeys,
        CancellationToken ct)
    {
        var values = new ContextSourceValue?[sources.Count];
        var errors = new Exception?[sources.Count];
        var pendingReads = new Task[sources.Count];

        for (var i = 0; i < sources.Count; i++)
        {
            var index = i;
            var source = sources[index];

            async Task ReadOneAsync()
            {
                try
                {
                    values[index] = await source.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 读取异常映射为 Unavailable，绝不当作 Removed（Review Focus #1）
                    errors[index] = ex;
                    values[index] = new ContextSourceValue(ContextValueState.Unavailable, null, null);
                }
            }

            pendingReads[index] = ReadOneAsync();
        }

        await Task.WhenAll(pendingReads).ConfigureAwait(false);

        for (var i = 0; i < errors.Length; i++)
        {
            if (errors[i] is not { } error)
                continue;

            var key = sources[i].Key.Value;
            skippedKeys.Add(key);
            issues.Add($"源 {key} 读取失败，已按不可用处理：{error.GetType().Name}: {error.Message}");
            _logger.LogWarning(
                error,
                "上下文源读取失败，已按不可用处理：{SourceKey} {SessionId}",
                key,
                session.Id);
        }

        return values;
    }

    private string? SafeRenderInitial(
        IContextSource source,
        ContextSourceValue value,
        List<string> issues)
    {
        try
        {
            return source.RenderInitial(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add($"源 {source.Key.Value} 初始渲染失败：{ex.GetType().Name}: {ex.Message}");
            _logger.LogWarning(
                ex,
                "上下文源初始渲染失败：{SourceKey}",
                source.Key.Value);
            return null;
        }
    }

    private string? SafeRender(
        IContextSource source,
        ContextSourceValue previous,
        ContextSourceValue current,
        List<string> issues)
    {
        try
        {
            return source.Render(previous, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add($"源 {source.Key.Value} 增量渲染失败：{ex.GetType().Name}: {ex.Message}");
            _logger.LogWarning(
                ex,
                "上下文源增量渲染失败：{SourceKey}",
                source.Key.Value);
            return null;
        }
    }

    // ===== 提交 / 排序 / 工具 =====

    private static void InsertBeforeAnchor(SessionData session, string? anchorMessageId, SessionMessage message)
    {
        var index = session.IndexOfMessage(anchorMessageId);
        if (index < 0)
        {
            session.AddMessage(message);
            return;
        }

        session.InsertMessage(index, message);
    }

    private string JoinSystemBaseline(ContextBaselineState state)
    {
        List<string>? parts = null;
        HashSet<string>? seen = null;

        foreach (var source in _sources)
        {
            // Message 落点的基线文本属于历史，不进入系统提示词
            if (source.Target != ContextSourceTarget.System)
                continue;

            var key = source.Key.Value;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // 与 ValidateSources 一致：重复键只取首个，避免基线文本重复拼接
            if (!(seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(key))
                continue;

            if (!state.Sources.TryGetValue(key, out var sourceState))
                continue;

            if (string.IsNullOrWhiteSpace(sourceState.BaselineText))
                continue;

            (parts ??= new List<string>()).Add(sourceState.BaselineText);
        }

        return parts is null ? string.Empty : string.Join("\n\n", parts);
    }

    private static IReadOnlyList<IContextSource> OrderSources(IEnumerable<IContextSource> sources) =>
        sources
            .Where(source => source is not null)
            .OrderBy(source => source.Scope)
            .ThenBy(source => source.Key.Value ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    private List<IContextSource> ValidateSources(List<string> issues)
    {
        var validated = new List<IContextSource>(_sources.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in _sources)
        {
            var key = source.Key.Value;

            if (string.IsNullOrWhiteSpace(key))
            {
                issues.Add($"上下文源 {source.GetType().FullName} 返回空键，已跳过。");
                _logger.LogWarning(
                    "上下文源键为空，已跳过：{SourceType}",
                    source.GetType().FullName);
                continue;
            }

            if (!seen.Add(key))
            {
                issues.Add($"上下文源键重复：{key}，仅保留首个。");
                _logger.LogWarning("上下文源键重复，仅保留首个：{SourceKey}", key);
                continue;
            }

            validated.Add(source);
        }

        return validated;
    }

    private void WarnIfBaselineTooLong(
        SessionData session,
        IContextSource source,
        string baselineText,
        List<string> issues)
    {
        // 截断由源在渲染内部完成（截断结果即存库文本）；框架只观测、不复裁，避免临界点每轮告警。
        if (source.MaxBaselineChars <= 0 || baselineText.Length <= source.MaxBaselineChars)
            return;

        issues.Add(
            $"源 {source.Key.Value} 的基线文本超出声明上限（{baselineText.Length} > {source.MaxBaselineChars}），已按源渲染结果存库。");
        _logger.LogWarning(
            "上下文源基线超限：{SourceKey} {SessionId} 实际={ActualChars} 上限={MaxChars}",
            source.Key.Value,
            session.Id,
            baselineText.Length,
            source.MaxBaselineChars);
    }

    /// <summary>
    /// 增量信封：框架只加 <c>&lt;context-update source="…"&gt;</c> 包裹，内部原样使用源渲染文本。
    /// </summary>
    private static string FormatDelta(string sourceKey, string text) =>
        $"<{DeltaEnvelopeTag} source=\"{sourceKey}\">\n{text}\n</{DeltaEnvelopeTag}>";

    /// <summary>
    /// 构造 previous 值：仅当从未叙述过时为 Initial；否则为 Value（Removed 时调用方要求 previous 为 Value）。
    /// </summary>
    private static ContextSourceValue BuildPreviousValue(ContextSourceState sourceState) =>
        sourceState.CurrentValues is null
            ? new ContextSourceValue(ContextValueState.Initial, null, null)
            : new ContextSourceValue(
                ContextValueState.Value,
                JsonSerializer.SerializeToElement(sourceState.CurrentValues),
                null);

    private static JsonObject? CloneObject(JsonObject? value) => (JsonObject?)value?.DeepClone();

    private static bool JsonEquals(JsonObject? left, JsonObject? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return string.Equals(CanonicalOf(left), CanonicalOf(right), StringComparison.Ordinal);
    }

    private static string CanonicalOf(JsonNode node) =>
        CanonicalJson.Serialize(JsonSerializer.SerializeToElement(node));

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static SemaphoreSlim[] CreateGates(int count)
    {
        var gates = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++)
            gates[i] = new SemaphoreSlim(1, 1);

        return gates;
    }

    private static int GetGateIndex(string? sessionId) =>
        (string.IsNullOrEmpty(sessionId) ? 0 : StringComparer.Ordinal.GetHashCode(sessionId))
        & (s_sessionGates.Length - 1);
}
