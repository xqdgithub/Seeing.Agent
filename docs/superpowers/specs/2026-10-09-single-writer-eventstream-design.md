# EventStreamHandler 去写（单一权威写入者）设计

**日期:** 2026-10-09
**状态:** 待实施
**范围:** `Seeing.Agent.Hosting`（ChatEventTracker / ChatOrchestrator / TaskTool）+ `Seeing.Agent.Abstractions`（IChatOrchestrator / Task 元数据键）+ `Seeing.Agent.WebUI`（EventStreamHandler / SessionWindow）
**触发:** `SessionMessage` 内容不可变契约（`2026-10-08`）落地后，仍需收敛「同一 `SessionData` 多写入者」的结构性问题
**约束:** 按产品方惯例，本设计文档不自动提交 git

---

## 1. 背景与问题

`SessionData` 消息内容在**执行期**存在两个写入者：

- `ChatEventTracker`（Hosting，权威）：`ExecutionJobService:646` **先投影再 `Publish:649`**，UI 收到事件时会话已写好。
- `EventStreamHandler`（WebUI，冗余投影）：`HandleToolCall`/`HandleModeUpdate` 在同一批 `SessionMessage`/`SessionToolCall` 上重复写入（建/改工具调用、`ToolCalls.Add`、`Status/Result/Error/Title/Metadata/DurationMs`、`SelectedAcpMode`），且与 `ChatEventTracker` 并发。

此外 `EventStreamHandler` 的两个边界方法（`MarkIncompleteTasksCancelledAsync`、`ReconcileOrphanTaskCardsAsync`）在取消/加载时也直接写 `SessionData`，同属「UI 写领域模型」。

多写入者即使有不可变契约，仍是**架构债**：写入语义分叉、真相源模糊、每加一个事件字段都要两处同步、无 UI 宿主（TUI/Gateway/Headless）的投影行为不一致。

## 2. 目标

| 目标 | 验收要点 |
|------|----------|
| 单一权威写入者 | 执行期仅 `ChatEventTracker` 写 `SessionData` 消息内容 |
| EventStreamHandler 纯 UI 投影 | 只读 `SessionData` + 维护 UI 本地态，不再写领域模型 |
| Task* 字段不丢失 | `task_id/task_agent/task_description/task_background` 仍正确写入并由各宿主共享 |
| 边界写入上收服务端 | 取消/加载的未完成标记由 Hosting 负责，UI 不再写 |
| 零读取方改动 | 渲染/落盘/LLM 出站读取路径不变 |

## 3. 非目标

- 不改造 `TaskCardAggregator`（子流聚合，独立职责，非执行期父会话写入者）。
- 不改 `ChatEventTracker` 既有投影逻辑（仅**新增** Task* 映射）。
- 不改造 `SessionData.Metadata/Context/State`、`SessionGroup`、Permission 等其它共享模型。
- 不引入跨进程写协调。

---

## 4. 架构契约

> **执行期 `SessionData` 消息内容只有唯一写入者 `ChatEventTracker`（Hosting）。**
>
> - WebUI/TUI/Gateway 等宿主均为**只读投影**：订阅事件 → 维护本地 UI 态 / 从 `SessionData` 派生视图。
> - 宿主不得直接写**执行期**消息内容（`SessionMessage`/`SessionToolCall` 的字段，含元素标量）。
> - 需要「投影写入」的语义（如 task 字段、未完成标记）由 Hosting 在权威路径完成，再经事件/查询暴露。
> - **例外**：宿主在**非执行期**注入的会话级系统消息（如 Gateway 取消/错误横幅 `AddMessage`、会话重置）与 `SelectedModel/SelectedAcpMode` 用户显式选择不属执行期投影，仍由宿主负责。

该契约与「消息内容发布即不可变」（`04-development-standards.md` §5.1）互补：前者约束**谁写**，后者约束**怎么写**。

## 5. 方案

| 方案 | 结论 |
|------|------|
| A. `ChatEventTracker` 独占写入，`EventStreamHandler` 纯读 | **采用** |
| B. `EventStreamHandler` 独占 | 否决（无 UI 宿主丢投影） |
| C. 抽公共投影器两者共用 | 否决（仍是双调用点，未消除双写） |

---

## 6. EventStreamHandler 去写清单

