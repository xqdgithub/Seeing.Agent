# 统一多流订阅基础设施 + Task 卡片 UI 层聚合

**日期:** 2026-08-27
**状态:** 已实现并合并至 master（2026-08-27），含最终代码审查修复
**分支:** `master`（实现分支 `feat/task-card-ui-aggregation` 已合入）

---

## 1. 背景与问题

### 1.1 执行中 Task 卡片无进度

fdd0437 重构删除了服务端 Task 事件体系（`Task*Event` + `TaskEventProjector` + `TaskSessionProjector`）。重构后 TaskTool 前台/后台**均不阻塞**：`SubmitAsync` 后立即返回 `task_id: xxx\nstate: running`（`TaskTool.StartCompletionListener` 异步监听完成 → 注入 synthetic + 唤醒）。因此 `task_id` 本身可立即获取；但**子会话执行中的工具调用事件不会进入父会话事件流**，UI 无法展示进度步骤——执行中展开 Task 卡片无内容。

### 1.2 多个子代理并行

一个父 Session 一次 Loop 可能同时发起多个 `task` 工具调用（并行多个子代理）。关联机制必须支持多实例精确匹配。

### 1.3 现有事件消费架构缺陷

- `Session.razor` 直接编写父会话订阅循环（`ConsumeEventStreamAsync`，约 200 行事件分派逻辑），订阅机制散落页面内。
- `EventStreamHandler` 为 Scoped 单例，持有**单流式状态**（`_currentAssistantMessage`/`_currentLoopId`/`_currentStep`/`_accumulated*`），与页面级 `SessionState` 耦合，无法支撑多会话。
- 未来规划支持 **Agent 大屏**：同时操作与展示若干会话。当前架构无法承载。

### 1.4 方向约束（用户确认）

- **不恢复**已删除的服务端 Task 事件体系。
- 子会话完整正常事件流已含全部信息；**展示聚合只在 UI 层**。
- 保留两套执行路径语义；UI 均需提前拿到 `task_id` 并订阅进度。
- 建立**统一多流订阅基础设施**，父会话渲染与子会话聚合都基于它；面向未来大屏。
- `EventStreamHandler` 渲染状态**本期即多实例化**（每会话一个实例）。
- 代码膨胀问题通过**策略模式**等重构解决，允许重构。

---

## 2. 目标

1. 统一多流订阅基础设施：任意会话流只订阅一次，广播给多个消费者（支持同会话多页面/大屏共存）。
2. Task 工具调用通过**关联元数据**精确关联子会话，支持一个 Session 多个子代理并行。
3. 执行中 Task 卡片即有内容（子会话 ID + 进度步骤）。
4. 页面刷新 / 会话切换后恢复进行中任务进度（TaskSteps 落盘）。
5. `EventStreamHandler` 多实例化（每会话一个渲染策略），为未来大屏打基础。

## 3. 非目标

- 不恢复服务端 Task 事件 / 投影器 / `BackgroundTaskManager`。
- 不改变前台/后台执行语义。
- 不做子会话文本内容实时镜像（仅聚合工具调用步骤）。
- 大屏页面本身不在本期实现（仅基础设施与架构支持）。

---

## 4. 后端改动

### 4.1 TaskTool 写子会话关联元数据

`src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`：创建子会话后（`CreateChildAsync` 或续跑分支）写入：

```csharp
if (session.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out _) == false)
    session.Metadata[SessionMetadataKeys.OriginToolCallId] = context.CallId ?? string.Empty;
await _sessionManager.SaveAsync(session.Id);
```

- `origin_tool_call_id` = 父会话中本次 task 工具调用的 `CallId`。
- **续跑分支不覆盖**已有关联（保留旧卡片映射，避免枚举匹配失效）。
- 执行路径不变。
- 新增常量 `SessionMetadataKeys.OriginToolCallId = "origin_tool_call_id"`。

### 4.2 ChatOrchestrator 变 Singleton

`src/Seeing.Agent.App/ChatOrchestrator.cs`：
- 移除从未使用的 `_permissionChannel` 字段（唯一 Scoped 依赖，死字段）。
- 其余依赖（`ExecutionJobService`/`ISessionManager`/`IAgentRegistry`/`IWorkspaceProvider`/`IAgentExecutor`/`ICommandRegistry`/`AgentSelectionResolver`/`IModelManager`/`ChatExecutionQueue`/`ChatRunTracker`）均为 Singleton。
- `ChatOrchestratorServiceExtensions.cs:44` 改为 `AddSingleton<IChatOrchestrator, ChatOrchestrator>()`。

**理由**：`SessionEventStreamRouter`（Singleton）需要订阅任意会话事件流，依赖 `IChatOrchestrator`；ChatOrchestrator 变 Singleton 后无 DI 生命周期冲突，且保持"WebUI 只通过 IChatOrchestrator 读写 Session"的架构约束，Router 不直接访问 `ExecutionJobService`。

