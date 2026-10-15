# 会话消息投递（Talk）与会话控制（Ownership）设计

> **⚠️ 已被取代（SUPERSEDED，2026-10-10）**：本设计的 "Talk" 命名与 `talk.pending` 布尔模型已被
> [`2026-10-10-session-inbox-runtime-design.md`](2026-10-10-session-inbox-runtime-design.md) 取代。
> 新设计将子系统命名为 **SessionInbox**（并入 **SessionRuntime**），未读改用**消息级 `inbox.consumed` 标记**，
> 并新增**硬停中断**能力。本文档仅作历史参考，不再作为实施依据。

**日期:** 2026-10-10
**状态:** 已取代
**范围:** `Seeing.Agent.Abstractions`（Talk/SessionControl 契约）+ `Seeing.Agent.Core`（TalkService 实现 / AgentLoopScheduler 泛化）+ `Seeing.Agent.Hosting`（ChatOrchestrator / TaskTool / ExecutionJobService）+ `Seeing.Agent.Tools.Session`（session_talk 等工具）+ `Seeing.Agent.Scheduler`/`Gateway`（系统通知收敛）+ `Seeing.Agent.WebUI`（放开子会话输入 / 通知呈现）
**触发:** 子会话当前只读、完成时无条件唤醒父会话；"通知指定会话"能力散落在 TaskTool/Scheduler/Gateway，缺统一机制
**约束:** 按产品方惯例，本设计文档不自动提交 git

---

## 1. 背景与问题

现状（已由代码核实）存在**三条各自为政的"往会话放消息"路径**：

| 路径 | 入口 | 行为 |
|------|------|------|
| 人输入 | `IChatOrchestrator.SubmitAsync` → `IExecutionSubmitter.SubmitAsync` | 立即落盘 user 消息（`ExecutionJobService.cs:198-204`），触发或排队执行 |
| 系统注入 | `IAgentLoopScheduler.InjectSyntheticAsync` + `TryResumeWhenIdleAsync` | 落 `synthetic=true` user 消息，空闲时经 resume handler 空输入续跑（`AgentLoopSchedulerHostedService.cs:37-55`） |
| 直写 | Scheduler/Gateway dispatcher、`SessionHandoffTool` | 直接 `AddMessageAsync`，不经执行引擎 |

由此产生的问题：

1. **子会话不可介入**：WebUI `SessionWindowContext.ApplyCapabilities` 里 `IsReadOnly = isChild`（`SessionWindowContext.cs:78-94`）硬关子会话输入/提交。
2. **完成上报不可打断**：`TaskTool` 后台监听器在子执行终态后**无条件** `InjectSyntheticAsync(父)` + `TryResumeWhenIdleAsync(父)`（`TaskTool.cs:241-296`），没有"该子会话已被人接管"的概念，父 agent 会被无意义唤醒。
3. **没有通用"通知指定会话"能力**：TaskTool / Scheduler / Gateway 各写一套。
4. **没有 steer/interrupt**：执行中向会话追加消息只能排队到下一轮（`SessionExecutionQueue` 每会话串行，重复提交 → `Queued`）。
5. `SystemReminder` 的 `reminder_*` 元数据只写不消费，UI 对 `system-reminder` 无特殊渲染。

## 2. 目标 / 非目标

### 目标

| 目标 | 验收要点 |
|------|----------|
| 子会话可介入 | WebUI 放开子会话输入；人发消息即接管 |
| 统一投递契约 | 新增 `ITalkService`：向指定会话投递消息，可选唤醒；Sender/Presentation/Delivery 正交 |
| 接管语义 | 子会话被接管后，完成时**不唤醒**父 agent，父会话仅得一条 UI 通知 |
| 系统通知收敛 | cron/heartbeat/task 通知统一走 talk |
| 工具面 | 提供 `session_talk`（+ `session_takeover`/`session_release`） |
| 依赖方向 | 能力包只依赖 Abstractions（talk 契约必须落在 Abstractions） |

### 非目标

