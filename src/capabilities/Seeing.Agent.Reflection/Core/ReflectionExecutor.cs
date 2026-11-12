using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Integration;
using Seeing.Session.Core;
using System.Text;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 复盘执行器：把「取快照 → 建内部会话 → 投递并等待 → 收集建议 → 清理」串成一次完整复盘。
/// <para>
/// 设计要点：
/// </para>
/// <list type="number">
/// <item>门禁在读取任何会话内容之前完成：关闭 / 超预算 / 超频率时直接返回，不产生任何 I/O。</item>
/// <item>只读取活跃消息（内存优先），绝不使用 <see cref="ISessionManager.LoadAsync"/>——
/// 那会整盘加载历史并替换缓存实例，可能踩到正在执行的会话。</item>
/// <item>复盘跑在同组内部子会话里（<see cref="SessionOrigin.Reflection"/>），
/// 不占用用户会话，也不出现在任务视图。</item>
/// <item>成功即清理子会话；失败 / 超时保留子会话供人工诊断。</item>
/// <item>本类不向调用方抛业务异常：所有「没做」的原因都以 <see cref="ReflectionRunResult"/> 返回。</item>
/// </list>
/// </summary>
public sealed class ReflectionExecutor : IReflectionExecutor
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionGroupManager _groupManager;
    private readonly ISessionRuntime _runtime;
    private readonly IReflectionJournal _journal;
    private readonly IExecutionUsageLedger _ledger;
    private readonly ReflectionRateLimiter _rateLimiter;
    private readonly ReflectionSuggestionsWriter _suggestionsWriter;
    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ILogger<ReflectionExecutor> _logger;

    /// <summary>创建执行器。</summary>
    public ReflectionExecutor(
        ISessionManager sessionManager,
        ISessionGroupManager groupManager,
        ISessionRuntime runtime,
        IReflectionJournal journal,
        IExecutionUsageLedger ledger,
        ReflectionRateLimiter rateLimiter,
        ReflectionSuggestionsWriter suggestionsWriter,
        IOptionsMonitor<ReflectionOptions> options,
        ILogger<ReflectionExecutor> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _suggestionsWriter = suggestionsWriter ?? throw new ArgumentNullException(nameof(suggestionsWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ReflectionRunResult> RunAsync(ReflectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await RunCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 宿主停止：这是真实的取消，交给 Worker 处理（它会区分取消与故障）。
            throw;
        }
        catch (Exception ex)
        {
            // 双保险：执行器绝不把异常抛给后台循环，否则单次故障会被当成整轮失败。
            _logger.LogWarning(
                ex,
                "复盘执行异常，按失败处理：SessionId={SessionId}, Kind={Kind}",
                request.SessionId,
                request.Kind);
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Failed);
        }
    }

    /// <summary>五阶段主流程（异常统一由 <see cref="RunAsync"/> 兜底）。</summary>
    private async Task<ReflectionRunResult> RunCoreAsync(ReflectionRequest request, CancellationToken ct)
    {
        var options = _options.CurrentValue;

        // === 阶段 0：门禁（不产生任何 I/O） ===
        // 模式 Off 是硬停：手动触发也不得绕过（用户明确表达了「不要复盘」）。
        if (options.Mode == ReflectionMode.Off)
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Disabled);
        }

        // Enabled 是「自动复盘」的总开关，只管自动来源。手动触发是用户的显式动作，
        // 若也拦它，用户为了验证功能得先去改配置再回来点，功能感知上等于不存在。
        // 预算与频率两道闸对两种来源一视同仁——那是成本语义，不是开关语义。
        var isManual = request.Kind == ReflectionTriggerKind.Manual;
        if (!options.Enabled && !isManual)
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Disabled);
        }

        if (_ledger.UsedToday() >= options.DailyTokenBudget)
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Quota);
        }

        if (!_rateLimiter.TryAcquire(options.MaxRunsPerHour, DateTimeOffset.UtcNow))
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.RateLimit);
        }

        // === 阶段 1：取快照（只读） ===
        var source = await _sessionManager.TryGetOrLoadAsync(request.SessionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.NoSource);
        }

        var transcript = BuildTranscript(
            source.GetActiveMessages(),
            options.MaxToolResultChars,
            options.MaxTranscriptChars);

        // === 复盘身份（成功与失败路径共用同一个值） ===
        // 为什么不能拿 ExecutionId 当文件名：空闲/手动触发那里是空串，消毒后是 "unknown"，
        // 同一天的多条记录会写进同一个文件互相覆盖；而成功路径写的是「复盘子会话的执行 ID」、
        // 失败路径写的是「源会话的执行 ID」，两者本就不同 → 成功记录与失败快照永远配不上对。
        // RunId 是「这次复盘」的身份，与执行 ID 解耦。
        var runId = NewRunId();

        // === 阶段 2：建内部会话 ===
        var title = string.IsNullOrWhiteSpace(source.Title)
            ? ShortSessionId(source.Id) + " · 复盘"
            : source.Title + " · 复盘";

        var child = await _groupManager.CreateInternalAsync(
            parentId: source.Id,
            agentName: ReflectionAgentDefinition.AgentName,
            title: title,
            snapshot: source.PermissionSnapshot ?? new List<SessionPermissionRule>(),
            scenario: source.Scenario,
            origin: SessionOrigin.Reflection,
            ct: ct).ConfigureAwait(false);

        // === 阶段 3：投递并等待 ===
        var message = new InboxMessage
        {
            TargetSessionId = child.Id,
            Sender = new InboxSender
            {
                // Session 语义：本次复盘由源会话派生而来，来源可追溯。
                Kind = InboxSenderKind.Session,
                Id = source.Id,
                DisplayName = "复盘"
            },
            Body = Prompts.BuildUserPrompt(transcript, source.Id),
            Delivery = InboxDelivery.Wake,
            Presentation = InboxPresentation.User,
            AgentId = ReflectionAgentDefinition.AgentName,
            ModelId = options.Model,
            Kind = "reflection"
        };

        // 实际使用的模型：显式配置优先，其次继承源会话，最后为 null（进程默认）。
        // 记进 journal 是为了让「这次复盘烧了哪个模型的成本」事后可查——
        // 不强制要求配置 Model，但必须可追溯。
        var modelId = ResolveModelId(options.Model, source.SelectedModel);

        ExecutionOutcome outcome;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.RunTimeoutMinutes)));

            try
            {
                outcome = await _runtime.DeliverAndAwaitAsync(message, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 只有外层未取消时，才说明是本执行器的业务超时。
                _logger.LogWarning(
                    "复盘执行超时（{TimeoutMinutes} 分钟）：SessionId={SessionId}, ChildSessionId={ChildSessionId}",
                    options.RunTimeoutMinutes,
                    request.SessionId,
                    child.Id);
                await RecordFailureAndCleanupAsync(
                    request, child.Id, ReflectionSkipReasons.Timeout, null, "业务超时", modelId, source.Id, runId, ct)
                    .ConfigureAwait(false);
                return ReflectionRunResult.Skipped(ReflectionSkipReasons.Timeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "复盘投递失败：SessionId={SessionId}, ChildSessionId={ChildSessionId}",
                    request.SessionId,
                    child.Id);
                await RecordFailureAndCleanupAsync(
                    request, child.Id, ReflectionSkipReasons.Failed, null, ex.Message, modelId, source.Id, runId, ct)
                    .ConfigureAwait(false);
                return ReflectionRunResult.Skipped(ReflectionSkipReasons.Failed);
            }
        }

        if (outcome.Status != ExecutionStatus.Completed)
        {
            _logger.LogWarning(
                "复盘执行未成功（Status={Status}）：SessionId={SessionId}, ChildSessionId={ChildSessionId}",
                outcome.Status,
                request.SessionId,
                child.Id);
            var partial = await ReadLastAssistantTextAsync(child.Id, ct).ConfigureAwait(false);
            await RecordFailureAndCleanupAsync(
                request, child.Id, ReflectionSkipReasons.Failed, partial,
                "子会话终态为 " + outcome.Status, modelId, source.Id, runId, ct)
                .ConfigureAwait(false);
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Failed);
        }

        // === 阶段 4：收集 ===
        var text = await ReadLastAssistantTextAsync(child.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning(
                "复盘子会话没有可解析的 assistant 文本：ChildSessionId={ChildSessionId}",
                child.Id);
            await RecordFailureAndCleanupAsync(
                request, child.Id, ReflectionSkipReasons.ParseFailed, null,
                "子会话没有 assistant 文本", modelId, source.Id, runId, ct)
                .ConfigureAwait(false);
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.ParseFailed);
        }

        var suggestions = Suggestions.Parse(text);
        var usage = outcome.Usage;

        // 写入是执行器的职责（不是 agent 的工具）：建议清单在这里、写入结果也在这里，
        // 审计记录与真实副作用因此天然同源。SuggestOnly 模式下本方法直接返回空。
        var createdSkills = await _suggestionsWriter
            .ApplyAsync(suggestions, request.SessionId, ct)
            .ConfigureAwait(false);

        // Outcome 是给「复盘记录」页读的语义值，不是执行状态。
        // 原先写 outcome.Status.ToString() → 落盘 "Completed"，而 UiText 只映射
        // suggested / created / skipped / failed：页面显示英文、统计恒为 0，看着像没工作。
        var runOutcome = createdSkills.Count > 0
            ? ReflectionRunOutcomes.Created
            : suggestions.Count > 0
                ? ReflectionRunOutcomes.Suggested
                : ReflectionRunOutcomes.Skipped;

        var journalFailed = false;
        try
        {
            await _journal.AppendAsync(
                new ReflectionRunRecord(
                    SessionId: request.SessionId,
                    ExecutionId: outcome.ExecutionId ?? request.ExecutionId,
                    Kind: request.Kind,
                    At: request.At,
                    Outcome: runOutcome,
                    CreatedSkills: createdSkills,
                    Suggestions: suggestions,
                    InputTokens: usage?.InputTokens ?? 0,
                    OutputTokens: usage?.OutputTokens ?? 0,
                    ModelId: modelId,
                    RunId: runId),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 日志是事后排查用的旁路产物：写不进去只降级为「失败」，不能让已经跑完的分析白跑。
            _logger.LogWarning(ex, "复盘日志写入失败：SessionId={SessionId}", request.SessionId);
            journalFailed = true;
        }

        // 记账与清理必须互相独立：
        // 记账抛异常（磁盘权限 / 编码问题）不能让已经跑完的复盘留下孤儿子会话——
        // 两个动作都在 try 内且共用一段，任一失败就会跳过清理。
        if (usage is not null)
        {
            try
            {
                _ledger.Add(usage);
            }
            catch (Exception ex)
            {
                // 账本影响的是「预算闸」而不是本次结果，缺一次记账顶多让预算略宽松。
                _logger.LogWarning(ex, "复盘用量记账失败（已忽略）：SessionId={SessionId}", request.SessionId);
            }
        }

        // === 阶段 5：清理（只有成功才清理；失败/超时保留子会话） ===
        await CleanupChildAsync(child.Id, ct).ConfigureAwait(false);

        // 选中：journal 失败时返回 failed，让「日志缺失」在结果上可见（子会话已清理，正文不再可取）。
        if (journalFailed)
        {
            return ReflectionRunResult.Skipped(ReflectionSkipReasons.Failed);
        }

        // 解析为空也算执行成功：模型合法地表达了「本轮没有建议」。
        return new ReflectionRunResult(
            Executed: true,
            SkipReason: null,
            CreatedSkills: createdSkills,
            SuggestionCount: suggestions.Count);
    }

    /// <summary>
    /// 失败路径的统一出口：把诊断信息落进 journal，然后清理子会话。
    /// <para>
    /// <b>为什么失败也要清理</b>：原先「保留供诊断」的设计没有任何回收机制，
    /// 持续失败时每小时最多 6 个孤儿会话堆积在 <c>.seeing/sessions/</c>。
    /// 诊断真正需要的只有「模型输出了什么」——把它落到 <c>.fail.json</c> 后，
    /// 会话本身就没有保留价值了。
    /// </para>
    /// <para>
    /// 本方法<b>不抛异常</b>：失败路径已经在处理错误，再抛会把原始原因盖掉。
    /// </para>
    /// </summary>
    private async Task RecordFailureAndCleanupAsync(
        ReflectionRequest request,
        string childSessionId,
        string skipReason,
        string? transcript,
        string? detail,
        string? modelId,
        string sourceSessionId,
        string runId,
        CancellationToken ct)
    {
        try
        {
            await _journal.AppendFailureAsync(
                new ReflectionFailureRecord(
                    SessionId: request.SessionId,
                    ExecutionId: request.ExecutionId,
                    Kind: request.Kind,
                    At: request.At,
                    SkipReason: skipReason,
                    ChildSessionId: childSessionId,
                    // 截断到与正常路径同一上限：失败快照不该成为磁盘炸弹。
                    Transcript: Truncate(transcript, FailureTranscriptLimit),
                    Detail: detail,
                    ModelId: modelId,
                    RunId: runId),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 快照写不进去只影响可诊断性，不能因此跳过清理。
            _logger.LogWarning(ex, "复盘失败快照写入失败：SessionId={SessionId}", request.SessionId);
        }

        await CleanupChildAsync(childSessionId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "复盘失败已记录并清理子会话：SessionId={SourceSessionId}, ChildSessionId={ChildSessionId}, Reason={Reason}",
            sourceSessionId,
            childSessionId,
            skipReason);
    }

    /// <summary>
    /// 解析本次复盘实际使用的模型。
    /// <para>
    /// 显式配置 → 源会话已选模型 → <c>null</c>（交给运行时进程默认）。
    /// 不强制要求配置：多数场景继承父会话就是对的；但记录到 journal 才能事后核对成本。
    /// </para>
    /// </summary>
    private static string? ResolveModelId(string? configured, string? sourceSelected)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return string.IsNullOrWhiteSpace(sourceSelected) ? null : sourceSelected;
    }

    /// <summary>失败快照里 transcript 的字符上限。</summary>
    private const int FailureTranscriptLimit = 20_000;

    /// <summary>
    /// 生成一次复盘的稳定标识。
    /// <para>
    /// 形如 <c>run_20261112112834_ab12cd34</c>：时间前缀让文件按名即可大致排序，
    /// 随机后缀保证同秒并发不撞。之所以要自带前缀而不是纯 GUID：
    /// 这些文件会被人工直接翻阅，纯哈希名无从判断先后。
    /// </para>
    /// </summary>
    private static string NewRunId()
        => $"run_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..28];

    /// <summary>
    /// 读取子会话最后一条 assistant 文本；读不到会话或无文本时返回 <c>null</c>。
    /// </summary>
    private async Task<string?> ReadLastAssistantTextAsync(string sessionId, CancellationToken ct)
    {
        // 优先内存活实例：本轮刚执行完，消息必然已在缓存里；缺失再冷加载。
        var session = _sessionManager.Get(sessionId);
        if (session is null)
        {
            try
            {
                session = await _sessionManager.TryGetOrLoadAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取复盘子会话失败：ChildSessionId={ChildSessionId}", sessionId);
                return null;
            }
        }

        if (session is null)
        {
            return null;
        }

        var messages = session.GetActiveMessages();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (!string.Equals(message.Role, MessageRole.Assistant, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }

    /// <summary>清理复盘子会话：移除组成员 + 删除会话。失败只记录，不影响结果。</summary>
    private async Task CleanupChildAsync(string childSessionId, CancellationToken ct)
    {
        try
        {
            await _groupManager.RemoveSessionAsync(childSessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复盘子会话移出会话组失败：ChildSessionId={ChildSessionId}", childSessionId);
        }

        try
        {
            await _sessionManager.DeleteAsync(childSessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复盘子会话删除失败：ChildSessionId={ChildSessionId}", childSessionId);
        }
    }

    /// <summary>
    /// 把活跃消息压成复盘用的执行记录文本。
    /// <para>
    /// 两级截断：单条工具结果按 <paramref name="maxToolResultChars"/> 截断（工具输出往往是噪声主体），
    /// 全文按 <paramref name="maxTranscriptChars"/> 从<b>尾部</b>保留——复盘更关心任务如何收尾。
    /// </para>
    /// </summary>
    internal static string BuildTranscript(
        IReadOnlyList<SessionMessage> messages,
        int maxToolResultChars,
        int maxTranscriptChars)
    {
        var perItem = Math.Max(0, maxToolResultChars);
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            var role = string.IsNullOrWhiteSpace(message.Role) ? "unknown" : message.Role;

            if (string.Equals(role, MessageRole.Tool, StringComparison.Ordinal))
            {
                var toolName = string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;
                builder.Append('[').Append(role).Append(':').Append(toolName).Append(']').Append(' ');
                builder.AppendLine(Truncate(message.Content, perItem));
                continue;
            }

            builder.Append('[').Append(role).Append(']').Append(' ');
            builder.AppendLine(ExtractText(message));

            // 多模态段：不内联二进制/base64，只留「有什么」。复盘关心的是「模型看过一张图」
            // 这个事实，而不是像素；把 base64 写进 transcript 会瞬间吃满字数上限，
            // 反而把真正需要复盘的对话文本挤掉。
            AppendPartDigest(builder, message);

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    builder.Append("  - 调用 ").Append(call.Name).Append(' ');
                    builder.AppendLine(Truncate(call.Arguments, perItem));

                    if (!string.IsNullOrWhiteSpace(call.Result))
                    {
                        builder.Append("    结果 ");
                        builder.AppendLine(Truncate(call.Result, perItem));
                    }
                }
            }
        }

        var full = builder.ToString();
        var limit = Math.Max(0, maxTranscriptChars);

        if (limit == 0)
        {
            return string.Empty;
        }

        if (full.Length <= limit)
        {
            return full;
        }

        // 从尾部保留：最近的消息决定「这段工作是怎么收尾的」，开头往往是任务背景。
        const string marker = "（前文已截断）\n";
        var keep = Math.Max(0, limit - marker.Length);
        return marker + full[^keep..];
    }

    /// <summary>按字符数截断单段文本，超长时追加中文省略标记。</summary>
    private static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (limit <= 0)
        {
            return "（已省略）";
        }

        return text.Length <= limit ? text : text[..limit] + "…（已截断）";
    }

    /// <summary>
    /// 取消息的可读文本。
    /// <para>
    /// 多模态消息（<see cref="SessionMessage.UserMessageWithParts"/>）把正文放在
    /// <see cref="SessionMessage.Parts"/> 的 text 段里，<see cref="SessionMessage.Content"/> 为空。
    /// 只读 <c>Content</c> 会让「用户贴图 + 打字说明」这类消息在复盘里变成一行空白——
    /// 这正是「复盘看不到用户到底要什么」的成因。
    /// </para>
    /// </summary>
    private static string ExtractText(SessionMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
            return message.Content;

        if (message.Parts is not { Count: > 0 })
            return string.Empty;

        var texts = message.Parts
            .Where(p => string.Equals(p.Type, ContentPartType.Text, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join("\n", texts!);
    }

    /// <summary>
    /// 把消息的非文本内容段压成一行摘要（<c>[附件] 图片 screenshot.png</c>）。
    /// <para>
    /// 只留「类型 + 文件名」，不内联 URL / base64 / 本地路径：
    /// 复盘是分析流程的，二进制内容对它无用，而 base64 会瞬间吃满 transcript 上限，
    /// 把真正需要分析的对话挤出截断窗口。本地路径也不记——它是本机事实，
    /// 写进提示词只会诱导模型去「读文件」。
    /// </para>
    /// </summary>
    private static void AppendPartDigest(StringBuilder builder, SessionMessage message)
    {
        if (message.Parts is not { Count: > 0 })
            return;

        var parts = message.Parts
            .Where(p => !string.Equals(p.Type, ContentPartType.Text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (parts.Count == 0)
            return;

        builder.Append("  [附件]");
        foreach (var part in parts)
        {
            builder.Append(' ');
            builder.Append(part.Type switch
            {
                ContentPartType.Image => "图片",
                ContentPartType.Audio => "音频",
                _ => "文件"
            });

            if (!string.IsNullOrWhiteSpace(part.FileName))
                builder.Append(':').Append(part.FileName);
        }

        builder.AppendLine();
    }

    /// <summary>会话标题缺失时用 ID 前 8 位兜底，保证标题可读且稳定。</summary>
    private static string ShortSessionId(string sessionId)
        => string.IsNullOrWhiteSpace(sessionId)
            ? "session"
            : (sessionId.Length <= 8 ? sessionId : sessionId[..8]);
}
