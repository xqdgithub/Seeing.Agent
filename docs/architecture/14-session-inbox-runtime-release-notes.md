# 14 会话入站运行时（SessionInbox / SessionRuntime）Release Notes

**日期：** 2026-10-11
**设计规格：** [`2026-10-10-session-inbox-runtime-design.md`](../superpowers/specs/2026-10-10-session-inbox-runtime-design.md)
**相关模块：** `Seeing.Agent.Abstractions`（Runtime / Sessions 契约）、`Seeing.Agent.Hosting`（SessionRuntime / ExecutionRunner / SessionDirectory / TaskTool）、`Seeing.Agent.Tools.Session`（会话工具）、`Seeing.Agent.Core`（删除 AgentLoopScheduler 三件套）、`Seeing.Agent.Scheduler` / `Gateway`（通知收敛）、`Seeing.Agent.WebUI`

---

## 1. 概要

把三套各自为政的「往会话放消息并激活」机制（执行队列 / synthetic+idle resume / 直写）统一为**唯一驱动入口** `ISessionRuntime.DeliverAsync` + **每会话消费循环**。契约落 Abstractions（`ISessionRuntime` / `ISessionDirectory` / `InboxMessage` / `SessionControl`），实现在 Hosting。同时打开子会话输入（人发消息即接管）、补齐硬停中断与主动推送。

## 2. 背景与问题

重构前存在三条并行「放消息 + 激活」路径，语义重叠、状态双源：

| 路径 | 旧入口 | 行为 |
|------|--------|------|
| A. 执行队列 | `IChatOrchestrator.SubmitAsync` → `IExecutionSubmitter.SubmitAsync` → `SessionExecutionQueue` | 立即落盘 user 消息，空闲即跑、忙则排队（每会话串行） |
| B. 系统注入 | `IAgentLoopScheduler.InjectSyntheticAsync` + `TryResumeWhenIdleAsync` | 落 `synthetic=true` 消息，空闲时 resume 空输入续跑 |
| C. 直写 | Scheduler/Gateway dispatcher、`SessionHandoffTool` | 直接 `AddMessageAsync`，部分再补 Submit |

由此产生：激活语义重叠、忙闲状态双源（`AgentLoopScheduler._busy` 与队列活跃态手动同步）、pending 判定依赖文本位置（`HasPendingSyntheticUserMessage`）、唤醒靠 fire-and-forget 旁路、子会话只读不可介入、无中断/接管/主动推送。

## 3. 核心模型

### 3.1 两轴正交

- **轴一 · 写入（Inbox）**：`Sender`（`Human` / `Session(来源会话 Id)` / `System(来源标签)`）× `Presentation`（`User` / `System`(system-reminder 信封) / `Assistant`(外部产出写回) / `None`(不进 LLM 历史)）。
- **轴二 · 激活（Mailbox）**：`Delivery`（`Inject`=只落不跑 / `Wake`=落并激活消费 / `Notify`=只出 UI 通知，不落盘）。
- **场景映射**：人发消息 = `Human/User/Wake`；子会话完成上报父 = `Session(child)/System/Wake`；已被接管 = `Session(child)/None/Notify`；cron reminder = `System/System/Inject`；cron 产出写回 = `System/Assistant/Inject`（落盘即 consumed）；Gateway 横幅 = `System/None/Inject`。

### 3.2 消费循环（替代 SessionExecutionQueue + idle resume）

每会话一个 `SemaphoreSlim(1,1)` 门 + 内存 `_activeLoops: sessionId → Task`，**单飞 + 每会话串行化**：

```
Deliver(Wake)：落盘消息（inbox.delivery=wake, inbox.consumed=false）
  → 若 Sender=Human 且目标有父 → SetOwner(Human)
  → gate 内：若循环未跑则启动消费循环
RunLoopAsync（逐轮持 gate 完成"终止检查 + 未读判定 + 标记"，执行体在 gate 释放后运行）：
  while true:
    gate 内：终止检查（Stop/会话不存在→退出）；取所有未读置 consumed=true 并 SaveAsync；空则退出
    gate 外：ExecutionRunner.RunOnceAsync 跑一轮
```

- **合并消费**：一轮消费执行开始时**全部**未读输入（先插三条再唤醒一次跑完）。
- **执行中到达留给下轮**（不做 steer）。
- **未读判定**：`consumed=false && delivery=wake` 为「该跑」；`Presentation=Assistant` 落盘即 `consumed=true`，不参与未读、不唤醒。免疫消息删除/压缩（无悬空游标）。
- **无丢唤醒**：判定空→退出与 Deliver 启动循环互斥在同一 gate 临界区完成。
- **全局并发上限**：启动执行前检查 `MaxConcurrentExecutions`，超限延迟重试（进程级信号量占席，终态释放）；`MaxQueueSizePerSession` 随队列删除而移除。