**影响**：Singleton 化后跨 circuit 共享 `_executionQueue`/`_runTracker`（本身 Singleton 且无状态依赖），WebUI Scoped 消费方注入 Singleton 服务安全（向上依赖合法）。
- 同步更新 `ChatOrchestratorServiceExtensions.cs:43` 注释（原"Scoped because it depends on IPermissionChannel"改为 Singleton 说明）。
- Router 注册：`Program.cs` 增加 `AddSingleton<SessionEventStreamRouter>()`、`AddScoped<CircuitContext>()`、`AddScoped<TaskCardAggregator>()`（供 Router 工厂经 scope 解析创建）；`EventStreamHandler` 保持 Scoped（Router 工厂经 scope 解析）。

---

## 5. 统一多流订阅基础设施

### 5.1 消费者接口

```csharp
public interface IStreamConsumer
{
    /// <summary>
    /// 消费者标识会话（Router 按显式 sessionId 参数注册/分发，不读取此属性；
    /// 聚合器等同时监听多会话的实现返回其主会话 Id）。
    /// </summary>
    string SessionId { get; }
    void OnEvent(IMessageEvent evt);          // 会话事件
    void OnStreamEnd();                        // 会话流结束（订阅释放）
}
```

### 5.2 订阅路由器 SessionEventStreamRouter

**生命周期: Singleton**

```
_subscriptions: ConcurrentDictionary<string, SessionSubscription>
  SessionSubscription = { Cts, ConsumeTask, Consumers: ConcurrentBag<IStreamConsumer> }
_consumerScopes: ConcurrentDictionary<(circuitId, sessionId), IServiceScope>
  # Router 工厂创建的 Scoped consumer 的 scope 归属（circuit 关闭时释放）

AttachConsumer(sessionId, consumer, replay: false)
   # 追加消费者；该会话尚未订阅则启动 consume loop；replay=true 表示需回放缓冲历史
DetachConsumer(sessionId, consumer)   # 移除消费者；空则释放订阅（取消 CTS）
GetOrCreateConsumer<T>(sessionId, ctx?)  # 工厂：创建 T 实例（per-circuit，经 IServiceScopeFactory 建 scope）
Broadcast(sessionId, evt)             # 分发事件给该会话全部消费者（单消费者 try/catch 隔离）
DetachAllForCircuit(circuitId)        # circuit 关闭时释放其全部 scope 与消费者（SeeingCircuitHandler 回调）
```

**流消费循环**（每会话一个）：

```csharp
await foreach (var evt in _orchestrator.SubscribeEvents(sessionId, cts.Token))
{
    Broadcast(sessionId, evt);
}
```

**生命周期规则**：
- 会话流随首个消费者 `AttachConsumer` 创建；随最后一个 `DetachConsumer` 释放（取消 CTS）。
- **重放去重（C-1 修订 + 终审 I3 + 签字轮 Minor）**：`ExecutionEventPublisher` 的 `CircularBuffer<IMessageEvent>` 是无 ID 对象列表，`IMessageEvent` 无持久化标识，"水位"方案不可行。改为引用快照去重，**按 loop 语义描述**（skipSet 实现在 loop 层，对历史事件一律不广播，与消费者类型无关）：
  - `skipSet` 按 **consume loop 创建时**的 `GetBufferedEvents(sessionId)` 引用快照构建（`ReferenceEqualityComparer`）；loop 内**历史事件不广播**（丢出）。
  - `replay=true` 消费者（聚合器）：Router 在 `AttachConsumer` 时对 `GetBufferedEvents` **全量调用一次 `OnEvent` 补历史**，此后仅收实时事件。Attach 回放与 loop Broadcast 之间可能存在短时竞态重合窗口（事件被回放+实时各投一次），**被聚合器 ToolCallId 幂等合并与终态去重吸收**。
  - **适用范围**：渲染消费者（`EventStreamHandler`）累积状态对重放事件无条件 Append 会错乱，故依赖 skipSet；父会话未被投影事件（`CommandResultEvent`/`ExecutionStartedEvent`/`ExecutionCompleteEvent`/`SessionTitleChangedEvent`）由页面 `RestoreExecutionFromOverview` / `SessionEvent.Updated` 兜底。
  - 刷新行为（M7）：skipSet 使 handler 丢弃 buffer 中的流尾 delta，刷新瞬间在途流尾到下一事件才渲染——需集成测试确认可接受（现有回放重建累积语义弱化为待下一事件）。
  - **loop 自然结束（签字轮 Minor 6）**：`ExecutionEventPublisher.CompleteSession` 会 TryComplete 全部 subscriber channel，loop 正常退出但消费者未必已 Detach。此时 Router 向仍挂载的消费者广播 `OnStreamEnd` 并移除该订阅，消费者随后 Detach；`OnStreamEnd` 由消费者清理自身状态。§10 测试覆盖。
  - **Detach 定位 scope（签字轮 Minor 7）**：`DetachConsumer(sessionId, consumer)` 无 circuitId，Router 在 `GetOrCreateConsumer` 时维护 `consumer → circuitId` 反向映射，Detach 据此释放对应 `(circuitId, sessionId)` 的 scope。
