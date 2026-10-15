# LLM 流式轮次重试 + 运行时错误不落消息 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让流式 LLM 请求在连接中断时可整轮重试（可回滚、UI 可见），并让运行时错误/取消不再污染会话消息与 LLM 历史。

**Architecture:** 分层——传输层保持"未产出前才重试"；新增应用层 `ILlmTurnRetryPolicy` 在 `AgentExecutor` 单轮做无限重试（仅用户取消/不可重试错误终止）并回滚该轮产出；以 `StreamStartEvent.Attempt` 定义幂等重置契约；新增 `LlmRetryScheduledEvent` 驱动 UI "X 秒后重试"状态；错误/取消只走事件与执行记录。

**Tech Stack:** .NET 10 / C#、xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12、Blazor Server (WebUI)。

**Spec:** `docs/superpowers/specs/2026-09-21-llm-stream-turn-retry-and-error-persistence-design.md`（Q1–Q4 已决）

---

## 并行执行编排

本计划按**轨道**组织，Wave 1 三 Agent 并行、Wave 2 串行收尾：

| Wave | Agent | 任务 | 文件集（互不冲突） |
|------|-------|------|-------------------|
| 1 | A | A1, A2, A3 | `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs`、`ExecutionJobService.cs`、tests |
| 1 | B | B1, B2, B3, B4 | `src/abstractions/.../Events/MessageEventTypes.cs`、`Llm/ILlmTurnRetryPolicy.cs`、`Llm/LlmTurnRetryOptions.cs`、`src/spine/.../Llm/DefaultLlmTurnRetryPolicy.cs`、tests |
| 1 | C | C1, C2, C3 | `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`、`Components/SessionWindow.razor`、`src/spine/.../Llm/RetryLlmClientDecorator.cs`、tests |
| 2 | D | D1, D2, D3, D4, D5 | `src/spine/.../Extensions/ServiceCollectionExtensions.cs`、`Core/AgentExecutor.cs`、`samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`、`Components/SessionWindow.razor`、tests |

**冲突约束：**
- Agent A 独占 `ChatEventTracker.cs`（Wave 2 的 D3 只改 `EventStreamHandler.cs`，不冲突）。
- Agent C 的 C1 与 Wave 2 的 D3 都改 `EventStreamHandler.cs` → **必须顺序**：先 C1（Wave 1），D3 在 Wave 2 基于 C1 结果继续。
- `MessageEventTypes.cs` 由 Agent B 独占。

**测试命令基线（AGENTS.md）：**
```pwsh
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
```

---

## 文件结构

**新建**
- `src/abstractions/Seeing.Agent.Abstractions/Llm/ILlmTurnRetryPolicy.cs` — 应用层轮次重试策略契约
- `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmTurnRetryOptions.cs` — 策略配置
- `src/spine/Seeing.Agent.Core/Llm/DefaultLlmTurnRetryPolicy.cs` — 默认实现
- `tests/spine/Seeing.Agent.Tests/Llm/LlmTurnRetryPolicyTests.cs` — 策略单测
- `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorTurnRetryTests.cs` — 轮次重试单测
- `tests/spine/Seeing.Agent.Tests/App/Execution/ChatEventTrackerStreamResetTests.cs` — 投影重置单测

**修改**
- `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs` — 重置 + 不落消息
- `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs` — 历史过滤（`BuildHistoryFromSession`）
- `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs` — `StreamStartEvent.Attempt`、`LlmRetryScheduledEvent`
- `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs` — 注册策略
- `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs` — attempt 循环 + 事件产出
- `src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs` — 边界注释 + 日志
- `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` — StreamStart 重置 + 重试态 + 错误展示态
- `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor` — 移除本地取消 SystemMessage + 重试/错误提示条

---

## Wave 1 · Agent A（Hosting 投影与历史）

### Task A1: `ChatEventTracker` — `StreamStart` 幂等重置

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs:44-53`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/ChatEventTrackerStreamResetTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class ChatEventTrackerStreamResetTests
{
    [Fact]
    public void StreamStart_RepeatedForSameStep_RemovesPreviousPartialAssistantMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "partial" });

        session.Messages.Where(m => m.Role == MessageRole.Assistant).Should().HaveCount(1);

        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "retry" });

        var assistants = session.Messages.Where(m => m.Role == MessageRole.Assistant).ToList();
        assistants.Should().HaveCount(1);
        assistants[0].Content.Should().Be("retry");
    }

    [Fact]
    public void StreamStart_NextStep_KeepsPreviousCompletedAssistantMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "first" });
        tracker.ApplyEvent(session, new StreamCompleteEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Message = new ChatMessage { Role = "assistant", Content = "first" }
        });

        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 1 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "second" });

        var assistants = session.Messages.Where(m => m.Role == MessageRole.Assistant).ToList();
        assistants.Should().HaveCount(2);
        assistants[0].Content.Should().Be("first");
        assistants[1].Content.Should().Be("second");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChatEventTrackerStreamResetTests"`
Expected: FAIL（当前会产生 2 条 assistant 消息）

- [ ] **Step 3: 实现重置**

替换 `ChatEventTracker.cs:44-53` 的 `case StreamStartEvent streamStart:` 分支：

```csharp
            case StreamStartEvent streamStart:
                // 同一 (LoopId, Step) 重发 = 该轮重置：仅移除同一轮的未完成 assistant 消息，避免误删上一步已完成消息
                RemovePartialAssistantForTurn(session, streamStart.LoopId, streamStart.Step);
                if (!string.IsNullOrEmpty(streamStart.LoopId))
                    _currentLoopId = streamStart.LoopId;
                _currentStep = streamStart.Step;
                _currentAssistantMessage = null;
                break;
```

在类中新增：

