using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Services;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>本批输入摘要：供标题门控与历史构建。</summary>
internal sealed record InboxBatch(
    IReadOnlyList<SessionMessage> Inputs,
    bool HasUserInput,
    string? FirstUserText,
    string? FirstUserMessageId = null);

/// <summary>跑一轮执行的端口（供 SessionRuntime 注入与测试 mock）。</summary>
internal interface IExecutionRunner
{
    Task<ExecutionRecord> RunOnceAsync(ExecutionRecord record, InboxBatch batch, CancellationToken executionToken);
}

/// <summary>
/// 跑一轮执行：模型/模式落盘、指令注入、投影事件、终态落盘，返回终态记录。
/// <para>
/// 不做忙闲/循环/队列管理——这些由 <c>SessionRuntime</c> 的消费循环负责。
/// </para>
/// </summary>
internal sealed class ExecutionRunner : IExecutionRunner
{
    /// <summary>
    /// 单个终态观察者的调用超时（生产默认值）。
    /// <para>
    /// 取值理由：观察者契约要求毫秒级返回（典型实现只做入队），5 秒已远超任何合规实现；
    /// 但又要足够宽容，避免机器繁忙时把正常的入队调用误判为超时。
    /// 超时的代价只是放弃本次观察（记告警），不影响本轮终态与其它观察者。
    /// </para>
    /// </summary>
    internal static readonly TimeSpan ObserverTimeout = TimeSpan.FromSeconds(5);

    private TimeSpan _observerTimeout;
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly ExecutionOptions _options;
    private readonly IConfigSectionStore _configStore;
    private readonly CompactionRunner _compactionRunner;
    private readonly ILogger<ExecutionRunner> _logger;
    private readonly ISessionToolSchemaResolver _schemaResolver;