- **不做 steer/interrupt**（打断当前推理并把新消息插入本轮）。本期只支持"排队到下一轮"与"空闲唤醒"；`Interrupt` 字段预留不实现。
- 不改 `SessionGroup` 谱系模型（Child/Fork/Handoff）。
- 不改事件流单写者契约（执行期 `SessionData` 写入仍由 `ChatEventTracker` 独占）。
- 不引入跨进程写协调。
- 不改造 `AgentExecutor` 的 in-loop ephemeral 提醒（Todo 提醒只进本次 Loop、不落盘，属另一类，见 §9）。

## 3. 核心模型

### 3.1 消息三维度（正交）

一条 talk 消息由三个独立维度描述，取代"路径决定一切"：

| 维度 | 取值 | 语义 |
|------|------|------|
| **Sender**（谁在说） | `Human` / `Session(来源会话 Id)` / `System(来源标签)` | 身份，决定权限与接管 |
| **Presentation**（进上下文以什么身份） | `User`（平权用户消息）/ `System`（system-reminder 信封）/ `None`（不落历史） | 是否进 LLM 历史、是否算"意图用户消息" |
| **Delivery**（何时跑） | `Inject`（只落不跑）/ `Wake`（落并跑，忙则挂起待唤醒）/ `Notify`（只出 UI 通知） | 是否触发 Loop |

场景映射：

| 场景 | Sender | Presentation | Delivery |
|------|--------|--------------|----------|
| 人给子会话补一句纠正 | Human | User | Wake |
| 先插三条、最后一条唤醒 | Human | User | 3×`Inject` + 1×`Wake` |
| 子会话完成上报父会话 | Session(child) | System | Wake |
| 子会话完成但已被接管 | Session(child) | None | Notify |
| cron / heartbeat 触发 | System | System | Wake |
| Gateway 取消/错误横幅 | System | None | Notify |

### 3.2 会话控制（接管）

新增 `SessionControl`（持久化于 `SessionData.Metadata`，跨重载存活）：

- `Owner`：`Parent`（默认，SubAgent 由父委托）/ `Human`（已被人接管）/ `Self`（已分离为独立会话）
- `TakenOverAt` / `TakenOverBy`

## 4. 架构契约（分层与依赖方向）

> **Talk 契约在 Abstractions，实现（`TalkService`）在 Core。**
>
> 理由：`IAgentLoopScheduler` 位于 Core，能力包（`Seeing.Agent.Tools.Session`）碰不到；而能力包又必须能调用 talk。故契约下沉 Abstractions，实现上收 Core，通过 DI 反向注入 `IExecutionSubmitter`（Abstractions，实现于 Hosting）。

| 层 | 新增/改造 |
|----|-----------|
| **Abstractions** | `Talk/`（`ITalkService`、`TalkRequest`、`TalkResult`、`TalkSender`、`TalkDelivery`、`TalkPresentation`、`TalkMetadataKeys`）+ `Sessions/`（`SessionOwner`、`SessionControl`、`SessionControlKeys`、`SessionControlExtensions`）+ 新事件 `TalkNoticeEvent` |
| **Core（脊柱）** | `TalkService` 实现；`AgentLoopScheduler` 的 pending 判定泛化；`AddSeeingCore` 注册 |
| **Hosting** | `ChatOrchestrator.SubmitAsync` 委托 talk（人路径）；`TaskTool` 完成上报改走 talk + 读 Ownership；`ExecutionJobService` 承载消息来源元数据 + 执行开始清 pending |
| **Capability** | `Seeing.Agent.Tools.Session` 增 `session_talk` / `session_takeover` / `session_release` |
| **Scheduler / Gateway** | 通知收敛为 `Sender=System` 的 talk |
| **WebUI** | `SessionWindowContext` 放开子会话输入；人提交走 talk；`TalkNoticeEvent` 渲染 |

## 5. 契约层设计

### 5.1 Talk 契约（`Seeing.Agent.Abstractions.Talk`）

