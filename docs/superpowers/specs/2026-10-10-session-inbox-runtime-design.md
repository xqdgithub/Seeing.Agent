# 会话入站运行时（SessionInbox / SessionRuntime）设计

**日期:** 2026-10-10
**状态:** 待评审
**取代:** `2026-10-10-session-talk-and-ownership-design.md`（Talk 命名与 pending 布尔模型被本设计取代）
**范围:** `Seeing.Agent.Abstractions`（Runtime / Catalog / Inbox / SessionControl 契约）+ `Seeing.Agent.Hosting`（SessionRuntime 实现 / 消费循环 / ExecutionRunner / TaskTool）+ `Seeing.Agent.Core`（删除 AgentLoopScheduler 三件套 / 保留 ExecutionEventPublisher）+ `Seeing.Agent.Tools.Session`（会话工具）+ `Seeing.Agent.Scheduler`/`Gateway`（系统通知收敛）+ `Seeing.Agent.WebUI`（放开子会话 / 中断 / 推送 / 来源徽章）
**触发:** 子会话当前只读；"往会话放消息"存在三套并行的激活机制（执行队列 / synthetic+idle resume / 直写），语义重叠、状态双源、判定脆弱；缺"中断 / 接管 / 主动推送"
**约束:** 按产品方惯例，本设计文档不自动提交 git

---

## 1. 背景与问题

现状（已由代码核实）存在**三条各自为政的"往会话放消息并激活"路径**：

| 路径 | 入口 | 行为 |
|------|------|------|
| A. 执行队列 | `IChatOrchestrator.SubmitAsync` → `IExecutionSubmitter.SubmitAsync` → `SessionExecutionQueue` | 立即落盘 user 消息，空闲即跑、忙则排队（每会话串行，≤10） |
| B. 系统注入 | `IAgentLoopScheduler.InjectSyntheticAsync` + `TryResumeWhenIdleAsync` | 落 `synthetic=true` user 消息，空闲时经 resume handler 空输入续跑 |
| C. 直写 | Scheduler/Gateway dispatcher、`SessionHandoffTool` | 直接 `AddMessageAsync`，不经执行引擎（部分再补 Submit） |

由此产生四个架构病灶：

1. **激活语义重叠**：A（队列项）与 B（会话消息 metadata + busy 标志）都表达"消息待执行"，却各有一套实现。
2. **忙闲状态双源**：`AgentLoopScheduler._busy` 字典（`AgentLoopScheduler.cs:13`）与队列活跃态是两份状态，靠 `ExecutionJobService.cs:513` / `:731` 手动同步，存在不一致窗口。
3. **pending 判定脆弱**：`HasPendingSyntheticUserMessage`（`AgentLoopScheduler.cs:146-169`）以"最后一条 assistant 之后的 synthetic 消息"的**文本位置**判定，依赖消息顺序与文本标记。
4. **唤醒靠旁路**：`SetLoopBusy(false)` 内 `Task.Run` fire-and-forget 补唤醒（`AgentLoopScheduler.cs:43-53`），失败仅记日志；且子会话完成**无条件**唤醒父（`TaskTool.cs:256-296`），无"接管"概念。

另有产品能力缺口：

5. **子会话不可介入**：WebUI `SessionWindowContext.ApplyCapabilities` 里 `IsReadOnly = isChild`（`SessionWindowContext.cs:78-94`）硬关子会话输入/提交。
6. **无中断 / 接管 / 主动推送**：执行中追加消息只能排队到下一轮（`SessionExecutionQueue` 每会话串行）；接管语义、把子会话产出主动推给父会话的能力均缺失。

## 2. 目标 / 非目标

### 目标

| 目标 | 验收要点 |
|------|----------|
| 统一写入 + 激活 | 所有**入站输入**经唯一入口 `ISessionRuntime.DeliverAsync` 落盘并决定激活 |
| 唯一驱动入口 | "驱动会话执行"只此一口；旧 `IExecutionSubmitter.SubmitAsync` 对外移除 |
| 合并消费 | 忙时到达的多条输入，恢复后**一次消费**（先插三条再唤醒一次跑完） |
| 子会话可介入 | WebUI 放开子会话输入；人发消息即接管 |
| 硬停中断 | 运行中可强制中断当前执行，已消费批次不回退，继续消费剩余 |
| 接管语义 | 子会话被接管后，完成时**不唤醒**父 agent，父仅得 UI 通知 |
| 主动推送 | 人可把子会话**当前产出**手动 deliver 给指定会话 |
| 依赖方向 | 契约落 Abstractions，能力包只依赖 Abstractions |

### 非目标

- **不做 steer**：执行中到达的输入留给下一轮消费（不做 in-loop 插入）。
- **不做重启自动恢复**：进程重启后已落盘的未读 Wake 保留为未读，等下次投递/显式触发才跑。
- **不改** `SessionGroup` 谱系模型（Child/Fork/Handoff）。
- **不改**执行期"单一权威写入者"契约（执行期 assistant/tool 消息写入仍由 `ChatEventTracker` 独占）。
- **不引入**跨进程写协调。
- **不改造** `AgentExecutor` 的 in-loop ephemeral 提醒（Todo 提醒只进本次 Loop、不落盘）。
- **不改** cron/heartbeat 的"产出写回不唤醒"行为（见 §9）。

## 3. 核心模型：两轴正交

### 3.1 轴一 · 写入（Inbox）

| 维度 | 取值 | 语义 |
|------|------|------|
| **Sender**（谁在说） | `Human` / `Session(来源会话 Id)` / `System(来源标签)` | 身份，决定接管与来源标注 |
| **Presentation**（进上下文以什么身份） | `User` / `System`（system-reminder 信封）/ `Assistant`（外部产出写回）/ `None`（不进 LLM 历史，但可持久化展示） | 是否进 LLM 历史、是否算"意图用户消息" |

> `Assistant` 仅用于**外部执行产出写回**（cron/heartbeat 任务结果），视为已消费、不参与未读判定、不唤醒（见 §9）。

### 3.2 轴二 · 激活（Mailbox）

| 维度 | 取值 | 语义 |
|------|------|------|
| **Delivery**（何时跑） | `Inject`（只落不跑）/ `Wake`（落并激活消费）/ `Notify`（只出 UI 通知，不落盘） | 是否触发消费循环 |

### 3.3 场景映射

| 场景 | Sender | Presentation | Delivery |
|------|--------|--------------|----------|
| 人给会话发消息 | Human | User | Wake |
| 系统预插（先插后唤醒） | System / Session | System / User | Inject |
| 子会话完成上报父会话 | Session(child) | System | Wake |
| 子会话完成但已被接管 | Session(child) | None | Notify |
| cron/heartbeat 的用户输入 reminder | System | System | Inject（不唤醒） |
| cron/heartbeat 的产出写回 | System | Assistant | Inject（不唤醒，视为已消费） |
| Gateway 取消/错误横幅 | System | None | Inject（持久化，不唤醒） |
| 人手动推送子会话产出 | Session(child) | System | Wake |

### 3.4 接管（Ownership，独立概念）

`Owner` 表示"子会话完成结果的**接收者**"，**只做完成上报收件人**，与投递正交：

- `Parent`（默认，SubAgent 由父委托）
- `Human`（已被人接管）
- `Self`（已分离为独立会话）

> 准入（谁能投 Wake）**不归 Owner**，交给既有 `SessionToolBase` 同组授权；父 agent 属同组"自己人"，接管后仍可正常驱动子会话（如 task 续跑）。

## 4. 架构契约与分层

### 4.1 契约三分

| 契约 | 职责 | 内容 |
|------|------|------|
| **`ISessionRuntime`** | 会话运行时（内聚系统，对外唯一入口） | `DeliverAsync`（唯一驱动）、`InterruptAsync`（停当前轮、循环继续）、`StopAsync`（停当前轮+终止循环、级联子会话）、`CancelAllInFlightAsync`、`WaitForIdleAsync`、`GetOverview`/`HasAnyActiveExecution`、`SubscribeEvents`/`GetBufferedEvents`/`ReconcileIncompleteTasksAsync` |
| **`ISessionDirectory`** | 会话目录（CRUD） | `GetAsync`/`EnsureAsync`/`ListAsync`/`CreateAsync`/`DeleteAsync`/`RenameAsync`/`BranchAsync` |
| **SessionInbox** | 内部子系统（**不对外**） | 写入标注、consumed 标记、消费循环编排 |