| 成员 | 现行为 | 处置 |
|------|--------|------|
| `HandleToolCall` | 建/改 `SessionToolCall`、`ToolCalls.Add`、`LoopId`、`Status/Result/Error/Title/Metadata/DurationMs` | 删除全部会话写入；仅保留 `EnsureCurrentAssistantMessage`（只读指针绑定） |
| `HandleModeUpdate` | `session.SelectedAcpMode = evt.ModeId` | 删除（`ChatEventTracker:160` 已写） |
| `MarkIncompleteTasksCancelledAsync` | 写 `tc.Status/Error` | 删除（服务端取消路径 `IncompleteToolCallMarker` 已写） |
| `ReconcileOrphanTaskCardsAsync` | 写 `tc.Status/Error` + `SaveAsync` | 迁至服务端（`IChatOrchestrator.ReconcileIncompleteTasksAsync`） |
| `TryFillTaskFieldsFromArguments` / `TryFillTaskIdFromResult` | 写 `Task*` | 删除（改由 `ChatEventTracker` 从事件 Metadata/Arguments 映射） |
| `FormatArgumentsJson` / `MapToolCallStatus` / `IsIncompleteTaskToolCall` | 仅服务被删写入 | 删除 |
| `GetCurrentAssistantContent/Reasoning/ToolCalls`、`GetToolCallPositions`、`_accumulated*`、`_toolCallPositions` | 死代码（全仓无消费者） | 删除 |
| `IsStreamActive` / `ExecutionStatus` / 重试态 / `LastError` / Todo/Budget 投影 | UI 本地态 | 保留 |

保留的事件处理：`LoopStart/Complete`、`ExecutionStarted/Complete`、`StreamStart/Delta/Complete`（仅绑定指针）、`LlmRetry`、`LoopCancelled`、`Error`、`TodoUpdate`（UI 本地 Todo VM）、`BudgetStatus`、`SkillContent`、`Compaction*`、`Permission*`（广播回调）。

## 7. Task* 字段权威来源

`TaskTool` 在 `ToolResult.Metadata` 发出：

```
task_id          = 子会话 Id
task_agent       = subagent_type
task_description = description
task_background  = bool
```

- 常量集中定义于 Abstractions（如 `ToolMetadataKeys`），TUI 现有 `task_id`/`task_agent`/`task_description` 读取随之对齐。
- `ChatEventTracker` 将事件 `Metadata` 映射到 `SessionToolCall.Task*`。
- 因 `Running` 事件早于 `TaskTool` 执行（无 Metadata），**同轮对 `Name=="task"` 回填 `Arguments` 的 `description/subagent_type/background`**，保证 Running 态卡片不退化；`Complete` 事件的 Metadata 覆盖为权威值。

## 8. 取消 / 加载边界迁移

- `SessionWindow.OnCancelExecution`：删除 `MarkIncompleteTasksCancelledAsync` 调用。取消已由 `CancelBySessionAsync` → 执行体 `catch(OCE)` → `IncompleteToolCallMarker.MarkCancelled` 落盘。
- 新增 `IChatOrchestrator.ReconcileIncompleteTasksAsync(sessionId, ct)`（服务端实现）：扫描会话消息，将「`Name=="task"` 且 running/pending 且无活跃执行」的卡片标记 cancelled 并落盘；返回处理数。
- `SessionWindow.ReconcileIncompleteTasksAsync` 改调 `ChatOrchestrator.ReconcileIncompleteTasksAsync`（活跃判定在服务端 `GetOverview` 内完成，UI 不再回调）。

## 9. 测试策略

1. **删除/重写** `EventStreamHandlerTests`：移除 3 个「写会话」断言（`ShouldWriteToolCallToOwnSession`、`ShouldStillFillTaskIdFromResult`、`BashOutputWithTaskIdLiteral`）；保留 UI 本地态/事件广播/执行态测试。
2. **新增** `ChatEventTracker` 测试：`ToolCallEvent` Metadata → `Task*` 映射；`Running` 态 Arguments 回填。
3. **新增** `TaskTool` 测试：成功结果 Metadata 含 4 个 task 键。
4. **新增** `ChatOrchestrator.ReconcileIncompleteTasksAsync` 测试（无活跃执行标记 cancelled；有活跃执行不标记）。
5. **回归**：`Seeing.Agent.WebUI.Tests`、`Seeing.Agent.Tests`、`Seeing.Session.Tests`。

## 10. 破坏性变更

- `IChatOrchestrator` 新增 `ReconcileIncompleteTasksAsync`（加接口方法，实现方 `ChatOrchestrator` 同步；Mock 测试自动可用）。
- `EventStreamHandler` 删除公开方法 `MarkIncompleteTasksCancelledAsync`/`ReconcileOrphanTaskCardsAsync` 与 4 个死代码读取方法。
- `ToolResult.Metadata` 对 task 工具新增 4 个键（增量，不破坏既有消费者）。

## 11. 已知边界

- `ChatEventTracker` 对 `Running` 态 task 的 `task_description/agent/background` 来自 Arguments；若 LLM 未按 schema 传参则不回填（与旧 `EventStreamHandler` 行为一致）。
- 取消时若执行体未走到 `catch(OCE)`（极端进程终止），未完成标记仍依赖加载期 reconcile 兜底。
- `TaskCardAggregator` 仍写 `SessionToolCall.TaskSteps/TaskId/Error`（父会话消息）——属子流聚合的既定设计，本次不动（后续可评估）。