```csharp
public enum TalkSenderKind { Human, Session, System }

public sealed record TalkSender
{
    public required TalkSenderKind Kind { get; init; }
    /// <summary>Session：来源会话 Id；System：子系统标签（task/cron/heartbeat/gateway…）。</summary>
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>进上下文以什么身份：User=平权用户消息；System=system-reminder 信封；None=不落历史。</summary>
public enum TalkPresentation { User, System, None }

/// <summary>投递语义：Inject=只落不跑；Wake=落并跑（忙则挂起待唤醒）；Notify=只出 UI 通知。</summary>
public enum TalkDelivery { Inject, Wake, Notify }

public sealed record TalkRequest
{
    public required string TargetSessionId { get; init; }
    public required TalkSender Sender { get; init; }
    /// <summary>Inject/Wake 时的正文；Notify 可空。</summary>
    public string? Body { get; init; }
    public TalkDelivery Delivery { get; init; } = TalkDelivery.Wake;
    public TalkPresentation Presentation { get; init; } = TalkPresentation.User;
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
    /// <summary>System 呈现的来源标签，复用 SystemReminder.Sources。</summary>
    public string? SourceTag { get; init; }
    /// <summary>System 呈现的种类，复用 SystemReminder.Kinds。</summary>
    public string? Kind { get; init; }
    /// <summary>幂等键：同键重复投递复用首次结果（参照 SessionHandoffTool 在途标记）。</summary>
    public string? IdempotencyKey { get; init; }
    public bool WaitForCompletion { get; init; }
    /// <summary>【预留·本期不实现】取消当前执行后再投递。</summary>
    public bool Interrupt { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
}

public sealed record TalkResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }    // 落盘消息 Id（如有）
    public string? ExecutionId { get; init; }  // 唤醒产生的执行 Id（如有）
    public ExecutionStatus? Status { get; init; }
    public int? QueuePosition { get; init; }
}

public interface ITalkService
{
    Task<TalkResult> TalkAsync(TalkRequest request, CancellationToken ct = default);
}
```

### 5.2 消息元数据键（`TalkMetadataKeys`）

落盘 talk 消息的 `SessionMessage.Metadata` 上，供投递/接管/UI 判定：

```
talk.id            本次投递唯一 Id
talk.sender_kind   human | session | system
talk.sender_id     来源会话 Id / 子系统标签
talk.delivery      inject | wake
talk.presentation  user | system
talk.source        System 来源标签（task/cron/…）
talk.kind          System 种类（completed/failed/…）
talk.idem          幂等键
```

### 5.3 Ownership 契约（`Seeing.Agent.Abstractions.Sessions`）

```csharp
public enum SessionOwner { Parent, Human, Self }

public sealed record SessionControl
{
    public SessionOwner Owner { get; init; } = SessionOwner.Parent;
    public DateTimeOffset? TakenOverAt { get; init; }
    public string? TakenOverBy { get; init; }
    public bool IsHumanOwned => Owner == SessionOwner.Human;
}

public static class SessionControlKeys
{
    public const string Owner = "control.owner";
    public const string TakenOverAt = "control.taken_over_at";
    public const string TakenOverBy = "control.taken_over_by";
}

public static class SessionControlExtensions
{
    public static SessionControl GetControl(this SessionData session);
    public static void SetOwner(this SessionData session, SessionOwner owner, string? by);
}
```

> `SessionData.Metadata` 为可变 `Dictionary<string,string>`（`SessionData.cs:160`），写入后由调用方 `SaveAsync` 落盘，符合既有 `OriginToolCallId`/`LastError` 的用法。

## 6. TalkService 实现与投递语义（Core）

`TalkService` 组合 `ISessionManager`（原语）+ `ISessionGroupManager`（原语）+ `IExecutionSubmitter?`（Abstractions）+ `IAgentLoopScheduler`（Core）+ `ISystemReminderRenderer`（Abstractions）。

`TalkAsync` 流程：

1. **解析目标会话**：`GetOrLoadAsync(target)`；不存在则失败。
2. **鉴权**（§11）：同组 / 父链默认放行；跨组经 `IPermissionAuthorizerFactory`（缺失则放行，沿用 `SessionToolBase.AuthorizeAsync` 语义）。
3. **幂等预检**：`IdempotencyKey` 命中则复用首次结果（在目标会话 `Metadata` 存 `talk.idem:{key} → messageId/executionId`）。
4. **接管**：`Sender.Kind == Human` 且目标有父（`GetParentAsync != null`）→ `SetOwner(Human)`（§7）。
5. **按 Delivery 分发**：

| Delivery | 落盘 | 唤醒 | 通知 |
|----------|------|------|------|
| `Inject` | Presentation=User → 平权 user 消息；=System → `ISystemReminderRenderer.CreateUserMessage` 信封；=None → 不落 | 否（**不置 pending**） | 否 |
| `Wake` | 同 Inject（Body 非空时） | **是**（置 pending + 尝试 resume） | 否 |
| `Notify` | 否 | 否 | 发布 `TalkNoticeEvent` |