### 3.3 Ownership（接管，独立于投递）

`SessionControl.Owner` ∈ `Parent`（默认，SubAgent 由父委托）/ `Human`（已被人接管）/ `Self`（已分离）。仅表示**完成结果的接收者**，与投递正交：

- `Sender=Human` 对子会话 `Deliver` → 自动 `Parent → Human`（记 `TakenOverAt/By`）。
- `session_release` / UI「交还父会话」→ `Human → Parent`。
- `TaskTool` 完成上报读 `Owner`：`Parent` → `System/Wake`（唤醒父 agent）；`Human` → `None/Notify`（父仅收 UI 通知）。

### 3.4 中断 / 停止（硬停）

| 操作 | 语义 |
|------|------|
| `InterruptAsync(sessionId)` | 取消当前执行体；**consumed 不回退**；循环继续消费剩余未读 |
| `StopAsync(sessionId)` | 取消当前轮 + **终止**消费循环 + 不再消费剩余未读 + **级联子会话**；用于回滚/删除/UI「取消执行」 |

- 取消类操作收敛为上述两种会话级；移除 `CancelAsync(executionId)` / `CancelBySessionAsync`。
- `StopAsync(parent)` 级联停止子会话时，**先**在子会话写 `control.stop_reason=cascaded_from_parent_stop` 并落盘，**再**取消；`TaskTool` 完成监听器读子自身终止原因，命中则**不投递**父（父已主动放弃）。该标记在子会话被重新 `Deliver` 时清除。
- `Stop` 后可被**任何**新 `Deliver` 重新激活（不设白名单）。

### 3.5 单一权威写入者保持

执行期 assistant/tool 消息仍由 `ChatEventTracker` 独占（在 `ExecutionRunner` 内）；投递方只写**入站 user/system 消息与元数据**（`inbox.*`/`control.*`），本批 `consumed=true` 由循环侧在启动执行体前于 gate 内一次写定。两侧写不同消息对象，契约不破。

## 4. 破坏性变更清单

### 删除

| 类型 / 成员 | 说明 |
|-------------|------|
| `ExecutionJobService` | 重拆为 `SessionRuntime`（入站/循环/记录/控制）+ `ExecutionRunner`（跑一轮）+ `SessionDirectory`（CRUD） |
| `ChatOrchestrator` | 职责拆入 `ISessionRuntime` + `ISessionDirectory` |
| `SessionExecutionQueue`（Hosting） | 替换为消费循环；**注意** `Seeing.Agent.Gateway.Core.SessionExecutionQueue`（Gateway 通道串行锁）**不删** |
| `IChatOrchestrator` / `IExecutionSubmitter` / `IExecutionStatusProvider` | 接口整体删除 |
| `ISessionEventBus` / `ExecutionSessionEventBus` | 死契约 |
| `AgentLoopScheduler` 的 `_busy` / `InjectSyntheticAsync` / `TryResumeWhenIdleAsync` / `HasPendingSyntheticUserMessage`、`IAgentLoopScheduler`、`AgentLoopSchedulerHostedService` | synthetic + idle resume 机制整体移除 |
| `ExecutionRecord.QueuePosition`、`SessionExecutionOverview.QueuedExecutions` / `QueueLength` / `HasQueuedExecutions` | 队列概念移除 |
| `CancelAsync(executionId)` / `CancelBySessionAsync` / `GetExecution(executionId)` | 取消/查询收敛为会话级 |

### 新增

| 类型 | 位置 |
|------|------|
| `ISessionRuntime` / `ISessionDirectory` / `InboxMessage` / `InboxResult` / `InboxSender` / `InboxDelivery` / `InboxPresentation` / `InboxMetadataKeys` | `Abstractions/Runtime/` |
| `SessionControl` / `SessionOwner` / `SessionControlKeys` / `SessionControlExtensions` | `Abstractions/Sessions/` |
| `InboxNoticeEvent`（`MessageEventType.InboxNotice`） | `Abstractions/Events/` |
| `SessionRuntime` / `ExecutionRunner` / `SessionDirectory` | `Hosting/` |
| `session_inbox_send` / `session_takeover` / `session_release` | `Seeing.Agent.Tools.Session` |
| `SessionExecutionOverview.LastExecution` / `PendingInputCount` | Overview 字段语义调整（`LastExecution` 终态后保留，为终态判定唯一来源） |

### 保留

- `ExecutionEventPublisher` / `IExecutionEventPublisher`。
- `IExecutionInFlightBoundary`（热重载边界，由 `SessionRuntime` 实现）。
- `ExecutionRecord.ExecutionId` 字段（事件仍携带；仅取消/查询不再按它界定）。

