# 13 单一权威写入者（EventStreamHandler 去写）Release Notes

**日期：** 2026-10-09
**设计规格：** [`2026-10-09-single-writer-eventstream-design.md`](../superpowers/specs/2026-10-09-single-writer-eventstream-design.md)
**实施计划：** [`2026-10-09-single-writer-eventstream.md`](../superpowers/plans/2026-10-09-single-writer-eventstream.md)
**相关模块：** `Seeing.Agent.Hosting`（ChatEventTracker / ChatOrchestrator / TaskTool）、`Seeing.Agent.Abstractions`、`Seeing.Agent.WebUI`

---

## 1. 概要

将执行期 `SessionData` 消息内容的写入收敛为**唯一权威写入者** `ChatEventTracker`（Hosting）。WebUI `EventStreamHandler` 从「冗余投影写入者」退化为**纯 UI 投影**：只读 `SessionData` + 维护 UI 本地态。Task* 字段改由 `TaskTool` 结果 Metadata 驱动；取消/加载边界的未完成标记上收服务端。

## 2. 背景

`ExecutionJobService` 执行期**先投影（`ChatEventTracker`）再发布事件**；`EventStreamHandler` 收到事件后又重复写同一 `SessionMessage`/`SessionToolCall`（建/改工具调用、`Status/Result/Error/Title/Metadata/DurationMs`、`SelectedAcpMode`），与权威写入者并发且语义分叉。

## 3. 契约（写入唯一性）

- 执行期仅 `ChatEventTracker` 写 `SessionData` 消息内容。
- 宿主只读投影；需写领域模型时上提 `IChatOrchestrator` 服务端方法。
- 契约写入 [`04 开发规范](04-development-standards.md)` §5.2 + 反模式表。

## 4. 破坏性变更清单

| 类型 / 成员 | 变更 |
|-------------|------|
| `IChatOrchestrator` | **新增** `Task<int> ReconcileIncompleteTasksAsync(string, CancellationToken)` |
| `EventStreamHandler` | **删除** `MarkIncompleteTasksCancelledAsync`、`ReconcileOrphanTaskCardsAsync` |
| `EventStreamHandler` | **删除**死代码 `GetCurrentAssistantContent/Reasoning/ToolCalls`、`GetToolCallPositions` |
| `EventStreamHandler.HandleToolCall` | 不再写 `SessionData`（仅绑定指针 + todowrite UI 本地态） |
| `EventStreamHandler.HandleModeUpdate` | 不再写 `SelectedAcpMode`（`ChatEventTracker` 已写） |
| `ToolResult.Metadata`（task 工具） | **新增** 4 键：`task_id`/`task_agent`/`task_description`/`task_background`（增量） |

新增常量类 `Seeing.Agent.Abstractions.Tools.TaskMetadataKeys`。

## 5. 行为差异

- WebUI 不再在收到工具事件时写入会话；工具调用状态/结果完全以服务端投影为准（事件到达前已写好）。
- Task 卡片的 `Task*` 展示字段：权威来源为 `TaskTool` 结果 Metadata；`Running` 态（无 Metadata）由 `ChatEventTracker` 从 `Arguments` 回填 `description/subagent_type/background`，`Complete` 时以 Metadata 覆盖。
- 用户主动取消：未完成 task 卡片由服务端 `CancelBySessionAsync → 执行体 catch(OCE) → IncompleteToolCallMarker` 落盘（UI 不再标记）。
- 加载会话：残留「执行中」task 卡片由 `IChatOrchestrator.ReconcileIncompleteTasksAsync` 服务端标记并落盘；**会话自身仍有活跃执行时直接跳过**（避免前台 task 在途、`TaskId` 未回填期间被误判为孤儿）。

## 6. 迁移对照

| 旧写法（宿主） | 新写法 |
|----------------|--------|
| `handler.MarkIncompleteTasksCancelledAsync(...)` | 删除调用（服务端取消路径已处理） |
| `handler.ReconcileOrphanTaskCardsAsync(pred)` | `ChatOrchestrator.ReconcileIncompleteTasksAsync(sessionId)` |
| 宿主内联写 `tc.Status/Result/...` | 删除；由 `ChatEventTracker` 权威写入 |
| 宿主从 Output 文本解析 `task_id` | 读取事件/结果 `Metadata[TaskMetadataKeys.TaskId]` |

## 7. 验证

- 全解决方案 `dotnet build Seeing.Agent.slnx` 0 error。
- `Seeing.Agent.Tests` 1112 通过（含新增 `ChatEventTrackerTaskFieldTests`、`ChatOrchestratorReconcileTests`、`TaskTool` metadata 断言）。
- `Seeing.Agent.WebUI.Tests` 291 通过（写断言测试改为「不写会话模型」）。
- `Seeing.Session.Tests` 219 通过。

## 8. 已知边界

- `TaskCardAggregator` 仍写父会话 `SessionToolCall.TaskSteps/TaskId/Error`（子流聚合的既定设计），本次不动。
- `Running` 态 Task* 依赖 LLM 按 schema 传参；未传则不回填（与旧行为一致）。
- 极端进程终止下取消未走 `catch(OCE)` 时，仍依赖加载期 reconcile 兜底。
- **例外**：宿主在**非执行期**注入的会话级系统消息（Gateway 取消/错误横幅 `AddMessage`、会话重置）与 `SelectedModel/SelectedAcpMode` 用户显式选择，不属执行期投影，仍由宿主负责。