所有落盘消息统一打 `TalkMetadataKeys`。

### 6.1 Inject 与 Wake 的区分（关键）

- **Inject 绝不唤醒**：只落盘，不置 `talk.pending`。
- **Wake 置 pending**：`SessionData.Metadata["talk.pending"] = "true"`。
- `AgentLoopScheduler.TryResumeWhenIdleAsync` 的 pending 判定由"存在 pending synthetic"泛化为：**`talk.pending == true` 或 存在遗留 pending synthetic**（`AgentLoopScheduler.HasPendingSyntheticUserMessage` 保留兼容）。
- **执行开始时清除 `talk.pending`**（在 `ExecutionJobService.ProcessExecutionAsync` 置忙处，与 `SetLoopBusy(true)` 同点）——单一清除点，避免 resume 失败时丢唤醒。
- 忙时唤醒：`Wake` 置 pending 后若 Loop 忙碌，不额外排队；依赖既有 `SetLoopBusy(false)` 的补唤醒（`AgentLoopScheduler.cs:43-53`）自动消费。空闲时直接 `TryResumeWhenIdleAsync` → resume handler 空输入续跑。

> 该设计保证"先插三条、最后一条唤醒"：三条 `Inject` 静默落盘；第四条 `Wake` 置 pending 并触发；执行开始时所有消息已在历史中，一次 Loop 消费完毕。

### 6.2 与 `SubmitAsync` 的关系（保持单一持久化）

- `Wake` 的"跑"委托给 `IExecutionSubmitter.SubmitAsync`，但**以 `SkipUserMessagePersist = true`** 提交（消息已由 `TalkService` 落盘，避免双写）。
- `SubmitAsync` 其余职责（项目指令注入、出站回填、模型/思考强度应用）照常执行；`AgentId`/`ModelId` 经 `ChatOptions` 传入。
- 由此保持"非执行期消息由投递方写、执行期内容由 `ChatEventTracker` 独占"的单一写入者边界。

## 7. Ownership 流转

| 事件 | 迁移 |
|------|------|
| `SubAgent` 会话创建（`CreateChildAsync`） | → `Parent`（默认，无需显式写） |
| `Sender=Human` 对某会话 talk（含 UI 提交） | `Parent` → `Human`，记 `TakenOverAt/By` |
| `session_release` / UI"交还父会话" | `Human` → `Parent` |
| 分离为独立会话（既有 detach） | → `Self` |

**完成上报判定**（`TaskTool` 后台监听器）：读子会话 `GetControl().Owner`：

- `Parent`（未被接管）→ `Sender=Session(child)`、`Presentation=System`、`Delivery=Wake` 通知父会话（**保持既有行为**）。
- `Human`（已被接管）→ `Sender=Session(child)`、`Presentation=None`、`Delivery=Notify`，父会话**只收 UI 通知、不唤醒 agent**。

## 8. TaskTool 完成上报改造

`TaskTool.cs:214-302` 的完成监听器由"直接 `InjectSyntheticAsync` + `TryResumeWhenIdleAsync`"改为构造 `TalkRequest` 调 `ITalkService.TalkAsync`：

- 终态映射（Completed/Cancelled/Failed）→ `Kind` + `Body` 不变（复用 `SystemReminderRenderer.Wrap` 与 `BuildReminderMeta` 的内容）。
- `Sender = new TalkSender { Kind = Session, Id = childId }`；`Presentation = System`（未被接管）或 `None`（已被接管）。
- `Delivery = Wake`（未被接管）或 `Notify`（已被接管）。
- `SourceTag = SystemReminder.Sources.Task`。
- 对模型透明：`task` 工具的参数/返回不变；`BuildReminderMeta` 的既有 `reminder_*` 键保留，另加 `talk.*`。

## 9. 系统通知统一

