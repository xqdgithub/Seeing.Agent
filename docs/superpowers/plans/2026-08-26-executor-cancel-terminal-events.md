# Agent 执行器取消终态事件完备性修复计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Native `AgentExecutor` 在取消时发布 `LoopCancelledEvent`、保证工具执行终态事件（含 `Cancelled`）完整送达，并让 WebUI 取消路径原子化落终态，使界面不再卡在"执行中"。

**Architecture:** 将 `AgentExecutor.ExecuteAsync` 从"裸迭代器 + 取消即抛 OCE"重构为"事件流水线"：后台生产者把 `LoopStart → 恰一个终态事件` 的不变量写进 outbound channel，取消/未知异常在生产者侧统一转换为终态事件（`LoopCancelledEvent` / `ErrorEvent`+`LoopCompleteEvent`），消费侧只用 `CancellationToken.None` 正常读取。这与既有 ACP 执行器（`AcpPassthroughExecutor` 的 `EventYieldingSink` + `runTask`）是同一模式。工具执行内部 channel 改为"取消后仍排空"，保证工具 `Cancelled` 终态事件不因竞态丢失；`ExecutionJobService` 消费循环取消时不再主动丢事件；WebUI `OnCancelExecution` 状态清理原子化。

**Tech Stack:** C# / .NET 10 / xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12

---

## 背景与根因（来自系统化调查）

| 问题 | 位置 | 现状 |
|------|------|------|
| Native 取消不发布 `LoopCancelledEvent` | `src/Seeing.Agent/Core/AgentExecutor.cs` | 取消时直接抛 `OperationCanceledException`，无任何终态事件；ACP 执行器已正确发布（`AcpPassthroughExecutor.cs:153`） |
| 工具取消终态事件竞态丢失 | `AgentExecutor.cs:591` + `ExecutionJobService.cs:441-442` | 工具任务用 `CancellationToken.None` 写 `Cancelled` 终态，但消费端 `ReadAllAsync(cancellationToken)` 取消即抛，已写入事件被丢弃；JobService 消费循环取消即 `throw` |
| WebUI 的取消事件处理对 Native 是死代码 | `EventStreamHandler.cs:659`、`Session.razor:1546` | `HandleLoopCancelled` 存在，但 Native 从不发 `LoopCancelledEvent`，永不触发 |
| WebUI 取消清理脆弱 | `Session.razor:1761-1801` | 状态清理在 `ChatOrchestrator.Cancel`（同步阻塞）之后，其中途异常则清理不执行 |
| 队列推进后误清事件缓冲（竞态） | `ExecutionJobService.cs:520-528` | `CancelAsync` 已把 `_currentExecution` 换成下一项后，被取消执行的 finally 仍可能 `ClearBuffer` |
| TaskTool 子任务取消语义不一致 | `TaskTool.cs:296-301` | 子任务取消时工具终态为 `Failed` 而非 `Cancelled` |

**目标不变量（契约）：** 一次 Agent Loop = `LoopStartEvent` + 业务事件... + **恰一个终态事件**（`LoopCompleteEvent` | `LoopCancelledEvent`），且终态事件是流中最后一个事件。执行器（而非消费方）负责保证该不变量。

---

## 文件结构

| 文件 | 职责 | 动作 |
|------|------|------|
| `src/Seeing.Agent/Core/AgentExecutor.cs` | 事件流水线改造：生产者/消费者拆分、取消转 `LoopCancelledEvent`、工具排空 | 修改 |
| `src/Seeing.Agent.App/Execution/ExecutionJobService.cs` | 消费循环取消时不丢事件；`ClearBuffer` 守卫 | 修改 |
| `tests/Seeing.Agent.Tests/Core/AgentExecutorCancellationTests.cs` | Native 执行器取消终态事件单测（新建） | 新建 |
| `tests/Seeing.Agent.Tests/App/Execution/ExecutionJobServiceCancelTests.cs` | 补"取消后终态事件不被丢弃"测试 | 修改 |
| `samples/Seeing.Agent.WebUI/Pages/Session.razor` | `OnCancelExecution` 原子化 | 修改 |

---

## Task 1: 新建 AgentExecutor 取消终态事件失败测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Core/AgentExecutorCancellationTests.cs`

- [ ] **Step 1: 写测试文件（先红）**