```csharp
    /// <summary>
    /// 若当前累积的 assistant 消息属于同一轮（LoopId+Step），移除之（重试重置）。
    /// 跨步（step 递增）时保留上一步已完成的消息。
    /// </summary>
    private void RemovePartialAssistantForTurn(SessionData session, string? loopId, int step)
    {
        if (_currentAssistantMessage != null)
        {
            var sameStep = _currentAssistantMessage.Step == step;
            var sameLoop = string.IsNullOrEmpty(loopId)
                || string.IsNullOrEmpty(_currentAssistantMessage.LoopId)
                || string.Equals(_currentAssistantMessage.LoopId, loopId, StringComparison.Ordinal);
            if (sameStep && sameLoop)
            {
                session.RemoveMessage(_currentAssistantMessage);
                _currentAssistantMessage = null;
            }
            return;
        }

        // 仅在有明确 LoopId 时按 Id 兜底移除，避免 null LoopId 误删历史轮次
        if (string.IsNullOrEmpty(loopId))
            return;
        var expectedId = string.Format("{0}_step{1}", loopId, step);
        session.RemoveMessages(m => m.Id == expectedId);
    }
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChatEventTrackerStreamResetTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```pwsh
git add src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs tests/spine/Seeing.Agent.Tests/App/Execution/ChatEventTrackerStreamResetTests.cs
git commit -m "fix(hosting): reset partial assistant message on StreamStart re-emit"
```

### Task A2: `ChatEventTracker` — 错误/取消不落 `Messages`

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs:147-153`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/ChatEventTrackerStreamResetTests.cs`（追加测试）

- [ ] **Step 1: 写失败测试**（追加到 `ChatEventTrackerStreamResetTests`）

```csharp
    [Fact]
    public void ErrorEvent_DoesNotAddMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new ErrorEvent { SessionId = session.Id, Message = "网络连接错误" });

        session.Messages.Should().BeEmpty();
    }

    [Fact]
    public void LoopCancelledEvent_DoesNotAddMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopCancelledEvent { SessionId = session.Id, LoopId = "L1", Reason = "user" });

        session.Messages.Should().BeEmpty();
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChatEventTrackerStreamResetTests"`
Expected: FAIL（当前会各产生 1 条 system 消息）

- [ ] **Step 3: 实现（改为不落消息）**

将 `ChatEventTracker.cs:147-153` 两个分支替换为：

```csharp
            case ErrorEvent:
                // 运行时观测，不进对话消息/LLM 历史；由 ErrorEvent 事件驱动 UI 与执行记录
                break;

            case LoopCancelledEvent:
                // 同上：取消态由 LoopCancelledEvent + ExecutionStatus.Cancelled 表达
                break;
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChatEventTrackerStreamResetTests"`
Expected: PASS（3 个测试）

- [ ] **Step 5: Commit**

```pwsh
git add src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs tests/spine/Seeing.Agent.Tests/App/Execution/ChatEventTrackerStreamResetTests.cs
git commit -m "fix(hosting): stop persisting runtime error/cancel as session messages"
```

### Task A3: `BuildHistoryFromSession` — 存量运行时消息过滤

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs:1089-1092`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/BuildHistoryFromSessionTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class BuildHistoryFromSessionTests
{
    [Theory]
    [InlineData("错误: 网络连接错误")]
    [InlineData("对话已取消: user")]
    public void BuildHistory_SkipsLegacyRuntimeSystemMessages(string content)
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.SystemMessage(content));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Content.Should().Be("hi");
    }

    [Fact]
    public void BuildHistory_SkipsTransientFlaggedSystemMessage()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var transient = SessionMessage.SystemMessage("anything");
        transient.Metadata = new Dictionary<string, object> { ["transient"] = true };
        session.AddMessage(transient);

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
    }

    [Fact]
    public void BuildHistory_KeepsNormalSystemReminder()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.SystemMessage("<system-reminder>todo</system-reminder>"));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(2);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~BuildHistoryFromSessionTests"`
Expected: FAIL（前两个用例）

- [ ] **Step 3: 实现过滤**

在 `BuildHistoryFromSession`（`ExecutionJobService.cs:1089`）循环体开头**仅**追加以下判定（`IsSchemaSnapshotCarrierMessage` 判定 `:1091-1092` 已存在，勿重复添加）：

```csharp
            if (IsRuntimeNotificationMessage(msg))
                continue;
```

并在 `IsSchemaSnapshotCarrierMessage` 附近新增：

```csharp
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
            || msg.Content.StartsWith("对话已取消: ", StringComparison.Ordinal);
    }
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~BuildHistoryFromSessionTests"`
Expected: PASS（4 个用例）

- [ ] **Step 5: Commit**

```pwsh
git add src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs tests/spine/Seeing.Agent.Tests/App/Execution/BuildHistoryFromSessionTests.cs
git commit -m "fix(hosting): exclude runtime notifications from LLM history"
```

### Task A4: 运行时错误真相源迁移（修复 task_status 回归）

**背景**：A2 移除 `SystemMessage` 后，`TaskStatusTool.FallbackFromSessionAsync` 失去错误文本来源（回归：`TaskStatusToolTests.ExecuteAsync_FailedWithoutActiveExecution_ShouldReturnErrorState` 期望输出含错误文本）。按 Spec「执行记录/会话状态为真相源」，把错误文本落到 `SessionData.Metadata`（不进 `Messages`/LLM 历史）。

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs`（新增 `LastError`）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`（`MarkSessionError` 增参写入、`ClearSessionError` 清除）
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskStatusTool.cs`（回落优先读 `Metadata[SessionMetadataKeys.LastError]`，保留存量 `"错误:"` 前缀兜底）

- [ ] **Step 1: 先跑回归确认失败**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecuteAsync_FailedWithoutActiveExecution_ShouldReturnErrorState"`
Expected: FAIL（输出缺错误文本）

- [ ] **Step 2: 实现**

`SessionMetadataKeys.cs` 追加：

```csharp
    /// <summary>
    /// 子会话最近一次执行失败的错误信息（ExecutionJobService 在 MarkSessionError 时写入；
    /// 仅作运行时错误真相源，不进入 Messages/LLM 历史）。成功续跑时清除。
    /// </summary>
    public const string LastError = "last_error";
```