- `IChatOrchestrator` **删除**，职责拆入 `ISessionRuntime`（执行/控制）+ `ISessionDirectory`（CRUD）。
- `IExecutionSubmitter` **删除**（`SubmitAsync` 降为运行时内部；`Cancel`/`Wait` 等并入 `ISessionRuntime`）。
- `IExecutionStatusProvider` **删除**（`GetOverview`/`HasAnyActiveExecution` 并入 `ISessionRuntime`）；消费者（TaskTool/TaskStatusTool/TUI）改走 `ISessionRuntime`。
- `IExecutionInFlightBoundary` **保留**（热重载边界小契约），由 `SessionRuntime` 实现。

### 4.2 分层与依赖方向

> **Runtime 契约在 Abstractions，实现（`SessionRuntime`）在 Hosting。**
>
> **依赖方向硬约束**：现有依赖为 `Abstractions ← Core ← Hosting`（Hosting 引用 Core，反向不成立）。"跑一轮"需要 `ChatEventTracker`（执行期唯一写者）、命令处理、压缩、标题、schema 结算——**均在 Hosting**。故消费循环与 `SessionRuntime` **必须在 Hosting**；若放 Core，Core 须反向引用 Hosting，形成环。这不是偏好，是硬约束。
>
> **能力包/Core 如何驱动**：契约 `ISessionRuntime` 落 Abstractions，Core 与能力包均引用 Abstractions，注入接口即可驱动，**无需引用 Hosting 实现**；DI 在宿主组合根解析。Core 不加空实现（未接 Hosting 的纯 Core 宿主无执行引擎，与现状一致——`ExecutionJobService` 本就在 Hosting）。
>
> 理由（为何下沉 Abstractions）：能力包（`Seeing.Agent.Tools.Session`）必须能调用投递，但碰不到 Hosting 实现。

| 层 | 新增/改造 |
|----|-----------|
| **Abstractions** | `Runtime/`（`ISessionRuntime`、`ISessionDirectory`、`InboxMessage`、`InboxResult`、`InboxSender`、`InboxDelivery`、`InboxPresentation`、`InboxMetadataKeys`）+ `Sessions/`（`SessionOwner`、`SessionControl`、`SessionControlKeys`、`SessionControlExtensions`）+ 新事件 `InboxNoticeEvent` |
| **Hosting** | 新增 `SessionRuntime`（唯一入口 + 消费循环 + 执行记录 + 取消/中断 + 热重载边界）；抽出 `ExecutionRunner`（internal，跑一轮执行体）；新增 `SessionDirectory`（CRUD 实现）；`TaskTool` 完成上报改走 `DeliverAsync` + 读 Ownership；**删除** `ExecutionJobService` / `SessionExecutionQueue` / `ChatOrchestrator` |
| **Core** | **删除** `AgentLoopScheduler` 三件套（`_busy`/`InjectSyntheticAsync`/`TryResumeWhenIdleAsync`/`HasPendingSyntheticUserMessage`）与 `IAgentLoopScheduler`；**保留** `ExecutionEventPublisher` |
| **Capability** | `Seeing.Agent.Tools.Session` 增 `session_inbox_send` / `session_takeover` / `session_release` |
| **Scheduler / Gateway** | 通知收敛为 `Sender=System` 的 `DeliverAsync` |
| **WebUI** | `SessionWindowContext` 放开子会话输入；人提交走 `DeliverAsync`；中断/推送/交还；`InboxNoticeEvent` 渲染；来源徽章 |

### 4.3 彻底重拆 `ExecutionJobService`（职责归属）

现有 `ExecutionJobService`（~1700 行，实现 `IExecutionSubmitter`/`IExecutionStatusProvider`/`IExecutionInFlightBoundary`）职责按目标架构重新归属：

| 现有职责 | 归属 |
|----------|------|
| `SubmitAsync` 落盘 user 消息 + 入队 | **删除**，并入 `SessionRuntime.DeliverAsync` |
| `SessionExecutionQueue`（当前项/排队/推进/取消推进） | **删除**，替换为消费循环 |
| `ProcessExecutionAsync`（命令/压缩/标题/schema 结算/ChatEventTracker 投影/终态/落盘） | **抽出为 `ExecutionRunner.RunOnceAsync`**（internal） |
| `_executions` 记录 + `GetOverview`/`HasAnyActiveExecution` | 迁入 `SessionRuntime` |
| `CancelAsync`/`CancelBySessionAsync` | **删除**，收敛为 `InterruptAsync` / `StopAsync`（§5.1） |
| `CancelAllInFlightAsync` | 迁入 `SessionRuntime`（对所有活跃会话 Stop） |
| `WaitForExecutionAsync` | 改为 `WaitForIdleAsync(sessionId)`（等待循环空闲） |
| `GetExecution(executionId)` | **删除全局查询**（不再有按 executionId 的全局查询/取消）；`ExecutionRecord.ExecutionId` 字段保留（事件仍携带） |
| `GetBufferedEvents`/`SubscribeEvents` | 迁入 `SessionRuntime`（委托 `IExecutionEventPublisher`） |
| `CleanupIdleSessions`（空闲回收） | 迁入 `SessionRuntime`（回收空闲消费循环/记录/`_stoppedSessions`） |
| `MaxConcurrentExecutions`（全局并发上限） | 迁入 `SessionRuntime`（消费循环启动执行前检查，超限延迟重试） |
| `IExecutionInFlightBoundary`（热重载边界） | `SessionRuntime` 实现 |
| `ChatEventTracker` 投影（单写者） | 保留于 `ExecutionRunner` 内（执行期唯一写者不变） |

**Overview 字段语义**（删队列后）：

- `SessionExecutionOverview.CurrentExecution`：保留（当前轮执行记录；**空闲时为 null**）。
- `SessionExecutionOverview.LastExecution`：**新增**（该会话最近一轮执行记录，**终态后保留**至下一轮开始；**空闲态终态判定的唯一来源**，供 TaskTool 监听器与 TaskStatusTool 读取）。
- `SessionExecutionOverview.QueueLength`：**移除**（无排队执行概念）。
- `SessionExecutionOverview.QueuedExecutions`：**移除**；`HasQueuedExecutions` 一并移除。
- `SessionExecutionOverview.PendingInputCount`：**新增**（会话未消费输入数；仅用于 UI 展示，**不用于终态判定**）。
- `ExecutionRecord.QueuePosition`：**移除**（替代为会话级 `PendingInputCount`）；`ExecutionRecord.ExecutionId` **保留**（事件/协议仍携带）。
- `IExecutionStatusProvider.GetExecution(executionId)`：**接口整体删除**（`GetOverview`/`HasAnyActiveExecution` 并入 `ISessionRuntime`；`GetExecution` 经 `GetOverview(sessionId).LastExecution` 取代）。
- 受影响调用点（`TaskStatusTool.cs:72,91,96,109,116,243`、`SessionWindow.razor:478/1378`、`GatewayOrchestratorV2.cs:64`、`TuiSessionController.cs:107`、`TuiChatEngine.cs:66,142,1919`）随 §12 迁移。

**反上帝对象**：`SessionRuntime` 只管"入站 + 消费循环 + 执行记录/控制 + 事件订阅"；执行细节全在 `ExecutionRunner`；事件发布在 `ExecutionEventPublisher`；CRUD 在 `SessionDirectory`。

## 5. 契约层设计

### 5.1 Runtime 契约（`Seeing.Agent.Abstractions.Runtime`）