| 来源 | 现状 | 改造 |
|------|------|------|
| TaskTool 完成 | `InjectSyntheticAsync` | → `Talk(..., Sender=Session(child), Presentation=System)` |
| Scheduler cron/heartbeat | dispatcher 直写 + `AgentJob` 包 reminder | → `Talk(..., Sender=System, Presentation=System, Delivery=Wake)` |
| Gateway 取消/错误横幅 | `GatewayOrchestratorV2` 直写 transient system | → `Talk(..., Sender=System, Presentation=None, Delivery=Notify)` |
| AgentExecutor Todo 提醒 | in-loop ephemeral `ChatMessage`（不落盘） | **不动**（属同 Loop 内提醒，非跨会话投递） |

`IAgentLoopScheduler.InjectSyntheticAsync` 保留为兼容 shim（内部转 `Talk(Presentation=System, Delivery=Inject)`）或标记 `[Obsolete]`，迁移完成后删除。

## 10. 事件与 UI 呈现

新增事件类型（`MessageEventType`）：`TalkNotice = "talk.notice"`，配套 record：

```csharp
public record TalkNoticeEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.TalkNotice;

    public required string SenderKind { get; init; }   // human | session | system
    public string? SenderId { get; init; }
    public string? SourceTag { get; init; }
    public string? Kind { get; init; }
    public string? Body { get; init; }
}
```

- 经 `IExecutionEventPublisher.Publish(sessionId, evt)` 发布 → `SessionEventStreamRouter` 扇出 → `EventStreamHandler` 渲染为**内联通知条 / Toast**（如"子会话 X 已完成（已被你接管，未唤醒父会话）"）。
- 纯通知不落盘、不进 LLM 历史。
- `SessionTitleService.IsIntentionalUserMessage` 需将 `Presentation=System`（`talk.presentation=system`）的 talk 消息视同 `synthetic`，不计入"首条意图用户消息"。

## 11. 权限模型

- `session_talk` 走 `tool.execute` 门（`AgentExecutor.EvaluatePermissionAsync`），与 `task`/`session_handoff` 一致。
- 目标范围默认 **同组 + 父链**（`SessionToolBase.IsSameGroupAsync` + `GetParentAsync`）；跨组需 `IPermissionAuthorizerFactory` 授权（缺失则放行，沿用既有约定）。
- `session_takeover` / `session_release` 视为会话控制操作，走同一授权门。
- `Sender=Human` 的接管由宿主（UI）发起，视为已授权。

## 12. WebUI 放开子会话

- `SessionWindowContext.ApplyCapabilities`：`IsReadOnly = isChild` → 子会话**允许输入/提交**（`IsReadOnly = false`）。其余能力标记（改名/清空/模型选择/自动批准切换）暂维持现状，仅放开聊天输入（最小变更）。
- 人提交路径：`SessionWindow.OnSubmitMessage` → 保持 `IChatOrchestrator.SubmitAsync` 调用，但其实现内部委托 `ITalkService`（`Sender=Human, Presentation=User, Delivery=Wake`），从而自动触发接管。
- 子会话被接管后，其完成不再唤醒父 agent；父会话 task 卡片显示"已完成（已被接管）"通知（`TalkNoticeEvent`）。
- 提供 UI"交还父会话"操作 → `session_release`。

## 13. 关键时序

**A. 人介入子会话并接管**

```
人 → UI 输入 → IChatOrchestrator.SubmitAsync
  → ITalkService.TalkAsync(Sender=Human, Presentation=User, Delivery=Wake)
      → SetOwner(child, Human)            // 接管
      → 落盘 user 消息（talk.* 元数据）
      → 置 talk.pending；空闲 → TryResumeWhenIdleAsync → resume handler 空输入续跑
      → 执行开始 → 清 talk.pending
```

**B. 子会话完成（未被接管）**

```
TaskTool 后台监听器 → 终态 → Owner==Parent
  → TalkAsync(Target=parent, Sender=Session(child), Presentation=System, Delivery=Wake)
      → 落盘 system-reminder 到父 → 置 pending → 唤醒父 Loop（既有行为）
```

**C. 子会话完成（已被接管）**

```
TaskTool 后台监听器 → 终态 → Owner==Human
  → TalkAsync(Target=parent, Sender=Session(child), Presentation=None, Delivery=Notify)
      → 发布 TalkNoticeEvent（仅 UI，不唤醒父 agent）
```

**D. 先插三条再唤醒**

```
Talk(Inject) ×3  → 静默落盘，不置 pending
Talk(Wake, Body="") → 置 pending → 唤醒 → 一次 Loop 消费三条 + 唤醒点
```