完整代码如下。要点：
- `AgentExecutor` 用真实实例 + mock 依赖（不 mock `IAgentExecutor`，因为被测对象就是它）。
- mock `ILlmService.CompleteStreamAsync(modelId, request, sessionId, ct)`。
- 手工 `ITool`（`BlockingTool`）等取消，验证工具 `Cancelled` 终态。
- 测试 1（LLM 流式取消）、测试 2（工具执行中取消）、测试 3（正常完成回归）。

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentExecutorCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_CancelledDuringLlmStream_ShouldEmitLoopCancelledAsLastEvent()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken ct) => StreamAwaitCancellation(ct));

        var executor = CreateExecutor(llm.Object);
        using var cts = new CancellationTokenSource();

        var events = new List<IMessageEvent>();
        await foreach (var evt in EnumerateWithCancel(executor, cts))
            events.Add(evt);
        cts.Cancel();

        events.Last().Should().BeOfType<LoopCancelledEvent>();
        events.Should().NotContain(e => e is LoopCompleteEvent);
        events.Count(e => e is LoopCancelledEvent).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDuringToolExecution_ShouldEmitToolCancelledTerminalAndLoopCancelled()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
                StreamEmitToolCallThenEnd(MakeToolCall("tc1", "blocking")));

        var executor = CreateExecutor(llm.Object, tm => tm.RegisterTool(new BlockingTool()));
        using var cts = new CancellationTokenSource();

        var events = new List<IMessageEvent>();
        await foreach (var evt in EnumerateWithCancel(executor, cts))
        {
            events.Add(evt);
            if (evt is ToolCallEvent tc && tc.ToolCallId == "tc1"
                && tc.Status == ToolCallStatus.Running)
            {
                cts.Cancel();
            }
        }

        events.OfType<ToolCallEvent>().Should().ContainSingle(t =>
            t.ToolCallId == "tc1" && t.Status == ToolCallStatus.Cancelled);
        events.Last().Should().BeOfType<LoopCancelledEvent>();
        events.Should().NotContain(e => e is LoopCompleteEvent);
    }

    [Fact]
    public async Task ExecuteAsync_NoToolCalls_ShouldEmitLoopCompleteSuccessAsLastEvent()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => StreamPlainCompletion());

        var executor = CreateExecutor(llm.Object);
        using var cts = new CancellationTokenSource();

        var events = new List<IMessageEvent>();
        await foreach (var evt in EnumerateWithCancel(executor, cts))
            events.Add(evt);

        events.Last().Should().BeOfType<LoopCompleteEvent>().Which.Success.Should().BeTrue();
        events.Should().NotContain(e => e is LoopCancelledEvent);
    }

    // ==================== 工具 ====================

    private sealed class BlockingTool : ITool
    {
        public string Id => "blocking";
        public string Description => "阻塞直到取消";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new object() });

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return ToolResult.Succeeded("unreachable");
        }
    }

    // ==================== LLM 流 mock ====================

    private static async IAsyncEnumerable<StreamUpdate> StreamAwaitCancellation(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamEmitToolCallThenEnd(ToolCall toolCall)
    {
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "tool_calls",
            ToolCallDeltas = new List<ToolCall> { toolCall }
        };
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
    }

    private static ToolCall MakeToolCall(string id, string name) => new()
    {
        Id = id,
        Type = "function",
        Function = new FunctionCall { Name = name, Arguments = "{}" }
    };

    // ==================== 执行器编排 ====================

    /// <summary>枚举事件流，同时让调用方可从外部观察（本轮未取消，仅返回全部事件）。</summary>
    private static async IAsyncEnumerable<IMessageEvent> EnumerateWithCancel(
        AgentExecutor executor, CancellationTokenSource cts)
    {
        var agent = new AgentDefinition
        {
            Name = "test",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            SystemPrompt = null
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            CancellationToken = cts.Token
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, cts.Token))
            yield return evt;
    }

    private static AgentExecutor CreateExecutor(
        ILlmService llm,
        Action<ToolManager>? configureTools = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        configureTools?.Invoke(toolManager);

        var agentRegistry = new Mock<IAgentRegistry>();

        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? _, PermissionContext _, CancellationToken _) =>
                PermissionResult.Allow(default, "test-allow"));

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions());

        return new AgentExecutor(
            llm,
            toolManager,
            permission.Object,
            hookManager,
            agentRegistry.Object,
            new PromptBuilder(agentRegistry.Object, null!),
            options.Object,
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }
}
```

> 说明：`CancelledDuringLlmStream` 里取消在首次枚举 `MoveNextAsync` 前触发（`await foreach` 首次迭代后立刻 `Cancel()`），模拟"LLM 调用进行中取消"。`CancelledDuringToolExecution` 在工具进入 `Running` 后取消。

- [ ] **Step 2: 构建并运行测试，确认失败（红）**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorCancellationTests"
```
Expected: 测试 1 失败（当前取消直接抛 OCE，`events.Last()` 永不执行 / 或得到 `LoopCompleteEvent`）；测试 2 失败（无 `ToolCallStatus.Cancelled` 终态，且取不到 `LoopCancelledEvent`）；测试 3 通过。

- [ ] **Step 3: Commit**

```bash
git add tests/Seeing.Agent.Tests/Core/AgentExecutorCancellationTests.cs
git commit -m "test: 新增 AgentExecutor 取消终态事件失败测试（红）"
```

---

## Task 2: AgentExecutor 事件流水线改造（取消 → LoopCancelledEvent）

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`

核心改动（架构层）：
1. `ExecuteAsync` 改为**外层转发器**：启动后台生产者 → `ReadAllAsync(CancellationToken.None)` 转发 → `finally await producer`。
2. 主循环逻辑移入 `RunProducerAsync`（Task），所有 `yield return X` 改为 `await writer.WriteAsync(X, ...)`；取消/异常统一在 `catch` 转终态事件；`finally writer.Complete()`。
3. `BuildRequestAsync` 增加 `CancellationToken token` 参数（替换内部 `context.CancellationToken`）。
4. LLM 读取后、空响应处理前增加取消检查，避免"取消被误报为空响应错误"。

- [ ] **Step 1: 改写 `ExecuteAsync` 为转发器 + 新增 `RunProducerAsync`**

将 `src/Seeing.Agent/Core/AgentExecutor.cs` 中 `ExecuteAsync`（当前 86-494 行）整体替换为：

```csharp
public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
    Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
    IReadOnlyList<ChatMessage> messages,
    AgentContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // 检查禁用状态（保持原行为：直接产出错误，无需走事件流水线）
    if (agent.Disabled)
    {
        yield return new ErrorEvent
        {
            SessionId = context.SessionId,
            Message = $"Agent '{agent.Name}' is disabled"
        };
        yield break;
    }

    // 合并取消令牌
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        context.CancellationToken, cancellationToken);
    var effectiveToken = linkedCts.Token;

    // AgentExecutor 为 Singleton：每次执行重置 Loop 局部状态，避免跨执行泄漏
    _todoEmptyReminded = false;
    _incompleteReminded = false;
    _totalToolCallsExecuted = 0;

    // 事件流水线：生产者保证「LoopStart → 恰一个终态事件」；取消/异常在生产者侧转换为终态事件。
    // 与 ACP 执行器（EventYieldingSink + runTask）同一模式，消费侧只需正常读取。
    var outbound = Channel.CreateUnbounded<IMessageEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    var producer = RunProducerAsync(agent, messages, context, effectiveToken, outbound.Writer);

    try
    {
        await foreach (var evt in outbound.Reader.ReadAllAsync(CancellationToken.None))
            yield return evt;
    }
    finally
    {
        // 生产者内部已兜底终态事件；此处仅防御性等待，避免异常从 finally 逃逸
        try { await producer; }
        catch { _logger.LogWarning("[AgentExecutor] 生产者异常已由内部兜底，忽略"); }
    }
}