`MarkSessionError` 增加 `string? errorMessage` 参数并在 `failedSession.Metadata[SessionMetadataKeys.LastError] = errorMessage;` 写入；`ClearSessionError` 中 `session.Metadata.Remove(SessionMetadataKeys.LastError);`；两处调用改传 `record.ErrorMessage`。

`TaskStatusTool.FallbackFromSessionAsync` 的 `state == "error"` 分支：

```csharp
            string? errMsg = null;
            if (session.Metadata?.TryGetValue(SessionMetadataKeys.LastError, out var storedErr) == true)
                errMsg = storedErr as string;
            errMsg ??= activeMessages
                .LastOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
                                    && m.Content?.Contains("错误:", StringComparison.OrdinalIgnoreCase) == true)
                ?.Content;
```

- [ ] **Step 3: 运行确认通过**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskStatusToolTests"`
Expected: PASS

- [ ] **Step 4: 全量回归**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
Expected: 全绿（1051）

- [ ] **Step 5: Commit**

```pwsh
git add src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs src/hosting/Seeing.Agent.Hosting/Tools/TaskStatusTool.cs
git commit -m "fix(hosting): persist subagent error in session metadata for task_status"
```

---

## Wave 1 · Agent B（Abstractions 契约与 Core 策略）

### Task B1: `StreamStartEvent.Attempt`

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs:199-208`

- [ ] **Step 1: 加字段与契约注释**

```csharp
public record StreamStartEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.StreamStart;

    /// <summary>轮次索引（step=0, 1, 2...）</summary>
    public int Step { get; init; }

    /// <summary>
    /// 该轮第几次尝试（从 1 开始）。
    /// <para>契约：同一 (LoopId, Step) 多次发出本事件表示该轮被重开，消费端必须视为“重置”——
    /// 投影层移除该轮未完成 assistant 消息，UI 清空该轮渲染缓冲。</para>
    /// </summary>
    public int Attempt { get; init; } = 1;
}
```

- [ ] **Step 2: 构建**

Run: `dotnet build src/spine/Seeing.Agent.Core`
Expected: 成功（新增可选字段不破坏现有构造点）

- [ ] **Step 3: Commit**

```pwsh
git add src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs
git commit -m "feat(abstractions): add StreamStartEvent.Attempt reset contract"
```

### Task B2: `LlmRetryScheduledEvent`

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`

- [ ] **Step 1: 加常量与事件**

在 `MessageEventType` 内追加常量：

```csharp
    /// <summary>应用层轮次重试已排期（UI 展示“X 秒后重试”）</summary>
    public const string LlmRetry = "llm.retry";
```

在 `ErrorEvent` 定义之后追加 record：

```csharp
/// <summary>
/// LLM 轮次重试排期事件 - 应用层决定重试、进入退避等待前发出（不落盘、不进对话历史）。
/// </summary>
public record LlmRetryScheduledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LlmRetry;

    /// <summary>轮次索引</summary>
    public int Step { get; init; }

    /// <summary>即将进行的第几次尝试（2、3…）</summary>
    public int Attempt { get; init; }

    /// <summary>本次退避等待时长（毫秒），供 UI 倒计时</summary>
    public int NextDelayMs { get; init; }

    /// <summary>失败简述</summary>
    public string? Reason { get; init; }
}
```

- [ ] **Step 2: 构建**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions`
Expected: 成功

- [ ] **Step 3: Commit**

```pwsh
git add src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs
git commit -m "feat(abstractions): add LlmRetryScheduledEvent"
```

### Task B3: `ILlmTurnRetryPolicy` + `LlmTurnRetryOptions`

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Llm/ILlmTurnRetryPolicy.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmTurnRetryOptions.cs`

- [ ] **Step 1: 创建契约与选项**

`ILlmTurnRetryPolicy.cs`：

```csharp
namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 应用层轮次重试策略：决定单次 LLM 调用失败后是否整轮重开（可回滚），与传输层重试分层。
/// </summary>
public interface ILlmTurnRetryPolicy
{
    /// <summary>是否启用轮次重试。</summary>
    bool Enabled { get; }

    /// <summary>异常是否可做轮次重试（与传输层同一 IsRetryable 语义）。</summary>
    bool CanRetry(Exception ex, CancellationToken cancellationToken);

    /// <summary>第 attempt 次失败后的退避延迟（attempt 从 1 开始，指数封顶）。</summary>
    TimeSpan NextDelay(int attempt);

    /// <summary>
    /// 是否继续重试。默认不受次数/时长上限约束（仅 Enabled 约束），
    /// 可选上限字段用于运维收紧。
    /// </summary>
    bool ShouldRetry(int attempt, TimeSpan elapsed, TimeSpan nextDelay);
}
```

`LlmTurnRetryOptions.cs`：

```csharp
namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 应用层轮次重试配置。Q1 决策：默认无限重试，仅用户取消/不可重试错误终止。
/// </summary>
public sealed class LlmTurnRetryOptions
{
    /// <summary>是否启用（默认启用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>退避基数（毫秒），默认 500。</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>单次退避上限（毫秒），默认 10000。</summary>
    public int MaxDelayMs { get; set; } = 10_000;

    /// <summary>最大尝试次数（含首次）；0 = 不限（默认）。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>累计等待预算（毫秒）；0 = 不限（默认）。</summary>
    public int TotalBudgetMs { get; set; }

    public static LlmTurnRetryOptions Default => new();
}
```

