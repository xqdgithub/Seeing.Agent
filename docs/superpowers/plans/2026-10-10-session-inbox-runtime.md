# 会话入站运行时（SessionInbox / SessionRuntime）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **并行策略**：本计划按「阶段 + 任务批次」组织。标注 `[PARALLEL-<组>]` 的任务在同组内**无相互文件依赖**（顺序依赖见各阶段头说明），可派多个子 Agent 并行执行；未标注或标 `[SERIAL]` 的为串行依赖任务。仅在**阶段检查点**（CP1–CP5）做人工审查，批次内不逐步审查。

**Goal:** 把三套并行的「往会话放消息并激活」机制统一为 SessionRuntime（唯一驱动入口 `DeliverAsync` + 消费循环），支持子会话硬停中断、接管、主动推送。

**Architecture:** 两轴正交——写入（Inbox：Sender/Presentation）+ 激活（Mailbox：Inject/Wake/Notify，消息级 `inbox.consumed` 标记 + 合并消费）。`SessionRuntime`（Hosting）持有唯一入口与消费循环；`ExecutionRunner` 抽出一轮执行体；`SessionDirectory` 承载 CRUD。契约在 Abstractions，实现必须落 Hosting（依赖方向硬约束）。

**Tech Stack:** .NET 10、C#、xUnit 2.9 + Moq + FluentAssertions、Microsoft.Extensions.DependencyInjection、VSTest（见 AGENTS.md：MTP 无法发现测试）。

**Spec:** `docs/superpowers/specs/2026-10-10-session-inbox-runtime-design.md`

---

## 关键约束（每个任务都必须遵守）

1. **依赖方向**：`Abstractions ← Core ← Hosting`。契约（接口/DTO）落 `src/abstractions/Seeing.Agent.Abstractions/Runtime/`；实现落 `src/hosting/Seeing.Agent.Hosting/`。Core 不得引用 Hosting。
2. **单一权威写入者**：执行期 assistant/tool 消息由 `ChatEventTracker` 独占写；`inbox.*`/`control.*` 仅在非执行期写（Deliver）与「执行前一次性标记 consumed」（循环侧）。
3. **命名冲突**：新 `ISessionDirectory` 与既有 `Seeing.Session.Storage.ISessionCatalog` 区分，不得重名。
4. **测试运行**：`dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<Class>"`
5. **提交粒度**：每个任务结束提交一次；提交信息用 `feat(inbox): ...` / `refactor(inbox): ...`。
6. **禁止**：`async void`、静默吞异常、魔法字符串（用常量）、`\\` 与 `/` 混用。

---

## 文件结构（先锁定边界）

**新增 — Abstractions**
- `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxSender.cs`（`InboxSenderKind`、`InboxSender`）
- `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMessage.cs`（`InboxPresentation`、`InboxDelivery`、`InboxMessage`、`InboxResult`）
- `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMetadataKeys.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionRuntime.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionDirectory.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Sessions/SessionControl.cs`（`SessionOwner`、`SessionControl`、`SessionControlKeys`、`SessionControlExtensions`）
- `src/abstractions/Seeing.Agent.Abstractions/Events/InboxNoticeEvent.cs`

**新增 — Hosting**
- `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`（internal，跑一轮）
- `src/hosting/Seeing.Agent.Hosting/Execution/InboxBatch.cs`（internal，本批输入摘要；可与 ExecutionRunner 同文件）
- `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`（`ISessionRuntime` 实现 + 消费循环）
- `src/hosting/Seeing.Agent.Hosting/Execution/SessionExecutionState.cs`（internal，会话运行态：`CurrentExecution`/`LastExecution`/`PendingInputCount`；`SessionRuntime` 的 `_states` 值类型）
- `src/hosting/Seeing.Agent.Hosting/Execution/TokenBudgetAutoCompactionPeek.cs`（从 ExecutionJobService 迁出）
- `src/hosting/Seeing.Agent.Hosting/SessionDirectory.cs`（`ISessionDirectory` 实现，CRUD）

**修改 — Hosting**
- `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`（重拆）
- `src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs`（删除）
- `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs`、`TaskStatusTool.cs`
- `src/hosting/Seeing.Agent.Hosting/HostingServiceCollectionExtensions.cs`