## 14. 破坏性变更 / 兼容

- `Abstractions` 新增 `Talk/`、`Sessions/` 契约与 `TalkNoticeEvent`（纯增量）。
- `IAgentLoopScheduler.InjectSyntheticAsync` 语义收敛（保留兼容 shim 或标 `[Obsolete]`）。
- `AgentLoopScheduler.TryResumeWhenIdleAsync` pending 判定泛化（新增 `talk.pending`，保留 synthetic 兼容）。
- `ChatOrchestrator.SubmitAsync` 内部改走 talk（签名不变，行为等价 + 接管副作用）。
- `TaskTool` 完成上报改走 talk（对模型透明）。
- `SessionWindowContext` 子会话由只读变为可输入（UI 行为变更）。
- `SessionData.Metadata` 新增 `control.*` / `talk.*` / `talk.idem:*` 键；`SessionMessage.Metadata` 新增 `talk.*` 键。
- `SessionTitleService.IsIntentionalUserMessage` 需识别 `talk.presentation=system`。

## 15. 测试策略

1. **TalkService**：Inject 不唤醒、Wake 唤醒、Notify 不落盘；幂等键复用；跨组鉴权拒绝/放行。
2. **Ownership**：Human talk 翻 `Human`；`TaskTool` 在 `Human` 下走 Notify、在 `Parent` 下走 Wake。
3. **pending 机制**：`Wake` 置 pending → `TryResumeWhenIdleAsync` 触发；`Inject` 不触发；执行开始清 pending；忙时 `SetLoopBusy(false)` 补唤醒。
4. **先插三条再唤醒**：一次 Loop 消费全部消息。
5. **工具**：`session_talk`/`session_takeover`/`session_release` 授权与参数校验。
6. **回归**：`Seeing.Agent.Tests`（TaskTool/AgentLoopScheduler/ExecutionJobService）、`Seeing.Session.Tests`、`Seeing.Agent.WebUI.Tests`（SessionWindow 输入放开）。
7. **VSTest 运行**（见 AGENTS.md：MTP 无法发现测试，改用 `dotnet vstest`）。

## 16. 分阶段实施

| 阶段 | 内容 | 可独立验收 |
|------|------|-----------|
| P1 | Abstractions 契约 + `SessionControl` + `TalkService`（Core）+ pending 泛化 | TalkService 单测通过 |
| P2 | `TaskTool` 完成上报改走 talk + Ownership 判定 | 接管后不唤醒父会话 |
| P3 | `session_talk` / `session_takeover` / `session_release` 工具 | 工具可被 agent 调用 |
| P4 | WebUI 放开子会话输入 + 人路径走 talk + `TalkNoticeEvent` 渲染 | 人可介入子会话并接管 |
| P5 | Scheduler / Gateway 通知收敛 | 系统通知统一 |

## 17. 已知边界与开放问题

- **无 steer/interrupt**：执行中 `Wake` 只能排队到下一轮，无法插入当前推理。`Interrupt` 字段预留。
- **`talk.pending` 为会话级布尔**：多来源并发 Wake 合并为一次唤醒（可接受）。
- **接管粒度是会话级**：无法"部分接管"（如只接管某轮）。
- **跨进程**：`talk.pending` 落盘，但 resume handler 注册在进程内；Gateway 多实例需各自注册（与现有 `TryResumeWhenIdleAsync` 同一边界）。
- **旧数据兼容**：已落盘的 `synthetic` 消息无 `talk.*` 键，判定保留 synthetic 分支。

## 18. 本设计替产品方拍板的决定（可否决）

1. **接管自动**：人一旦对子会话发消息即自动接管（`Owner=Human`），另提供显式 `session_release` 交还。
2. **接管后父会话收到 UI 通知（非唤醒）**：保留"子任务其实做完了"的信息差，但不唤醒父 agent。
3. **人路径统一走 talk**：`IChatOrchestrator.SubmitAsync` 内部委托 `ITalkService`，接管语义只有一套。
4. **talk 权限默认同组 + 父链**，跨组需授权。
5. **Talk 契约落 Abstractions、实现落 Core**（受 `IAgentLoopScheduler` 位于 Core 的硬约束）。