    public ExecutionRunner(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        IConfigSectionStore configStore,
        CompactionRunner compactionRunner,
        ILogger<ExecutionRunner> logger,
        ISessionToolSchemaResolver schemaResolver)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _options = options ?? new ExecutionOptions();
        _configStore = configStore;
        _compactionRunner = compactionRunner ?? throw new ArgumentNullException(nameof(compactionRunner));
        _logger = logger;
        _observerTimeout = ObserverTimeout;
        // schema 唯一计算点经端口 ISessionToolSchemaResolver：必需注入，缺失即构造期失败，
        // 不再自构造回退实例，杜绝第二套结算路径与类型耦合。
        _schemaResolver = schemaResolver ?? throw new ArgumentNullException(nameof(schemaResolver));
    }

    /// <summary>
    /// 测试专用：把观察者超时改成较短值，避免用例真的等满 <see cref="ObserverTimeout"/>。
    /// <para>仅供 <c>InternalsVisibleTo</c> 的测试程序集使用；生产路径永远用默认值。</para>
    /// </summary>
    internal void OverrideObserverTimeoutForTest(TimeSpan timeout) => _observerTimeout = timeout;

    /// <summary>跑一轮执行：模型/模式落盘、指令注入、投影事件、终态落盘，返回终态记录。不做忙闲/循环管理。</summary>
    public async Task<ExecutionRecord> RunOnceAsync(
        ExecutionRecord record,
        InboxBatch batch,
        CancellationToken executionToken)
    {
        // Create scope for this execution
        using var scope = _serviceProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        var agentRegistry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        var executionRouter = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
        var agentSelectionResolver = scope.ServiceProvider.GetRequiredService<AgentSelectionResolver>();
        var modelManager = scope.ServiceProvider.GetRequiredService<IModelManager>();
        var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();
        var executionWorld = scope.ServiceProvider.GetRequiredService<IExecutionWorld>();
        var commandRegistry = scope.ServiceProvider.GetRequiredService<ICommandRegistry>();

        // === 本轮终态事实的承接变量（只能在真正发生的位置写入，禁止提前填「将要执行」的推测值） ===
        // Agent 名称刻意不在此处由 context.Agent 预填：命令短路时 Agent 从未执行，
        // 名称若提前赋值就会伪造「执行过某 Agent」的事实（见下方 if (!commandConsumedExecution)）。
        string? executedAgentName = null;
        var agentExecuted = false;
        var totalSteps = 0;
        TokenUsage? usage = null;
        var loopDuration = TimeSpan.Zero;

        try
        {
            // 会话确保（失败则本轮失败：与旧 ProcessExecutionAsync 一致）
            var session = await sessionManager.EnsureSessionAsync(
                record.SessionId,
                scenario: scope.ServiceProvider.GetService<IDefaultWorkModeProvider>()?.GetDefaultScenario());

            try
            {
                // === 执行前职责（原 SubmitAsync）：出站回填、模型/模式/思考强度落盘、cwd、指令注入 ===
                // 新模型：入站消息已由 Deliver 落盘，本轮不再落盘 user 消息（SkipUserMessagePersist 语义恒真），
                // 故这些副作用失败一律容忍（与旧 SkipUserMessagePersist==true 分支一致，仅告警不中断本轮）。
                TryBackfillChannelRoute(session, record.Options?.ChannelRoute);

                // ⭐ Persist model/mode selection to session (ensures they're saved even if execution fails)
                ApplyInboundModelAndMode(session, record.Options?.ModelId, record.Options?.ModeId, modelManager);
                if (record.Options?.ThinkingEffort is not null)
                {
                    session.SelectedThinkingEffort = record.Options.ThinkingEffort.Trim();
                    session.UpdatedAt = DateTime.Now;
                }

                var cwd = record.Options?.WorkingDirectory
                    ?? session.WorkingDirectory
                    ?? executionWorld.Cwd;
                if (!string.Equals(session.WorkingDirectory, cwd, StringComparison.Ordinal))
                {
                    session.WorkingDirectory = cwd;
                }

                // 上下文装配不在此处：本轮命令可能短路、工具视图结算可能失败，
                // 那时执行没有发生，就不能叙述「已告知模型」（见下方命令之后、历史之前的装配点）。

                await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist pre-execution state for execution {ExecutionId}", record.ExecutionId);
            }

            _logger.LogInformation("Execution {ExecutionId} started", record.ExecutionId);

            // Publish execution started event
            _eventPublisher.Publish(record.SessionId, new ExecutionStartedEvent
            {
                SessionId = record.SessionId,
                ExecutionId = record.ExecutionId
            });

            // 自动压缩门控：TokenBudget 标记 + 配置开启时，每轮 Agent 循环开始前触发压缩
            // GetSection 契约应返回非 null；防御性 ?. 避免测试/错误 mock 返回 null 时整轮执行 NRE 短路
            var autoCompaction = _configStore.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget")
                ?.AutoCompactionEnabled == true;
            if (autoCompaction &&
                session.PendingCompaction &&
                session.Messages.Count > 0)
            {
                // 统一入口：Started 先于摘要 Delta 发布，UI 进度时序正确
                var compactionOutcome = await _compactionRunner.RunAsync(session.Id, reason: "auto");
                // 失败防抖：不保留标记，避免每轮循环反复触发耗时的压缩尝试拖慢对话
                if (!compactionOutcome.Success)
                {
                    _logger.LogWarning("自动压缩失败，跳过本轮重试: {SessionId} {Error}",
                        session.Id, compactionOutcome.ErrorMessage);
                }
                session.PendingCompaction = false;
            }

            // Build execution context with background permission channel
            // 会话级结算 + schema 在此快照；本轮中途改 session.Scenario 不影响已算结果
            var context = await BuildExecutionContextAsync(
                session, record, agentRegistry, agentSelectionResolver, modelManager, workspaceProvider, executionWorld,
                scope.ServiceProvider);

            // 旁路生成标题（不阻塞主对话；命令不生成标题）
            // 标题门控按「本批含 Presentation=User 输入」：纯系统/子会话通知批次不生成标题。
            // 平台内部会话（复盘 / 定时 / 心跳）即使以 User 呈现也不生成标题——
            // 它们的标题是创建时显式给定的，再生成一次纯属浪费一次 LLM 调用。
            if (batch.HasUserInput
                && session.Origin == SessionOrigin.User
                && !IsCommandInput(record.Input?.Text))
            {
                var titleSource = ResolveTitleSourceText(record.Input);
                if (!string.IsNullOrWhiteSpace(titleSource))
                {
                    var titleEnsuring = scope.ServiceProvider.GetRequiredService<ISessionTitleService>();
                    var fallbackModel = context.RequestModelId ?? session.SelectedModel;
                    _ = EnsureTitleFireAndForgetAsync(
                        titleEnsuring,
                        record.SessionId,
                        titleSource,
                        fallbackModel);
                }
            }

            // Process command if applicable
            var inputText = record.Input?.Text;
            // 命令要求结束本轮（shouldContinue=false / shouldExit）时短路，不再执行 Agent：
            // 避免把命令文本作为普通消息发送给大模型（如 /compact 后仍发起 LLM 请求导致失败）
            var commandConsumedExecution = false;

            // 执行级事实（cwd / 工作区根 / 钉住技能快照）必须在**命令之前**填充：
            // /skill 与 /<技能名> 按名解析技能，必须与随后的系统提示词清单、skill 工具看到同一份快照，
            // 否则同一次执行内会出现「清单/工具一个版本、命令另一个版本」的两个真相。
            var executionScope = scope.ServiceProvider.GetRequiredService<IContextExecutionScope>();
            executionScope.SessionId = record.SessionId;
            executionScope.Cwd = context.WorkingDirectory ?? context.WorkspaceRoot ?? executionWorld.Cwd;
            executionScope.WorkspaceRoot = context.WorkspaceRoot ?? workspaceProvider.GetProjectRoot();
            executionScope.SkillSnapshot = await ResolveSkillSnapshotAsync(
                scope.ServiceProvider, record.ExecutionId, executionToken);
            // 本轮用户查询（本批最后一条用户输入）：供「查询依赖」的上下文源（如记忆召回）使用。
            // 取 record.Input（= 本批最后一条），不是 batch.FirstUserText（= 本批第一条）。
            executionScope.UserQuery = record.Input?.Text;

            if (IsCommandInput(inputText))
            {
                CommandResultEvent? lastCommandEvent = null;
                await foreach (var cmdEvent in ProcessCommandAsync(
                    record.SessionId, inputText, session, context, commandRegistry, scope.ServiceProvider,
                    executionToken))
                {
                    if (cmdEvent != null)
                    {
                        _eventPublisher.Publish(record.SessionId, cmdEvent);
                        // 短路判定仅看命令结果事件（ProcessCommandAsync 只 yield 命令结果或 null）
                        if (cmdEvent is CommandResultEvent commandResult)
                        {
                            lastCommandEvent = commandResult;
                        }
                    }
                }
                commandConsumedExecution = lastCommandEvent != null && !lastCommandEvent.ShouldContinue;
            }

            if (!commandConsumedExecution)
            {
                // === 上下文装配点：命令处理之后、历史构建之前 ===
                // 1) 命令短路（/clear、/compact 等）上面已跳过整块：本轮不执行 Agent，不装配、不叙述。
                // 2) 结算（工具视图）在上面已成功：结算失败会在 BuildExecutionContextAsync 抛出并进入失败路径。
                // 3) 执行作用域已在命令之前填充（见上），此处不再重复填充：源与命令共用同一份钉住快照。
                // 4) Agent 名称只在这里赋值：能走到这一行才代表「本轮真的执行 Agent」，
                //    提前在上方拿 context.Agent 赋值会让命令短路轮次伪造出「执行过 build」的事实。
                agentExecuted = true;
                executedAgentName = context.Agent.Name;

                var assembler = scope.ServiceProvider.GetRequiredService<IContextAssembler>();

                // 跳过集强类型：只排除项目指令源，其余源照常装配。
                IReadOnlySet<ContextSourceKey>? skip = record.Options?.SkipInstructionInject == true
                    ? new HashSet<ContextSourceKey> { ProjectInstructionsContextSource.SourceKey }
                    : null;

                try
                {
                    var assembly = await assembler.EnsureFreshAsync(
                        session,
                        ContextAssemblyMode.Persist,
                        batch.FirstUserMessageId,
                        skip,
                        executionToken);

                    // 非致命问题（读取失败 / 渲染不确定 / 超限）不得只在装配器内部逐条记录：
                    // 调用方必须看到「本轮是否降级」，汇总成一条告警（禁止静默吞掉）。
                    if (assembly.Issues.Count > 0)
                    {
                        _logger.LogWarning(
                            "上下文装配存在非致命问题（本轮已降级）: ExecutionId={ExecutionId}, SessionId={SessionId}, Issues={Issues}",
                            record.ExecutionId,
                            record.SessionId,
                            string.Join(" | ", assembly.Issues));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 装配失败不阻断对话：记录可诊断事实，本轮仍带已存基线执行
                    _logger.LogError(
                        ex,
                        "上下文装配失败，本轮沿用已存基线: ExecutionId={ExecutionId}, SessionId={SessionId}",
                        record.ExecutionId,
                        record.SessionId);
                }

                context.ContextBaseline = assembler.BuildSystemBaseline(session);
                context.SkillSnapshot = executionScope.SkillSnapshot;

                // Build history（含装配刚插入的增量叙述消息）
                var messages = BuildHistoryFromSession(session);

                // 服务端负责将事件投影到 Session 并落盘；UI 只订阅展示
                var eventTracker = new ChatEventTracker();

                // schema 快照事件：与随后 ChatRequest.Tools 同源（同一份工具视图结算）
                var settlement = context.ToolSettlement ?? SessionToolSchemaResolution.Empty;
                var schemaSnapshot = new SchemaSnapshotEvent
                {
                    SessionId = record.SessionId,
                    ExecutionId = record.ExecutionId,
                    ToolIds = settlement.InlineSchemas
                        .Where(s => s.Function != null)
                        .Select(s => s.Function.Name)
                        .ToArray(),
                    SectionIds = context.SectionIds,
                    ExposureMode = settlement.Layout == ToolExposureLayout.Tiered
                        ? ToolExposurePolicyMode.Tiered
                        : ToolExposurePolicyMode.All,
                    InlineToolCount = settlement.InlineToolCount,
                    DeferredToolCount = settlement.DeferredToolCount,
                    EstimatedDefinitionTokens = settlement.SchemaTokens,
                    CatalogTokens = settlement.CatalogTokens
                };
                eventTracker.ApplyEvent(session, schemaSnapshot);
                _eventPublisher.Publish(record.SessionId, schemaSnapshot);
                if (ShouldPersistEvent(schemaSnapshot))
                    await sessionManager.SaveAsync(record.SessionId);

                // Execute agent
                // 取消不再主动 throw：执行器（事件流水线）会产出终态事件（工具 Cancelled / LoopCancelledEvent），
                // 这些事件必须在取消后仍被处理与发布。OCE 由下方 catch 兜底（执行器未转换为事件的极端情况）。
                // 令牌已在本方法入参绑定本记录（executionToken），BuildAgentContext 与 ExecuteAsync 共用同一令牌。
                var loopFailed = false;
                string? loopError = null;
                // 已计入事实的 LoopId：同一次执行的终态事件可能重复出现（「Complete 后又来 Cancelled」、
                // 重放/重投递），同一 LoopId 的同一批事实只计一次。
                // ⚠ 先到先得：Cancelled 先到会占位，后续 Complete 被丢弃（少算）。当前不可达，详见下方分支注释。
                var terminalLoopIds = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var evt in executionRouter.ExecuteAsync(
                    context.Agent,
                    messages,
                    BuildAgentContext(context, executionToken),
                    executionToken))
                {
                    // 事件流语义：LLM/Agent 失败以 ErrorEvent + LoopCompleteEvent(Success=false) 表达（不抛异常）
                    // 若不在此捕获，下方正常结束路径会把 record 标记为 Completed，导致 task_status 无法判定失败。
                    if (evt is ErrorEvent err)
                    {
                        loopFailed = true;
                        loopError = err.Message;
                    }
                    else if (evt is LoopCompleteEvent loopEnd)
                    {
                        // 按 LoopId 去重 + 累加，两者正交且都必须：
                        // ① 去重：同一次执行的终态事件可能重复出现（「Complete 后又来 Cancelled」、
                        //    重放/重投递），同一 LoopId 的同一批事实只能计一次；
                        // ② 累加：不同 LoopId（多轮）必须逐轮相加。
                        //
                        // ⚠ 去重是**先到先得**，且这里有一个已知的隐性语义：
                        //    若 Cancelled（只带部分事实）先到、Complete 后到，Complete 会被丢弃 → **少算**。
                        //    当前执行器不可达（OCE 分支只发 Cancelled，正常分支只发 Complete，互斥），
                        //    但一旦将来出现「先取消后收尾」的事件序列，必须改成按事件类型排序
                        //    （Complete 优先）而不是按到达顺序。
                        if (terminalLoopIds.Add(loopEnd.LoopId))
                        {
                            totalSteps += loopEnd.TotalSteps;
                            usage = MergeUsage(usage, loopEnd.Usage);
                            loopDuration += loopEnd.Duration;
                        }

                        if (!loopEnd.Success)
                        {
                            loopFailed = true;
                            loopError ??= loopEnd.Error;
                        }
                    }
                    else if (evt is LoopCancelledEvent loopCancelled)
                    {
                        // 取消路径**不产出** LoopCompleteEvent（AgentExecutor 的 OCE 分支只写本事件），
                        // 部分事实只能从这里取：不取就会把「取消前已完成的工作」全部记为 0。
                        // 与 LoopComplete 共用同一个去重集合：同一 LoopId 已有终态事实时不再重复计入。
                        if (terminalLoopIds.Add(loopCancelled.LoopId))
                        {
                            totalSteps += loopCancelled.CompletedSteps;
                            usage = MergeUsage(usage, loopCancelled.PartialUsage);
                        }
                    }

                    var liveSession = sessionManager.Get(record.SessionId) ?? session;
                    eventTracker.ApplyEvent(liveSession, evt);

                    // 先投影再发布，保证 UI 读到的是已写入的 SessionData
                    _eventPublisher.Publish(record.SessionId, evt);

                    if (ShouldPersistEvent(evt))
                        await sessionManager.SaveAsync(record.SessionId);
                }

                if (loopFailed)
                {
                    record.Status = ExecutionStatus.Failed;
                    record.ErrorMessage = loopError;
                }
            }

            // 执行器以终态事件正常结束时：若本执行已被取消（Interrupt/Stop 取消令牌）或
            // 事件流已标记 Failed，保留对应终态；否则标记完成。
            if (record.Status != ExecutionStatus.Cancelled &&
                record.Status != ExecutionStatus.Failed &&
                executionToken.IsCancellationRequested)
                record.Status = ExecutionStatus.Cancelled;
            else if (record.Status != ExecutionStatus.Cancelled &&
                     record.Status != ExecutionStatus.Failed)
                record.Status = ExecutionStatus.Completed;

            // 事件流失败（非异常）或 catch 异常失败：统一标记会话 Error 状态。
            // 执行失败后执行记录很快进入终态，仅靠执行记录无法长期判定失败，
            // task_status 回落判定依赖 SessionStatus。
            if (record.Status == ExecutionStatus.Failed)
                MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);
            else if (record.Status == ExecutionStatus.Completed)
                ClearSessionError(sessionManager, record.SessionId);

            _logger.LogInformation("Execution {ExecutionId} completed with status {Status}", record.ExecutionId, record.Status);
        }
        catch (OperationCanceledException)
        {
            record.Status = ExecutionStatus.Cancelled;
            _logger.LogInformation("Execution {ExecutionId} was cancelled", record.ExecutionId);

            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, "用户取消") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取消后标记未完成 Task 失败: {ExecutionId}", record.ExecutionId);
            }
        }
        catch (Exception ex)
        {
            record.Status = ExecutionStatus.Failed;
            record.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Execution {ExecutionId} failed", record.ExecutionId);

            // 标记会话 Error 状态：task_status 回落判定依赖 SessionStatus
            MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);

            // 失败路径兜底：标记未走到终态的工具调用（pending/running）为 cancelled，
            // 避免异常退出后遗留孤儿的"运行中"工具状态（对齐 OperationCanceledException 路径）。
            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, $"执行失败：{ex.Message}") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception markerEx)
            {
                _logger.LogWarning(markerEx, "失败后标记未完成工具调用异常: {ExecutionId}", record.ExecutionId);
            }

            // Publish error event
            _eventPublisher.Publish(record.SessionId, new ErrorEvent
            {
                SessionId = record.SessionId,
                Message = ex.Message
            });
        }
        finally
        {
            record.CompletedAt = DateTime.UtcNow;

            // 把本轮事实写回记录：记录是「可变生命周期」层，其它消费方
            // （SessionRuntime.BuildOutcome、task_status 等）只能读记录，故事实必须先落到这里。
            record.AgentExecuted = agentExecuted;
            record.AgentName = executedAgentName;
            record.TotalSteps = totalSteps;
            record.Usage = usage;

            // Final save（先写 history 再落盘，避免 history 永远落后一次）
            try
            {
                await AppendExecutionHistoryAsync(sessionManager, record);
                await sessionManager.SaveAsync(record.SessionId);
                // 执行终态落盘：flush 确保最终状态持久化
                await sessionManager.FlushAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save final state for execution {ExecutionId}", record.ExecutionId);
            }

            // === 组装终态事实并发布 + 扇出观察者（落盘之后：事件里的快照与磁盘一致） ===
            //
            // ⚠ 已知边界（诚实声明，不是「更利于暴露缺陷」）：
            //    此处**没有** try/catch。若 BuildOutcome / Publish / 扇出真的抛出，异常会逃出 finally，
            //    后果是「双重坏」而不是「暴露缺陷」：
            //      ① SessionRuntime 的调用点把**已成功**的 record.Status 改写成 Failed（`catch (Exception)`）；
            //      ② ExecutionCompleteEvent **从未发布** → UI 可能一直停在「执行中」。
            //    真正能兜住这个风险的只有 ResolveSessionRelation 内部的 catch（覆盖会话读取失败）。
            //    要彻底消除该边界，需要把「组装 + 发布」也包上 try/catch 并降级为最小快照 —— 但那会隐瞒
            //    发布失败，属于另一处取舍，本轮不改。
            // 会话活动时间戳：主链事实，不依赖任何可选能力是否开启。
            // 原先只有记忆钩子会 Touch，导致「记忆捕获关闭 → 复盘的 idle 触发永久失效」——
            // 这是隐性耦合：后台扫描器靠这个时间戳判定空闲，而它不该由某个可选能力决定是否存在。
            try
            {
                scope.ServiceProvider.GetService<ISessionActivityTracker>()?.Touch(record.SessionId);
            }
            catch (Exception ex)
            {
                // 活跃度只影响后台扫描的时机，不能让它影响执行终态。
                _logger.LogDebug(ex, "记录会话活跃度失败（已忽略）: SessionId={SessionId}", record.SessionId);
            }

            var outcome = BuildOutcome(record, scope.ServiceProvider, agentExecuted, executedAgentName, totalSteps, usage, loopDuration);

            // 发布执行终态事件：UI/TUI 据此结束「执行中」态并刷新。
            // 每轮执行（含消费循环的每一轮）恰发布一次。
            _eventPublisher.Publish(record.SessionId, new ExecutionCompleteEvent
            {
                SessionId = record.SessionId,
                ExecutionId = record.ExecutionId,
                Outcome = outcome
            });

            await FanOutObserversAsync(scope.ServiceProvider, outcome);
        }

        return record;
    }

    /// <summary>
    /// 组装本轮终态事实快照。
    /// <para>
    /// 只陈述已发生的事实：<paramref name="agentExecuted"/> 为 <c>false</c> 时名称必为 <c>null</c>、步数为 0；
    /// 会话关系读不到时按顶层处理（保守：不把根会话误判成子 Agent）。
    /// </para>
    /// <para>
    /// <c>CompletedAt</c> 必须显式取自 <see cref="ExecutionRecord.CompletedAt"/>：契约上的默认值
    /// <c>DateTime.UtcNow</c> 在对象构造时求值，漏赋值会让「构造时刻」冒充「终态时刻」。
    /// </para>
    /// </summary>
    private ExecutionOutcome BuildOutcome(
        ExecutionRecord record,
        IServiceProvider services,
        bool agentExecuted,
        string? agentName,
        int totalSteps,
        TokenUsage? usage,
        TimeSpan loopDuration)
    {
        var (isTopLevel, parentSessionId, origin) = ResolveSessionRelation(services, record.SessionId);
        var completedAt = record.CompletedAt ?? DateTime.UtcNow;

        return new ExecutionOutcome
        {
            SessionId = record.SessionId,
            ExecutionId = record.ExecutionId,
            Status = record.Status,
            AgentName = agentExecuted ? agentName : null,
            AgentExecuted = agentExecuted,
            IsTopLevel = isTopLevel,
            ParentSessionId = parentSessionId,
            Origin = origin,
            TotalSteps = totalSteps,
            Usage = usage,
            Duration = loopDuration,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// 解析「是否顶层 + 父会话 Id + 会话来源」。
    /// <para>
    /// 真值来源是 <see cref="SessionData.Kind"/>（<c>SubAgent</c> → 非顶层）、会话组的父子关系、
    /// 以及 <see cref="SessionData.Origin"/>；<b>不得</b>读 <c>ChatExecutionContext.IsTopLevel</c>
    /// （该字段在唯一构造点从未赋值）。
    /// </para>
    /// <para>
    /// 三者**同一次**读取会话得出：把 <c>Origin</c> 一并下发，观察者就不必为了判定来源再回读会话
    /// （终态钩子上不该出现第二次 I/O）。<paramref name="services"/> 是执行作用域，
    /// <see cref="ISessionManager.Get"/> 命中内存缓存，不产生磁盘访问。
    /// </para>
    /// <para>读不到会话时按「顶层 + 用户来源」处理（保守：不把根会话误判成内部会话）。</para>
    /// </summary>
    private (bool IsTopLevel, string? ParentSessionId, SessionOrigin Origin) ResolveSessionRelation(
        IServiceProvider services, string sessionId)
    {
        try
        {
            var session = services.GetService<ISessionManager>()?.Get(sessionId);
            if (session is null)
                return (true, null, SessionOrigin.User);

            var origin = session.Origin;
            if (session.Kind != SessionKind.SubAgent)
                return (true, null, origin);

            var groupManager = services.GetService<ISessionGroupManager>();
            var parentId = groupManager is not null && groupManager.TryGetParent(sessionId, out var parent)
                ? parent
                : null;
            return (false, parentId, origin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话关系失败，按顶层处理: SessionId={SessionId}", sessionId);
            return (true, null, SessionOrigin.User);
        }
    }

    /// <summary>
    /// 逐字段累加 Token 用量：任一为 <c>null</c> 时返回另一方（<c>null</c> 表示「未知」而不是零）。
    /// </summary>
    internal static TokenUsage? MergeUsage(TokenUsage? total, TokenUsage? delta)
    {
        if (delta is null)
            return total;
        if (total is null)
            return delta;

        return new TokenUsage
        {
            InputTokens = total.InputTokens + delta.InputTokens,
            OutputTokens = total.OutputTokens + delta.OutputTokens,
            ReasoningTokens = total.ReasoningTokens + delta.ReasoningTokens
        };
    }

    /// <summary>
    /// 扇出终态给全部观察者。
    /// <para>
    /// 每个观察者独立 try/catch 与独立超时（一个失败/超时都不阻断其它、也不影响本轮终态）；
    /// 异常与超时只记 <c>LogWarning</c>。
    /// 取消令牌一律用 <see cref="CancellationToken.None"/>：本轮被取消同样是一次有效终态，
    /// 观察者必须收到通知，不能因为执行令牌已取消而被静默跳过。
    /// </para>
    /// <para>
    /// 依次 <c>await</c>（带超时）而非 fire-and-forget：契约要求实现「毫秒级返回」，
    /// 正常情况下 <c>await</c> 的代价是一次入队调用的开销；顺序等待保证 async 方法
    /// <c>await</c> 之后抛出的异常（落在返回的 Task 上）也能被这里捕获隔离，
    /// 而丢弃 Task（<c>_ = ...</c>）会让这类异常变成未观察异常，测试也无确定性观测点。
    /// </para>
    /// <para>
    /// <b>已知边界（无总预算）：</b>超时是**每个观察者各自**的上限，不是扇出整体预算。
    /// N 个都超时的观察者会串行耗掉约 N×<see cref="ObserverTimeout"/>，全程持有
    /// <c>SessionRuntime</c> 的全局并发额度（<c>RunOnceAsync</c> 返回前不会释放）。
    /// 当前观察者数量为个位数且契约要求毫秒级返回，故不引入总预算；
    /// 若将来观察者变多或普遍慢，应改为「整轮扇出共享一个总预算」。
    /// </para>
    /// </summary>
    private async Task FanOutObserversAsync(IServiceProvider services, ExecutionOutcome outcome)
    {
        IReadOnlyList<IExecutionOutcomeObserver> observers;
        try
        {
            observers = services.GetServices<IExecutionOutcomeObserver>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析终态观察者失败，跳过扇出: ExecutionId={ExecutionId}", outcome.ExecutionId);
            return;
        }

        foreach (var observer in observers)
        {
            var observerName = observer.GetType().Name;
            try
            {
                if (!observer.ShouldObserve(outcome))
                    continue;

                // 超时兜底：观察者契约要求毫秒级返回，超时即视为「实现违约」。
                // 不设超时会让一个慢观察者连带阻塞 DeliverAndAwaitAsync 的兑现与全局并发额度
                // （RunOnceAsync 返回前必须先走完 finally）。
                await observer
                    .OnExecutionCompletedAsync(outcome, CancellationToken.None)
                    .WaitAsync(_observerTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "终态观察者超时（已隔离，不影响其它观察者）: Observer={Observer}, ExecutionId={ExecutionId}, TimeoutSeconds={TimeoutSeconds}",
                    observerName,
                    outcome.ExecutionId,
                    _observerTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "终态观察者处理失败（已隔离，不影响其它观察者）: Observer={Observer}, ExecutionId={ExecutionId}",
                    observerName,
                    outcome.ExecutionId);
            }
        }
    }

    /// <summary>
    /// Builds the execution context for an execution.
    /// </summary>
    private async Task<ChatExecutionContext> BuildExecutionContextAsync(
        SessionData session,
        ExecutionRecord record,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        IWorkspaceProvider workspaceProvider,
        IExecutionWorld executionWorld,
        IServiceProvider services)
    {
        var agentId = await agentSelectionResolver.ResolveAgentIdAsync(
            record.Options?.AgentId,
            session.SelectedAgent,
            CancellationToken.None).ConfigureAwait(false);

        var agentDef = await agentRegistry.GetAgentAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        if (agentDef.Disabled)
            throw new InvalidOperationException($"Agent '{agentId}' is disabled");

        // 执行级权限授权器：经 IPermissionAuthorizerFactory 构造，
        // 覆盖值来自 ChatOptions.AutoApprove（null=FollowGlobal）；实时生效策略由授权器/引擎解析。
        var permissionAuthorizer = ResolvePermissionAuthorizer(
            services, record.SessionId, record.Options?.AutoApprove);

        var sessionModelRef = modelManager.GetSessionModelRef(session);
        var sessionModelRefOrNull = string.IsNullOrEmpty(sessionModelRef) ? null : sessionModelRef;

        string? requestModelId = agentDef.Runtime == AgentRuntime.AcpPassthrough
            ? modelManager.ResolveAcpModel(record.Options?.ModelId, sessionModelRefOrNull)
            : modelManager.ResolveNativeModel(record.Options?.ModelId, sessionModelRefOrNull, agentId);

        var acpModeId = agentSelectionResolver.ResolveAcpModeId(
            record.Options?.ModeId,
            session.SelectedAcpMode);

        var projectRoot = workspaceProvider.GetProjectRoot();
        var cwd = record.Options?.WorkingDirectory
            ?? session.WorkingDirectory
            ?? executionWorld.Cwd;

        // 工具视图唯一出口：轮首先把上一轮登记的发现提升为生效，再结算本轮视图，
        // 最后把结算增量提交回会话（发现集裁剪、升级粘滞位、每轮度量）。
        // 写入统一经 ISessionToolExposureWriter（主链与 Scheduler 旁路共用同一实现）。
        var exposureWriter = services.GetRequiredService<ISessionToolExposureWriter>();
        exposureWriter.PromotePending(session);
        var schemaSettlement = await _schemaResolver.ResolveAsync(
            session, agentDef).ConfigureAwait(false);
        exposureWriter.Commit(
            session,
            schemaSettlement.Discovered,
            schemaSettlement.IsEscalated,
            schemaSettlement.SchemaTokens,
            schemaSettlement.CatalogTokens);
        var sectionIds = schemaSettlement.SectionIds;

        string? requestThinkingEffort = record.Options?.ThinkingEffort;
        if (requestThinkingEffort is null && !string.IsNullOrWhiteSpace(session.SelectedThinkingEffort))
            requestThinkingEffort = session.SelectedThinkingEffort;

        return new ChatExecutionContext
        {
            SessionId = record.SessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = cwd,
            WorkspaceRoot = projectRoot,
            PermissionAuthorizer = permissionAuthorizer,
            ChannelRoute = record.Options?.ChannelRoute,
            AcpModeId = acpModeId,
            RequestModelId = requestModelId,
            RequestThinkingEffort = requestThinkingEffort,
            ToolSettlement = schemaSettlement,
            SectionIds = sectionIds
        };
    }

    /// <summary>
    /// 构造本执行级权限授权器（经 <see cref="IPermissionAuthorizerFactory"/>）。
    /// <para>
    /// <paramref name="autoApprove"/> 为执行级冻结覆盖（来自 <c>ChatOptions.AutoApprove</c>）；
    /// null 表示 FollowGlobal，由授权器/引擎实时解析会话三态与全局开关。
    /// </para>
    /// </summary>
    internal IPermissionAuthorizer? ResolvePermissionAuthorizer(
        IServiceProvider services,
        string sessionId,
        SessionAutoApprove? autoApprove)
    {
        var factory = services.GetService<IPermissionAuthorizerFactory>();
        if (factory is null)
        {
            _logger.LogWarning(
                "IPermissionAuthorizerFactory 未注册，本次执行无权限授权器: SessionId={SessionId}",
                sessionId);
            return null;
        }

        return factory.Create(sessionId, autoApprove);
    }

    /// <summary>
    /// 解析本轮执行钉住的技能快照：执行开始时固化一次，随后装配器与工具看到同一个对象。
    /// <para>未组合技能模块时返回 null（技能源按不可用处理）；目录刷新失败时告警并退回当前快照，
    /// 不阻断对话，也不静默吞掉失败。</para>
    /// </summary>
    private async Task<SkillCatalogSnapshot?> ResolveSkillSnapshotAsync(
        IServiceProvider services,
        string executionId,
        CancellationToken cancellationToken)
    {
        var catalog = services.GetService<ISkillCatalog>();
        if (catalog is null)
            return null;

        try
        {
            return await catalog.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "技能目录刷新失败，本轮钉住当前快照: ExecutionId={ExecutionId}, Version={Version}",
                executionId,
                catalog.Current.Version);
            return catalog.Current;
        }
    }

    /// <summary>
    /// 是否应在服务端落盘（UI 不负责 Save）。
    /// </summary>
    private static bool ShouldPersistEvent(IMessageEvent evt) => evt switch
    {
        SchemaSnapshotEvent => true,
        StreamCompleteEvent => true,
        ToolCallEvent
        {
            Status: ToolCallStatus.Pending or ToolCallStatus.Success
            or ToolCallStatus.Failed or ToolCallStatus.Rejected or ToolCallStatus.Cancelled
        } => true,
        LoopCompleteEvent or LoopCancelledEvent or ErrorEvent => true,
        _ => false
    };

    /// <summary>
    /// Writes <see cref="SessionData.ChannelRoute"/> only when the session route is empty (first-write-wins).
    /// </summary>
    /// <returns>True when the route was written.</returns>
    internal static bool TryBackfillChannelRoute(SessionData session, SessionChannelRoute? inbound)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!IsEmptyChannelRoute(session.ChannelRoute))
            return false;

        if (inbound is null || string.IsNullOrWhiteSpace(inbound.ChannelId))
            return false;

        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = inbound.ChannelId.Trim(),
            Kind = inbound.Kind.Trim(),
            ExternalId = inbound.ExternalId.Trim(),
            Title = string.IsNullOrWhiteSpace(inbound.Title) ? null : inbound.Title.Trim()
        };
        return true;
    }

    private static bool IsEmptyChannelRoute(SessionChannelRoute? route) =>
        route is null || string.IsNullOrWhiteSpace(route.ChannelId);

    /// <summary>
    /// Persists inbound model/mode from <see cref="ChatOptions"/> onto the session before execution.
    /// </summary>
    internal static bool ApplyInboundModelAndMode(
        SessionData session,
        string? modelId,
        string? modeId,
        IModelManager modelManager)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modelManager);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(modelId))
            changed |= modelManager.ApplyModelToSession(session, modelId);

        changed |= TryBackfillSessionAcpMode(session, modeId);

        if (changed)
            session.UpdatedAt = DateTime.Now;

        return changed;
    }

    /// <summary>
    /// Updates session ACP mode when provided in inbound options.
    /// </summary>
    internal static bool TryBackfillSessionAcpMode(SessionData session, string? modeId)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(modeId))
            return false;

        var trimmed = modeId.Trim();
        if (string.Equals(session.SelectedAcpMode ?? string.Empty, trimmed, StringComparison.Ordinal))
            return false;

        session.SelectedAcpMode = trimmed;
        return true;
    }

    /// <summary>
    /// 从输入提取用于标题生成的文本（优先 Text，否则拼接文本 Parts）。
    /// </summary>
    private static string? ResolveTitleSourceText(ChatInput? input)
    {
        if (input == null)
            return null;

        if (!string.IsNullOrWhiteSpace(input.Text))
            return input.Text;

        return null;
    }

    /// <summary>
    /// Fire-and-forget 标题确保；独立于主执行取消。
    /// </summary>
    private async Task EnsureTitleFireAndForgetAsync(
        ISessionTitleService ensuring,
        string sessionId,
        string userText,
        string? fallbackModel)
    {
        try
        {
            var title = await ensuring.TryEnsureAsync(
                sessionId,
                userText,
                fallbackModel,
                CancellationToken.None);

            if (!string.IsNullOrEmpty(title))
            {
                _eventPublisher.Publish(sessionId, new SessionTitleChangedEvent
                {
                    SessionId = sessionId,
                    Title = title
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title ensure failed: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 判定是否为命令输入（/ 开头、至少一个有效字符、非 // 转义）。
    /// </summary>
    private static bool IsCommandInput([NotNullWhen(true)] string? text)
        => text != null
           && text.Length > 1
           && text[0] == '/'
           && !text.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// 移除命令消息（按输入文本匹配最后一条 user 消息，命令文本不得残留在会话中）。
    /// </summary>
    private static void RemoveCommandMessage(SessionData session, string? inputText)
    {
        session.RemoveLastMessage(m =>
            string.Equals(m.Content, inputText, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds history from session.
    /// </summary>
    internal static List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();

        // 统一消息来源：仅活跃消息（已压缩标记的旧消息保留展示但不传递给 LLM）
        // 跳过旧版空 system 仅承载 schema_snapshot 的载体（不进 LLM 历史）
        foreach (var msg in session.GetActiveMessages())
        {
            if (IsSchemaSnapshotCarrierMessage(msg))
                continue;

            if (IsRuntimeNotificationMessage(msg))
                continue;

            if (IsNonHistoryInboxMessage(msg))
                continue;

            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent,
                ReasoningSignature = msg.ReasoningSignature,
                ToolCallId = msg.ToolCallId
            };

            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                chatMessage.Parts = msg.Parts.Select(p => new ChatContentPart
                {
                    Type = p.Type,
                    Text = p.Text,
                    Url = p.Url,
                    DataBase64 = p.DataBase64,
                    MimeType = p.MimeType,
                    FileName = p.FileName
                }).ToList();
            }

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                chatMessage.ToolCalls = msg.ToolCalls.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new FunctionCall
                    {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }).ToList();
            }

            history.Add(chatMessage);

            // 工具结果在会话中内嵌于 assistant 消息的 ToolCalls[].Result，
            // 此处展开为独立的 tool 消息，满足 OpenAI 对 assistant(tool_calls)
            // 后必须紧跟对应 tool 消息的要求，否则重建历史会触发 400。
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = ToolResultFormatting.ToModelContent(
                            string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase),
                            tc.Result,
                            tc.Error,
                            tc.Name,
                            tc.Status,
                            tc.Title)
                    });
                }
            }
        }

        return history;
    }

    /// <summary>
    /// 旧版空 system 仅承载 schema_snapshot，不进入 LLM 历史。
    /// </summary>
    private static bool IsSchemaSnapshotCarrierMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(msg.Content))
            return false;
        if (msg.Parts is { Count: > 0 })
            return false;
        return msg.Metadata?.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey) == true;
    }

    /// <summary>
    /// 运行时通知（错误/取消）不进 LLM 历史。
    /// 新数据已不再产生；此处兼容存量消息（前缀）与显式 transient 标记。
    /// </summary>
    private static bool IsRuntimeNotificationMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;

        if (msg.Metadata?.TryGetValue("transient", out var flag) == true)
        {
            if (flag is bool b && b)
                return true;
            // 落盘再加载后 Metadata 值为 JsonElement，需兼容
            if (flag is System.Text.Json.JsonElement je
                && je.ValueKind == System.Text.Json.JsonValueKind.True)
                return true;
        }

        if (string.IsNullOrEmpty(msg.Content))
            return false;

        return msg.Content.StartsWith("错误: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("对话已取消: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("压缩失败", StringComparison.Ordinal)
            || msg.Content.StartsWith("⚠️ 执行已取消", StringComparison.Ordinal)
            || msg.Content.StartsWith("❌ 执行出错", StringComparison.Ordinal);
    }

    /// <summary>
    /// 入站消息中 <c>inbox.presentation=none</c>（可持久化展示但不进 LLM 历史，如 Gateway 横幅）。
    /// 落盘再加载后 Metadata 值可能为 <see cref="JsonElement"/>，故用 <c>ToString()</c> 兼容。
    /// </summary>
    private static bool IsNonHistoryInboxMessage(SessionMessage msg)
    {
        if (msg.Metadata?.TryGetValue(InboxMetadataKeys.Presentation, out var raw) != true || raw is null)
            return false;

        var presentation = raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };

        return string.Equals(presentation, "none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds agent context from execution context.
    /// </summary>
    private static AgentContext BuildAgentContext(ChatExecutionContext context, CancellationToken cancellationToken)
    {
        var agentContext = new AgentContext
        {
            SessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory ?? context.WorkspaceRoot ?? "",
            WorkspaceRoot = context.WorkspaceRoot ?? "",
            PermissionAuthorizer = context.PermissionAuthorizer,
            CancellationToken = cancellationToken,
            ToolSettlement = context.ToolSettlement,
            ContextBaseline = context.ContextBaseline,
            SkillSnapshot = context.SkillSnapshot
        };

        // 传递请求级模型选择到 Metadata（适用于 Native Agent 和 ACP Passthrough）
        // 优先级：用户选择 > Agent 配置 > 全局默认
        if (!string.IsNullOrEmpty(context.RequestModelId))
            agentContext.Metadata[AgentContextKeys.RequestModelId] = context.RequestModelId;

        if (context.RequestThinkingEffort is not null)
            agentContext.Metadata[AgentContextKeys.RequestThinkingEffort] = context.RequestThinkingEffort;

        if (!string.IsNullOrEmpty(context.AcpModeId))
            agentContext.Metadata[AgentContextKeys.AcpModeId] = context.AcpModeId;

        return agentContext;
    }

    /// <summary>
    /// Processes a command during execution.
    /// </summary>
    private async IAsyncEnumerable<IMessageEvent?> ProcessCommandAsync(
        string sessionId,
        string input,
        SessionData session,
        ChatExecutionContext context,
        ICommandRegistry commandRegistry,
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cmdName = input.Split(' ').FirstOrDefault()?.TrimStart('/') ?? "";
        var currentRuntime = context.Agent.Runtime;

        // 按 Runtime 查找命令
        var command = commandRegistry.GetCommand(cmdName, currentRuntime);
        if (command == null)
        {
            // 没有匹配的命令，透传给 Agent
            yield return null;
            yield break;
        }

        // 直接执行命令（薄上下文：仅信息传递，命令自包含会话操作，宿主不做写同步）
        // Services + 钉住快照一并传入：命令不得依赖「上下文里没人填过的槽位」，也不得读目录当前态。
        var executionScope = services.GetService<IContextExecutionScope>();
        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            Input = input,
            Arguments = input.Contains(' ') ? input.Substring(input.IndexOf(' ') + 1) : "",
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            CancellationToken = cancellationToken,
            Services = services,
            Skill = executionScope?.SkillSnapshot
        };

        var result = await command.ExecuteAsync(cmdContext, cancellationToken);

        // 命令产生操作结果声明是否移除命令消息（默认移除），由宿主统一处理；
        // 命令可返回 false 保留（如 Skill 展开后把命令消息替换为技能内容）。
        // 未知命令不在此路径（command==null 时文本保留并作为普通消息透传）。
        if (result.RemoveCommandMessage)
        {
            RemoveCommandMessage(session, input);
        }

        // 根据 CommandResult 决定是否继续
        // 命令结果事件总是发布：短路命令据此终止本轮；继续类命令（如 /skill 展开、
        // Markdown 模板）借此携带 NeedsRefresh，让 UI 刷新被命令改写的消息内容
        // （否则内容已改并落盘但界面不刷新，需手动刷新页面才可见）。
        yield return new CommandResultEvent
        {
            SessionId = sessionId,
            CommandName = cmdName,
            Success = result.Success,
            Message = result.Success ? result.Message : result.ErrorMessage,
            NavigationTarget = result.GetNavigationTarget(),
            NeedsRefresh = result.NeedsRefresh,
            // 短路标志：宿主据此跳过 Agent 执行，不再把命令文本发给大模型
            ShouldContinue = result.ShouldContinue && !result.ShouldExit
        };

        if (!result.ShouldContinue || result.ShouldExit)
            yield break;

        // 继续执行 Agent
        yield return null;
    }

    /// <summary>
    /// 标记会话为 Error 状态（执行失败时调用）。
    /// 仅标记 SubAgent 子会话：task_status 回落判定依赖 SessionStatus，而 Root/Fork 会话无此消费方。
    /// 子会话执行失败后，ExecutionRecord 终态很快从队列移除，仅靠执行记录无法长期判定失败。
    /// </summary>
    private void MarkSessionError(ISessionManager sessionManager, string sessionId, string? errorMessage)
    {
        try
        {
            var failedSession = sessionManager.Get(sessionId);
            if (failedSession != null && failedSession.Kind == SessionKind.SubAgent)
            {
                failedSession.Status = SessionStatus.Error;
                failedSession.UpdatedAt = DateTime.Now;
                if (!string.IsNullOrEmpty(errorMessage))
                    failedSession.Metadata[SessionMetadataKeys.LastError] = errorMessage;
            }
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "标记会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 子会话失败标记 Error 后，成功续跑（传递 task_id 继续任务）时清除 Error 标记。
    /// 否则子会话一旦失败，即使后续成功续跑，task_status 仍会误判为 error。
    /// </summary>
    private void ClearSessionError(ISessionManager sessionManager, string sessionId)
    {
        try
        {
            var session = sessionManager.Get(sessionId);
            if (session != null &&
                session.Kind == SessionKind.SubAgent &&
                session.Status == SessionStatus.Error)
            {
                session.Status = SessionStatus.Active;
                session.Metadata.Remove(SessionMetadataKeys.LastError);
                session.UpdatedAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Appends execution history to session metadata.
    /// </summary>
    private async Task AppendExecutionHistoryAsync(ISessionManager sessionManager, ExecutionRecord record)
    {
        var session = sessionManager.Get(record.SessionId);
        if (session == null) return;

        var historyJson = session.Metadata.GetValueOrDefault("execution_history", "[]");
        var history = JsonSerializer.Deserialize<List<ExecutionHistoryEntry>>(historyJson) ?? new();

        history.Add(new ExecutionHistoryEntry
        {
            ExecutionId = record.ExecutionId,
            Status = record.Status,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            ErrorMessage = record.ErrorMessage
        });

        // Limit history size
        if (history.Count > _options.ExecutionHistoryLimit)
        {
            history = history.TakeLast(_options.ExecutionHistoryLimit).ToList();
        }

        session.Metadata["execution_history"] = JsonSerializer.Serialize(history);
    }
}

/// <summary>
/// Entry for execution history.
/// </summary>
public class ExecutionHistoryEntry
{
    public string ExecutionId { get; set; } = "";
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