```csharp
public enum InboxSenderKind { Human, Session, System }

public sealed record InboxSender
{
    public required InboxSenderKind Kind { get; init; }
    /// <summary>Session：来源会话 Id；System：子系统标签（task/cron/heartbeat/gateway…）。</summary>
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>进上下文身份：User=平权用户消息；System=system-reminder 信封；Assistant=外部产出写回；None=不落历史。</summary>
public enum InboxPresentation { User, System, Assistant, None }

/// <summary>激活语义：Inject=只落不跑；Wake=落并激活消费；Notify=只出 UI 通知。</summary>
public enum InboxDelivery { Inject, Wake, Notify }

public sealed record InboxMessage
{
    public required string TargetSessionId { get; init; }
    public required InboxSender Sender { get; init; }
    /// <summary>Inject/Wake 时的正文；Notify 可空。</summary>
    public string? Body { get; init; }
    public InboxDelivery Delivery { get; init; } = InboxDelivery.Wake;
    public InboxPresentation Presentation { get; init; } = InboxPresentation.User;
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
    /// <summary>System 呈现的来源标签，复用 SystemReminder.Sources。</summary>
    public string? SourceTag { get; init; }
    /// <summary>System 呈现的种类，复用 SystemReminder.Kinds。</summary>
    public string? Kind { get; init; }
    /// <summary>幂等键：同键重复投递复用首次结果（落点见 §5.2）。</summary>
    public string? IdempotencyKey { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
}

public sealed record InboxResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }    // 落盘消息 Id（如有）
    public string? ExecutionId { get; init; }  // 本轮消费产生的执行 Id（**尽力而为，可能为空**：循环异步启动）
    public int PendingInputCount { get; init; } // 会话当前未消费输入数
}
public interface ISessionRuntime
{
    /// <summary>唯一驱动入口：投递一条入站消息（写入 + 激活）。</summary>
    Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default);

    /// <summary>硬停当前轮：取消当前执行体；consumed 不回退，循环继续消费剩余未读（人接管插话）。</summary>
    Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default);

    /// <summary>停止会话：取消当前轮 + 终止消费循环 + 不再消费剩余未读；级联子会话。用于回滚/删除。</summary>
    Task<int> StopAsync(string sessionId, CancellationToken ct = default);

    // —— 控制与查询 ——
    Task<int> CancelAllInFlightAsync(CancellationToken ct = default);   // 进程级：对所有活跃会话 Stop
    /// <summary>等待会话消费循环空闲（无在途轮、无未读 Wake）。会话已 Stop（循环退出）视为 idle。</summary>
    Task WaitForIdleAsync(string sessionId, CancellationToken ct = default);
    SessionExecutionOverview GetOverview(string sessionId);
    bool HasAnyActiveExecution();
    IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId);
    IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default);
    Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default);
}
```

> **取消类操作收敛为两种会话级**（`InterruptAsync` / `StopAsync`）：
> - `InterruptAsync`：停当前轮、**保留**循环、继续消费剩余未读（"中断并执行"）。
> - `StopAsync`：停当前轮 + **终止**循环 + 不再消费剩余、级联子会话（UI「取消执行」/回滚/删除）。
> - **移除**旧 `CancelAsync(executionId)`（旧队列模型产物，非用户概念）与 `CancelBySessionAsync`（被 `StopAsync` 取代）。
> - `WaitForExecutionAsync(executionId)` 改为 `WaitForIdleAsync(sessionId)`：**等待循环空闲**（当前轮结束且无未读 Wake），前台 `task` 阻塞等待即用此语义。
> - `GetExecution(executionId)` 移除（不再有按 executionId 的全局查询/取消）；`ExecutionRecord.ExecutionId` 字段保留（事件仍携带）。

> **UI 不暴露 Delivery**：UI 的「发送」固定映射 `Deliver(Wake)`，忙则由消费循环天然排队；`Inject`/`Notify` 仅为架构层使用。
> `InboxMessage.WaitForCompletion` **不定义**（YAGNI，已从旧 spec 删除）。

### 5.2 消息元数据键（`InboxMetadataKeys`）

落盘入站消息的 `SessionMessage.Metadata`：

```
inbox.delivery     inject | wake
inbox.consumed     true | false
inbox.sender_kind  human | session | system
inbox.sender_id    来源会话 Id / 子系统标签
inbox.sender_name  来源显示名（UI 徽章用；来自 InboxSender.DisplayName）
inbox.presentation user | system | assistant | none
inbox.source       System 来源标签（task/cron/…）
inbox.kind         System 种类（completed/failed/…）
```

**幂等键落点（统一）**：会话级 `SessionData.Metadata`，键 `inbox.idem:{IdempotencyKey}` → 存 `messageId|executionId`。重复投递时命中即复用首次结果，不重复落盘。**不再**使用 `SessionMessage.Metadata` 存幂等键（修正旧 spec 矛盾）。

### 5.3 Ownership 契约（`Seeing.Agent.Abstractions.Sessions`）

```csharp
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
    /// <summary>停止原因：仅由父 StopAsync 级联停止时置 cascaded_from_parent_stop（供 TaskTool 完成上报判定）。</summary>
    public const string StopReason = "control.stop_reason";
    public const string StopReasonCascadedFromParent = "cascaded_from_parent_stop";
}

public static class SessionControlExtensions
{
    public static SessionControl GetControl(this SessionData session);
    public static void SetOwner(this SessionData session, SessionOwner owner, string? by);
}
```

> `SessionData.Metadata` 为可变 `Dictionary<string,string>`，写入后由调用方 `SaveAsync` 落盘，符合既有 `OriginToolCallId` / `LastError` 用法。

## 6. Mailbox 与消费循环

### 6.1 未读表达（消息级 consumed 标记）

**每条入站输入消息自带标记**：

- `inbox.delivery`：`wake` | `inject`
- `inbox.consumed`：`true` | `false`

**未读判定**（替代 `HasPendingSyntheticUserMessage` 文本位置判定）：

```
有未读该跑的输入 = 存在 (consumed=false && delivery=wake)
有未读输入（任意）= 存在 (consumed=false && presentation != assistant)
```

- `Inject` 落盘 `delivery=inject, consumed=false`，**不触发跑**。
- `Wake` 落盘 `delivery=wake, consumed=false`，触发消费循环。
- `Presentation=Assistant`（外部产出写回）落盘即 `consumed=true`，**不参与未读判定、不唤醒**。

免疫消息删除/压缩：消息被删则其标记一并消失，无悬空游标。

> **不考虑历史兼容**：允许破坏性变更，历史数据已清空。所有入站输入消息均带 `inbox.*` 键，无旧数据兼容分支（`synthetic` 旧机制随 `AgentLoopScheduler` 一并删除）。

### 6.2 消费循环（替代 SessionExecutionQueue + idle resume）

**单飞 + 每会话串行化**：每会话一个 `SemaphoreSlim(1,1)`（`_sessionGates`）+ 内存 `_activeLoops: sessionId → Task`。

```
Deliver(Wake):
  1. 解析目标会话 + 授权
  2. 落盘 SessionMessage（inbox.delivery=wake, inbox.consumed=false, inbox.* 来源键）
  3. Sender=Human 且目标有父 → 置 Owner=Human
  4. await _sessionGates[sessionId].WaitAsync()
       若 _stoppedSessions 含该会话 → 清除（新投递重新激活，见 §7）
       若该会话 Metadata 含 control.stop_reason → 清除（§7）
       若该会话循环未在跑 → 启动循环（_activeLoops[sessionId] = RunLoopAsync(...)）
       _sessionGates[sessionId].Release()

消费循环 RunLoopAsync（单飞；逐轮持 gate 完成"终止检查 + 未读判定 + 标记"，执行体在 gate 释放后运行）:
  while true:
    await gate.WaitAsync()
    try:
      // 终止检查：会话已被 Stop（或已删除）→ 退出循环，不再消费
      if _stoppedSessions.Contains(sessionId) || 会话不存在:
        从 _activeLoops 移除自身；break
      pending = 当前所有 (consumed=false && presentation != assistant) 的入站消息
      if pending 为空:
        从 _activeLoops 移除自身；break        // 退出与检查在同一 gate 内，无丢唤醒窗口
      将 pending 全部置 consumed=true；SaveAsync
    finally:
      gate.Release()
    try:
      跑一轮执行（ExecutionRunner.RunOnceAsync，历史 = 会话消息含本批）
    catch (OperationCanceledException):
      // Interrupt/Stop 取消：本执行已终态；consumed 不回退
      // Interrupt → 循环继续消费剩余未读；Stop → 下轮终止检查命中退出
    catch (Exception):
      // 执行失败：记录 Failed；继续循环消费剩余未读（避免整循环崩溃）
    // 执行中到达的输入 consumed=false，留在下一轮
```