- **Scope 管理（I-1 + M4）**：Router 持有 `IServiceScopeFactory`；`GetOrCreateConsumer` 创建的 Scoped consumer 记录 `circuitId → (scope, consumer)`。`DetachAllForCircuit` 释放全部 scope 并清理消费者；**单个 `DetachConsumer` 也应释放对应 `(circuitId, sessionId)` 的 consumer-scope 映射**（避免长 circuit 内遍历多会话累积实例/scope）。**重访会话**复用旧 handler 时，页面需先调用 `handler.ClearCache()`（现有 `Session.razor:1247/2018` 已有调用点，§8.1 需明确保留）。
- **Circuit 上下文（I-6 + 终审修订）**：新增 Scoped `CircuitContext`（承载当前 `circuit.Id`）；`SeeingCircuitHandler.OnCircuitOpenedAsync` 写入。**Router(Singleton) 无法从自建 scope 读取页面 scope 的 CircuitContext**——改为：页面在自身 scope 内解析 `CircuitContext` 后，将 `circuitId` 作为 `ctx` **显式传入** `GetOrCreateConsumer<T>(sessionId, circuitId)`，Router 据此关联 `(circuitId, sessionId)` 到 scope 映射。§8.1 调用点必须传参。
- **广播隔离（I-7）**：`Broadcast` 对单个消费者 `try/catch` 并记录日志，任一消费者异常不终止该会话 consume loop。
- consumer 不跨 circuit 共享：`GetOrCreateConsumer` 创建 per-circuit 实例（Scoped）；Router 只保证"每会话一个 consume loop"。Detach 按 attachment 维度，不会误摘其他 circuit 的消费者。
- **flush 顺序（M1）**：`DetachAllForCircuit`/聚合器 Detach 时**先 flush 未落盘变更（SaveAsync），后释放 scope**。

**事件源**：`IChatOrchestrator.SubscribeEvents(sessionId)`（后端 `ExecutionEventPublisher` 按 sessionId fan-out + 缓冲回放；Router 作为 UI 侧聚合层，不与后端 fan-out 冲突）。

### 5.3 消费者归属

| 消费者 | 职责 | 生命周期 | 会话绑定 |
|--------|------|----------|----------|
| `EventStreamHandler` | 父会话渲染（绑定该会话 SessionData 指针 + 维护累积状态 + 通知页面刷新） | Scoped per-circuit，Router 工厂创建并管理 scope | 每会话一个实例 |
| `TaskCardAggregator` | 父会话流中识别 task toolCall → 挂载子会话订阅 → 聚合 `TaskSteps` 写回父 toolCall | Scoped per-circuit，**统一经 Router 工厂获取**（与 handler 一致） | 每父会话一实例（内部多 taskId）；会话切换时重绑 `ParentSessionId` 并清空 `_tasks` |

> **`IStreamConsumer.SessionId` 语义（M-1）**：Router 按显式 `sessionId` 参数注册/分发，不读取该属性；消费者实现该属性仅作标识用途（聚合器返回 `ParentSessionId`）。

---

## 6. 消费者：EventStreamHandler（多实例化重构）

### 6.1 改造

- 构造：不再注入 `SessionState`，改为注入 `ISessionManager` + 绑定 `SessionId`。
- 流式状态（`_currentAssistantMessage`/`_currentLoopId`/`_currentStep`/`_accumulated*`/`_toolCallPositions`/`_pendingPermissions`）**属于该实例**（该会话）。
- 会话数据定位：`_sessionManager.Get(SessionId)`（不再经 `SessionState.CurrentSession`）。会话数据的**实际落盘/投影由服务端 `ChatEventTracker` 完成**（`ExecutionJobService.ProcessExecutionAsync`），handler 仅绑定 SessionData 指针 + 维护流式累积状态，不再"写 SessionData"。
- 渲染结果：`OnStateChanged(IMessageEvent evt)` 事件通知页面（**携带触发事件**，供页面区分 `StreamDelta` 与工具/终态事件，精确复刻节流语义，见 §8.2）。
- 查询接口（`GetCurrentAssistantMessage`/`GetCurrentLoopId`/`MarkIncompleteTasksCancelledAsync`/`ReconcileOrphanTaskCardsAsync`/`ClearCache`）保留，操作自身 `SessionId`。
- **Todo/Budget 查询（M-5）**：handler 暴露 `CurrentTodoList`/`BudgetStatus` 等只读查询属性，页面在 `OnStateChanged` 回调中读取后决定是否写入 `SessionState`。
- **执行态跟踪**：跟踪当前活跃 `ExecutionId`（由执行开始事件建立）；`ExecutionCompleteEvent` 仅当其 `ExecutionId` 匹配当前活跃执行时才触发页面执行态清理。排队场景（同会话连续提交）不会误清。

### 6.2 实现 IStreamConsumer

- `OnEvent(evt)` → 现有 `ProcessEventAsync` 逻辑。
- `OnStreamEnd()` → 清理自身状态。

### 6.3 与 SessionState 解耦

`SessionState` 保留页面级交互状态（执行锁、取消令牌、当前会话指针、Todo/Budget 展示）。`EventStreamHandler` 不再写入它；Todo/Budget 更新经 `OnStateChanged` 或专用事件上抛，由页面决定写入。

---

## 7. 消费者：TaskCardAggregator（子会话聚合）

### 7.1 职责与粒度