### 行为变更

- `SessionWindowContext.ApplyCapabilities` 子会话由只读变为可输入/提交。
- `SessionTitleService.IsIntentionalUserMessage` 识别 `inbox.presentation != user`（视同 synthetic，不计入首条意图用户消息）。
- 标题生成门控由旧 `SkipUserMessagePersist != true` 改为**本批是否含 `Presentation=User` 输入**（旧门控在新模型恒 false）。
- Gateway 订阅改**会话级**（订阅到空闲/超时），不再按 `executionId` 界定边界。

## 5. 迁移对照（旧接口 → 新接口）

| 旧 | 新 |
|----|----|
| `IExecutionSubmitter.SubmitAsync` | `ISessionRuntime.DeliverAsync`（`Sender=Human, Presentation=User, Delivery=Wake`） |
| `IChatOrchestrator.SubmitAsync` | `ISessionRuntime.DeliverAsync` |
| `IChatOrchestrator`（CRUD 方法） | `ISessionDirectory` |
| `IAgentLoopScheduler.IsLoopBusy(id)` | `ISessionRuntime.GetOverview(id).HasActiveExecution` |
| `IAgentLoopScheduler.InjectSyntheticAsync` + `TryResumeWhenIdleAsync` | `ISessionRuntime.DeliverAsync`（读子终止原因 + Owner 定 Wake/Notify） |
| `WaitForExecutionAsync(executionId)` | `ISessionRuntime.WaitForIdleAsync(sessionId)` |
| `GetExecution(executionId)`（终态读取） | `GetOverview(sessionId).LastExecution?.Status` |
| `CancelAsync(executionId)` / `CancelBySessionAsync` | `ISessionRuntime.StopAsync(sessionId)` |
| （排队中）「中断并执行」 | `ISessionRuntime.InterruptAsync(sessionId)` |
| `result.QueuePosition` / `overview.QueueLength` | `InboxResult.PendingInputCount` / `GetOverview().PendingInputCount` |
| `IChatOrchestrator.ReconcileIncompleteTasksAsync` | `ISessionRuntime.ReconcileIncompleteTasksAsync` |
| 宿主「发送」 | 固定映射 `Deliver(Wake)`；忙则消费循环天然排队（UI 不暴露 Delivery 选择器） |

> `TaskTool` / `TaskStatusTool` / `SessionHandoffTool` / WebUI / TUI / Gateway 全量调用点迁移见 spec §12。

## 6. 已知边界

- **无 steer**：执行中 `Wake` 只能排队到下一轮（不做 in-loop 插入）。
- **不自动恢复**：进程重启 / 会话重载后，`consumed=false && delivery=wake` 的输入保留为未读，等下次投递或显式触发才驱动。
- **接管粒度是会话级**：无法「部分接管」。
- **跨进程**：`consumed` 落盘但消费循环在进程内；Gateway 多实例需各自驱动（与现状同一边界）。
- **中断的半截历史**：中断时当前 user 消息已在历史、无回复；按「不回退」语义，后续轮次 LLM 历史会同时看到该 user 消息与后续消息（无回复），为可接受语义。
- **前台 task 父取消**：父 `Interrupt` 时前台 `task` 返回失败，但子会话继续运行且前台分支无完成上报（与后台分支不同）。
- **无历史兼容**：允许破坏性变更，历史数据已清空；无 `synthetic` 旧机制兼容分支。

## 7. 测试策略

覆盖点（见 spec §15）：Deliver 三态（Inject 不触发 / Wake 触发 / Notify 不落盘）、幂等键复用、消费循环合并消费与单飞/退出竞态、未读判定、`InterruptAsync` 续跑、`StopAsync` 级联与剩余不消费、Ownership 流转与 Wake/Notify 分流、推送不触发新执行、单写者边界、cron Inject/Assistant 不唤醒、三工具授权。

关键测试：`SessionRuntimeDeliverTests`、`SessionRuntimeControlTests`、`SessionRuntimeReconcileTests`、`InboxTypesTests`、`InboxNoticeEventTests`、`SessionDirectoryTests`，加全量回归（`Seeing.Agent.Tests` / `Seeing.Session.Tests` / `Seeing.Agent.WebUI.Tests` / TUI / Gateway）。运行用 `dotnet vstest`（MTP 无法发现测试，见根 `AGENTS.md`）。

## 8. 相关文档

- 设计规格：[`2026-10-10-session-inbox-runtime-design.md`](../superpowers/specs/2026-10-10-session-inbox-runtime-design.md)
- 前置：[`13 单一权威写入者 Release Notes`](13-single-writer-release-notes.md)（执行期单写者契约）
- 合规：[`06 合规审查`](06-compliance-audit.md)