**Stop 竞态防护**：`ExecutionRunner.RunOnceAsync` 启动前在 gate 内**二次校验** `_stoppedSessions`（因 `StopAsync` 可能落在"终止检查通过后、执行体启动前"）。命中则本轮不启动，直接进入下轮终止检查退出。

**全局并发上限**：`ExecutionRunner.RunOnceAsync` 启动前检查 `MaxConcurrentExecutions`（`ExecutionOptions`，默认保留）。超限时本轮**延迟重试**（等其它会话执行结束），不失败、不丢弃；等待机制：进程级 `SemaphoreSlim`（每执行占一席，终态释放），避免轮询空转。`MaxQueueSizePerSession` 随队列删除而**移除**（未读输入不再有队列上限）。

**并发正确性（消除丢唤醒竞态）**：
- 新 `Deliver` 在循环"判定空 → 退出"之间到达时，因**判定与退出在同一 gate 临界区内**完成：要么 Deliver 先获得 gate（循环判定非空，继续），要么循环先退出（Deliver 随后见 `_activeLoops` 无自身，启动新循环）。
- 因"循环从 `_activeLoops` 移除自身"发生在 gate 内，Deliver 在 gate 内检查 `_activeLoops` 不会看到"即将退出但仍在跑"的循环。

**关键语义**

- **合并消费**：一轮消费"执行开始时已落盘的**所有**未读输入"（Inject + Wake）。
- **执行中到达留给下轮**：不做 steer。
- **单一清除点**：标记即状态；无独立 pending 键。
- **空闲判定** = 循环不在跑，**删除 `_busy` 字典**。
- **终止检查点**：循环每轮 gate 内先查 `_stoppedSessions` / 会话存在性，`StopAsync` 后下一轮即退出。

### 6.3 写入窗口与单一权威写入者

- **非执行期写入**：`Deliver` 落盘消息、`control.owner`、`inbox.*` 标记。若目标会话正在执行（忙时排队场景），投递方仍只写**入站 user/system 消息与元数据**，不碰执行期 assistant/tool 消息。
- **执行前一次性写**：本批 `consumed=true` 由**循环侧在启动 `ExecutionRunner` 之前**在 gate 内完成并 `SaveAsync`。执行体启动后**零写入**这些键。
- **执行期独占**：assistant/tool 消息内容仍由 `ChatEventTracker` 独占写（`ExecutionRunner` 内）。两侧写的是**不同消息对象**，`SessionData.AddMessage` 内部锁 + `WriteBehindSessionStore` 去抖保证并发安全。
- **外部产出例外**：cron/heartbeat 的产出写回（`Presentation=Assistant`，§9）在**非执行期**由投递方落盘，属 release notes 13 已认可的"宿主非执行期系统消息"例外。
- 结论：**单写者契约保持**——执行期不写 `inbox.*`/`control.*`；非执行期不修改**执行期产出的** assistant/tool 消息对象。

### 6.4 与执行引擎的关系

- 消费循环的"跑一轮"委托 `ExecutionRunner.RunOnceAsync`；本轮输入消息**已由 Deliver 落盘**，执行体以 `SkipUserMessagePersist = true` 语义跑（不重复落盘）。
- 执行体其余职责（项目指令注入、出站回填、模型/思考强度应用、命令/压缩/schema 结算）照常。
- **标题生成门控**：旧门控 `SkipUserMessagePersist != true`（`ExecutionJobService.cs:556`）在新模型下恒 false（一律 skip），会使标题对所有会话失效。改为**基于本批是否含 `Presentation=User` 输入**：本批含意图用户消息（且非命令）→ 生成标题；纯系统/子会话通知批次 → 不生成。与 §10 `IsIntentionalUserMessage` 口径一致。
- `AgentId`/`ModelId` 经执行选项传入。

### 6.5 重启恢复

**不自动恢复**。进程重启 / 会话重载后，`consumed=false && delivery=wake` 的输入保留为未读，等下次 `Deliver` 或显式触发才驱动。与现状同一边界。

## 7. 中断 / 停止（硬停）

两种会话级操作（区别见 §5.1 注）：

**`InterruptAsync(sessionId)`（停当前轮、循环继续）**

1. 取消当前执行体（复用既有取消链路：`ExecutionRecord.Cts` → `CancellationToken`）。
2. **consumed 不回退**：已被本批标记的消息保持 `consumed=true`，不重跑。
3. 消费循环检测到仍有未读（`consumed=false && delivery=wake`）→ 继续下一轮。
4. 若无未读 → 循环退出。

**`StopAsync(sessionId)`（停当前轮 + 终止循环 + 不再消费，级联子会话）**

1. **先对级联子会话写标记**：在**每个子会话** `Metadata[SessionControlKeys.StopReason] = cascaded_from_parent_stop` 并落盘（**先于**加入 `_stoppedSessions` 与取消，保证监听器读到时标记已可见）。
2. 标记 `_stoppedSessions` 加入该会话（及级联子会话）。
3. 取消当前执行体（同 Interrupt）。
4. 循环下一轮 gate 内**终止检查**命中 → 从 `_activeLoops` 移除自身、退出；剩余未读**保持 `consumed=false`**（不消费，也不清除）。
5. 用于回滚（`SessionHandoffTool`）/ 删除会话（`DeleteSessionAsync`）/ UI「取消执行」。

> **`control.stop_reason` 清除时机**：子会话**被重新 `Deliver`（含重新激活）时清除**（与 `_stoppedSessions` 清除同点，§6.2 步骤 4）。否则子会话 Stop 后被重新驱动并正常完成时，陈旧标记会让 `TaskTool` 把合法完成误判为"父级联停止"而不投递。

> **`_stoppedSessions` 生命周期**：
> - 会话被删除（`RemoveSessionAsync`）→ 从 `_stoppedSessions` 清理。
> - **停止后再次 `Deliver` → 清除该会话的停止态并正常启动消费循环**（停止不是永久冻结，而是"当前批次不再消费"；**任何**来源的新投递均可重新激活，不设白名单）。这保证"父 `task_id` 续跑"等场景在 Stop 后仍可驱动。
> - **`StopAsync` 与 `InterruptAsync` 的区别仅在循环去向**：Interrupt 循环继续消费剩余；Stop 循环退出且剩余不消费。二者都不回退 `consumed`。
> - **防止"取消后被重新拉起"是触发方职责**：`TaskTool` 完成监听器按**子自身终止原因**判定——子被父 `StopAsync` 级联停止则**不投递**（见 §8.2），而非由 `_stoppedSessions` 限制谁能激活、也非查父状态。

> **回滚数据流（`SessionHandoffTool`）**：失败即 `StopAsync(successor.Id)`（终止后继循环，脱离 `ExecutionId`）+ `RollbackAsync`（`RemoveSessionAsync` 删后继并 `NormalizeAnchor` 还原源为锚点 + `SetActiveAsync` 还原活跃）。`RemoveSessionAsync` 同时从 `_stoppedSessions`/`_activeLoops`/`_sessionGates` 清理该会话运行态。

**取消的半截历史**：中断时当前 user 消息已在历史、无回复。按"不回退"语义，后续轮次的 LLM 历史会同时看到该 user 消息与后续消息（无回复）。此为可接受语义。

## 8. Ownership 流转与完成上报

### 8.1 流转

| 事件 | 迁移 |
|------|------|
| `SubAgent` 会话创建（`CreateChildAsync`） | → `Parent`（默认，无需显式写） |
| `Sender=Human` 对某会话 `Deliver` | `Parent` → `Human`，记 `TakenOverAt/By` |
| `session_release` / UI"交还父会话" | `Human` → `Parent` |
| 分离为独立会话（既有 detach） | → `Self` |