**每父会话一实例**（Scoped per-circuit，**经 `Router.GetOrCreateConsumer<TaskCardAggregator>(sessionId, parentCtx)` 获取**，与 handler 统一），内部按 `taskId` 字典管理多个子会话订阅：

```
TaskCardAggregator : IStreamConsumer
  ParentSessionId           # 该实例绑定的父会话（会话切换时重绑并清空 _tasks）
  _tasks: ConcurrentDictionary<string, TaskCardState>
       key = taskId；value = { ToolCall, Owner(assistant消息), Detached }
  OnEvent(evt)              # 父会话流事件：识别 task toolCall → AttachFromToolCall
  AssistantChanged: Action<SessionMessage>?   # 触发页面时间线增量同步
```

**会话切换重绑（I-2）**：同一 circuit 在父会话 A/B 间切换时，调用 `Aggregator.Rebind(parentSessionId)`：清空 `_tasks`、对各子会话 `DetachConsumer`、重设 `ParentSessionId`，再重新订阅父会话流。切换由页面驱动，见 §8。

**并发写契约（I-3 + M3）**：聚合器作为父流与多个子流 consumer，`OnEvent` 由不同会话的独立 consume loop 并行调用；同时 `EventStreamHandler.HandleToolCall` 与服务端 `ChatEventTracker` 也在写同一 `SessionToolCall`。契约：
- 聚合器**只写** `TaskSteps`、`Error`（工具未完成时）、`TaskId`（关联解析时标量覆盖）**绝不触碰** `Status`/`Result`。
- `Error`/`TaskId` 为多方幂等覆盖写（`ChatEventTracker`/`EventStreamHandler`/聚合器），非结构性竞争，可接受；`TaskSteps` 是唯一需要结构化串行化的字段。
- TaskSteps 采用**整体替换列表引用**（`toolCall.TaskSteps = newSteps`）而非就地修改，避免与读取方（页面渲染遍历）竞争。
- 必要时对 `TaskCardState.ToolCall` 加轻量锁（聚合器内部串行化自己的写）。

### 7.2 挂载触发（两条路径）

1. **父流事件触发**：作为父会话流 consumer，在父流中识别 `toolName == "task"` 的 `ToolCallEvent` → `AttachFromToolCall`（解决执行中续跑产生的新 task 卡片）。
2. **页面恢复触发**：`Reconcile(parentSessionId)` 遍历会话内所有 task 工具调用重新 `AttachFromToolCall`。

### 7.3 关联解析

`AttachFromToolCall(toolCall, owner)`：

1. **幂等（I-5）**：首查 `_tasks` 是否已有该 `toolCall.Id` 对应条目且未 `Detached`，有则直接返回，避免 `Reconcile` 与父流识别重复挂载。
2. `toolCall.TaskId` 非空 → 直接使用。
3. 否则枚举子会话：`sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent)`（内存缓存）。**冷缓存兜底（I1 修订）**：`LoadAsync(parentSessionId)` 只加载父会话文件、不会填充子会话缓存，`LoadAllFromStorageAsync` 过重；故**新增 `SessionManager.LoadChildrenFromStorageAsync(parentId)`**：遍历 store 中 `ParentSessionId == parentId` 且 `Kind == SubAgent` 的会话文件载入 `_sessionDataCache`（**缓存已存在则跳过，仿 `LoadAllFromStorageAsync`，避免磁盘旧快照覆盖执行中活跃子会话的内存 TaskSteps**），再重查。匹配 `child.Metadata[OriginToolCallId] == toolCall.Id` → `toolCall.TaskId = child.Id`。
4. 未匹配 → 定时重试（10 次 × 500ms）。**注意（M-2）**：前台路径父 Loop 阻塞在 `WaitForExecutionAsync`，父流在 Running 后无新事件，事件驱动重试不成立；统一采用定时重试即可。

**toolCall 对象来源（Minor）**：父流 `ToolCallEvent` 只有 `Id`/`Name`/`Arguments`，聚合器从 `sessionManager.Get(parentSessionId).Messages` 中按 `ToolCallId` 定位对应的 `SessionToolCall`（owner 消息内）；定位失败则忽略该事件（等 Reconcile 兜底）。

拿到 taskId 后：`_router.AttachConsumer(taskId, this, replay: true)`（订阅子会话流并回放缓冲，补挂载早期步骤，见 M-4）。

### 7.4 聚合规则

| 子会话事件 | 行为 | 继续订阅 |
|-----------|------|---------|
| `ToolCallEvent` | 合并 `SessionTaskStep`（同 `ToolCallId` 更新状态/预览）；整体替换 `toolCall.TaskSteps` 引用；**防抖落盘（I-4）** | 是 |
| `ExecutionCompleteEvent` | **终态立即落盘**；结束订阅（不改写 `toolCall.Status`，以父工具状态为准） | 否 |
| `LoopCancelledEvent` | `toolCall.Error` 回填（工具未完成时）；**即 Detach** | 否 |
| `ErrorEvent` | `toolCall.Error = evt.Message`（工具未完成时）；结束订阅 | 否 |
| 其他 | 忽略 | 是 |

`SessionTaskStep`：`StepKind`（tool_pending/running/complete）、`ToolCallId`、`ToolName`、`Status`、`Preview`（≤200 字符）、`Timestamp`。