**修改 — Core**
- 删除 `src/spine/Seeing.Agent.Core/Core/Scheduling/AgentLoopScheduler.cs`、`IAgentLoopScheduler.cs`
- `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`（删 `IAgentLoopScheduler` 依赖）
- `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（删注册）

**修改 — 其他**
- `src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs`、`SessionToolsModule.cs`（+ 新增 3 工具）
- `src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs`、`Jobs/AgentJob.cs`
- `src/gateway/Seeing.Agent.Gateway/*`、`src/gateway/Seeing.Gateway.Client/*`、`src/gateway/Seeing.Gateway.WeCom/*`、`src/gateway/Seeing.Gateway.QQ/*`
- `samples/Seeing.Agent.WebUI/*`、`samples/Seeing.Agent.Tui/*`

---

# 阶段 1：Abstractions 契约

> **依赖**：无。**可并行度**：Task 1.1–1.4、1.6 中，**1.1 必须先于 1.3**（1.3 的 `ISessionRuntime` 引用 1.1 的 `InboxMessage`/`InboxResult`）；其余可并行。**Task 1.5（DTO 删除）必须最后串行执行**（见下方构建策略）。
>
> **⚠️ 构建策略（重要）**：阶段 1+2 是**同一个内聚重构**（DTO 变更 + ExecutionRunner 抽取 + 旧类删除互相纠缠，拆开独立编译不可达）。
>
> - **Task 1.1–1.4、1.6 为纯新增**（只加类型，不破坏现有编译），其测试构建步骤可正常执行。
> - **Task 1.5（DTO 删除）会破坏 `ExecutionJobService` 编译**——故 **Task 1.5 必须在 1.1–1.4、1.6 全部完成并提交后，最后执行**；执行 1.5 后 `Seeing.Agent.slnx` 全量构建将失败，这是**预期**（`ExecutionJobService` 在 Task 2.5 删除）。1.5 的验收仅 `dotnet build src/abstractions/Seeing.Agent.Abstractions`。
> - 全量编译验收统一在 **CP3**（阶段 3 收口）。

### Task 1.1: Inbox 基础类型 [PARALLEL-A]

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxSender.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMessage.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMetadataKeys.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Runtime/InboxTypesTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Runtime;

public class InboxTypesTests
{
    [Fact]
    public void InboxMessage_ShouldDefault_WakeAndUser()
    {
        var msg = new InboxMessage
        {
            TargetSessionId = "s1",
            Sender = new InboxSender { Kind = InboxSenderKind.Human },
            Body = "hi"
        };

        msg.Delivery.Should().Be(InboxDelivery.Wake);
        msg.Presentation.Should().Be(InboxPresentation.User);
    }

    [Fact]
    public void InboxMetadataKeys_ShouldExposeExpectedKeys()
    {
        InboxMetadataKeys.Delivery.Should().Be("inbox.delivery");
        InboxMetadataKeys.Consumed.Should().Be("inbox.consumed");
        InboxMetadataKeys.SenderKind.Should().Be("inbox.sender_kind");
        InboxMetadataKeys.SenderId.Should().Be("inbox.sender_id");
        InboxMetadataKeys.SenderName.Should().Be("inbox.sender_name");
        InboxMetadataKeys.Presentation.Should().Be("inbox.presentation");
        InboxMetadataKeys.Source.Should().Be("inbox.source");
        InboxMetadataKeys.Kind.Should().Be("inbox.kind");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions` → 编译失败（类型不存在）

- [ ] **Step 3: 实现**

`InboxSender.cs`:
```csharp
namespace Seeing.Agent.Abstractions.Runtime;

public enum InboxSenderKind { Human, Session, System }

public sealed record InboxSender
{
    public required InboxSenderKind Kind { get; init; }
    /// <summary>Session：来源会话 Id；System：子系统标签（task/cron/heartbeat/gateway…）。</summary>
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
}
```

`InboxMessage.cs`:
```csharp
using Seeing.Agent.Abstractions.Models;

namespace Seeing.Agent.Abstractions.Runtime;

/// <summary>进上下文身份：User=平权用户消息；System=system-reminder 信封；Assistant=外部产出写回；None=不进 LLM 历史（可持久化展示）。</summary>
public enum InboxPresentation { User, System, Assistant, None }

/// <summary>激活语义：Inject=只落不跑；Wake=落并激活消费；Notify=只出 UI 通知。</summary>
public enum InboxDelivery { Inject, Wake, Notify }

public sealed record InboxMessage
{
    public required string TargetSessionId { get; init; }
    public required InboxSender Sender { get; init; }
    public string? Body { get; init; }
    public InboxDelivery Delivery { get; init; } = InboxDelivery.Wake;
    public InboxPresentation Presentation { get; init; } = InboxPresentation.User;
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
    public string? SourceTag { get; init; }
    public string? Kind { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
}

public sealed record InboxResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }
    /// <summary>本轮消费产生的执行 Id（尽力而为，可能为空：循环异步启动）。</summary>
    public string? ExecutionId { get; init; }
    public int PendingInputCount { get; init; }

    public static InboxResult Ok(string? messageId = null, string? executionId = null, int pendingInputCount = 0)
        => new() { Success = true, MessageId = messageId, ExecutionId = executionId, PendingInputCount = pendingInputCount };

    public static InboxResult Failed(string error)
        => new() { Success = false, Error = error };
}
```

`InboxMetadataKeys.cs`:
```csharp
namespace Seeing.Agent.Abstractions.Runtime;

public static class InboxMetadataKeys
{
    public const string Delivery = "inbox.delivery";
    public const string Consumed = "inbox.consumed";
    public const string SenderKind = "inbox.sender_kind";
    public const string SenderId = "inbox.sender_id";
    public const string SenderName = "inbox.sender_name";
    public const string Presentation = "inbox.presentation";
    public const string Source = "inbox.source";
    public const string Kind = "inbox.kind";
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions && dotnet build tests/spine/Seeing.Agent.Tests`
Expected: Abstractions 通过；测试项目可编译（本任务纯新增，不破坏现有编译）

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Runtime/ tests/spine/Seeing.Agent.Tests/Runtime/
git commit -m "feat(inbox): add inbox base types and metadata keys"
```

### Task 1.2: SessionControl 契约 [PARALLEL-A]

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Sessions/SessionControl.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Sessions/SessionControlTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Sessions;

public class SessionControlTests
{
    [Fact]
    public void GetControl_WithoutMetadata_ShouldReturnParent()
    {
        var session = new SessionData { Id = "s1" };
        session.GetControl().Owner.Should().Be(SessionOwner.Parent);
    }

    [Fact]
    public void SetOwner_Human_ShouldPersistAndRoundTrip()
    {
        var session = new SessionData { Id = "s1" };
        session.SetOwner(SessionOwner.Human, "tester");
        session.GetControl().Owner.Should().Be(SessionOwner.Human);
        session.Metadata[SessionControlKeys.TakenOverBy].Should().Be("tester");
        session.Metadata[SessionControlKeys.Owner].Should().Be("human");
    }

    [Fact]
    public void StopReasonKeys_ShouldBeStable()
    {
        SessionControlKeys.StopReason.Should().Be("control.stop_reason");
        SessionControlKeys.StopReasonCascadedFromParent.Should().Be("cascaded_from_parent_stop");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 编译失败

- [ ] **Step 3: 实现**

```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Sessions;

public enum SessionOwner { Parent, Human, Self }

public sealed record SessionControl
{
    public SessionOwner Owner { get; init; } = SessionOwner.Parent;
    public DateTimeOffset? TakenOverAt { get; init; }
    public string? TakenOverBy { get; init; }
}

public static class SessionControlKeys
{
    public const string Owner = "control.owner";
    public const string TakenOverAt = "control.taken_over_at";
    public const string TakenOverBy = "control.taken_over_by";
    public const string StopReason = "control.stop_reason";
    public const string StopReasonCascadedFromParent = "cascaded_from_parent_stop";
}

public static class SessionControlExtensions
{
    public static SessionControl GetControl(this SessionData session)
    {
        var owner = SessionOwner.Parent;
        if (session.Metadata.TryGetValue(SessionControlKeys.Owner, out var raw)
            && Enum.TryParse<SessionOwner>(raw, ignoreCase: true, out var parsed))
            owner = parsed;

        DateTimeOffset? takenAt = null;
        if (session.Metadata.TryGetValue(SessionControlKeys.TakenOverAt, out var at)
            && DateTimeOffset.TryParse(at, out var parsedAt))
            takenAt = parsedAt;

        session.Metadata.TryGetValue(SessionControlKeys.TakenOverBy, out var by);
        return new SessionControl { Owner = owner, TakenOverAt = takenAt, TakenOverBy = by };
    }

    public static void SetOwner(this SessionData session, SessionOwner owner, string? by)
    {
        session.Metadata[SessionControlKeys.Owner] = owner.ToString().ToLowerInvariant();
        if (owner == SessionOwner.Human)
        {
            session.Metadata[SessionControlKeys.TakenOverAt] = DateTimeOffset.UtcNow.ToString("O");
            if (!string.IsNullOrEmpty(by))
                session.Metadata[SessionControlKeys.TakenOverBy] = by;
        }
        else
        {
            session.Metadata.Remove(SessionControlKeys.TakenOverAt);
            session.Metadata.Remove(SessionControlKeys.TakenOverBy);
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionControlTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Sessions/ tests/spine/Seeing.Agent.Tests/Sessions/
git commit -m "feat(inbox): add SessionControl ownership contract"
```

### Task 1.3: ISessionRuntime / ISessionDirectory 契约 [SERIAL-after-1.1]

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionRuntime.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionDirectory.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Runtime/RuntimeContractsTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Runtime;

public class RuntimeContractsTests
{
    [Fact]
    public void ISessionRuntime_ShouldDeclareDeliverInterruptStop()
    {
        var t = typeof(ISessionRuntime);
        t.GetMethod(nameof(ISessionRuntime.DeliverAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.InterruptAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.StopAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.WaitForIdleAsync)).Should().NotBeNull();
    }

    [Fact]
    public void ISessionRuntime_ShouldNotDeclare_ExecutionLevelCancelOrGetExecution()
    {
        var names = typeof(ISessionRuntime).GetMethods().Select(m => m.Name).ToList();
        names.Should().NotContain("CancelAsync");
        names.Should().NotContain("GetExecution");
    }

    [Fact]
    public void ISessionDirectory_ShouldDeclareCrud()
    {
        var t = typeof(ISessionDirectory);
        foreach (var n in new[] { "GetAsync", "EnsureAsync", "ListAsync", "CreateAsync", "DeleteAsync", "RenameAsync", "BranchAsync" })
            t.GetMethod(n).Should().NotBeNull($"ISessionDirectory must declare {n}");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 编译失败

- [ ] **Step 3: 实现**

`ISessionRuntime.cs`:
```csharp
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionRuntime
{
    /// <summary>唯一驱动入口：投递一条入站消息（写入 + 激活）。</summary>
    Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default);

    /// <summary>硬停当前轮：取消当前执行体；consumed 不回退，循环继续消费剩余未读。</summary>
    Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default);

    /// <summary>停止会话：取消当前轮 + 终止消费循环 + 不再消费剩余未读；级联子会话。</summary>
    Task<int> StopAsync(string sessionId, CancellationToken ct = default);

    Task<int> CancelAllInFlightAsync(CancellationToken ct = default);
    Task WaitForIdleAsync(string sessionId, CancellationToken ct = default);
    SessionExecutionOverview GetOverview(string sessionId);
    bool HasAnyActiveExecution();
    IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId);
    IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default);
    Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default);
}
```

`ISessionDirectory.cs`:
```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionDirectory
{
    Task<SessionData?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<SessionData> EnsureAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionData>> ListAsync(CancellationToken ct = default);
    Task<SessionData> CreateAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task RenameAsync(string sessionId, string newTitle, CancellationToken ct = default);
    Task<SessionData> BranchAsync(string sessionId, string? title = null, CancellationToken ct = default);
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~RuntimeContractsTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Runtime/ tests/spine/Seeing.Agent.Tests/Runtime/
git commit -m "feat(inbox): add ISessionRuntime and ISessionDirectory contracts"
```

### Task 1.4: InboxNoticeEvent [PARALLEL-A]

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Events/InboxNoticeEvent.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Events/InboxNoticeEventTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class InboxNoticeEventTests
{
    [Fact]
    public void Type_ShouldBeInboxNotice()
    {
        IMessageEvent evt = new InboxNoticeEvent { SessionId = "s1", SenderKind = "session" };
        evt.Type.Should().Be(MessageEventType.InboxNotice);
        MessageEventType.InboxNotice.Should().Be("inbox.notice");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 编译失败

- [ ] **Step 3: 实现**

在 `MessageEventTypes.cs` 的 `MessageEventType` 类内追加：
```csharp
    public const string InboxNotice = "inbox.notice";
```

`InboxNoticeEvent.cs`:
```csharp
namespace Seeing.Agent.Abstractions.Events;

public record InboxNoticeEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.InboxNotice;

    public required string SenderKind { get; init; }   // human | session | system
    public string? SenderId { get; init; }
    public string? SourceTag { get; init; }
    public string? Kind { get; init; }
    public string? Body { get; init; }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~InboxNoticeEventTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Events/ tests/spine/Seeing.Agent.Tests/Events/
git commit -m "feat(inbox): add InboxNoticeEvent"
```

### Task 1.5: DTO 字段变更（Overview / ExecutionRecord / ExecutionOptions）[SERIAL-最后]

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/SessionExecutionOverview.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionRecord.cs`
- Modify: `src/spine/Seeing.Agent.Core/Execution/ExecutionOptions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/OverviewDtoTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class OverviewDtoTests
{
    [Fact]
    public void Overview_ShouldHaveLastExecutionAndPendingInputCount()
    {
        var o = new SessionExecutionOverview { LastExecution = null, PendingInputCount = 3 };
        o.PendingInputCount.Should().Be(3);
        typeof(SessionExecutionOverview).GetProperty("LastExecution").Should().NotBeNull();
        typeof(SessionExecutionOverview).GetProperty("QueueLength").Should().BeNull();
        typeof(SessionExecutionOverview).GetProperty("QueuedExecutions").Should().BeNull();
    }

    [Fact]
    public void ExecutionRecord_ShouldNotHaveQueuePosition()
    {
        typeof(ExecutionRecord).GetProperty("QueuePosition").Should().BeNull();
    }

    [Fact]
    public void ExecutionOptions_ShouldNotHaveMaxQueueSizePerSession()
    {
        typeof(ExecutionOptions).GetProperty("MaxQueueSizePerSession").Should().BeNull();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 测试失败

- [ ] **Step 3: 实现**

`SessionExecutionOverview.cs`：删除 `QueueLength`、`QueuedExecutions`、`HasQueuedExecutions`；新增：
```csharp
    /// <summary>该会话最近一轮执行记录；终态后保留至下一轮开始。空闲态终态判定的唯一来源。</summary>
    public ExecutionRecord? LastExecution { get; set; }

    /// <summary>会话当前未消费输入数（仅 UI 展示，不用于终态判定）。</summary>
    public int PendingInputCount { get; set; }
```
`ExecutionRecord.cs`：删除 `QueuePosition`、`QueuedAt`（保留 `ExecutionId` 字段）。
`ExecutionOptions.cs`：删除 `MaxQueueSizePerSession`。

- [ ] **Step 4: 运行确认通过（仅 Abstractions 项目）**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions`
Expected: 通过（此步不构建 Hosting；`ExecutionJobService` 的编译错延至 Task 2.5 修）

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Execution/ src/spine/Seeing.Agent.Core/Execution/ExecutionOptions.cs tests/spine/Seeing.Agent.Tests/Execution/
git commit -m "feat(inbox): adjust overview/record/options DTOs"
```

### Task 1.6: SessionTitleService 识别 inbox.presentation [PARALLEL-A]

> **说明**：本任务与 1.1–1.4 一样为纯新增/局部修改，测试可正常构建。

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Services/SessionTitleService.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Services/SessionTitleServiceInboxTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Services;

public class SessionTitleServiceInboxTests
{
    [Fact]
    public void IsIntentionalUserMessage_ShouldExclude_NonUserPresentation()
    {
        var m = new SessionMessage { Role = "user" };
        m.Metadata = new Dictionary<string, object> { ["inbox.presentation"] = "system" }.ToImmutableDictionary();
        // 断言：含 inbox.presentation != user 的消息不计入意图用户消息
        // 通过反射调用 internal static IsIntentionalUserMessage
    }
}
```
> 注：`IsIntentionalUserMessage` 为 `internal static`（`SessionTitleService.cs:61`）；测试经 `InternalsVisibleTo` 访问。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 测试失败

- [ ] **Step 3: 实现**

在 `IsIntentionalUserMessage`（`SessionTitleService.cs:61-79`）中，除现有 `synthetic` 排除外，追加：**仅当 `Metadata` 含 `inbox.presentation` 键且值非 `user` 时排除**（键缺失的普通用户消息不受影响）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionTitleServiceInboxTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Services/SessionTitleService.cs tests/spine/Seeing.Agent.Tests/Services/
git commit -m "feat(inbox): exclude non-user inbox presentation from title intent"
```

### ✅ 检查点 CP1（人工审查）

- [ ] Abstractions 六个任务完成；`dotnet build src/abstractions/Seeing.Agent.Abstractions` 通过（全量 `Seeing.Agent.slnx` 因 Task 1.5 删 DTO 而暂失败，延至 **CP3**）。
- [ ] 确认无 `ISessionRuntime`/`ISessionDirectory` 命名冲突；契约签名与 spec §5 一致。
- [ ] **审查通过后进入阶段 2。**

---

# 阶段 2：Hosting 运行时核心

> **依赖**：CP1。**实际执行顺序（串行）**：`2.1 → 2.2 → 2.3 → 2.4 → 2.5`（2.2 的 SessionRuntime 需 2.1 的 ExecutionRunner 类型）。
>
> **⚠️ 构建策略**：阶段 2 的 2.1–2.4 只做增量编译检查（可能因 Task 1.5 的 DTO 删除而报 `ExecutionJobService` 错误，属预期）；**Hosting 全量编译验收在 Task 2.5**；**全量 `Seeing.Agent.slnx` 验收在 CP3**。

### Task 2.1: 抽出 ExecutionRunner（跑一轮）

**Files:**
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/ExecutionRunnerTests.cs`

**说明**：把 `ExecutionJobService.ProcessExecutionAsync`（`ExecutionJobService.cs:480-776`）的执行逻辑 + `SubmitAsync`（`:108-252`）的执行前职责（指令注入、模型/模式/思考强度落盘、cwd 设置、出站回填、标题）搬入 `ExecutionRunner`。**不改行为**，只搬移。`ExecutionRunner` 保留 `ChatEventTracker` 投影、命令/压缩/schema 结算；删除 `_loopScheduler.SetLoopBusy`（忙闲改由循环管理）。

> **测试代码由子 Agent 补全**：本计划中 Task 1.6/2.1/2.3/2.4 的「失败测试」为场景描述骨架，执行子 Agent 需据此补写可编译的实际断言代码（TDD 先失败）。

**职责边界**：
- `DeliverAsync`（SessionRuntime）负责：落盘入站消息、幂等、接管、启动循环。
- `RunOnceAsync`（ExecutionRunner）负责：本轮的模型/模式落盘、cwd、指令注入、出站回填、命令/压缩/标题/schema/事件投影/终态落盘。**标题门控按「本批含 Presentation=User」**（见 Task 2.3 Step 3.6）。
- `SessionRuntime` 另负责：执行记录与 Overview、`CleanupIdleSessions`、`ReconcileIncompleteTasksAsync`（从 `ChatOrchestrator.cs:94-143` 搬入）、`SubscribeEvents`/`GetBufferedEvents`。

- [ ] **Step 1: 写失败测试（验证 RunOnceAsync 产出终态记录）**

```csharp
using FluentAssertions;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

public class ExecutionRunnerTests
{
    [Fact]
    public void ExecutionRunner_ShouldBeConstructible() 
    {
        // 占位契约测试：确认类型存在且 public/internal 可见
        typeof(ExecutionRunner).Should().NotBeNull();
    }
}
```
> 注：`ExecutionRunner` 依赖大量 DI 服务，深度单测留待 Task 2.3 集成测试覆盖；本任务以「搬移后全量回归通过」为验收。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests` → 编译失败

- [ ] **Step 3: 搬移实现**

从 `ExecutionJobService.cs` 抽取出 `ExecutionRunner`（internal sealed，**实现 `IExecutionRunner` 接口以便 mock**）：
```csharp
/// <summary>本批输入摘要：供标题门控与历史构建。</summary>
internal sealed record InboxBatch(
    IReadOnlyList<SessionMessage> Inputs,
    bool HasUserInput,
    string? FirstUserText);

/// <summary>跑一轮执行的端口（供 SessionRuntime 注入与测试 mock）。</summary>
internal interface IExecutionRunner
{
    Task<ExecutionRecord> RunOnceAsync(ExecutionRecord record, InboxBatch batch, CancellationToken executionToken);
}

internal sealed class ExecutionRunner : IExecutionRunner
{
    public ExecutionRunner(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        IConfigSectionStore configStore,
        CompactionRunner compactionRunner,
        ILogger<ExecutionRunner> logger,
        IScenarioCatalog? scenarioCatalog = null)
    { /* ... */ }

    /// <summary>跑一轮执行：模型/模式落盘、指令注入、投影事件、终态落盘，返回终态记录。不做忙闲/循环管理。</summary>
    public async Task<ExecutionRecord> RunOnceAsync(
        ExecutionRecord record,
        InboxBatch batch,
        CancellationToken executionToken)
    { /* 原 SubmitAsync 执行前职责 + ProcessExecutionAsync 主体，去掉队列 Start/Complete/SetLoopBusy/inFlight 部分 */ }
}
```
> `SessionRuntime` ctor 注入 `IExecutionRunner`（非具体 `ExecutionRunner`）；DI 注册 `IExecutionRunner → ExecutionRunner`。测试可 mock `IExecutionRunner`。
搬移时删除：`queue.StartAsync()`、`queue.CompleteAsync()`、`_loopScheduler.SetLoopBusy(...)`、`_inFlightExecutions`、`ProcessQueueAsync`、`_executions`（迁 SessionRuntime）。`internal static` 助手（`BuildHistoryFromSession`/`TryBackfillSessionOutbound`/`ApplyInboundModelAndMode`/`MergeDynamicToolIds`/`ResolvePermissionAuthorizer`）随迁入 `ExecutionRunner`（保持 `internal` 可见性供测试）。

**同文件其他类型的归属**（`ExecutionJobService.cs` 内非方法类型，Task 2.5 删文件前必须迁移）：
- `TokenBudgetAutoCompactionPeek`（`:1684`，被 7+ 测试引用）→ 迁入 `src/hosting/Seeing.Agent.Hosting/Execution/TokenBudgetAutoCompactionPeek.cs`（独立文件，保持可见性）。
- `ExecutionHistoryEntry`（`:1675`）→ 迁入 `ExecutionRunner` 所在文件或独立文件。
- 生命周期方法 `CleanupExecutionAsync`/`CancelStaleQueueAsync`/`PublishCancelledComplete`/`Dispose`/`DisposeAsync` → `PublishCancelledComplete` 归 `SessionRuntime`；其余按需迁 `SessionRuntime`（清理/释放）或 `ExecutionRunner`（执行相关）。
- `AutoCompactionEnabled` 属性（`:1686`）→ 随 `TokenBudgetAutoCompactionPeek` 迁移。

- [ ] **Step 4: 增量编译检查**

Run: `dotnet build src/hosting/Seeing.Agent.Hosting`
Expected: 可能因 Task 1.5 删除 DTO 字段（`ExecutionJobService` 尚引用 `QueueLength` 等）报错——**属预期**，无需在此修复；Hosting 的最终编译验收在 Task 2.5。本步骤只需确认 `ExecutionRunner.cs` 自身无语法错误（构建输出中无 `ExecutionRunner` 相关错误）。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs tests/hosting/
git commit -m "refactor(inbox): extract ExecutionRunner.RunOnceAsync"
```