> **读取点**：`TakenOverAt/By` 由 UI 展示（子会话"已被接管"徽章 + 悬停显示接管时间/接管人，§10 来源徽章同处）；`Owner` 由 `TaskTool` 完成上报读取（§8.2）。

### 8.2 完成上报（`TaskTool` 后台监听器）

由"直接 `InjectSyntheticAsync` + `TryResumeWhenIdleAsync`"改为构造 `InboxMessage` 调 `ISessionRuntime.DeliverAsync`：

- **投递判据 = 子自身的终止原因，不查父状态**：
  - 子**自己**完成/失败/被独立停止 → **一律投递**给父（无论父当前状态）。
  - 子是被"父被用户手动 `StopAsync` 级联停止"的 → **不投递**（父已主动放弃，通知无意义）。
  - 实现：`StopAsync(parent)` 级联停止子会话时，**先**在子会话写 `SessionControlKeys.StopReason = cascaded_from_parent_stop` 并落盘，**再**取消子执行；`TaskTool` 完成监听器读子自身 `StopReason`，命中则不发，否则正常发。该标记在子会话被重新 `Deliver` 时清除（§7）。
  - 替代旧 `parentToken.IsCancellationRequested` 判据（新模型下 Interrupt 也取消父轮令牌，父轮令牌不再等价于"父被停止"）。
  - **级联范围**：`StopAsync(parent)` 级联停止其**所有子会话**（含 `Owner=Human` 的），并一律打 `cascaded_from_parent_stop`（父停止即放弃整棵子树，不论子是否被人接管）。
- 读子会话 `GetControl().Owner`：
  - `Parent`（未被接管）→ `Sender=Session(child)`、`Presentation=System`、`Delivery=Wake`，目标 = 父会话（**保持既有行为**）。
  - `Human`（已被接管）→ `Sender=Session(child)`、`Presentation=None`、`Delivery=Notify`，父会话**只收 UI 通知、不唤醒 agent**。
- 终态映射（Completed/Cancelled/Failed）→ `Kind` + `Body` 不变（复用 `SystemReminderRenderer.Wrap` 与 `BuildReminderMeta`）。
- **终态读取**：监听器 `await WaitForIdleAsync(childId)` 后读 `GetOverview(childId).LastExecution?.Status`（`LastExecution` 终态后保留）。会话已 Stop 时 `WaitForIdleAsync` 直接返回（Stop 态视为 idle），不挂起。
- `SourceTag = SystemReminder.Sources.Task`。对模型透明：`task` 工具参数/返回不变。
- **删除** `TaskTool` 两处"父令牌回调取消子执行"（`TaskTool.cs:305-318`、`334-347`）：级联停止统一由 `StopAsync(parent)` 负责，避免父 `Interrupt` 时误级联取消子。
- **前台 task 父取消**：`WaitForIdleAsync(childId, context.CancellationToken)` 传入父轮令牌（保留 `TaskTool.cs:352` 的 `IsCancellationRequested → Failure` 语义）。**已知边界**：父 `Interrupt` 时前台 task 返回失败，但子会话继续运行且前台分支无完成上报（与后台不同）。

### 8.3 主动推送（人触发）

- UI「推送」= 取子会话**当前产出**（复用 `ReadChildResult` + 状态摘要，`system-reminder` 信封）→ `Deliver(目标, Sender=Session(child), Presentation=System, Delivery=Wake)`。
- **不跑新执行、不生成总结**：与完成上报是**同一份内容、同一投递**，区别仅在触发者（自动 vs 手动）。
- 目标由人选（默认谱系父会话）；默认唤醒。

## 9. 系统通知统一

| 来源 | 现状 | 改造 |
|------|------|------|
| TaskTool 完成 | `InjectSyntheticAsync` | → `Deliver(Sender=Session(child), Presentation=System, Wake/Notify)` |
| Scheduler cron/heartbeat · 用户输入 reminder | `SessionScheduleDispatcher` 直写 user | → `Deliver(Sender=System, Presentation=System, Delivery=Inject)`（**不唤醒**，行为不变） |
| Scheduler cron/heartbeat · 产出写回 | `SessionScheduleDispatcher` 直写 assistant | → `Deliver(Sender=System, Presentation=Assistant, Delivery=Inject)`（落盘即 consumed，**不唤醒**，行为不变） |
| Gateway 取消/错误横幅 | 直写 transient system（持久化） | → `Deliver(Sender=System, Presentation=None, Delivery=Inject)`（**保留持久化**，刷新后横幅仍在；不唤醒） |
| AgentExecutor Todo 提醒 | in-loop ephemeral `ChatMessage`（不落盘） | **不动**（同 Loop 内提醒，非跨会话投递） |

> cron/heartbeat 的 agent 任务由 Quartz `AgentJob` 内 `_executionRouter.ExecuteAsync` **自行执行**（`AgentJob.cs:334`），产出经 dispatcher 写回会话，**不经过会话消费循环**。统一到 Inbox 后仍**保持"写回不唤醒"**：reminder 与产出分别以 `Inject`/`Assistant` 落盘。
> `IAgentLoopScheduler` 与 `InjectSyntheticAsync` 在迁移完成后删除。

## 10. 事件与 UI 呈现

新增事件类型（`MessageEventType`）：`InboxNotice = "inbox.notice"`：