**持久化（C3 决策 + I-4 修订 + 终审 Important 2 + M2）**：TaskSteps **随变更防抖落盘**（内存每步更新；`SaveAsync(父会话Id)` 防抖节流，如 1s；终态立即落盘一次）。**Detach/circuit 关闭时 flush**：若存在防抖窗口内未落盘的变更，`DetachAllForCircuit`/聚合器 Detach 时**先 flush（SaveAsync）后释放 scope**（尽力而为）。**保存串行化链**：对同一父会话的防抖 Save 与 flush Save 用 per-session 串行队列（`SemaphoreSlim`/任务链）排队，避免旧快照晚落盘覆盖新快照（文件锁只串行化写入、不保证快照顺序）。落盘是**尽力而为的崩溃恢复**，**不是刷新依赖**——刷新恢复依赖内存最新态（`ChatOrchestrator.GetSessionAsync` 优先内存缓存）。

**续跑重置（M-3）**：`task_id` 续跑命中已 `Detached` 的 task 时，重置 `Detached=false` 并重新 `AttachConsumer(taskId, this, replay: true)` 订阅新执行。

### 7.5 生命周期

- 会话切换 / 页面 Dispose：`_router.DetachConsumer(父会话Id, this)` + 各子会话 `DetachConsumer(taskId, this)`。
- circuit 关闭：`_router.DetachAllForCircuit(circuitId)`。

---

## 8. 页面接入（Session.razor）

### 8.1 绑定

```csharp
@inject SessionEventStreamRouter Router
@inject Seeing.Agent.WebUI.Services.CircuitContext CircuitContext   // Scoped，承载当前 circuit.Id

// 加载/切换会话时
var circuitId = CircuitContext.Id;
var handler = Router.GetOrCreateConsumer<EventStreamHandler>(SessionId, circuitId);   // 显式传 circuitId（Important 1）
handler.ClearCache();                        // 重访会话：复用旧实例前清理流式状态（原 Session.razor:1247/2018 语义调整）
var aggregator = Router.GetOrCreateConsumer<TaskCardAggregator>(SessionId, circuitId);
aggregator.Rebind(SessionId);  // 会话切换：重绑 ParentSessionId + 清空 _tasks + 重订阅

handler.OnStateChanged += OnHandlerChanged;     // 时间线同步 + 执行态清理 + StateHasChanged
Router.AttachConsumer(SessionId, handler);      // 确保订阅
Router.AttachConsumer(SessionId, aggregator);   // 父流 consumer（识别 task toolCall）
aggregator.AssistantChanged += OnAssistantChanged;
```

### 8.2 事件分派表（I1 修复）

`ConsumeEventStreamAsync` 现有事件逻辑迁移位置：

| 事件 | 处理位置 |
|------|----------|
| `StreamDelta`/`ToolCallEvent`/`StreamStart`/`StreamComplete`/`LoopStart`/`LoopComplete` | `EventStreamHandler`（绑定 SessionData 指针 + 累积状态；**落盘/投影由服务端 ChatEventTracker 完成**）→ `OnStateChanged` 驱动时间线同步 |
| `ExecutionCompleteEvent`/`LoopCancelledEvent` | `EventStreamHandler`（ExecutionId/LoopId 匹配终态）→ 页面清理执行态 + 时间线同步 |
| `ErrorEvent` | `EventStreamHandler` 展示用（**无 ExecutionId，不做匹配**，由 LoopComplete/ExecutionComplete 兜底清执行态）→ 页面时间线同步 |

> **流式节流归属（终审 Important 4）**：现有页面 `ConsumeEventStreamAsync` 的 `charCount`/代码块深度节流（`_currentCodeBlockDepth`，约 1535-1558 行）是渲染性能机制。迁移后：**页面 `OnHandlerChanged` 回调内保留节流**——handler 每次 `OnStateChanged` 触发页面 `SyncTimelineAssistantFromStream` 前，页面按字符量/代码块阈值决定是否真正重绘（节流逻辑留在页面，不并入 handler）。避免长流式输出时每事件全量刷新性能退化。

> **排队闪断（M-6）**：同会话排队 exec1+exec2 时，exec1 的 Complete 会清页面执行态、exec2 的 Started 才恢复，存在短暂"执行中→空闲→执行中"闪断。本期容忍（记录已知边界）；如需消除，后续由 handler 检测"仍有排队项时不清态"。
| `PermissionRequest`/`PermissionResponse` | `EventStreamHandler` 上抛（per-circuit，多标签不串） |
| `SkillContentEvent`（`AppEventTypeConstants.SkillContent`） | `EventStreamHandler` 处理（内容由服务端更新 Session，UI 仅触发刷新）→ `OnStateChanged` |
| `TodoUpdate`/`ModeUpdate`/`BudgetStatus`/`BudgetWarning` | `EventStreamHandler` 更新自身查询属性 → `OnStateChanged` 上抛，页面读取后写 `SessionState` |
| `CompactionStarted/Delta/Completed/Failed` | 页面回调（`_compactionInProgress` 等），`OnStateChanged` 不携带压缩状态，页面自行处理 |
| `NavigateEvent`/`CommandResultEvent`(NavigationTarget) | 页面回调（导航） |
| `CommandResultEvent`(NeedsRefresh) | 页面回调（`ResetTimelineFromSession`） |
| `SessionTitleChangedEvent` | 经执行流到达（`ExecutionJobService.cs:832`）；页面兜底靠 `SessionEvent.Updated` 独立订阅（`Session.razor:673-694`），不并入 Router |
| 未知事件 | 页面增量同步（`SyncTimelineAssistantFromStream` + `ReconcileTimelineFromSession`） |