- [ ] **Step 2: 构建**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions`
Expected: 成功

- [ ] **Step 3: Commit**

```pwsh
git add src/abstractions/Seeing.Agent.Abstractions/Llm/ILlmTurnRetryPolicy.cs src/abstractions/Seeing.Agent.Abstractions/Llm/LlmTurnRetryOptions.cs
git commit -m "feat(abstractions): add ILlmTurnRetryPolicy and options"
```

### Task B4: `DefaultLlmTurnRetryPolicy` + 单测

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Llm/DefaultLlmTurnRetryPolicy.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmTurnRetryPolicyTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using System.Net;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class LlmTurnRetryPolicyTests
{
    private static DefaultLlmTurnRetryPolicy Create(
        int maxAttempts = 0, int totalBudgetMs = 0, int baseDelayMs = 500, int maxDelayMs = 10_000, bool enabled = true)
        => new(new LlmTurnRetryOptions
        {
            Enabled = enabled,
            MaxAttempts = maxAttempts,
            TotalBudgetMs = totalBudgetMs,
            BaseDelayMs = baseDelayMs,
            MaxDelayMs = maxDelayMs
        });

    [Fact]
    public void CanRetry_Http4xx_ReturnsFalse()
        => Create().CanRetry(new HttpRequestException("400", null, HttpStatusCode.BadRequest), default)
            .Should().BeFalse();

    [Fact]
    public void CanRetry_IOException_ReturnsTrue()
        => Create().CanRetry(new IOException("broken"), default).Should().BeTrue();

    [Fact]
    public void CanRetry_LlmStreamingExceptionWithInnerIOException_ReturnsTrue()
        => Create().CanRetry(
                new LlmStreamingException("流式响应读取失败", new IOException("remote closed")), default)
            .Should().BeTrue();

    [Fact]
    public void CanRetry_LlmRetryExhausted_ReturnsFalse()
        => Create().CanRetry(
                new LlmRetryExhaustedException(3, new IOException("x")), default)
            .Should().BeFalse();

    [Fact]
    public void NextDelay_GrowsExponentiallyAndCaps()
    {
        var p = Create(baseDelayMs: 500, maxDelayMs: 2000);
        p.NextDelay(1).TotalMilliseconds.Should().Be(500);
        p.NextDelay(2).TotalMilliseconds.Should().Be(1000);
        p.NextDelay(3).TotalMilliseconds.Should().Be(2000);
        p.NextDelay(4).TotalMilliseconds.Should().Be(2000);
    }

    [Fact]
    public void ShouldRetry_NoLimits_AlwaysTrue()
        => Create().ShouldRetry(attempt: 100, TimeSpan.Zero, TimeSpan.FromSeconds(10)).Should().BeTrue();

    [Fact]
    public void ShouldRetry_MaxAttemptsCaps()
        => Create(maxAttempts: 3).ShouldRetry(attempt: 3, TimeSpan.Zero, TimeSpan.FromSeconds(1)).Should().BeFalse();

    [Fact]
    public void ShouldRetry_BudgetCaps()
        => Create(totalBudgetMs: 5_000)
            .ShouldRetry(attempt: 1, TimeSpan.FromMilliseconds(4_500), TimeSpan.FromSeconds(1)).Should().BeFalse();

    [Fact]
    public void ShouldRetry_Disabled_ReturnsFalse()
        => Create(enabled: false).ShouldRetry(attempt: 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)).Should().BeFalse();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests`
Expected: 编译失败（`DefaultLlmTurnRetryPolicy` 不存在）

- [ ] **Step 3: 实现**

`DefaultLlmTurnRetryPolicy.cs`：

```csharp
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 应用层轮次重试默认实现：复用 <see cref="LlmRetryPolicy"/> 的可重试判定与退避计算。
/// </summary>
public sealed class DefaultLlmTurnRetryPolicy : ILlmTurnRetryPolicy
{
    private readonly LlmTurnRetryOptions _options;

    public DefaultLlmTurnRetryPolicy()
        : this(LlmTurnRetryOptions.Default) { }

    public DefaultLlmTurnRetryPolicy(LlmTurnRetryOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public bool Enabled => _options.Enabled;

    public bool CanRetry(Exception ex, CancellationToken cancellationToken)
    {
        // AgentExecutor 拿到的异常是 LlmService 包装后的 LlmException 子类（LlmStreamingException/
        // LlmConnectionException/LlmTimeoutException），它们不是 IOException/OCE，直接交给
        // LlmRetryPolicy.IsRetryable 会恒为 false；必须按 LlmException.IsRetryable 判定。
        if (ex is LlmException llmEx)
            return llmEx.IsRetryable;

        return LlmRetryPolicy.IsRetryable(ex, cancellationToken);
    }

    public TimeSpan NextDelay(int attempt)
        => LlmRetryPolicy.ComputeDelay(
            attempt,
            TimeSpan.FromMilliseconds(_options.BaseDelayMs > 0 ? _options.BaseDelayMs : 500),
            TimeSpan.FromMilliseconds(_options.MaxDelayMs > 0 ? _options.MaxDelayMs : 10_000));

    public bool ShouldRetry(int attempt, TimeSpan elapsed, TimeSpan nextDelay)
    {
        if (!_options.Enabled)
            return false;

        if (_options.MaxAttempts > 0 && attempt >= _options.MaxAttempts)
            return false;

        if (_options.TotalBudgetMs > 0 &&
            elapsed + nextDelay > TimeSpan.FromMilliseconds(_options.TotalBudgetMs))
            return false;

        return true;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~LlmTurnRetryPolicyTests"`
Expected: PASS（9 个用例）

- [ ] **Step 5: Commit**

```pwsh
git add src/spine/Seeing.Agent.Core/Llm/DefaultLlmTurnRetryPolicy.cs tests/spine/Seeing.Agent.Tests/Llm/LlmTurnRetryPolicyTests.cs
git commit -m "feat(core): add DefaultLlmTurnRetryPolicy"
```

---

## Wave 1 · Agent C（WebUI 局部 + 传输层）

### Task C1: `EventStreamHandler.HandleStreamStart` 重置修复

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:372-389`

- [ ] **Step 1: 实现（无条件重置）**

替换 `HandleStreamStart`（本任务只做渲染缓冲重置；重试态字段的清除在 Task D3 追加）：

```csharp
        /// <summary>
        /// 处理流式开始事件 - 轮次边界（含重试重开），无条件重置该轮渲染状态。
        /// </summary>
        private void HandleStreamStart(StreamStartEvent evt)
        {
            if (!string.IsNullOrEmpty(evt.LoopId))
            {
                _currentLoopId = evt.LoopId;
            }

            _currentStep = evt.Step;
            _currentAssistantMessage = null;
            _toolCallPositions.Clear();
            _accumulatedContent.Clear();
            _accumulatedReasoning.Clear();
        }