```csharp
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

- 经 `IExecutionEventPublisher.Publish` → `SessionEventStreamRouter` 扇出 → 渲染为内联通知条 / Toast。
- 纯通知不落盘、不进 LLM 历史。
- `SessionTitleService.IsIntentionalUserMessage` 需将 `Presentation != User` 的入站消息视同 `synthetic`，不计入"首条意图用户消息"。

### UI 行为

- **子会话放开输入**：`SessionWindowContext.ApplyCapabilities` 的 `IsReadOnly = isChild` → 子会话允许输入/提交。其余能力标记（改名/清空/模型选择/自动批准切换）暂维持现状。
- **发送** = `Deliver(Wake)`；忙时自动排队（消费循环续跑），空闲立即跑。**UI 不出现 Wake/Inject 选择器**。
- **强制中断** = `InterruptAsync`（停当前轮、保留循环、立即消费排队消息）。
- **取消执行** = `StopAsync`（停止会话：停当前轮 + 终止循环 + 不再消费剩余）。
- **推送** = 选目标 + `Deliver`（当前产出）。
- **交还父会话** = `Owner → Parent`（**仅改归属，不 `StopAsync`**；交还后父 agent 可继续驱动该子会话）。
- **来源徽章**：非人类来源消息视觉区分 + 悬停显示 `inbox.*` 元数据。

## 11. 权限模型

- `session_inbox_send` 走 `tool.execute` 门（`AgentExecutor.EvaluatePermissionAsync`），与 `task`/`session_handoff` 一致。
- 目标范围默认 **同组 + 父链**（`SessionToolBase.IsSameGroupAsync` + `GetParentAsync`）；跨组需 `IPermissionAuthorizerFactory` 授权（缺失则放行，沿用既有约定）。
- `session_takeover` / `session_release` 视为会话控制操作，走同一授权门。
- `Sender=Human` 的接管由宿主（UI）发起，视为已授权。

## 12. 调用点迁移表（补全旁路）

`IExecutionSubmitter` / `IChatOrchestrator` 删除后，全部调用点归宿：

| 调用点 | 现调用 | 迁移到 |
|--------|--------|--------|
| `TaskTool.cs:178`（新建/续跑子会话的 user 消息预落盘） | `AddMessageAsync` | **删除**；改由 `DeliverAsync` 统一落盘（§6.4） |
| `TaskTool.cs:204,328` | `IExecutionSubmitter.SubmitAsync`（启动子执行） | `ISessionRuntime.DeliverAsync(Sender=Session(parent), Presentation=User, Delivery=Wake)`（正文 = userPrompt；不再预落盘、不再传 `SkipUserMessagePersist`） |
| `TaskTool.cs:135` | `_loopScheduler.IsLoopBusy(session.Id)` | `ISessionRuntime.GetOverview(session.Id).HasActiveExecution`（删除 `IAgentLoopScheduler` 依赖） |
| `TaskTool.cs:220` | `WaitForExecutionAsync(executionId)` | `ISessionRuntime.WaitForIdleAsync(childId)` |
| `TaskTool.cs:222` | `GetExecution(executionId)`（后台终态读取） | `GetOverview(childId).LastExecution?.Status` |
| `TaskTool.cs:349`（前台分支） | `WaitForExecutionAsync(result.ExecutionId)` | `ISessionRuntime.WaitForIdleAsync(childId, context.CancellationToken)`（传父轮令牌，保留 `:352` 取消判定） |
| `TaskTool.cs:256-296` | `InjectSyntheticAsync` + `TryResumeWhenIdleAsync` | `ISessionRuntime.DeliverAsync`（读子终止原因 + Owner 定 Wake/Notify，见 §8.2） |
| `TaskTool.cs:305-318`、`334-347` | 父令牌回调取消子执行 | **删除**（级联停止统一由 `StopAsync(parent)` 负责，避免父 Interrupt 误级联） |
| `TaskTool.cs:352`（前台父取消判定） | `context.CancellationToken.IsCancellationRequested` | 保留（前台阻塞等待中父轮被取消即返回失败；与后台完成上报无关） |
| `TaskStatusTool.cs:82` | `IExecutionSubmitter.WaitForExecutionAsync` | `ISessionRuntime.WaitForIdleAsync` |
| `TaskStatusTool.cs:91` | `GetExecution(executionId)`（wait 后重取） | `GetOverview(taskId).LastExecution`（空闲时 CurrentExecution 为 null） |
| `TaskStatusTool.cs:72,96,243` | `current == null && overview.QueueLength == 0`（子任务状态判定 + `MapExecutionState(current!.Status)`） | 状态源改为 `current ?? overview.LastExecution`；`current==null && LastExecution==null` 再回落到 `child.Status`。**不再依赖 `QueueLength`/`PendingInputCount`**，避免 Stop 态空引用 |
| `SessionHandoffTool.cs:125-130`（后继 user 消息预落盘） | `AddMessageAsync` | **删除**；改由 `DeliverAsync` 统一落盘 |
| `SessionHandoffTool.cs:132` | `IExecutionSubmitter.SubmitAsync` | `ISessionRuntime.DeliverAsync` |
| `SessionHandoffTool.cs:143-150`（失败回滚） | 依赖 `submit.ExecutionId` + `CancelAsync` | 改为：`DeliverAsync` 失败/抛异常即 `StopAsync(successor.Id)` + `RollbackAsync`（删后继、还原活跃）。**回滚不依赖 ExecutionId** |
| `SessionToolsModule.cs:63` | `GetService<IExecutionSubmitter>()` 门控 | `GetService<ISessionRuntime>()` |
| `AgentLoopSchedulerHostedService.cs` | `SubmitAsync`（idle resume）+ 内含 `ExecutionSessionEventBus` | **删除**（idle resume 移除；`ExecutionSessionEventBus` 死契约一并删） |
| `AgentExecutor.cs:46,63`（`IAgentLoopScheduler` 构造依赖，未使用） | — | **删除该依赖** |
| Core DI `ServiceCollectionExtensions.cs:690` | 注册 `IAgentLoopScheduler` | **删除注册** |
| `HostingServiceCollectionExtensions.cs:44,52,53,54,60,61,82,91,92-94,97` | DI 注册 | `:44` 删 `IChatOrchestrator`；`:52` 删 TaskTool 的 `IAgentLoopScheduler` 参数；`:53,60` TaskTool/TaskStatusTool 依赖改 `ISessionRuntime`；`:54,61` 删两工具的 `IExecutionStatusProvider` 参数；`:82` 删 `ISessionEventBus`/`ExecutionSessionEventBus` 注册；`:91` `ExecutionJobService` → `SessionRuntime`；`:92-94` 改注册 `ISessionRuntime` + `IExecutionInFlightBoundary`（**不注册 `IExecutionStatusProvider`**，已删）；`:97` 删 `AgentLoopSchedulerHostedService` |
| `ChatOrchestrator` 全部方法 | `IChatOrchestrator` | 执行/控制 → `ISessionRuntime`；CRUD → `ISessionDirectory` |
| WebUI `SessionWindow.razor`、`SessionEventStreamRouter` | `IChatOrchestrator` | 按方法分流到 `ISessionRuntime` / `ISessionDirectory` |
| WebUI `SessionWindow.razor:470-484`（`RestoreExecutionFromOverview`，读 `QueuePosition`）、`:1378` | `QueuePosition` | 读 `PendingInputCount`（§4.3） |
| WebUI `SessionWindow.OnCancelExecution` | `CancelBySessionAsync` | `ISessionRuntime.StopAsync(sessionId)`（UI 取消 = 停止会话） |
| WebUI「中断并执行」（排队中） | — | `ISessionRuntime.InterruptAsync(sessionId)`（停当前轮、保留循环、立即消费排队消息） |
| TUI `TuiChatEngine`/`TuiEventPump` | `IChatOrchestrator` | `ISessionRuntime` |
| TUI `Program.cs:136` | `sp.GetRequiredService<IChatOrchestrator>()` | `sp.GetRequiredService<ISessionRuntime>()` |
| TUI `TuiChatEngine.cs:56,132,2130` | `IExecutionSubmitter`（注入 + `CancelAllInFlightAsync`） | `ISessionRuntime`（`CancelAllInFlightAsync`） |
| TUI `TuiChatEngine.cs:66,142,1919` | `IExecutionStatusProvider.GetOverview(sessionId).HasActiveExecution` | `ISessionRuntime.GetOverview(sessionId).HasActiveExecution` |
| TUI `TuiSessionController.cs:15,21,26` | `IExecutionSubmitter` 注入 | `ISessionRuntime` |
| TUI `TuiSessionController.cs:107` | `current.QueueLength = result.QueuePosition` | 读 `PendingInputCount`（§4.3） |
| TUI `TuiSessionController.cs:120` | `CancelAsync(executionId)` | `ISessionRuntime.StopAsync(sessionId)`（TUI 持有 sessionId） |
| TUI `TuiSessionController.cs:127` | `CancelBySessionAsync`（`/cancel all`） | `ISessionRuntime.StopAsync(sessionId)` |
| TUI `TuiViewState.cs:55`、`StatusBarView.cs:123-124` | `QueueLength`/`QueuePosition` UI 字段 | 读 `PendingInputCount` |
| Gateway `GatewayOrchestratorV2.cs:54` | `IChatOrchestrator.SubmitAsync` | `ISessionRuntime.DeliverAsync` |
| Gateway `GatewayOrchestratorV2.cs:59` | 以 `string.IsNullOrEmpty(result.ExecutionId)` 判定提交失败 | 改以 `!result.Success` 判定（`DeliverAsync` 为 fire-and-forget，`ExecutionId` 可能为空，不得据此判失败） |
| Gateway `GatewayOrchestratorV2.cs:64` | `result.QueuePosition` | `InboxResult.PendingInputCount` |
| Gateway `GatewayOrchestratorV2.cs:73-242`（`SubscribeExecutionEventsAsync` 按 executionId 过滤 + `RegisterRun(executionId)`） | 订阅到匹配的 `ExecutionCompleteEvent` | 改**会话级订阅到空闲**：订阅 `SubscribeEvents(sessionId)`，流式转发，直到会话消费循环空闲（`WaitForIdleAsync` 语义）或超时；不再按 `executionId` 过滤/注册（合并消费下一次 Deliver 可能对应多轮、或多提交共享一轮）。`GatewayEvent.ExecutionId` 字段可保留但不再作为边界 |
| Gateway `GatewayOrchestratorV2.cs:137-144,189` / `GatewayWebSocketHandler.cs:271` / `GatewayEndpoints.cs:56` / `WebSocketGatewayClient.cs:578` | `CancelAsync(executionId)` / `SubscribeEvents` | `StopAsync(sessionId)` / `SubscribeEvents`（协议取消按 sessionId） |
| Gateway `GatewayWebSocketHandler.cs:211` | `if (!submit.Success \|\| IsNullOrEmpty(submit.ExecutionId)) return;` | 改 `if (!submit.Success) return;`；订阅改会话级（同上） |
| Gateway `IGatewayClient.cs:14` / `HttpGatewayClient.cs:39-60` / `WebSocketGatewayClient.cs:543-575` | `SubscribeAsync(sessionId, executionId)` 按 executionId 过滤 | 改 `SubscribeAsync(sessionId)` **会话级**：订阅到空闲/超时，不按 executionId 过滤 |
| Gateway `GatewayEndpoints.cs:60-83` | HTTP 订阅端点要求 `executionId` | 改会话级（仅 sessionId） |
| Gateway `GatewayRunTracker.cs:14`（`RegisterRun(executionId, sessionId)`/`CancelRun`/`TryGetSessionId`） | executionId 键 | 改 **session 键**（`RegisterRun(sessionId)`）；`GatewayHost.cs:72`、`AdminEndpoints.cs:35`、`GatewayEndpoints.cs:139` 构造同步 |
| Gateway `GatewayOrchestratorV2.cs:116,122,127` | `with { ExecutionId = executionId }` | 会话级订阅下改写（`ExecutionId` 字段保留但不再作边界，可留空或取当前轮记录） |
| 渠道桥 `WeComChannelBridge.cs:332,338,340`、`QQChannelBridge.cs:277,284,285` | `if (!submit.Success \|\| IsNullOrEmpty(submit.ExecutionId)) fail` + `SubscribeAsync(sessionId, executionId)` | 改 `if (!submit.Success) fail`；订阅改会话级 |
| `WeComChannelBridge.cs:402` | `_gatewayFacade.CancelAsync(executionId)` | `ISessionRuntime.StopAsync(sessionId)`（按 sessionId） |
| Gateway `GatewayWebSocketHandler.cs:207`、`WebSocketGatewayClient.cs:484` | `QueuePosition` 协议字段 | 改携 `PendingInputCount` |
| Gateway 协议帧 `GatewayWsFrame.cs`（`GatewayCancelPayload.ExecutionId`/`GatewayCancelAckPayload.ExecutionId`/`GatewayExecutionCompletePayload.ExecutionId`）与 `IGatewayConnection.cs:20`/`WebSocketGatewayClient.cs:136` 连接级 `CancelAsync(executionId)` | executionId 取消 | 改携 `sessionId`（取消按会话）；HTTP 取消端点 body（`GatewayEndpoints.cs:150`）同样改携 `sessionId` |
| Gateway 模型/协议字段 `GatewaySubmitResult.QueuePosition`、`GatewaySubmitAckPayload.QueuePosition` | `QueuePosition` | 改 `PendingInputCount` |
| UI 队列字段消费点：`SessionWindow.razor:1064`、`SessionToolbar.razor:69`、`SessionState.cs:176`、`SessionWindowContext.cs:97` | `QueueLength`/`QueuePosition` | 读 `PendingInputCount` |
| WebUI 页面注入 `IChatOrchestrator`（CRUD）：`MainLayout.razor:15`、`Index.razor`、`Session.razor`、`Conference.razor`、`SessionsPage.razor` | `IChatOrchestrator` | CRUD → `ISessionDirectory`；执行/控制 → `ISessionRuntime` |
| Embed Demo、测试桩（`StubExecutionSubmitter` 等） | `IChatOrchestrator` / `IExecutionSubmitter` | `ISessionRuntime` |

> **执行级取消无归宿**：新模型取消统一为会话级（`InterruptAsync`/`StopAsync`），不再有 `executionId` 取消。所有"按 executionId 取消"的调用点（TUI/Gateway/WebUI）改为按 `sessionId` 取消；Gateway 协议若以 executionId 取消，需在取消请求中携带 sessionId（或由 Gateway 维护 executionId→sessionId 映射）。

> **Gateway 订阅边界改会话级**：旧模型"提交拿 executionId → 订阅该 execution 到匹配 `ExecutionCompleteEvent`"在新模型下失效（`ExecutionId` 可能为空；合并消费使一次 Deliver 对应多轮）。**全链路**改为会话级：`IGatewayClient.SubscribeAsync(sessionId)`、`GatewayRunTracker`（session 键）、HTTP 订阅端点、WeCom/QQ 渠道桥提交守卫（改 `!Success`）、`GatewayEvent.ExecutionId`（保留字段但不再作边界）；流式转发至会话消费循环空闲（`WaitForIdleAsync` 语义）或超时。权限呈现 `IPermissionSurfaceRegistry.Register(presenter(sessionId))` 本身会话级，注册生命周期随新订阅语义（到空闲/超时）变化。

> `ISessionDirectory` 方法名：`GetAsync`/`EnsureAsync`/`ListAsync`/`CreateAsync`/`DeleteAsync`/`RenameAsync`/`BranchAsync`。

## 13. 关键时序

**A. 人介入子会话并接管**

```
人 → UI 发送 → ISessionRuntime.DeliverAsync(Sender=Human, Presentation=User, Delivery=Wake)
  → SetOwner(child, Human)
  → 落盘 user 消息（inbox.* 元数据）
  → gate 内：循环未跑 → 启动消费循环
      → 标记本批 consumed=true → RunOnce
      → 执行中到达的留给下轮