### 8.3 提交执行后

不再手动起订阅循环；Router 已订阅父会话流，handler 统一处理事件与终态。

### 8.4 清理

会话切换 / `Dispose`：`Router.DetachConsumer(旧会话Id, handler/aggregator)` + 退订事件。

**Dispose 取消语义协调（M-7 → 已修复 2026-08-27）**：原设计"保留现有行为（页面关闭即停止该会话及其子代理执行）"。用户反馈刷新页面即取消会话（"对话已取消: user"）。**已修复**（`9af835c`）：`Session.razor.Dispose` 移除 `CancelBySessionAsync` 与 `MarkIncompleteTasksCancelledAsync("会话已关闭")` 调用，刷新/导航/关闭标签仅清理 UI 订阅（后端执行继续，重新打开经 `Reconcile` 恢复进度）；切换会话本就不取消（`ReconcileIncompleteTasksAsync` 仅处理无活跃执行的孤儿任务）；用户主动取消走 `OnCancelExecution`（保留）；程序关闭由进程终止天然取消。

---

## 9. 数据流总览

```
父会话 Loop
  └─ task 工具（前台/后台）
       ├─ CreateChildAsync + Metadata[OriginToolCallId] = callId
       └─ SubmitAsync → 子会话执行（ExecutionEventPublisher 按 sessionId 广播）

UI
  Session.razor
    ├─ Router.GetOrCreateConsumer<EventStreamHandler>(父会话Id) → 父会话渲染
    ├─ Router.AttachConsumer(父会话Id, handler + aggregator)
    └─ aggregator（父流 consumer）识别 task toolCall → AttachFromToolCall
        └─ Router.AttachConsumer(taskId, aggregator) → 订阅子会话流
            └─ ToolCallEvent(子) → SessionTaskStep 写回父 toolCall.TaskSteps → SaveAsync(父) → AssistantChanged
  Session.razor
    ├─ handler.OnStateChanged → 时间线同步 + 执行态清理
    └─ aggregator.AssistantChanged → Timeline.SyncAssistantMessage(owner)

子会话完成
  ├─ 后台: InjectSynthetic + TryResumeWhenIdleAsync（后端现有逻辑）
  └─ 聚合器: ExecutionCompleteEvent → 终态落盘 → Detach
```

---

## 10. 测试

| 层 | 必测 |
|----|------|
| TaskToolTests | 创建子会话后 `Metadata[OriginToolCallId]` 正确写入并落盘；续跑不覆盖；前台/后台路径回归 |
| ChatOrchestrator 单测 | Singleton 化后依赖解析正常；`_permissionChannel` 移除无影响 |
| SessionEventStreamRouter 单测 | Attach/Detach 生命周期；同会话多消费者广播；**重放引用快照去重（skipSet 按 loop 创建时快照，历史不广播）**；**replay=true 消费者补历史 + 竞态窗口幂等吸收**；**loop 自然结束（CompleteSession）广播 OnStreamEnd 并移除订阅**；**Detach 经 consumer→circuit 反向映射释放 scope**；**单消费者异常隔离**；无消费者释放；**Scope 管理（circuit 关闭释放）** |
| EventStreamHandler 单测 | 绑定 sessionId 独立状态；ExecutionId 匹配终态（排队不误清）；多实例互不干扰；Todo/Budget 查询属性 |
| TaskCardAggregator 单测 | 关联解析（TaskId 优先/枚举匹配/`LoadChildrenFromStorageAsync` 冷缓存兜底）；父流识别 task toolCall 自动挂载；**幂等（Reconcile 与父流识别重复）**；TaskSteps 合并与**防抖落盘（含串行化链）**；终态 Detach；**续跑重置**；**并发写契约（只写 TaskSteps/Error/TaskId，整体替换引用）**；多实例并行；**Rebind 会话切换**；Detach 释放 consumer-scope |
| WebUI 集成 | 执行中卡片有子会话 ID + 进度；刷新恢复（含已完成任务）；切换会话不串台（Rebind）；多标签独立；**CircuitContext 关联** |

---

## 11. 决策记录