### Task 2.2: SessionDirectory + SessionRuntime 骨架 [SERIAL]

> 注：本任务仅实现 `SessionDirectory`（不依赖 `ExecutionRunner`）与 `SessionRuntime` **骨架**（构造函数声明 `ExecutionRunner` 参数即可，编译期只需类型存在——故需在 2.1 之后执行，或先以接口占位）。建议实际执行顺序：先 2.1，再 2.2。

**Files:**
- Create: `src/hosting/Seeing.Agent.Hosting/SessionDirectory.cs`
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Session/SessionDirectoryTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Hosting;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Session;

public class SessionDirectoryTests
{
    [Fact]
    public void SessionDirectory_ShouldImplement_ISessionDirectory()
    {
        typeof(ISessionDirectory).IsAssignableFrom(typeof(SessionDirectory)).Should().BeTrue();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests` → 编译失败

- [ ] **Step 3: 实现**

`SessionDirectory.cs`：把 `ChatOrchestrator` 的 CRUD 方法（`ChatOrchestrator.cs:182-298`：`GetSessionAsync`/`EnsureSessionAsync`/`ListSessionsAsync`/`CreateSessionAsync`/`DeleteSessionAsync`/`RenameSessionAsync`/`BranchSessionAsync`）搬入，实现 `ISessionDirectory`。方法名映射：`GetSessionAsync`→`GetAsync`、`EnsureSessionAsync`→`EnsureAsync`、`ListSessionsAsync`→`ListAsync`、`CreateSessionAsync`→`CreateAsync`，其余同名。

`SessionRuntime.cs` 骨架（本任务仅类型与构造，循环在 2.3）：
```csharp
public sealed class SessionRuntime : ISessionRuntime, IExecutionInFlightBoundary, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionExecutionState> _states = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<string, Task> _activeLoops = new();
    private readonly ConcurrentDictionary<string, byte> _stoppedSessions = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();
    private readonly SemaphoreSlim _globalConcurrency;
    private readonly IExecutionRunner _runner;

    // ctor 为 internal（IExecutionRunner 为 internal；public class 的 public ctor 不能暴露 internal 类型，否则 CS0051）
    internal SessionRuntime(IExecutionRunner runner, IExecutionEventPublisher publisher, IServiceProvider services, ExecutionOptions options, ILogger<SessionRuntime> logger) { /* ... */ }

    public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
        => throw new NotImplementedException(); // 2.3 实现
    // 其余接口方法先抛 NotImplementedException，2.3-2.4 逐个实现
}
```
> 注：DI 用工厂注册 `services.AddSingleton<SessionRuntime>(sp => new SessionRuntime(...))` 以调用 internal ctor（public 类的 internal ctor 不被 DI 直接解析）；见 Task 2.5。

- [ ] **Step 4: 增量编译检查**

Run: `dotnet build src/hosting/Seeing.Agent.Hosting`
Expected: 确认 `SessionDirectory.cs`/`SessionRuntime.cs` 无语法错误（其余 `ExecutionJobService` 报错属预期）。**单测执行延至测试迁移后（CP3）**。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/SessionDirectory.cs src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs tests/hosting/
git commit -m "feat(inbox): add SessionDirectory and SessionRuntime skeleton"
```

### Task 2.3: DeliverAsync + 消费循环（核心）

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/SessionRuntimeDeliverTests.cs`

- [ ] **Step 1: 写失败测试（合并消费 + 单飞 + 退出竞态）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class SessionRuntimeDeliverTests
{
    // 用假 ExecutionRunner（记录调用次数）验证：
    // 1. Inject 不触发循环
    // 2. Wake 触发一次循环
    // 3. 先 Inject x3 + Wake x1 → 一次循环消费全部（runner 调 1 次）
    // 4. 执行中到达的输入留到下一轮（runner 调 2 次）
    // 5. 并发 Deliver 只起一个循环（单飞）
    // 详见 spec §6.2 / §15
}
```
> 实现时提供 `internal` 测试钩子（如 `internal ExecutionRunner` 可替换、`internal int LoopRunCount`）以支撑这些断言；或将 `ExecutionRunner` 抽象为接口 `IExecutionRunner` 便于 mock。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 测试失败

- [ ] **Step 3: 实现 DeliverAsync + RunLoopAsync**

按 spec §6.1/§6.2 实现：
1. `DeliverAsync`：解析会话（`ISessionManager.GetOrLoadAsync`）→ 鉴权（同组/父链，复用 `SessionToolBase` 语义）→ 幂等预检（`inbox.idem:{key}`）→ 落盘消息（`Presentation` 映射 `Role`；`Assistant`/`None` 特殊处理；打 `inbox.*` 键，`Assistant` 直接 `consumed=true`）→ `Sender=Human` 且有父则 `SetOwner(Human)` → 按 Delivery 分发（`Notify` 只发 `InboxNoticeEvent`；`Inject` 不启动循环；`Wake` 在 gate 内清 `_stoppedSessions`/`control.stop_reason` 并按需启动循环）。
2. `RunLoopAsync`：gate 内「终止检查 → 收集 `consumed=false && presentation != assistant` → 构造 `InboxBatch`（Inputs/HasUserInput/FirstUserText）→ 标记 consumed=true + SaveAsync」；gate 外 `_globalConcurrency.WaitAsync()` → `_runner.RunOnceAsync(record, batch, token)` → 释放；catch OCE/Exception 继续；循环直到 pending 空或停止。
3. `_globalConcurrency`：`new SemaphoreSlim(MaxConcurrentExecutions <= 0 ? int.MaxValue : MaxConcurrentExecutions)`。
4. **写入窗口（spec §6.3）**：本批 `consumed=true` 必须在 gate 内、`RunOnceAsync` 启动**之前**完成并 `SaveAsync`；`RunOnceAsync` 内**零写** `inbox.*`/`control.*`（保持单一权威写入者：执行期 assistant/tool 消息仍由 `ChatEventTracker` 独占写）。
5. **Presentation → Role 映射**：`User`→`user`（平权）；`System`→`user`（正文经 `ISystemReminderRenderer.Wrap` 包 `system-reminder` 信封）；`Assistant`→`assistant`（直接落 assistant 消息，`consumed=true`）；`None`→不落盘（仅 `Notify` 用）。`inbox.presentation` 键写对应小写值。
6. **标题门控（spec §6.4）**：`RunOnceAsync` 内标题生成条件改为「`batch.HasUserInput` 且非命令」；不再用 `SkipUserMessagePersist`。`RunOnceAsync` 接收 `InboxBatch` 参数（Task 2.1 已定义）。
7. **Stop 二次校验（spec §6.2）**：`RunOnceAsync` 启动前在 gate 内**二次校验** `_stoppedSessions`（防 `StopAsync` 落在"终止检查通过后、执行体启动前"）；命中则本轮不启动、进入下轮终止检查。

- [ ] **Step 4: 增量编译检查**

Run: `dotnet build src/hosting/Seeing.Agent.Hosting`
Expected: 确认 `SessionRuntime.cs` 无语法错误。**单测执行延至测试迁移后（CP3）**。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs tests/spine/Seeing.Agent.Tests/Execution/
git commit -m "feat(inbox): implement DeliverAsync and consumption loop"
```

### Task 2.4: 控制面（Interrupt / Stop / 查询 / 订阅 / 清理 / 热重载边界）

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/SessionRuntimeControlTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class SessionRuntimeControlTests
{
    // 1. InterruptAsync：取消当前执行、consumed 不回退、循环继续（runner 被再调一次）
    // 2. StopAsync：取消 + 终止循环（runner 不再被调）+ 剩余未读保持 consumed=false + 级联子会话写 stop_reason
    // 3. Stop 后再次 Deliver：清除 _stoppedSessions/stop_reason 并重新启动循环
    // 4. WaitForIdleAsync：Stop 态视为 idle 直接返回
    // 5. GetOverview：CurrentExecution 运行中 / LastExecution 终态后保留 / PendingInputCount 正确
    // 6. CancelAllInFlightAsync：对所有活跃会话 Stop
    // 7. IExecutionInFlightBoundary：HasInFlight / ListInFlightExecutionIds
    // 8. ReconcileIncompleteTasksAsync：running/pending 且无活跃执行的 task 卡片标 cancelled
    // 9. SubscribeEvents/GetBufferedEvents 委托 IExecutionEventPublisher
    // 10. CleanupIdleSessions：超时空闲会话的运行态（_states/_gates/_stoppedSessions/记录）被回收
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 测试失败

- [ ] **Step 3: 实现**

按 spec §7：
- `InterruptAsync`：取会话当前记录 `Cts.Cancel()`；返回是否取消到。
- `StopAsync`：级联 `ISessionGroupManager.ListChildrenAsync` → 对每个子会话**先写** `control.stop_reason=cascaded_from_parent_stop` 落盘 → 加入 `_stoppedSessions` → 取消当前 `Cts`；返回取消数量。
- `WaitForIdleAsync`：轮询「会话不在 `_activeLoops` 且（`_stoppedSessions` 含 或 无未读 Wake）」→ 返回。
- `GetOverview`：从 `_states[sessionId]` 组装（`CurrentExecution` 运行中、`LastExecution` 终态后保留、`PendingInputCount`）。
- `CancelAllInFlightAsync`：对 `_activeLoops.Keys` 逐个 `StopAsync`。
- `ReconcileIncompleteTasksAsync`：从 `ChatOrchestrator.cs:94-143` 搬入（改经 `ISessionManager`/`ISessionRuntime.GetOverview`）。
- `SubscribeEvents`/`GetBufferedEvents`：委托 `IExecutionEventPublisher`。
- `CleanupIdleSessions`：定时器（`ExecutionOptions.CleanupInterval`）回收超时（`SessionIdleTimeout`）空闲会话的 `_states`/`_sessionGates`/`_stoppedSessions`/记录。
- 热重载边界（`IExecutionInFlightBoundary`）方法实现。

- [ ] **Step 4: 增量编译检查**

Run: `dotnet build src/hosting/Seeing.Agent.Hosting`
Expected: 确认 `SessionRuntime.cs` 无语法错误。**单测执行延至测试迁移后（CP3）**。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs tests/spine/Seeing.Agent.Tests/Execution/
git commit -m "feat(inbox): implement runtime control plane (interrupt/stop/query)"
```

### Task 2.5: Hosting 内迁移 + DI 重接 + 删除旧类

> **边界（关键）**：本任务完成**所有 Hosting 项目内的迁移**，使 `dotnet build src/hosting/Seeing.Agent.Hosting` 在 CP2 通过。跨项目调用点（Core/Tools.Session/Scheduler/Gateway/WebUI/TUI/测试）归阶段 3。
> Hosting 内必须迁移的调用方：`TaskTool`、`TaskStatusTool`（完整迁移，非仅构造签名）、删除 `AgentLoopSchedulerHostedService.cs`、删除 `ChatOrchestrator.cs`/`ExecutionJobService.cs`/`SessionExecutionQueue.cs`。

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/HostingServiceCollectionExtensions.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs`（完整迁移）
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskStatusTool.cs`（完整迁移）
- Delete: `src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs`
- Delete: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`
- Delete: `src/hosting/Seeing.Agent.Hosting/Execution/SessionExecutionQueue.cs`
- Delete: `src/hosting/Seeing.Agent.Hosting/Execution/AgentLoopSchedulerHostedService.cs`（含 `ExecutionSessionEventBus`）
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Chat/IChatOrchestrator.cs`
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionSubmitter.cs`
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionStatusProvider.cs`

- [ ] **Step 1: 迁移 TaskTool / TaskStatusTool（Hosting 内，完整）**

按 spec §12：
- `TaskTool.cs:135` `IsLoopBusy` → `_runtime.GetOverview(session.Id).HasActiveExecution`
- `TaskTool.cs:178` 删除预落盘；`:204,328` `SubmitAsync` → `DeliverAsync(Sender=Session(parent), Presentation=User, Wake)`
- `TaskTool.cs:220,349` `WaitForExecutionAsync` → `WaitForIdleAsync(childId[, context.CancellationToken])`
- `TaskTool.cs:222` `GetExecution` → `GetOverview(childId).LastExecution?.Status`
- `TaskTool.cs:256-296` 完成上报 → `DeliverAsync`（读子 `control.stop_reason` 决定是否投递；读 Owner 定 Wake/Notify）
- `TaskTool.cs:305-318,334-347` 删除父令牌级联回调
- `TaskTool` 构造注入改 `ISessionRuntime`（删 `IAgentLoopScheduler`/`IExecutionSubmitter`/`IExecutionStatusProvider`）
- `TaskStatusTool.cs:72,82,91,96,243` 按 §12 迁移（状态源 `current ?? LastExecution`；构造注入改 `ISessionRuntime`）

- [ ] **Step 2: 重接 DI（HostingServiceCollectionExtensions）**

按 spec §12：`:44` 删 `IChatOrchestrator`；`:52` 删 TaskTool 的 `IAgentLoopScheduler`；`:53,60` TaskTool/TaskStatusTool 依赖改 `ISessionRuntime`；`:54,61` 删 `IExecutionStatusProvider`；`:82` 删 `ISessionEventBus`/`ExecutionSessionEventBus`；`:91` 注册 `SessionRuntime`；`:92-94` 注册 `ISessionRuntime` + `IExecutionInFlightBoundary`（不注册已删接口）；`:97` 删 `AgentLoopSchedulerHostedService`；新增 `ISessionDirectory → SessionDirectory`。
```csharp
services.AddSingleton<ExecutionRunner>();
services.AddSingleton<IExecutionRunner>(sp => sp.GetRequiredService<ExecutionRunner>());
services.AddSingleton<SessionRuntime>(sp => new SessionRuntime(
    sp.GetRequiredService<IExecutionRunner>(),
    sp.GetRequiredService<IExecutionEventPublisher>(),
    sp, sp.GetRequiredService<ExecutionOptions>(),
    sp.GetRequiredService<ILogger<SessionRuntime>>()));
services.AddSingleton<ISessionRuntime>(sp => sp.GetRequiredService<SessionRuntime>());
services.AddSingleton<IExecutionInFlightBoundary>(sp => sp.GetRequiredService<SessionRuntime>());
services.AddSingleton<ISessionDirectory, SessionDirectory>();
```
> 用工厂注册（`new SessionRuntime(...)`）以调用其 `internal` ctor；**不要** `AddSingleton<SessionRuntime>()`（public 类的 internal ctor 不被 DI 解析）。

- [ ] **Step 3: 删除旧类文件**

```bash
git rm src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs \
       src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs \
       src/hosting/Seeing.Agent.Hosting/Execution/SessionExecutionQueue.cs \
       src/hosting/Seeing.Agent.Hosting/Execution/AgentLoopSchedulerHostedService.cs \
       src/abstractions/Seeing.Agent.Abstractions/Chat/IChatOrchestrator.cs \
       src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionSubmitter.cs \
       src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionStatusProvider.cs
```
> `IExecutionSubmitter` 的部分方法（`CancelAllInFlightAsync`/`WaitForExecutionAsync`）已并入 `ISessionRuntime`；`IExecutionStatusProvider.GetOverview/HasAnyActiveExecution` 已并入 `ISessionRuntime`。`AgentLoopSchedulerHostedService` 删除后其 `ExecutionSessionEventBus`（`ISessionEventBus` 适配器）一并移除。

- [ ] **Step 4: Hosting 构建通过**

Run: `dotnet build src/hosting/Seeing.Agent.Hosting`
Expected: 通过。**不构建 samples/WebUI/其他能力包**（其调用点迁移在阶段 3）。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "refactor(inbox): migrate Hosting callers, rewire DI, delete legacy classes"
```

### ✅ 检查点 CP2（人工审查）

- [ ] `dotnet build src/hosting/Seeing.Agent.Hosting` 通过。
- [ ] `SessionRuntime` 核心（Deliver/循环/控制）**代码完成**（单测执行延至 CP3——测试项目在阶段 3 迁移后才可编译）。
- [ ] 确认无「双写」：执行期未写 `inbox.*`/`control.*`。
- [ ] **审查通过后进入阶段 3。**

---

# 阶段 3：调用方迁移（可大规模并行）

> **依赖**：CP2。**可并行度**：Task 3.1–3.6 分属不同项目/文件，**全部可并行**。
>
> **⚠️ 构建策略（重要）**：本阶段是「合并式迁移批次」。各任务**不要求**独立全量构建通过（删除接口后，未迁移的调用点必然编译失败）。执行方式：
> 1. 各子 Agent 完成本任务文件改动后提交（**不做全量构建验收**，只做本地 grep 确认自己负责的文件无旧类型引用）。
> 2. 全部任务合并后，由 **Task 3.7（编译收口）** 统一构建 + 按错误清单补漏。
> 3. 各任务 Step 中的 `dotnet build Seeing.Agent.slnx 通过` 均改为「**合并后由 Task 3.7 验收**」。

### Task 3.1: Core 清理 [PARALLEL-C]

**Files:**
- Delete: `src/spine/Seeing.Agent.Core/Core/Scheduling/AgentLoopScheduler.cs`、`IAgentLoopScheduler.cs`
- Delete: `src/spine/Seeing.Agent.Core/Core/Events/ISessionEventBus.cs`（死契约，spec §14）
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`（删 `IAgentLoopScheduler?` 构造参数与字段 `:46,:63,:76`）
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（删 `:690` 注册）
> 注：`AgentLoopSchedulerHostedService.cs`（含 `ExecutionSessionEventBus`）已在 **Task 2.5** 删除，本任务不重复。

- [ ] **Step 1: 删除 + 修改**（按上列）
- [ ] **Step 2:** grep 确认本任务涉及文件无 `IAgentLoopScheduler` 残留
- [ ] **Step 3: 提交** `git commit -m "refactor(inbox): remove AgentLoopScheduler and idle-resume"`

### Task 3.2: WebUI + TUI 源码迁移 [PARALLEL-C]

> TaskTool/TaskStatusTool 的迁移已在 Task 2.5（Hosting 内）完成。本任务覆盖 **samples 源码**（WebUI/TUI），使阶段 3 全量构建可达。UI **新功能**（放开子会话输入、中断/推送/交还、来源徽章）在阶段 4。

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`（`:50` `IChatOrchestrator` → `ISessionRuntime`）
- Modify: `samples/Seeing.Agent.WebUI/State/AppState.cs`（会话列表 → `ISessionDirectory`）
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（`:30` 注入 + 调用点 + `:478/1064/1378` 队列字段）
- Modify: `samples/Seeing.Agent.WebUI/Pages/Index.razor`、`Session.razor`、`Conference.razor`、`SessionsPage.razor`、`Components/MainLayout.razor:15`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor:69`、`State/SessionState.cs:176`、`Services/SessionWindowContext.cs:97`（`QueueLength`/`QueuePosition` → `PendingInputCount`）
- Modify: `samples/Seeing.Agent.Tui/Services/TuiChatEngine.cs`（`:56,132,2130` `IExecutionSubmitter`；`:66,142,1919` `IExecutionStatusProvider`）
- Modify: `samples/Seeing.Agent.Tui/Services/TuiSessionController.cs`（`:15,21,26` 注入；`:92` 返回类型；`:107` `QueuePosition`→`PendingInputCount`；`:120,127` `CancelAsync`/`CancelBySessionAsync`→`StopAsync`）
- Modify: `samples/Seeing.Agent.Tui/Services/TuiEventPump.cs`、`TuiViewState.cs:55`、`StatusBarView.cs:123-124`、`Program.cs:136`

- [ ] **Step 1: 按 spec §12 迁移 WebUI/TUI 源码**
  - `IChatOrchestrator.SubmitAsync` → `ISessionRuntime.DeliverAsync`
  - `IChatOrchestrator` CRUD → `ISessionDirectory`
  - `IExecutionSubmitter.*` → `ISessionRuntime.*`
  - `IExecutionStatusProvider.GetOverview` → `ISessionRuntime.GetOverview`
  - `ExecutionSubmitResult` → `InboxResult`（TUI `SubmitAsync:92`）
- [ ] **Step 2:** grep 确认 samples 无旧类型残留
- [ ] **Step 3: 提交** `git commit -m "refactor(inbox): migrate WebUI/TUI source to SessionRuntime"`

### Task 3.3: Tools.Session（handoff + 新工具）[PARALLEL-C]

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs`（删 `:125-130` 预落盘；`:132` → `DeliverAsync`；`:143-150` 回滚改 `StopAsync(successor.Id)` + `RollbackAsync`）
- Modify: `src/capabilities/Seeing.Agent.Tools.Session/SessionToolsModule.cs`（`:63` 门控改 `ISessionRuntime`）
- Create: `src/capabilities/Seeing.Agent.Tools.Session/SessionInboxSendTool.cs`（`session_inbox_send`）
- Create: `src/capabilities/Seeing.Agent.Tools.Session/SessionTakeoverTool.cs`（`session_takeover` → `SetOwner(Human)`）
- Create: `src/capabilities/Seeing.Agent.Tools.Session/SessionReleaseTool.cs`（`session_release` → `SetOwner(Parent)`）
- Test: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionInboxToolsTests.cs`

- [ ] **Step 1: 迁移 handoff + module 门控**
- [ ] **Step 2: 新增 3 工具**（走 `SessionToolBase.AuthorizeAsync`；`session_inbox_send` 参数 `session_id`/`body`/`delivery?`）
  - 权限（spec §11）：三工具均走 `tool.execute` 门（`SessionToolBase.AuthorizeAsync`）；目标范围默认同组 + 父链；跨组经 `IPermissionAuthorizerFactory`（缺失则放行）。
  - `session_takeover`：对目标 `SetOwner(Human)` 并 `SaveAsync`；`session_release`：`SetOwner(Parent)`。
- [ ] **Step 3:** grep 确认本任务涉及文件无 `IExecutionSubmitter` 残留
- [ ] **Step 4: 提交** `git commit -m "feat(inbox): migrate handoff, add session_inbox_send/takeover/release tools"`

### Task 3.4: Scheduler 通知收敛 [PARALLEL-C]

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`

- [ ] **Step 1: `SessionScheduleDispatcher` 改为 `DeliverAsync`**
  - reminder（`:58-62`）→ `Deliver(Sender=System, Presentation=System, Delivery=Inject)`
  - 产出（`:64-66`）→ `Deliver(Sender=System, Presentation=Assistant, Delivery=Inject)`
- [ ] **Step 2:** grep 确认本任务涉及文件无 `AddMessageAsync` 直写残留
- [ ] **Step 3: 提交** `git commit -m "refactor(inbox): converge scheduler dispatch to DeliverAsync (no wake)"`

### Task 3.5: Gateway 全链路会话级 [PARALLEL-C]

**Files:**
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs`、`GatewayEndpoints.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayRunTracker.cs`
- Modify: `src/gateway/Seeing.Gateway.Client/IGatewayClient.cs`、`HttpGatewayClient.cs`、`WebSocketGatewayClient.cs`
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelBridge.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelBridge.cs`

- [ ] **Step 1: 按 spec §12 + §18 决策 24 迁移**
  - `SubmitAsync` → `DeliverAsync`；提交失败判定改 `!Success`
  - 订阅 `SubscribeAsync(sessionId)` 会话级（到空闲/超时）；`GatewayRunTracker` 改 session 键
  - 渠道桥守卫改 `!Success`；`CancelAsync(executionId)` → `StopAsync(sessionId)`
  - 协议帧/HTTP body `ExecutionId` → `sessionId`；`QueuePosition` → `PendingInputCount`
  - **取消/错误横幅（spec §9）**：直写 transient system → `Deliver(Sender=System, Presentation=None, Delivery=Inject)`（保留持久化、不唤醒）
  - **`GatewayScheduleDispatcher.cs:52,56`**（直写 `AddMessageAsync` 的调度产出）→ `Deliver(Sender=System, Presentation=Assistant/System, Delivery=Inject)`（spec §9）
- [ ] **Step 2:** grep 确认 gateway 目录无 `executionId` 取消残留
- [ ] **Step 3: 提交** `git commit -m "refactor(inbox): migrate gateway to session-scoped deliver/subscribe"`

### Task 3.6: 测试迁移 [PARALLEL-C]

**Files:**（删除接口后**所有**引用旧类型的测试；以下为已知清单，执行时以 `dotnet build` 编译错误为准补全）
- Modify: `tests/spine/Seeing.Agent.Tests/Tools/TaskToolAutoApproveInheritanceTests.cs`、`TaskToolTests.cs`
- Modify: `tests/spine/Seeing.Agent.Tests/Execution/ExecutionAbstractionsContractsTests.cs`（删 `IExecutionSubmitter`/`IExecutionStatusProvider` 契约断言，改为 `ISessionRuntime`）
- Modify: `tests/spine/Seeing.Agent.Tests/App/IChatOrchestratorApiSurfaceTests.cs`（改 `ISessionRuntime`/`ISessionDirectory` 表面断言）
- Modify: `tests/spine/Seeing.Agent.Tests/App/Execution/*`（约 7 个 ExecutionJobService 相关测试）
- Modify: `tests/spine/Seeing.Agent.Tests/App/ChatOrchestrator*Tests.cs`（约 4 个）、`App/ExecutionJobService*.cs`（约 4 个）
- Modify: `tests/spine/Seeing.Agent.Tests/Invariants/ConfigPromptTaskTests.cs`（`IExecutionSubmitter`/`IExecutionStatusProvider` 参数断言）
- Modify: `tests/hosting/Seeing.Agent.Hosting.Tests/ChatOrchestratorServiceExtensionsTests.cs`
- Modify: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionToolTestHarness.cs`（`StubExecutionSubmitter` → stub `ISessionRuntime`）、`SessionHandoffToolTests.cs`、`SessionToolsModuleTests.cs`
- Modify: `tests/apps/Seeing.Agent.Tui.Tests/Services/TuiCommandRouterTests.cs`、`TuiChatEngineWiringTests.cs`
- Modify: `tests/apps/Seeing.Agent.WebUI.Tests/Services/*`（`IChatOrchestrator` mock → `ISessionRuntime`/`ISessionDirectory`）
- Modify: `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresentationTests.cs`
- Modify: `tests/spine/Seeing.Agent.Invariants.Tests/Modules/DynamicToolSettlementTests.cs`（用 `ExecutionJobService.MergeDynamicToolIds`）
- Modify: `tests/spine/Seeing.Agent.Tests/Invariants/SchemaScenarioTests.cs`

- [ ] **Step 1: 迁移测试**（按上列 + 编译错误补全）
  - 本任务与 3.1–3.5 同批并行；测试迁移实质依赖 3.1–3.5 的最终签名。**先按已知清单迁移，最终以 Task 3.7 编译错误补全**。
- [ ] **Step 2:** grep 确认 tests/ 无 `IChatOrchestrator`/`IExecutionSubmitter`/`IExecutionStatusProvider`/`ExecutionJobService`/`SessionExecutionQueue`/`IAgentLoopScheduler` 残留
- [ ] **Step 3: 提交** `git commit -m "test(inbox): migrate tests"`

### Task 3.7: 编译收口（串行，最后执行）

**Files:** 视编译错误而定

- [ ] **Step 1: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 逐条修复剩余编译错误（含阶段 4 之前的所有调用点），直至通过。

- [ ] **Step 2: 全量回归**

Run（逐个）:
```
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet test tests/primitives/Seeing.Session.Tests
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Session.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Session.Tests.dll
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet build tests/gateway/Seeing.Gateway.Client.Tests && dotnet vstest tests/gateway/Seeing.Gateway.Client.Tests/bin/Debug/net10.0/Seeing.Gateway.Client.Tests.dll
dotnet build tests/gateway/Seeing.Gateway.QQ.Tests && dotnet vstest tests/gateway/Seeing.Gateway.QQ.Tests/bin/Debug/net10.0/Seeing.Gateway.QQ.Tests.dll
dotnet build tests/gateway/Seeing.Gateway.WeCom.Tests && dotnet vstest tests/gateway/Seeing.Gateway.WeCom.Tests/bin/Debug/net10.0/Seeing.Gateway.WeCom.Tests.dll
dotnet test tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
```
Expected: 全绿（或修复至全绿）。

- [ ] **Step 3: 提交** `git commit -m "fix(inbox): compile closure after migration"`

### ✅ 检查点 CP3（人工审查）

- [ ] `dotnet build Seeing.Agent.slnx` 通过（由 Task 3.7 收口）。
- [ ] 全量回归通过。
- [ ] 确认无 `IChatOrchestrator`/`IExecutionSubmitter`/`IExecutionStatusProvider`/`IAgentLoopScheduler` 残留引用。
- [ ] **审查通过后进入阶段 4。**

---

# 阶段 4：UI 放开子会话（WebUI + TUI 可并行）

> **依赖**：CP3。**可并行度**：Task 4.1、4.2 可并行。

### Task 4.1: WebUI 新功能 [PARALLEL-D]

> 源码迁移（旧接口 → `ISessionRuntime`/`ISessionDirectory`）已在 Task 3.2 完成。本任务只做 UI 新功能。

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs`（`ApplyCapabilities`：`IsReadOnly = isChild` → `false`）
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（输入放开；「中断并执行」= `InterruptAsync`；「取消执行」= `StopAsync`；「推送」按钮；「交还父会话」）
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`（渲染 `InboxNoticeEvent`）
- Modify: `samples/Seeing.Agent.WebUI/Components/Messaging/*`（来源徽章：非人类来源视觉区分 + 悬停 `inbox.*`）

- [ ] **Step 1: 放开子会话输入 + 发送走 `DeliverAsync`**
- [ ] **Step 2: 中断/取消/推送/交还按钮**
  - **分离为独立会话**（既有 detach / `ForkSessionAsync`）路径：接到 `SetOwner(Self)`（spec §8.1 流转表）。
- [ ] **Step 3: 来源徽章 + `InboxNoticeEvent` 渲染**
- [ ] **Step 4:** `dotnet build samples/Seeing.Agent.WebUI` 通过
- [ ] **Step 5: 提交** `git commit -m "feat(inbox): enable subagent input, interrupt/push/release, source badges in WebUI"`

### Task 4.2: TUI 新功能 [PARALLEL-D]

> 源码迁移已在 Task 3.2 完成。本任务只做 TUI 侧的交互新功能（如需要）。

**Files:**
- Modify: `samples/Seeing.Agent.Tui/Services/*`（若需中断/取消交互）

- [ ] **Step 1: 按需补 TUI 交互**
- [ ] **Step 2:** `dotnet build samples/Seeing.Agent.Tui` 通过
- [ ] **Step 3:** `dotnet build tests/apps/Seeing.Agent.Tui.Tests && dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll`
- [ ] **Step 4: 提交** `git commit -m "feat(inbox): TUI interaction updates"`

### ✅ 检查点 CP4（人工审查）

- [ ] WebUI + TUI 构建通过。
- [ ] 手测：子会话可输入、发消息即接管、中断/取消/推送/交还可用、来源徽章正确。
- [ ] **审查通过后进入阶段 5。**

---

# 阶段 5：端到端验证与文档

> **依赖**：CP4。

### Task 5.1: 端到端集成测试

**Files:**
- Create: `tests/e2e/Seeing.Agent.E2E.Tests/Seeing.Agent.E2E.Tests.csproj`（新建项目：xUnit + 引用 Hosting/Abstractions/Session）
- Modify: `Seeing.Agent.slnx`（加入新项目）
- Create: `tests/e2e/Seeing.Agent.E2E.Tests/SessionInboxE2ETests.cs`

- [ ] **Step 1: 写关键场景测试**
  - A. 人介入子会话并接管（Owner→Human）
  - B. 子会话完成（未接管）→ 唤醒父
  - C. 子会话完成（已接管）→ 父仅 UI 通知
  - D. 先插三条再唤醒 → 一次消费
  - E. 中断后继续
  - F. 停止（回滚/删除）
- [ ] **Step 2: 建项目 + 加 slnx + 构建**

Run: `dotnet build tests/e2e/Seeing.Agent.E2E.Tests && dotnet vstest tests/e2e/Seeing.Agent.E2E.Tests/bin/Debug/net10.0/Seeing.Agent.E2E.Tests.dll`
- [ ] **Step 3: 提交** `git commit -m "test(inbox): add end-to-end scenarios"`

### Task 5.2: 文档更新

**Files:**
- Modify: `AGENTS.md`（WHERE TO LOOK：新增 SessionRuntime/ISessionRuntime 条目；删除 ExecutionJobService/ChatOrchestrator 引用）
- Modify: `docs/architecture/02-modules.md`、`13-single-writer-release-notes.md`（如需）
- Create: `docs/architecture/14-session-inbox-runtime-release-notes.md`

- [ ] **Step 1: 更新架构文档**
- [ ] **Step 2: 提交** `git commit -m "docs(inbox): add release notes and update architecture index"`

### ✅ 检查点 CP5（最终审查）

- [ ] 全量构建 + 全量测试通过。
- [ ] 无 TODO/占位符残留。
- [ ] 架构文档与实现一致。

---

## 并行执行编排建议

| 阶段 | 可并行批次 | 子 Agent 数 | 检查点 |
|------|-----------|------------|--------|
| 1 | 1.1–1.4、1.6（并行）→ 1.5（最后，串行） | 5 → 1 | CP1 |
| 2 | 2.1 → 2.2 → 2.3 → 2.4 → 2.5（串行） | 1 | CP2 |
| 3 | Task 3.1–3.6（并行）→ 3.7（收口，串行） | 6 → 1 | CP3 |
| 4 | Task 4.1 ∥ 4.2 | 2 | CP4 |
| 5 | 串行 | 1 | CP5 |

**审查策略**：仅 CP1–CP5 做人工审查；批次内任务由子 Agent 自测（TDD）后直接提交，不逐步审查。阶段 3 为「合并式迁移」：3.1–3.6 各自提交后由 3.7 统一编译收口。阶段 1 的 1.5（DTO 删除）破坏编译，须最后执行。

---

## 已知边界（实施时勿误判为 bug）

- **无 steer**：执行中 Wake 只能排队到下一轮。
- **不自动恢复**：重启后未读 Wake 保留为未读。
- **中断的半截历史**：user 消息无回复，后续轮 LLM 同时看到（spec §7）。
- **前台 task 父 Interrupt**：返回失败但子会话继续运行且无完成上报（spec §8.2）。
- **接管粒度会话级**：无法部分接管。