```

**B. 子会话完成（未被接管）**

```
TaskTool 后台监听器 → 终态 → Owner==Parent
  → Deliver(目标=parent, Sender=Session(child), Presentation=System, Delivery=Wake)
      → 落盘 system-reminder 到父 → 启动父消费循环
```

**C. 子会话完成（已被接管）**

```
TaskTool 后台监听器 → 终态 → Owner==Human
  → Deliver(目标=parent, Sender=Session(child), Presentation=None, Delivery=Notify)
      → 发布 InboxNoticeEvent（仅 UI，不唤醒父 agent）
```

**D. 先插三条再唤醒**

```
Deliver(Inject) ×3  → 静默落盘 consumed=false
Deliver(Wake)       → 落盘 + 启动循环 → 一次消费三条 + 唤醒点
```

**E. 中断后继续**

```
会话运行中；人发新消息 → 落盘 consumed=false
人点中断 → InterruptAsync → 取消当前执行体（consumed 不回退）
  → 循环检测仍有未读 → 下一轮消费新消息
```

**F. 停止（回滚/删除）**

```
SessionHandoffTool 提交失败
  → StopAsync(successor.Id)  // 终止后继循环，剩余未读不再消费
  → RollbackAsync            // RemoveSessionAsync 删后继 + NormalizeAnchor 还原源锚点 + SetActiveAsync 还原活跃