| 项 | 选择 |
|----|------|
| 恢复服务端 Task 事件 | 否（纯 UI 层聚合） |
| 关联机制 | TaskTool 写子会话 `Metadata[OriginToolCallId]`，UI 枚举精确匹配 |
| 多子代理 | taskId 为 key 多订阅并行 |
| 执行路径 | 前台/后台执行语义不变 |
| 订阅基础设施 | `SessionEventStreamRouter` Singleton，每会话流只订阅一次、广播多消费者 |
| ChatOrchestrator | 变 Singleton（移除死字段 `_permissionChannel`），Router 不绕过它 |
| 消费者 | `IStreamConsumer` 策略，per-circuit 实例（不缓存跨 circuit 共享） |
| 页面获取 | `Router.GetOrCreateConsumer<T>(sessionId, circuitId)` 工厂，页面显式传 circuitId |
| 终态处理 | consumer 统一处理，handler 按 ExecutionId 匹配（排队安全） |
| 渲染状态 | EventStreamHandler 本期多实例化（解耦 SessionState，绑 sessionId） |
| TaskSteps 持久化 | 防抖落盘（1s）+ 终态立即落盘 + Detach/circuit 关闭 flush |
| 聚合器粒度 | 每父会话一实例，内部多 taskId；同时作父流 consumer 自动挂载 |
| 清理 | `SeeingCircuitHandler` → `DetachAllForCircuit` 防泄漏 |
| 重放去重 | 引用快照去重（skipSet 按 loop 创建时快照构建，历史不广播），仅渲染消费者；replay=true 消费者 Attach 时补历史后收实时 |
| 磁盘补载 | 新增 `SessionManager.LoadChildrenFromStorageAsync(parentId)`（`LoadAsync(父)`不填子缓存、`LoadAllFromStorageAsync`过重） |
| OnStateChanged | 携带触发事件 `OnStateChanged(IMessageEvent evt)`，供页面精确复刻节流语义 |
| 保存串行化 | 同父会话防抖/flush Save 用 per-session 串行队列，防旧快照覆盖新快照 |
| Scope 管理 | Router 持有 `IServiceScopeFactory`，circuit→(scope, consumer) 映射，circuit 关闭释放 |
| Circuit 上下文 | 新增 Scoped `CircuitContext`，页面 scope 内解析后显式传参（Router 不自建 scope 读取） |
| 聚合器获取 | 统一经 `Router.GetOrCreateConsumer<TaskCardAggregator>` + `Rebind`（非页面 @inject） |
| 并发写 | 聚合器只写 TaskSteps/Error/TaskId（整体替换 TaskSteps 引用），不碰 Status/Result |
| 广播隔离 | 单消费者 try/catch，异常不终止 consume loop |
| 幂等 | AttachFromToolCall 首查 _tasks；AttachConsumer 去重 |
| 流式节流 | 页面 `OnHandlerChanged` 回调内保留节流（不并入 handler） |
| 大屏 | 本期提供基础设施与架构支持，页面本身后续实现 |

---

## 12. 最终实现状态与偏差（2026-08-27 合并后）

实现分支 `feat/task-card-ui-aggregation` 已 fast-forward 合入 `master`（截至 `b5f5c74`）。全部 13 个提交、约 +2516/-260 行。全量测试结果：**Seeing.Agent.Tests 766/766、Seeing.Agent.WebUI.Tests 95/95、Seeing.Session.Tests 100/100**，全解决方案编译 0 错误。

### 12.1 最终代码审查修复（合并前签字轮后）

合并前经最终整体审查发现 1 Critical + 3 Important，已在 `19e5917` 全部修复：

| 编号 | 问题 | 最终实现修正 |
|------|------|--------------|
| **C1（Critical）** | `GetOrCreateConsumer` 按 `(session, type)` 键，两个标签页（独立 circuit）打开同一会话**共享**同一 handler/聚合器，任一关闭即破坏另一标签实时流并可能 Dispose 共享锁（违反"consumer 不跨 circuit 共享"） | consumer 键改为 **`(circuitId, sessionId, type)`**；`_mainConsumerBySession` 改 `(circuitId, sessionId)`；`GetOrCreateConsumer/DetachConsumer/DetachAllForCircuit/ReleaseConsumer` 全部按含 circuit 键；`DetachAllForCircuit` 先从 `_consumerCircuit` 收集该 circuit 全部 consumer 统一释放。新增测试：`GetOrCreateConsumer_SameSessionDifferentCircuits_ShouldReturnDistinctInstances`、`DetachAllForCircuit_ShouldNotAffectOtherCircuitConsumers` |
| **I1（Important）** | 会话空闲清理（`ExecutionEventPublisher.CompleteSession` 30min）后 loop 自然结束，同页新提交不触发 AttachConsumer，新执行事件无 loop 消费、页面静默失效 | `AttachConsumer` 幂等：消费者已挂载但 `sub.Loop == null`/`IsCompleted` 时**重建 consume loop**（重建 skipSet+cts+`ConsumeLoopAsync`，引入 `LoopGeneration` 代次防竞态）；页面侧 `Session.razor.OnSubmitMessage` 提交前 `Router.AttachConsumer(SessionState.SessionId, _handler)` 触发重启。新增测试：`AttachConsumer_AfterStreamCompleted_ShouldRestartLoopAndDeliverEvents` |
| **I2（Important）** | 聚合器按 `(session,type)` 实例化，每访问新父会话新建实例，旧父会话聚合器及其订阅/scope 永不释放（仅 circuit 关闭清理），资源随访问会话数累积 | **采用方案 A**：新增 circuit 维度复用键 `_circuitConsumersByKey[(circuit, type)]`，`GetOrCreateConsumer<TaskCardAggregator>` 对**同 circuit 不同 session 返回同一聚合器实例**（不登记主 consumer，仅 circuit 关闭释放）；`EventStreamHandler` 仍按 `(circuit, session)` 分实例。每 circuit 至多 1 个聚合器。新增测试：复用/跨 circuit 隔离 |
| **I3（Important）** | circuit 关闭先 `scope.Dispose()`，防抖窗口内未落盘 TaskSteps 丢失；in-flight `FlushPersistAsync` 在 disposed `_saveLock` 抛异常 | `TaskCardAggregator` 实现 `IDisposable`：`Dispose()` 先 `FlushPersistAsync()`（try/catch）再释放 `_saveLock`/`_writeLock`/Timer；`Router.ReleaseConsumer` 释放 scope 前若 consumer 为 `IDisposable` 先 `Dispose()`。新增测试：`Dispose_ShouldFlushDirtyTaskStepsBeforeReleasingLocks` |
| **M3（Minor）** | 刷新/切换产生新 handler，`_activeExecutionId=null`，首个 Complete 即清执行态（UI 长期空闲态） | `EventStreamHandler.RestoreExecution(string executionId, ExecutionStatus status = Running)` 恢复 `_activeExecutionId`+`ExecutionStatus`；`Session.razor.EnsureStreamBinding` 绑定后若 `overview.CurrentExecution` 非终态则调用 |