/// <summary>
/// 事件生产者：负责整条 Loop 事件流的产出与「终态不变量」。
/// 取消（OCE）→ LoopCancelledEvent；未知异常 → ErrorEvent + LoopCompleteEvent(失败)。
/// </summary>
private async Task RunProducerAsync(
    Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
    IReadOnlyList<ChatMessage> messages,
    AgentContext context,
    CancellationToken effectiveToken,
    ChannelWriter<IMessageEvent> writer)
{
    // ========== 生成 LoopId（一次完整对话循环的唯一标识）==========
    var loopId = Guid.NewGuid().ToString("N");
    var loopStartTime = DateTime.Now;
    var totalSteps = 0;
    TokenUsage? totalUsage = null;
    string? errorMessage = null;

    var maxSteps = agent.MaxSteps ?? 32;
    var history = messages.ToList();

    // Ask 全局串行：并行工具不得并发弹出多个 Ask
    var permissionChannel = context.PermissionChannel
        ?? Seeing.Agent.Core.Permission.DefaultPermissionChannel.Instance;

    // SubAgent：合并 Session PermissionSnapshot 作为本 Loop 权限真相源
    context.PermissionContext = PermissionIntegrity.FromAgentContext(
        context, ResolvePolicy(agent, context), agent.Name);

    try
    {
        // ========== 发布 LoopStart 事件 ==========
        await writer.WriteAsync(new LoopStartEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            UserInput = history.LastOrDefault()?.Content
        }, effectiveToken);

        for (var step = 0; step < maxSteps; step++)
        {
            effectiveToken.ThrowIfCancellationRequested();
            totalSteps = step + 1;

            // === 循环开始检查：上一轮遗留的 paused todo ===
            if (step == 0 && _todoStore != null && !string.IsNullOrEmpty(context.SessionId))
            {
                var todoList = await LoadTodoList(context.SessionId);
                if (todoList.HasPaused())
                {
                    var reminder = BuildTodoReminderMessage(
                        SystemReminder.Kinds.TodoPaused,
                        todoList.FormatBrief());
                    if (reminder != null)
                        history.Add(reminder);
                }
            }

            // ========== 发布 StreamStart 事件（标记新轮次开始）==========
            await writer.WriteAsync(new StreamStartEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Step = step
            }, effectiveToken);

            // === TodoEmpty 检查：已执行多步但未创建 todo ===
            if (step >= 2 && !_todoEmptyReminded
                && _todoStore != null && !string.IsNullOrEmpty(context.SessionId))
            {
                var todoList = await LoadTodoList(context.SessionId);
                if (todoList.IsEmpty())
                {
                    var reminder = BuildTodoReminderMessage(
                        SystemReminder.Kinds.TodoEmpty,
                        "请评估当前任务是否需要使用 TodoWrite 规划。");
                    if (reminder != null)
                        history.Add(reminder);
                    _todoEmptyReminded = true;
                }
            }

            // 构建请求（异步注入动态上下文；token 改用 effectiveToken）
            var request = await BuildRequestAsync(agent, history, context, effectiveToken).ConfigureAwait(false);

            // 调用 LLM（流式）
            ChatMessage? assistantMessage = null;
            var streamingContent = new StringBuilder();
            var streamingReasoning = new StringBuilder();
            List<ToolCall>? accumulatedToolCalls = null;
            TokenUsage? lastUsage = null;

            // 使用 Channel 模式捕获 LLM 流式异常
            var llmChannel = System.Threading.Channels.Channel.CreateUnbounded<StreamUpdate>();
            Exception? llmException = null;

            var llmTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var update in _llm.CompleteStreamAsync(
                        ResolveModelId(agent, context),
                        request,
                        context.SessionId,
                        effectiveToken))
                    {
                        await llmChannel.Writer.WriteAsync(update, effectiveToken).ConfigureAwait(false);
                    }
                    llmChannel.Writer.Complete();
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == effectiveToken)
                {
                    // 用户主动取消，不作为错误
                    _logger.LogInformation("[AgentExecutor] LLM 请求被取消");
                    llmChannel.Writer.Complete();
                }
                catch (LlmRetryExhaustedException ex)
                {
                    llmException = ex;
                    _logger.LogError(ex, "[AgentExecutor] LLM 重试耗尽: {Message}", ex.Message);
                    llmChannel.Writer.Complete(ex);
                }
                catch (LlmTimeoutException ex)
                {
                    llmException = ex;
                    _logger.LogError(ex, "[AgentExecutor] LLM 请求超时");
                    llmChannel.Writer.Complete(ex);
                }
                catch (LlmStreamingException ex)
                {
                    llmException = ex;
                    _logger.LogError(ex, "[AgentExecutor] LLM 流式错误: {Message}", ex.Message);
                    llmChannel.Writer.Complete(ex);
                }
                catch (LlmException ex)
                {
                    llmException = ex;
                    _logger.LogError(ex, "[AgentExecutor] LLM 错误: {Message}", ex.Message);
                    llmChannel.Writer.Complete(ex);
                }
                catch (IOException ex)
                {
                    llmException = new LlmConnectionException("网络连接错误", ex);
                    _logger.LogError(ex, "[AgentExecutor] 网络连接错误");
                    llmChannel.Writer.Complete(llmException);
                }
                catch (Exception ex)
                {
                    llmException = new LlmException($"LLM 调用失败: {ex.Message}", ex);
                    _logger.LogError(ex, "[AgentExecutor] LLM 调用失败");
                    llmChannel.Writer.Complete(llmException);
                }
            }, effectiveToken);

            // 从 channel 读取流式更新
            await foreach (var update in llmChannel.Reader.ReadAllAsync(effectiveToken))
            {
                // ========== 处理思考过程增量 ==========
                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    streamingReasoning.Append(update.ReasoningDelta);
                    await writer.WriteAsync(new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        ReasoningDelta = update.ReasoningDelta
                    }, effectiveToken);
                }

                // ========== 处理正文内容增量 ==========
                if (!string.IsNullOrEmpty(update.ContentDelta))
                {
                    streamingContent.Append(update.ContentDelta);
                    await writer.WriteAsync(new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        ContentDelta = update.ContentDelta
                    }, effectiveToken);
                }

                // 累积工具调用
                if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
                {
                    accumulatedToolCalls ??= new List<ToolCall>();
                    accumulatedToolCalls.AddRange(update.ToolCallDeltas);
                }

                // 记录 Usage
                if (update.Usage != null)
                {
                    lastUsage = update.Usage;
                    if (totalUsage == null)
                    {
                        totalUsage = new TokenUsage
                        {
                            InputTokens = update.Usage.InputTokens,
                            OutputTokens = update.Usage.OutputTokens
                        };
                    }
                    else
                    {
                        totalUsage = totalUsage with
                        {
                            InputTokens = totalUsage.InputTokens + update.Usage.InputTokens,
                            OutputTokens = totalUsage.OutputTokens + update.Usage.OutputTokens
                        };
                    }
                }

                if (update.IsComplete)
                {
                    assistantMessage = BuildAssistantMessage(
                        update,
                        streamingContent.ToString(),
                        streamingReasoning.ToString(),
                        accumulatedToolCalls);
                }
            }

            // 确保后台任务完成
            await llmTask.ConfigureAwait(false);

            // 取消优先：取消后不再把「空响应」误报为错误，交由外层 catch 发布 LoopCancelledEvent
            if (effectiveToken.IsCancellationRequested)
                throw new OperationCanceledException(effectiveToken);

            // 处理 LLM 异常
            if (llmException != null)
            {
                errorMessage = llmException.Message;

                _hooks.TriggerFireAndForget(
                    HookRegistry.ChatOnError,
                    context.SessionId,
                    new Dictionary<string, object?>
                    {
                        ["modelId"] = ResolveModelId(agent, context),
                        ["error"] = llmException,
                        ["source"] = llmException is LlmException lle ? lle.Source : "unknown"
                    });

                await writer.WriteAsync(new ErrorEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Message = llmException.Message,
                    Exception = llmException,
                    Source = "llm"
                }, CancellationToken.None);

                await writer.WriteAsync(new LoopCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    TotalSteps = totalSteps,
                    Duration = DateTime.Now - loopStartTime,
                    Success = false,
                    Error = errorMessage
                }, CancellationToken.None);
                return;
            }

            if (assistantMessage == null)
            {
                _logger.LogWarning("[AgentExecutor] LLM 返回空响应");
                errorMessage = "LLM 返回空响应";

                await writer.WriteAsync(new ErrorEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Message = "LLM 返回空响应",
                    Source = "llm"
                }, CancellationToken.None);

                await writer.WriteAsync(new LoopCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    TotalSteps = totalSteps,
                    Duration = DateTime.Now - loopStartTime,
                    Success = false,
                    Error = errorMessage
                }, CancellationToken.None);
                return;
            }

            history.Add(assistantMessage);

            // ========== 发布 StreamComplete 事件 ==========
            await writer.WriteAsync(new StreamCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = assistantMessage,
                Usage = lastUsage
            }, effectiveToken);

            // 无工具调用则检查 todo 状态
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                // === 退出检查：未完成 todo ===
                if (_todoStore != null && !string.IsNullOrEmpty(context.SessionId))
                {
                    var todoList = await LoadTodoList(context.SessionId);
                    if (todoList.HasIncompletePendingOrInProgress())
                    {
                        if (!_incompleteReminded)
                        {
                            var reminder = BuildTodoReminderMessage(
                                SystemReminder.Kinds.TodoIncomplete,
                                todoList.FormatBrief());
                            if (reminder != null)
                                history.Add(reminder);
                            _incompleteReminded = true;
                            continue; // 不退出，再给一轮
                        }
                        // 第二次仍然未完成 → 信任 agent，允许退出
                    }
                }

                // 发布 LoopComplete 事件（成功）
                await writer.WriteAsync(new LoopCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    TotalSteps = totalSteps,
                    Duration = DateTime.Now - loopStartTime,
                    Success = true,
                    Usage = totalUsage
                }, CancellationToken.None);
                return;
            }

            // 累计工具调用次数
            if (assistantMessage.ToolCalls != null)
                _totalToolCallsExecuted += assistantMessage.ToolCalls.Count;

            // ========== 执行工具调用（内部排空，保证终态送达）==========
            await ExecuteToolCallsAsync(
                assistantMessage.ToolCalls ?? new List<ToolCall>(),
                agent,
                context,
                permissionChannel,
                loopId,
                writer,
                history,
                effectiveToken);
        }

        // 达到最大步数
        errorMessage = $"达到最大步数 {maxSteps}，已停止";

        await writer.WriteAsync(new ErrorEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Message = errorMessage,
            Source = "agent"
        }, CancellationToken.None);

        await writer.WriteAsync(new LoopCompleteEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            TotalSteps = totalSteps,
            Duration = DateTime.Now - loopStartTime,
            Success = false,
            Error = errorMessage
        }, CancellationToken.None);
    }
    catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
    {
        _logger.LogInformation("[AgentExecutor] Loop 已取消，发布 LoopCancelledEvent");
        await writer.WriteAsync(new LoopCancelledEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Reason = "user",
            CompletedSteps = totalSteps,
            PartialUsage = totalUsage
        }, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "[AgentExecutor] Loop 未捕获异常，发布 Error + LoopComplete(失败)");
        await writer.WriteAsync(new ErrorEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Message = ex.Message,
            Exception = ex,
            Source = "agent"
        }, CancellationToken.None);
        await writer.WriteAsync(new LoopCompleteEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            TotalSteps = totalSteps,
            Duration = DateTime.Now - loopStartTime,
            Success = false,
            Error = ex.Message
        }, CancellationToken.None);
    }
    finally
    {
        writer.Complete();
    }
}
```

- [ ] **Step 2: 修改 `BuildRequestAsync` 签名使用 effectiveToken**

将现有（499-548 行）签名改为：

```csharp
private async Task<ChatRequest> BuildRequestAsync(
    Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
    List<ChatMessage> messages,
    AgentContext context,
    CancellationToken cancellationToken)