```

> 顺带修正 `EventStreamHandler.cs:629` 附近 `HandleLoopCancelled` 内已失真的注释（"取消/错误消息由服务端 ChatEventTracker 写入 Session" → 改为"取消/错误不写入 Session，由事件与 UI 状态呈现"）。

- [ ] **Step 2: 构建**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: 成功

- [ ] **Step 3: Commit**

```pwsh
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs
git commit -m "fix(webui): always reset turn buffers on StreamStart"
```

### Task C2: `SessionWindow.razor` 移除本地取消 `SystemMessage`

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:1354-1357`

- [ ] **Step 1: 实现**

删除：

```csharp
        if (SessionState.CurrentSession != null)
        {
            SessionState.CurrentSession.AddMessage(SessionMessage.SystemMessage("用户取消了当前执行"));
        }
```

- [ ] **Step 2: 构建**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: 成功（若 `SessionMessage` 因此变为未使用 using，编译器不报错；如有 warning 保留即可）

- [ ] **Step 3: Commit**

```pwsh
git add samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "fix(webui): stop persisting local cancel system message"
```

### Task C3: `RetryLlmClientDecorator` 边界注释 + `∞` 日志

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs:86-89, 118, 131-134`

- [ ] **Step 1: 加边界注释与日志格式**

在 `CompleteStreamAsync` 的 `if (yielded || !LlmRetryPolicy.IsRetryable(captured, cancellationToken))` 前加注释：

```csharp
                // 传输层边界：一旦向调用方交付过 chunk，绝不在此重试（无法回滚已交付内容）。
                // 已产出后的重开由应用层（AgentExecutor + ILlmTurnRetryPolicy）负责。
                if (yielded || !LlmRetryPolicy.IsRetryable(captured, cancellationToken))
                    throw captured;
```

将两处日志的 `_settings.MaxRetries` 实参替换为 `FormatMax(_settings.MaxRetries)`，并新增：

```csharp
        private static string FormatMax(int maxRetries) => maxRetries > 0 ? maxRetries.ToString() : "∞";
```

同步把两处格式串改回 `{Max}` 占位（已是 `{Max}`），仅换传参。

- [ ] **Step 2: 运行既有测试**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~RetryLlmClientDecoratorTests|FullyQualifiedName~LlmRetryPolicyTests"`
Expected: PASS（现有行为不变）

- [ ] **Step 3: Commit**

```pwsh
git add src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs
git commit -m "docs(core): clarify transport retry boundary and format unlimited max"
```

---

## Wave 2 · Agent D（串行收尾）

### Task D1: DI 注册 `ILlmTurnRetryPolicy`

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（`RegisterLlmServices` 内）

- [ ] **Step 1: 找 `RegisterLlmServices` 并注册**

`RegisterLlmServices` 已存在（`ServiceCollectionExtensions.cs` 内，被 `AddSeeingCore` 调用）。用 `Select-String -LiteralPath "src\spine\Seeing.Agent.Core\Extensions\ServiceCollectionExtensions.cs" -Pattern "RegisterLlmServices"` 定位后，在该方法内追加：

在该方法内追加：

```csharp
            // 应用层轮次重试策略（默认无限重试；可由宿主覆盖）
            services.AddSingleton<Seeing.Agent.Abstractions.Llm.ILlmTurnRetryPolicy>(
                _ => new Seeing.Agent.Core.Llm.DefaultLlmTurnRetryPolicy());
```

- [ ] **Step 2: 构建**

Run: `dotnet build src/spine/Seeing.Agent.Core`
Expected: 成功

- [ ] **Step 3: Commit**

```pwsh
git add src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(core): register ILlmTurnRetryPolicy"
```

### Task D2: `AgentExecutor` 接入轮次重试 + 发 `LlmRetryScheduledEvent`

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:51-75, 189-397`
- Test: `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorTurnRetryTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentExecutorTurnRetryTests
{
    private sealed class CallCounter { public int Value; }

    private static async IAsyncEnumerable<StreamUpdate> FailThenSucceed(CallCounter counter)
    {
        if (counter.Value++ == 0)
            throw new LlmStreamingException("流式响应读取失败", new IOException("remote closed"));
        yield return new StreamUpdate { ContentDelta = "ok", IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_StreamFailsOnce_RetriesAndProducesSingleContent()
    {
        var calls = new CallCounter();
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => FailThenSucceed(calls));

        var executor = CreateExecutor(llm.Object, new DefaultLlmTurnRetryPolicy());
        var events = new List<IMessageEvent>();
        await foreach (var e in Enumerate(executor)) events.Add(e);

        calls.Value.Should().Be(2);
        events.OfType<StreamStartEvent>().Should().HaveCount(2);
        events.OfType<StreamStartEvent>().Last().Attempt.Should().Be(2);
        events.OfType<LlmRetryScheduledEvent>().Should().ContainSingle()
            .Which.Attempt.Should().Be(2);
        events.OfType<StreamDeltaEvent>().Select(d => d.ContentDelta).Where(c => c != null)
            .Aggregate("", (a, b) => a + b).Should().Be("ok");
        events.OfType<LoopCompleteEvent>().Last().Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NonRetryableFailure_DoesNotRetry()
    {
        var calls = new CallCounter();
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
                FailWith(calls.Value++, new HttpRequestException("400", null, System.Net.HttpStatusCode.BadRequest)));

        var executor = CreateExecutor(llm.Object, new DefaultLlmTurnRetryPolicy());
        var events = new List<IMessageEvent>();
        await foreach (var e in Enumerate(executor)) events.Add(e);

        calls.Value.Should().Be(1);
        events.OfType<LlmRetryScheduledEvent>().Should().BeEmpty();
        events.OfType<LoopCompleteEvent>().Last().Success.Should().BeFalse();
    }

    private static async IAsyncEnumerable<StreamUpdate> FailWith(int _, Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IMessageEvent> Enumerate(AgentExecutor executor)
    {
        var agent = new AgentDefinition { Name = "t", Runtime = AgentRuntime.Native, Mode = AgentMode.All };
        var context = new AgentContext { SessionId = "s1", WorkingDirectory = "w", WorkspaceRoot = "w" };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };
        await foreach (var e in executor.ExecuteAsync(agent, messages, context, default))
            yield return e;
    }

    private static AgentExecutor CreateExecutor(ILlmService llm, ILlmTurnRetryPolicy? retryPolicy)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        var agentRegistry = new Mock<IAgentRegistry>();
        var permission = new Mock<IPermissionService>();
        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm, toolManager, permission.Object, hookManager, agentRegistry.Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()), modelManager.Object,
            NullLogger<AgentExecutor>.Instance,
            sessionManager: null, loopScheduler: null, todoStore: null, turnRetryPolicy: retryPolicy);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests`
Expected: 编译失败（`AgentExecutor` 无 `turnRetryPolicy` 参数）

- [ ] **Step 3: 接入策略（构造 + 字段）**

在 `AgentExecutor` 字段区加：

```csharp
    private readonly ILlmTurnRetryPolicy? _turnRetryPolicy;