```

## 14. 破坏性变更 / 兼容

**删除**
- `ExecutionJobService`、`ChatOrchestrator`（重拆为 `SessionRuntime`/`ExecutionRunner`/`SessionDirectory`）。
- `Seeing.Agent.Hosting.Execution.SessionExecutionQueue`（执行队列）；**注意**：`Seeing.Agent.Gateway.Core.SessionExecutionQueue`（Gateway `(channel,session)` 通道串行锁）**不删**。
- `IChatOrchestrator`、`IExecutionSubmitter`、`IExecutionStatusProvider`（接口）。
- `ISessionEventBus` / `ExecutionSessionEventBus`（死契约）。
- `AgentLoopScheduler` 的 `_busy`/`InjectSyntheticAsync`/`TryResumeWhenIdleAsync`/`HasPendingSyntheticUserMessage`、`IAgentLoopScheduler`、`AgentLoopSchedulerHostedService`。
- `ISessionRuntime.CancelAsync(executionId)` / `CancelBySessionAsync` / `IExecutionStatusProvider.GetExecution(executionId)` / `ExecutionRecord.QueuePosition` / `SessionExecutionOverview.QueuedExecutions`（被 `StopAsync` / `LastExecution` / `PendingInputCount` 取代）。

**保留**
- `ExecutionEventPublisher` / `IExecutionEventPublisher`。
- `IExecutionInFlightBoundary`（热重载边界，由 `SessionRuntime` 实现）。

**新增**
- `Abstractions.Runtime`（`ISessionRuntime`/`ISessionDirectory`/`InboxMessage`/`InboxSender`/`InboxDelivery`/`InboxPresentation`/`InboxMetadataKeys`）。
- `Abstractions.Sessions`（`SessionControl` 等）。
- `InboxNoticeEvent` + `MessageEventType.InboxNotice`。
- `SessionData.Metadata` 新增 `control.*` / `inbox.idem:*`；`SessionMessage.Metadata` 新增 `inbox.*`。

**行为变更**
- `SessionWindowContext` 子会话由只读变为可输入。
- `SessionTitleService.IsIntentionalUserMessage` 识别 `inbox.presentation != user`。

**兼容**
- **不考虑历史兼容**：允许破坏性变更，历史数据已清空。无 `synthetic` 兼容分支（§6.1）。

## 15. 测试策略

1. **Deliver**：Inject 不触发、Wake 触发、Notify 不落盘；幂等键复用；跨组鉴权拒绝/放行。
2. **消费循环**：合并消费；执行中到达留到下轮；**单飞并发**（并发 Deliver 只起一个循环）；**退出竞态**（循环退出临界区与 Deliver 互斥，无丢唤醒）。
3. **未读判定**：`consumed=false && delivery=wake` 查询正确；`Presentation=Assistant` 不计入；消息删除后无悬空。
4. **中断**：`InterruptAsync` 取消当前执行、consumed 不回退、循环继续消费剩余。
5. **停止**：`StopAsync` 取消当前执行 + 终止循环（剩余未读保持 consumed=false）+ 级联子会话；回滚/删除后循环不再对已删会话续跑。
6. **Ownership**：Human Deliver 翻 `Human`；`TaskTool` 在 `Human` 下走 Notify、`Parent` 下走 Wake。
7. **推送**：取当前产出 deliver，不触发新执行。
8. **单写者**：执行期不写 `inbox.*`/`control.*`；非执行期不写 assistant/tool 内容。
9. **cron**：reminder（Inject）+ 产出（Assistant）落盘不唤醒。
10. **工具**：`session_inbox_send`/`session_takeover`/`session_release` 授权与参数校验。
11. **回归**：`Seeing.Agent.Tests`、`Seeing.Session.Tests`、`Seeing.Agent.WebUI.Tests`、TUI/Gateway 测试（调用点迁移）。
12. **VSTest 运行**（见 AGENTS.md：MTP 无法发现测试，改用 `dotnet vstest`）。

## 16. 分阶段实施

| 阶段 | 内容 | 可独立验收 |
|------|------|-----------|
| P1 | Abstractions 契约（`ISessionRuntime`/`ISessionDirectory`/`InboxMessage`/`SessionControl`/`InboxNoticeEvent`）+ `SessionRuntime`（Deliver + consumed 消费循环 + 执行记录迁入）+ `ExecutionRunner` 抽出 + `SessionDirectory` | Deliver/消费循环/执行迁移单测通过 |
| P2 | 删除 `ExecutionJobService`/`SessionExecutionQueue`/`ChatOrchestrator`/`AgentLoopScheduler` 三件套；调用点迁移（§12 全表）；DI 重接 | 全量回归通过 |
| P3 | `InterruptAsync` + Ownership 流转 + TaskTool 完成上报改造 + 推送 | 接管后不唤醒父；中断可续跑 |
| P4 | WebUI 放开子会话 + 中断/推送/交还 + 来源徽章 + `InboxNoticeEvent` 渲染 | 人可介入子会话并接管 |
| P5 | Scheduler/Gateway 通知收敛（§9）+ 工具 `session_inbox_send`/`session_takeover`/`session_release` | 系统通知统一；工具可被 agent 调用 |

## 17. 已知边界与开放问题

- **无 steer**：执行中 `Wake` 只能排队到下一轮。
- **不自动恢复**：重启后未读 Wake 保留为未读。
- **接管粒度是会话级**：无法"部分接管"。
- **跨进程**：`consumed` 落盘，但消费循环在进程内；Gateway 多实例需各自驱动（与现状同一边界）。
- **中断的半截历史**：见 §7，为可接受语义。
- **无历史兼容**：允许破坏性变更，历史数据已清空（§6.1）。

## 18. 本设计替产品方拍板的决定（可否决）

1. **唯一驱动入口 `DeliverAsync`**，`IExecutionSubmitter.SubmitAsync` 对外移除。
2. **未读用消息级 `inbox.consumed` 标记**，不用游标 Id / 会话级 pending。
3. **合并消费**：一轮消费执行开始时全部未读输入。
4. **不自动恢复**：重启后未读 Wake 保留。
5. **中断不回退**：已消费批次不重跑。
6. **接管自动**：人一旦对子会话发消息即自动接管（`Owner=Human`），另提供显式 `session_release`。
7. **接管后父仅收 UI 通知**（不唤醒 agent）。
8. **推送 = 直接 deliver 当前产出**，不触发新执行；目标由人选。
9. **UI 不暴露 Delivery**：发送固定 `Wake`，忙则排队，另有强制中断。
10. **契约三分**：`ISessionRuntime` + `ISessionDirectory`；`IChatOrchestrator` 删除。
11. **准入交授权模块**，Owner 只做完成上报收件人。
12. **彻底重拆 `ExecutionJobService`**：`SessionRuntime`（入站/循环/记录/控制）+ `ExecutionRunner`（跑一轮）+ `SessionDirectory`（CRUD）。
13. **cron/heartbeat 保持不唤醒**：reminder 走 `Inject`、产出走 `Assistant`。
14. **保留全局并发上限 `MaxConcurrentExecutions`**：消费循环启动执行前检查，超限延迟重试；`MaxQueueSizePerSession` 移除。
15. **Gateway 取消/错误横幅保留持久化**：走 `Inject`（落盘不唤醒）。
16. **SessionHandoff 回滚不依赖 ExecutionId**：失败即 `StopAsync(successor.Id)` + `RollbackAsync`。
17. **取消类操作收敛为两种会话级**：`InterruptAsync`（停当前轮、循环继续）+ `StopAsync`（停当前轮+终止循环+不再消费、级联子会话）；移除 `CancelAsync(executionId)` 与 `CancelBySessionAsync`。
18. **UI「取消执行」= `StopAsync`**；「中断并执行」= `InterruptAsync`。
19. **Stop 后可被任何新 `Deliver` 重新激活**（不设白名单）；"取消后不被重新拉起"由触发方按**子自身终止原因**判定（`TaskTool` 完成上报，见 §8.2）负责。
20. **消费循环与 `SessionRuntime` 必须在 Hosting**（依赖方向硬约束）；契约 `ISessionRuntime` 在 Abstractions，Core 不加空实现。
21. **子完成是否向父投递 = 按子自身终止原因**（被父 `StopAsync` 级联停止 → 不投递；否则投递）；删除 `TaskTool` 父令牌级联回调，级联统一由 `StopAsync` 负责。
22. **终态读取用 `SessionExecutionOverview.LastExecution`**（终态后保留）；空闲态 `CurrentExecution` 为 null，终态判定不依赖 `QueueLength`/`PendingInputCount`。
23. **标题生成门控改为"本批含 `Presentation=User` 输入"**（旧 `SkipUserMessagePersist` 门控在新模型恒 false）。
24. **Gateway 订阅改会话级**（全链路：服务端 + `IGatewayClient` 客户端契约 + `GatewayRunTracker` + HTTP 端点 + WeCom/QQ 渠道桥），订阅到空闲/超时，不再按 `executionId` 界定边界。
25. **`IExecutionStatusProvider` 删除**，`GetOverview`/`HasAnyActiveExecution` 并入 `ISessionRuntime`；消费者（TaskTool/TaskStatusTool/TUI）改走 `ISessionRuntime`。