```

并将方法内唯一调用处（原 521 行）改为：

```csharp
systemPrompt = await _promptBuilder.BuildAsync(promptContext, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 3: 运行测试 1、3，确认绿**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorCancellationTests"
```
Expected: 测试 1（LLM 取消 → LoopCancelledEvent）通过；测试 3（正常完成）通过；测试 2（工具取消）仍红（`ExecuteToolCallsAsync` 尚未改造）。

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Core/AgentExecutor.cs
git commit -m "feat: AgentExecutor 事件流水线化，取消发布 LoopCancelledEvent"
```

---

## Task 3: 工具执行取消终态完整送达 + 取消语义统一

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`

改动要点：
1. `ExecuteToolCallsAsync` 从 `IAsyncEnumerable` 改为 `Task`，直接写 outbound writer；**消费端用 `ReadAllAsync(CancellationToken.None)` 排空**，工具终态事件（含 `Cancelled`）不再因取消竞态丢失。
2. 工具终态事件写入历史（原 457-469 行逻辑）移入排空循环。
3. 排空受 `s_toolDrainTimeout`（10s）保护：个别工具不响应取消时，不无限阻塞终态。
4. `ExecuteSingleToolCallAsync` 返回失败结果时，若已取消则优先标 `Cancelled`（统一 TaskTool 子任务取消语义）。

- [ ] **Step 1: 替换 `ExecuteToolCallsAsync` 与 `RunToolCallWithEventsAsync` 相关部分**

将现有（553-595 行）`ExecuteToolCallsAsync` 替换为：

```csharp
/// <summary>
/// 工具终态事件排空时限：防止个别不响应取消的工具无限阻塞 Loop 终态。
/// </summary>
private static readonly TimeSpan s_toolDrainTimeout = TimeSpan.FromSeconds(10);

/// <summary>
/// 执行工具调用 - 直接写入事件流。
/// 关键：消费端用 CancellationToken.None 排空，取消后工具终态事件（Cancelled/Success/Failed）完整送达；
/// 排空受 s_toolDrainTimeout 保护，避免被不响应取消的工具无限阻塞。
/// </summary>
private async Task ExecuteToolCallsAsync(
    List<ToolCall> toolCalls,
    Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
    AgentContext context,
    IPermissionChannel permissionChannel,
    string loopId,
    ChannelWriter<IMessageEvent> writer,
    List<ChatMessage> history,
    CancellationToken cancellationToken)
{
    // 先全部 Pending，再并行执行；执行中通过 Channel 交错 Running / Emit / Complete
    foreach (var tc in toolCalls)
    {
        await writer.WriteAsync(new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallPending,
            ToolCallId = tc.Id,
            ToolName = tc.Function?.Name ?? "",
            Arguments = ParseArguments(tc.Function?.Arguments),
            Status = ToolCallStatus.Pending
        }, cancellationToken);
    }

    var channel = Channel.CreateUnbounded<IMessageEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    var runTasks = toolCalls.Select(tc => RunToolCallWithEventsAsync(
        tc, agent, context, permissionChannel, loopId, channel.Writer, cancellationToken)).ToList();

    var allDone = Task.WhenAll(runTasks).ContinueWith(
        t =>
        {
            channel.Writer.TryComplete(t.Exception?.InnerException ?? t.Exception);
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    // 排空转发：把每个工具终态事件写入 outbound，并同步追加 tool 历史。
    var forward = ForwardToolEventsAsync(channel.Reader, writer, history, cancellationToken);

    var drain = Task.WhenAll(forward, allDone);
    var winner = await Task.WhenAny(drain, Task.Delay(s_toolDrainTimeout, CancellationToken.None));
    if (!ReferenceEquals(winner, drain))
    {
        _logger.LogWarning(
            "[AgentExecutor] 工具终态事件排空超时({TimeoutSeconds}s)，跳过剩余工具事件",
            s_toolDrainTimeout.TotalSeconds);
        channel.Writer.TryComplete();
    }

    // RunToolCallWithEventsAsync 内部已捕获 OCE/异常，allDone 不会 fault
    await allDone.ConfigureAwait(false);
}

/// <summary>
/// 转发工具事件并同步追加 tool 历史（所有终态都必须有对应的 tool 消息，否则下一轮 LLM 报 400）。
/// </summary>
private static async Task ForwardToolEventsAsync(
    ChannelReader<IMessageEvent> reader,
    ChannelWriter<IMessageEvent> outbound,
    List<ChatMessage> history,
    CancellationToken cancellationToken)
{
    await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
    {
        await outbound.WriteAsync(evt, CancellationToken.None);
        if (evt is ToolCallEvent tcEvent &&
            tcEvent.Status is ToolCallStatus.Success or ToolCallStatus.Failed
                or ToolCallStatus.Rejected or ToolCallStatus.Cancelled)
        {
            history.Add(new ChatMessage
            {
                Role = ChatRole.Tool,
                ToolCallId = tcEvent.ToolCallId,
                Content = tcEvent.Output ?? tcEvent.Error ?? string.Empty
            });
        }
    }
    _ = cancellationToken; // 保留签名占位：转发本身不因取消中断
}
```

- [ ] **Step 2: `ExecuteSingleToolCallAsync` 失败时取消优先**

在现有（721-751 行）的 `catch (Exception ex)` 之后、返回 Success/Failed 之前，把 `result.Success == false` 分支改为先判断取消：

```csharp
if (!result.Success)
{
    // 取消优先于失败：工具内部吞掉 OCE 返回 Failure（如 TaskTool 子任务取消）时，
    // 只要本 Loop 已请求取消，终态就应标记为 Cancelled，避免 UI 显示为 Failed。
    if (cancellationToken.IsCancellationRequested)
    {
        return new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = arguments,
            Status = ToolCallStatus.Cancelled,
            Error = result.Error ?? "已取消",
            Duration = DateTime.Now - startTime
        };
    }

    return new ToolCallEvent
    {
        SessionId = context.SessionId,
        LoopId = loopId,
        Type = MessageEventType.ToolCallComplete,
        ToolCallId = tc.Id,
        ToolName = name,
        Arguments = arguments,
        Status = ToolCallStatus.Failed,
        Output = result.Output,
        Error = result.Error,
        Duration = DateTime.Now - startTime
    };
}

return new ToolCallEvent
{
    SessionId = context.SessionId,
    LoopId = loopId,
    Type = MessageEventType.ToolCallComplete,
    ToolCallId = tc.Id,
    ToolName = name,
    Arguments = arguments,
    Status = ToolCallStatus.Success,
    Output = result.Output,
    Error = result.Error,
    Duration = DateTime.Now - startTime
};
```

> 注：原代码是单个三元返回 `result.Success ? Success : Failed`，请替换为上述三分支。`RunToolCallWithEventsAsync`（原 597-640 行）保持不变——它已用 `CancellationToken.None` 写终态，本次只把消费侧改为排空即可。

- [ ] **Step 3: 运行全部取消测试，确认绿**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorCancellationTests"
```
Expected: 测试 1、2、3 全部通过。

- [ ] **Step 4: 运行 AgentExecutor 相关既有测试确认无回归**

Run:
```bash
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentModeFilterTests|FullyQualifiedName~ToolInvokerTests|FullyQualifiedName~ExecutionJobServiceCancelTests"
```
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Core/AgentExecutor.cs
git commit -m "fix: 工具执行取消终态完整送达，取消优先于失败"
```

---

## Task 4: ExecutionJobService 取消不丢事件

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- Modify: `tests/Seeing.Agent.Tests/App/Execution/ExecutionJobServiceCancelTests.cs`

改动要点：消费循环移除"取消即 throw"（原 441-442 行），让执行器产出的终态事件（含工具 `Cancelled`、`LoopCancelledEvent`）在取消后仍被处理与发布；`catch (OperationCanceledException)` 保留作为执行器未转事件时的兜底。

- [ ] **Step 1: 写失败测试（先红）**

在 `ExecutionJobServiceCancelTests.cs` 追加：

```csharp
[Fact]
public async Task Cancel_WhenExecutorYieldsTerminalEventsAfterCancel_ShouldForwardThem()
{
    var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
    var publisher = new Mock<IExecutionEventPublisher>();
    publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
        .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
    publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
    publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

    var session = SessionData.Create();
    var executor = new Mock<IAgentExecutor>();
    executor.Setup(e => e.ExecuteAsync(
            It.IsAny<AgentDefinition>(),
            It.IsAny<IReadOnlyList<ChatMessage>>(),
            It.IsAny<AgentContext>(),
            It.IsAny<CancellationToken>()))
        .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct)
            => YieldTerminalThenThrow(ct));

    using var fixture = CreateFixture(session, publisher.Object, executor.Object);
    var service = fixture.Service;

    var result = await service.SubmitAsync(
        session.Id,
        ChatInput.FromText("hi"),
        new ChatOptions
        {
            AgentId = "general",
            SkipUserMessagePersist = true,
            SkipInstructionInject = true
        });
    result.Success.Should().BeTrue();
    var executionId = result.ExecutionId;

    await WaitUntilAsync(() =>
        service.GetOverview(session.Id).CurrentExecution?.Status == ExecutionStatus.Running);

    service.Cancel(executionId);

    await WaitUntilAsync(() =>
        published.Any(e => e.Event is LoopCancelledEvent));

    published.Should().Contain(e => e.Event is ToolCallEvent
        { Status: ToolCallStatus.Cancelled });
    published.Should().Contain(e => e.Event is LoopCancelledEvent);
    published.Count(e => e.Event is ExecutionCompleteEvent c && c.ExecutionId == executionId)
        .Should().Be(1);
}