```

构造签名末尾追加可选参数并赋值：

```csharp
        ITodoStore? todoStore = null,
        ILlmTurnRetryPolicy? turnRetryPolicy = null)
    {
        // ... 既有赋值 ...
        _turnRetryPolicy = turnRetryPolicy;
    }
```

- [ ] **Step 4: 重构单轮为 attempt 循环**

**精确替换范围**（必须严格遵守，避免重复声明或漏删）：
- 删除 `AgentExecutor.cs:189-195`（原 `StreamStart` 块）。
- 保留 `:197-214`（`TodoEmpty` 检查 + `var request = await BuildRequestAsync(...)`）不动。
- 删除 `:216-361`（原 LLM 调用/累积/`cancel` 检查整段）。
- 保留 `:363` 起的 `llmException` 失败路径、空响应路径、成功路径（`history.Add`/`StreamComplete`/工具/`LoopComplete`）**不动**。
- 在 `:214`（`BuildRequest`）之后插入下述结构；`request` 由 `:214` 提供，**块内不得重复声明**。

```csharp
                ChatMessage? assistantMessage = null;
                Exception? llmException = null;
                var turnElapsed = TimeSpan.Zero;

                for (var attempt = 1; ; attempt++)
                {
                    assistantMessage = null;            // 每次 attempt 重置，避免残留上一尝试结果
                    var usageBeforeTurn = totalUsage;   // 重试失败时回滚本轮 usage 累加
                    var streamingContent = new StringBuilder();
                    var streamingReasoning = new StringBuilder();
                    string? streamingReasoningSignature = null;
                    List<ToolCall>? accumulatedToolCalls = null;
                    TokenUsage? lastUsage = null;
                    llmException = null;

                    await writer.WriteAsync(new StreamStartEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Step = step,
                        Attempt = attempt
                    }, effectiveToken);

                    var llmChannel = Channel.CreateUnbounded<StreamUpdate>();
                    var llmTask = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var update in _llm.CompleteStreamAsync(
                                ResolveModelId(agent, context), request, context.SessionId, effectiveToken))
                            {
                                await llmChannel.Writer.WriteAsync(update, effectiveToken).ConfigureAwait(false);
                            }
                            llmChannel.Writer.Complete();
                        }
                        catch (OperationCanceledException ex) when (ex.CancellationToken == effectiveToken)
                        {
                            _logger.LogInformation("[AgentExecutor] LLM 请求被取消");
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmRetryExhaustedException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 重试耗尽: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmTimeoutException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 请求超时");
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmStreamingException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 流式错误: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 错误: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (IOException ex)
                        {
                            llmException = new LlmConnectionException("网络连接错误", ex);
                            _logger.LogError(ex, "[AgentExecutor] 网络连接错误");
                            llmChannel.Writer.Complete();
                        }
                        catch (Exception ex)
                        {
                            llmException = new LlmException($"LLM 调用失败: {ex.Message}", ex);
                            _logger.LogError(ex, "[AgentExecutor] LLM 调用失败");
                            llmChannel.Writer.Complete();
                        }
                    }, effectiveToken);

                    await foreach (var update in llmChannel.Reader.ReadAllAsync(effectiveToken))
                    {
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

                        if (!string.IsNullOrEmpty(update.ReasoningSignature))
                            streamingReasoningSignature = update.ReasoningSignature;

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

                        if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
                        {
                            accumulatedToolCalls ??= new List<ToolCall>();
                            accumulatedToolCalls.AddRange(update.ToolCallDeltas);
                        }

                        if (update.Usage != null)
                        {
                            lastUsage = update.Usage;
                            totalUsage = totalUsage == null
                                ? new TokenUsage
                                {
                                    InputTokens = update.Usage.InputTokens,
                                    OutputTokens = update.Usage.OutputTokens
                                }
                                : totalUsage with
                                {
                                    InputTokens = totalUsage.InputTokens + update.Usage.InputTokens,
                                    OutputTokens = totalUsage.OutputTokens + update.Usage.OutputTokens
                                };
                        }

                        if (update.IsComplete)
                        {
                            assistantMessage = BuildAssistantMessage(
                                update,
                                streamingContent.ToString(),
                                streamingReasoning.ToString(),
                                streamingReasoningSignature,
                                accumulatedToolCalls);
                        }
                    }

                    await llmTask.ConfigureAwait(false);

                    if (effectiveToken.IsCancellationRequested)
                        throw new OperationCanceledException(effectiveToken);

                    if (llmException == null)
                        break;

                    // 应用层整轮重试判定（本轮工具尚未执行，回滚该轮产出安全）
                    if (_turnRetryPolicy is not { Enabled: true }
                        || !_turnRetryPolicy.CanRetry(llmException, effectiveToken))
                        break;

                    var nextDelay = _turnRetryPolicy.NextDelay(attempt);
                    if (!_turnRetryPolicy.ShouldRetry(attempt, turnElapsed, nextDelay))
                        break;

                    turnElapsed += nextDelay;
                    await writer.WriteAsync(new LlmRetryScheduledEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Step = step,
                        Attempt = attempt + 1,
                        NextDelayMs = (int)nextDelay.TotalMilliseconds,
                        Reason = llmException.Message
                    }, effectiveToken);

                    // 回滚本轮 usage 累加，避免重试重复计入
                    totalUsage = usageBeforeTurn;

                    _logger.LogWarning(
                        "[AgentExecutor] 轮次重试: Step={Step}, Attempt={Attempt}, NextDelayMs={DelayMs}",
                        step, attempt + 1, nextDelay.TotalMilliseconds);

                    await Task.Delay(nextDelay, effectiveToken).ConfigureAwait(false);
                }