### 12.2 与正文设计偏差（明确记录）

- **聚合器粒度（§5.3 / §7.1）**：正文写"每父会话一实例"。**最终实现为"每 circuit 一实例（按 circuitId 复用），切换父会话经 `Rebind` 重绑 `ParentSessionId` 并清空 `_tasks`"**（I2 方案 A）。即聚合器跨父会话复用而非每父会话新建，`Rebind` 语义不变。
- **C1 consumer 键（§5.2）**：正文"per-circuit 实例（Scoped）"描述正确，但**键具体为 `(circuitId, sessionId, type)`**，且 `DetachAllForCircuit` 从 `_consumerCircuit` 反向映射收集该 circuit 全部 consumer 统一释放（正文未细化此释放路径）。
- **结果摘要截断（UI 渲染层，不在本 spec 范围）**：父 Task 卡片"结果摘要"原本在 `TaskMessageComponent.razor` 被 `@Truncate(ToolCall.Result, 400)` **硬截断 400 字符**（即使展开也只显示前 400 字）。根因：上游 `ToolCall.Result` 完整（`ChatEventTracker.cs:124` `existing.Result = toolCall.Output`，无截断），唯一切断点在 UI。最终修复（`b5f5c74`）改为直接 `@ToolCall.Result`（`is-collapsed` 类提供展开+滚动），完整展示子会话结果。

### 12.3 已知边界（仍记录，未本期修复）

- **刷新瞬时流尾弱化（M7）**：skipSet 使 handler 丢弃 buffer 中流尾 delta，刷新后下一事件才渲染。
- **排队闪断（M-6）**：同会话排队 exec1+exec2 时，exec1 Complete 清态、exec2 Started 恢复，短暂"执行中→空闲→执行中"。
- **页面刷新/关闭不取消执行（M-7 已修复 2026-08-27）**：`Session.razor.Dispose` 不再 `CancelBySessionAsync`/标记取消（`9af835c`），刷新/导航/切换会话后端执行继续；主动取消（`OnCancelExecution`）与程序关闭（进程终止）仍取消。注意：关闭浏览器后前台执行会继续跑完（可接受的资源权衡，与"后台任务应继续"一致）。
- **M1 SafeInvoke 静默吞异常无日志**：最终修复保留隔离语义但未补日志（诊断时可加）。
- **M6 `LoadChildrenFromStorageAsync` 未命中即全量 `ListAsync` 扫描**：重试 10×500ms 每次可能全表扫，会话量大时建议加缓存 TTL（后续优化）。

### 12.4 提交清单（合并至 master）

```
5a437e4 feat(task): TaskTool 写子会话关联元数据 origin_tool_call_id
33cbec0 refactor(orchestrator): ChatOrchestrator 移除死字段并变 Singleton
1ca3474 feat(session): SessionManager.LoadChildrenFromStorageAsync 磁盘枚举子会话
4a00ace feat(webui): 统一多流订阅基础设施 SessionEventStreamRouter
6f32d35 refactor(webui): EventStreamHandler 多实例化（绑定 sessionId，解耦 SessionState）
95a48e7 feat(webui): TaskCardAggregator 子会话事件聚合 TaskSteps
3159e27 fix(webui): 修复多流聚合并发与 Router scope 泄漏，补 DI 注册
e66f24f fix(tests): FakeSessionManager 补 LoadChildrenFromStorageAsync 实现
d1bebee feat(webui): Session 页经 Router 订阅多流，移除自写事件循环
aec881a fix(webui): 页面刷新/切换后 Reconcile 补聚合 TaskSteps
97739e9 docs(webui): 明确 EventStreamHandler 占位注册为权限事件总线（不可移除）
19e5917 fix(webui): 修复跨 circuit consumer 共享/流重启/聚合器泄漏/circuit 关闭 flush
b5f5c74 fix(webui): TaskCard 结果摘要移除 400 字符硬截断，完整展示子会话结果
```