/// <summary>取消后先产出终态事件（工具 Cancelled + LoopCancelled），再抛 OCE 结束。</summary>
private static async IAsyncEnumerable<IMessageEvent> YieldTerminalThenThrow(CancellationToken ct)
{
    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ct.Register(() => resume.TrySetResult());
    await resume.Task;

    yield return new ToolCallEvent
    {
        SessionId = "s1",
        LoopId = "loop-1",
        Type = MessageEventType.ToolCallComplete,
        ToolCallId = "tc1",
        ToolName = "blocking",
        Status = ToolCallStatus.Cancelled,
        Error = "已取消"
    };
    yield return new LoopCancelledEvent
    {
        SessionId = "s1",
        LoopId = "loop-1",
        Reason = "user"
    };
    throw new OperationCanceledException(ct);
}
```

Run（应失败——当前 `ProcessExecutionAsync` 在取消后第一次迭代即 `throw`，两个终态事件被丢弃，`published` 不含 `LoopCancelledEvent`）：
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionJobServiceCancelTests"
```

- [ ] **Step 2: 移除消费循环的取消即 throw**

将 `ExecutionJobService.cs` 434-454 行的执行循环改为：

```csharp
// Execute agent
// 取消不再主动 throw：执行器（事件流水线）会产出终态事件（工具 Cancelled / LoopCancelledEvent），
// 这些事件必须在取消后仍被处理与发布。OCE 由下方 catch 兜底（执行器未转换为事件的极端情况）。
await foreach (var evt in executionRouter.ExecuteAsync(
    context.Agent,
    messages,
    BuildAgentContext(context, queue.CurrentCancellationToken),
    queue.CurrentCancellationToken))
{
    var liveSession = sessionManager.Get(record.SessionId) ?? session;
    eventTracker.ApplyEvent(liveSession, evt);
    TaskSessionProjector.Apply(liveSession, evt);

    // 先投影再发布，保证 UI 读到的是已写入的 SessionData
    _eventPublisher.Publish(record.SessionId, evt);

    if (ShouldPersistEvent(evt))
        await sessionManager.SaveAsync(record.SessionId);
}
```