```

> 原 `:363-397` 的 `llmException != null` 失败路径保持在其后（`errorMessage = llmException.Message;` + `ErrorEvent` + `LoopComplete(false)` + `return;`），`assistantMessage == null` 空响应分支同样保持在 attempt 循环之后。`totalUsage` 已由上面的 `usageBeforeTurn` 在重试前回滚，避免重复累加。
>
> **关键（否则重试不可达）**：异常分支一律 `llmChannel.Writer.Complete()`（**不带 error**）。若用 `Complete(ex)`，`ReadAllAsync` 会直接抛出该异常，drain 循环中断，后续 `await llmTask` 与重试判定全部跳过，落入 `RunProducerAsync` 外层 catch——这正是当前 `llmException` 分支形同死代码的原因（你日志中 `[AgentExecutor] 网络连接错误` 与 `Loop 未捕获异常` 同时出现即为此）。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorTurnRetryTests|FullyQualifiedName~AgentExecutorCancellationTests"`
Expected: PASS（新增 2 + 既有取消测试全部）

- [ ] **Step 6: Commit**

```pwsh
git add src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs tests/spine/Seeing.Agent.Tests/Core/AgentExecutorTurnRetryTests.cs
git commit -m "feat(core): add application-level LLM turn retry with rollback"
```

### Task D3: `EventStreamHandler` 重试态 + `AgentLoopInfo` 字段

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`（`AgentLoopInfo`、switch、`HandleLlmRetry`、`HandleStreamStart`）

- [ ] **Step 1: 扩展 `AgentLoopInfo`**

```csharp
        /// <summary>当前轮次重试次数（0 = 未重试）</summary>
        public int RetryAttempt { get; set; }

        /// <summary>本次退避等待毫秒（倒计时用）</summary>
        public int RetryNextDelayMs { get; set; }

        /// <summary>重试原因</summary>
        public string? RetryReason { get; set; }

        /// <summary>本次重试起始时间（倒计时基准）</summary>
        public DateTime? RetryAt { get; set; }

        /// <summary>是否处于重试等待中</summary>
        public bool IsRetrying => RetryAttempt > 0 && RetryAt.HasValue
            && DateTime.Now < RetryAt.Value.AddMilliseconds(RetryNextDelayMs);
```

- [ ] **Step 2: switch 增加分支**

在 `ProcessEventAsync` 的 `case MessageEventType.LlmRetry:` 分支：

```csharp
                case MessageEventType.LlmRetry:
                    HandleLlmRetry((LlmRetryScheduledEvent)evt);
                    break;
```

- [ ] **Step 3: 实现 `HandleLlmRetry` 与清除**

```csharp
        private void HandleLlmRetry(LlmRetryScheduledEvent evt)
        {
            if (_currentLoop != null)
            {
                _currentLoop.RetryAttempt = evt.Attempt;
                _currentLoop.RetryNextDelayMs = evt.NextDelayMs;
                _currentLoop.RetryReason = evt.Reason;
                _currentLoop.RetryAt = DateTime.Now;
            }
        }
```

新增私有方法，并在各清除点调用（Spec §5.8：收到这些事件即视为该轮结束/恢复）：

```csharp
        private void ClearRetryState()
        {
            if (_currentLoop == null)
                return;
            _currentLoop.RetryAttempt = 0;
            _currentLoop.RetryNextDelayMs = 0;
            _currentLoop.RetryReason = null;
            _currentLoop.RetryAt = null;
        }
```

调用点（在下列每个现有处理方法内追加 `ClearRetryState();`）：
- `HandleStreamStart`
- `HandleStreamDelta`（首个 delta 到达即恢复）
- `HandleStreamComplete`
- `HandleLoopComplete`
- `HandleLoopCancelled`
- `HandleError`

- [ ] **Step 4: 构建**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: 成功

- [ ] **Step 5: Commit**

```pwsh
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs
git commit -m "feat(webui): track LLM retry state in AgentLoopInfo"
```

### Task D4: 重试中提示条（SessionWindow banner）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`（暴露 `IsRetrying`/`RetryText`）
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（渲染提示条 + 样式 + 1s 倒计时刷新）

- [ ] **Step 1: `EventStreamHandler` 暴露重试态**

在 `EventStreamHandler` 公开属性区（`CurrentBudgetStatus` 附近）新增：

```csharp
        /// <summary>当前 Loop 是否处于重试等待中。</summary>
        public bool IsRetrying => _currentLoop?.IsRetrying == true;

        /// <summary>重试提示文案（含倒计时秒数）；未重试时为 null。</summary>
        public string? RetryText
        {
            get
            {
                var loop = _currentLoop;
                if (loop is null || !loop.IsRetrying || loop.RetryAt is null)
                    return null;
                var remain = (int)Math.Ceiling(
                    (loop.RetryAt.Value.AddMilliseconds(loop.RetryNextDelayMs) - DateTime.Now).TotalSeconds);
                return $"连接中断，{Math.Max(0, remain)} 秒后重试（第 {loop.RetryAttempt} 次）…";
            }
        }
```

- [ ] **Step 2: `SessionWindow.razor` 渲染提示条（含内联样式块）**

在消息滚动区末尾（"正在输入"指示附近、输入框之前）加入：

```razor
@if (_handler?.IsRetrying == true)
{
    <div class="retry-banner" role="status">
        <span class="retry-spinner"></span>
        <span>@_handler.RetryText</span>
    </div>
}
```

并在该 `.razor` 文件末尾（`@code` 之前或组件根元素之外允许的顶层位置）加入样式块：

```razor
<style>
    .retry-banner {
        display: flex; gap: 8px; align-items: center;
        padding: 8px 12px; margin: 8px 0; border-radius: 6px;
        background: var(--color-warning-bg, #fff7e6);
        color: var(--color-warning, #ad6800);
        font-size: 13px;
    }
    .retry-spinner {
        width: 12px; height: 12px;
        border: 2px solid currentColor; border-top-color: transparent;
        border-radius: 50%; animation: retry-spin 0.8s linear infinite;
    }
    @@keyframes retry-spin { to { transform: rotate(360deg); } }
</style>
```

> Razor 中 `@keyframes` 需转义为 `@@keyframes`；若样式块位置受限，等价做法是把上述 CSS 追加到应用全局样式文件（`samples/Seeing.Agent.WebUI/wwwroot` 下的全局 css，执行者按项目现有全局样式组织落地）。

- [ ] **Step 3: 倒计时刷新（1s Timer）**

在 `SessionWindow.razor` 的 `@code` 区新增：

```csharp
    private System.Timers.Timer? _retryTimer;

    private void EnsureRetryTimer()
    {
        var active = _handler?.IsRetrying == true;
        if (active && _retryTimer is null)
        {
            _retryTimer = new System.Timers.Timer(1000);
            _retryTimer.Elapsed += (_, _) => InvokeAsync(() =>
            {
                if (_handler?.IsRetrying != true)
                {
                    _retryTimer?.Stop();
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                }
                StateHasChanged();
            });
            _retryTimer.Start();
        }
        else if (!active && _retryTimer is not null)
        {
            _retryTimer.Stop();
            _retryTimer.Dispose();
            _retryTimer = null;
        }
    }
```

在现有 `_handler.OnStateChanged` 处理路径（`OnStateChanged += ...` 回调体内）追加 `EnsureRetryTimer();`；在组件的 `Dispose`/`DisposeAsync` 中停止并释放 `_retryTimer`。

- [ ] **Step 4: 构建并跑 WebUI 测试**

Run: `dotnet build samples/Seeing.Agent.WebUI && dotnet build tests/apps/Seeing.Agent.WebUI.Tests && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```pwsh
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "feat(webui): show retry countdown banner"
```

### Task D5: 错误提示条（替代被移除的 SystemMessage）

**背景**：Task A2 移除 `ChatEventTracker` 的错误 `SystemMessage` 后，WebUI 将失去错误展示——`HandleError` 现仅 `_ = FormatErrorDetails(evt)`（返回值被丢弃），`LoopGroupViewModel.Error` 无来源。必须补一条 UI 展示路径。

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`（`LastError` 状态 + `HandleError`）
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（错误提示条）

- [ ] **Step 1: `EventStreamHandler` 记录错误**

新增公开属性：

```csharp
        /// <summary>最近一次执行错误（仅展示，不写入 Messages/LLM 历史）。</summary>
        public string? LastError { get; private set; }
```

替换现有 `HandleError`：

```csharp
        private void HandleError(ErrorEvent evt)
        {
            EnsureCurrentAssistantMessage(evt.SessionId);
            LastError = FormatErrorDetails(evt);
        }
```

在 `HandleLoopStart`（`EventStreamHandler.cs:327` 附近）末尾清除：`LastError = null;`。

- [ ] **Step 2: `SessionWindow.razor` 渲染错误条**

紧邻重试 banner 之后加入：

```razor
@if (!string.IsNullOrEmpty(_handler?.LastError))
{
    <div class="retry-banner error-banner" role="alert">
        <span>@_handler.LastError</span>
    </div>
}
```

在 `<style>` 块补充：

```razor
    .error-banner {
        background: var(--color-error-bg, #fff1f0);
        color: var(--color-error, #cf1322);
    }
```

- [ ] **Step 3: 构建并跑 WebUI 测试**

Run: `dotnet build samples/Seeing.Agent.WebUI && dotnet build tests/apps/Seeing.Agent.WebUI.Tests && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 全部 PASS

- [ ] **Step 4: Commit**

```pwsh
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "feat(webui): surface execution errors without persisting session messages"
```

---

## 验证清单（收尾）

- [ ] 全量构建：`dotnet build Seeing.Agent.slnx`
- [ ] 核心测试：`dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
- [ ] WebUI 测试：`dotnet build tests/apps/Seeing.Agent.WebUI.Tests && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
- [ ] 手动场景：断网/强断流式请求 → UI 显示"连接中断，N 秒后重试（第 K 次）"→ 恢复网络后单条连续回复、无重复；错误不再出现在后续上下文中。

## 风险与回滚

- `AgentExecutor` attempt 循环是较大改动：`totalUsage`/`assistantMessage` 必须在 attempt 顶部重置、重试前回滚；若测试暴露重复内容，先检查 `usageBeforeTurn` 与 `assistantMessage = null` 是否到位。
- 应用层重试默认 `Enabled=true` 且**无限**：如需止血可临时注入 `LlmTurnRetryOptions { Enabled = false }`。
- 已知边界（非本期）：`SessionWindowTimelineSync` 对 `StreamStart` 仅增量追加、不删除；若 UI 曾按旧 partial 建过独立 turn（LoopId 未及时到达场景），重试后可能残留旧气泡。`ChatEventTracker` 复用同一 Id 原地覆盖可消除绝大多数情况；执行后用手动场景验证，若复现再补 timeline 层 Reset。
- 已知边界（非本期）：Gateway/ACP 未映射 `LlmRetryScheduledEvent`，远程渠道不展示"重试中"；`StreamStartEvent.Attempt` 按可选字段透传，未识别渠道忽略。
- 若 WebUI `<style>` 块位置受限，将 `.retry-banner`/`.retry-spinner`/`.error-banner` 移到应用全局样式文件。