（即：删除原 441-442 行的 `if (queue.CurrentCancellationToken.IsCancellationRequested) throw new OperationCanceledException();`。）

- [ ] **Step 3: 运行测试，确认绿**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionJobServiceCancelTests"
```
Expected: 新旧两条取消测试全部通过。

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent.App/Execution/ExecutionJobService.cs tests/Seeing.Agent.Tests/App/Execution/ExecutionJobServiceCancelTests.cs
git commit -m "fix: ExecutionJobService 取消后继续消费执行器终态事件，不再丢弃"
```

---

## Task 5: 队列推进后不再误清事件缓冲（竞态守卫）

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`

背景：`SessionExecutionQueue.CancelAsync` 取消当前项时会立即把 `_currentExecution` 换成队列下一项。被取消执行的 finally 里 `queue.CompleteAsync(取消项Id, ...)` 因 current 已是下一项而返回 `null`，触发 `_eventPublisher.ClearBuffer`，可能清掉下一项已发布的事件。

- [ ] **Step 1: 修改 finally 中的缓冲清理守卫**

将 `ExecutionJobService.cs` 519-528 行的：

```csharp
// Process next in queue（仅由此处启动，避免与 ProcessQueueAsync while 竞态双跑）
if (nextExecution != null)
{
    _ = ProcessExecutionAsync(nextExecution);
}
else
{
    // 队列为空才清理事件缓冲，避免清掉下一项已发布的事件
    _eventPublisher.ClearBuffer(record.SessionId);
}
```

改为：

```csharp
// Process next in queue（仅由此处启动，避免与 ProcessQueueAsync while 竞态双跑）
if (nextExecution != null)
{
    _ = ProcessExecutionAsync(nextExecution);
}
else if (queue.CurrentExecution == null && !queue.HasQueued)
{
    // 仅当队列完全空闲才清理事件缓冲：
    // CancelAsync 已把 current 推进到下一项时，CompleteAsync 会返回 null，
    // 此时不得清缓冲，否则会丢下一项已发布的事件。
    _eventPublisher.ClearBuffer(record.SessionId);
}
```

- [ ] **Step 2: 构建 + 运行 ExecutionJobService 相关测试**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionJobServiceCancelTests|FullyQualifiedName~ExecutionJobServiceCommandShortCircuitTests|FullyQualifiedName~ExecutionJobServiceCompactionTests"
```
Expected: 全部通过。

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent.App/Execution/ExecutionJobService.cs
git commit -m "fix: 队列推进后不误清事件缓冲"
```

---

## Task 6: WebUI OnCancelExecution 原子化

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`

改动要点：状态清理不再依赖后续可能抛异常的调用；`ChatOrchestrator.Cancel` 等调用全部 try/catch，确保 UI 无论如何落到终态。

- [ ] **Step 1: 重写 `OnCancelExecution`**

将现有 1761-1801 行的 `OnCancelExecution` 替换为：

```csharp
/// <summary>
/// 取消执行处理（使用新的 Cancel API）
/// <para>
/// 原子化：先立即落本地终态（不依赖后续可能抛异常的调用），
/// 后端取消与任务标记为尽力而为，异常不影响 UI 状态。
/// </para>
/// </summary>
private async Task OnCancelExecution()
{
    // 1) 立即清本地执行态（原子、无 await）
    var executionId = SessionState.CurrentExecutionId;
    var sessionId = SessionState.SessionId;
    SessionState.CancelExecution();
    SessionState.ExecutionStatus = ExecutionStatus.Cancelled;
    SessionState.CurrentExecutionId = null;

    // 2) 后端取消（尽力而为）
    if (!string.IsNullOrEmpty(sessionId))
    {
        try
        {
            await BackgroundTaskManager.CancelBySessionAsync(sessionId);
        }
        catch
        {
            // ignore
        }
    }

    try
    {
        await EventStreamHandler.MarkIncompleteTasksCancelledAsync("用户取消");
    }
    catch
    {
        // ignore
    }

    if (!string.IsNullOrEmpty(executionId))
    {
        try
        {
            ChatOrchestrator.Cancel(executionId);
        }
        catch
        {
            // ignore
        }
    }

    // 3) 收尾（无论后端结果如何都要执行）
    SessionState.CompleteExecution();

    if (SessionState.CurrentSession != null)
    {
        SessionState.CurrentSession.AddMessage(SessionMessage.SystemMessage("用户取消了当前执行"));
    }

    SyncTimelineAssistantFromStream(isComplete: true);
    CompleteCurrentTimelineTurn();
    ReconcileTimelineFromSession();
    await InvokeAsync(StateHasChanged);
}
```

- [ ] **Step 2: 构建 WebUI 验证编译**

Run:
```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 编译通过。

- [ ] **Step 3: 手动验证**

运行 WebUI，在 Agent 执行（含工具调用）过程中点击停止按钮，确认：
- 顶部/输入区"执行中"徽标立即消失；
- 时间线当前 Loop 的流式指示结束（不再是 streaming）；
- 工具卡片状态变为"已取消"；
- 会话追加一条"用户取消了当前执行"系统消息。

Run:
```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

- [ ] **Step 4: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Session.razor
git commit -m "fix: WebUI 取消执行状态清理原子化，杜绝卡在执行中"
```

---

## Task 7: 全量验证

- [ ] **Step 1: 构建整个解决方案**

Run:
```bash
dotnet build Seeing.Agent.slnx
```
Expected: 无编译错误。

- [ ] **Step 2: 运行关键测试程序集**

Run:
```bash
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorCancellationTests|FullyQualifiedName~ExecutionJobServiceCancelTests|FullyQualifiedName~ToolInvokerTests|FullyQualifiedName~AgentModeFilterTests"
```
Expected: 全部通过。

- [ ] **Step 3: 运行 Seeing.Agent.Acp.Tests 确认 ACP 路径无回归**

Run:
```bash
dotnet build tests/Seeing.Agent.Acp.Tests
dotnet vstest tests/Seeing.Agent.Acp.Tests/bin/Debug/net10.0/Seeing.Agent.Acp.Tests.dll
```
Expected: 全部通过。

- [ ] **Step 4: 手动端到端验证**

在 WebUI 中验证三个场景：
1. 无工具调用时点取消 → Loop 结束、状态落终态；
2. 工具执行中（长任务）点取消 → 工具卡片变"已取消"、Loop 结束、无 Failed 红叉误标；
3. 正常完成 → 行为与修复前一致（LoopComplete）。

- [ ] **Step 5: Commit（若有遗留）**

```bash
git status
```

---

## 自审记录（Self-Review）

**Spec coverage：**
- ✅ Native 取消发布 `LoopCancelledEvent` → Task 2
- ✅ 工具取消终态完整送达 → Task 3
- ✅ TaskTool 子任务取消语义（Failed→Cancelled）→ Task 3 Step 2
- ✅ WebUI 关注取消事件（死代码激活）→ Task 2 产生事件 + Task 6 清理
- ✅ JobService 取消不丢事件 → Task 4
- ✅ 队列缓冲误清竞态 → Task 5

**类型一致性：**
- `BuildRequestAsync` 新参数名 `cancellationToken` 在 Task 2 与后续引用一致；调用处仅 `RunProducerAsync` 一处。
- `ExecuteToolCallsAsync` 新签名（`writer`、`history`、`cancellationToken`）在 Task 2 与 Task 3 中一致。
- `s_toolDrainTimeout` 在 Task 3 定义，仅在 Task 3 内使用。

**已知边界（记录不修）：**
- 工具完全忽略取消（同步死循环）时，排空最长等待 10s 后强制进入终态；后台工具任务仍会泄漏（与修复前行为一致）。
- `LoopCancelledEvent.Reason` 当前统一为 `"user"`；后续如需区分 `timeout`/`resource_limit`，在 `RunProducerAsync` 的 catch 处扩展即可（`Reason` 字段已预留）。
- 若取消时 `ConsumeEventStreamAsync` 已因其它原因退出，执行器产出的终态事件无人消费属正常流式语义（消费者停止拉取即终止）。
