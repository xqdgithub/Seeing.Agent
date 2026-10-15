# EventStreamHandler 去写（单一权威写入者）Implementation Plan

> 快速实现：改动集中在少数文件，直接按任务顺序实现；最后整体审查 + 回归。

**Goal:** 让 `ChatEventTracker` 成为执行期 `SessionData` 消息内容的唯一写入者，`EventStreamHandler` 退化为纯 UI 投影；Task* 字段由 `TaskTool` Metadata 驱动。

**Architecture:** 服务端权威投影（先写后发）已有；本次删除 WebUI 冗余写入、上收取消/加载边界写入、把 Task* 解析改由事件 Metadata 驱动。

**设计规格:** `docs/superpowers/specs/2026-10-09-single-writer-eventstream-design.md`

---

## 任务

### T1 Abstractions：Task 元数据键
- [ ] `ToolMetadataKeys`（或等价）新增 `TaskId="task_id"`/`TaskAgent="task_agent"`/`TaskDescription="task_description"`/`TaskBackground="task_background"`。
- [ ] `IChatOrchestrator` 新增 `Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken cancellationToken = default)`。

### T2 TaskTool：结果发 Metadata
- [ ] `Success(...)` 的 metadata 字典补 4 个 task 键（成功/失败/后台各返回路径）。

### T3 ChatEventTracker：映射 Task*
- [ ] `ToolCallEvent` 分支：Metadata 有 task 键 → 写 `SessionToolCall.Task*`；`Name=="task"` 且字段空时从 `Arguments` 回填 `TaskDescription/TaskAgent/TaskBackground`；`TaskId` 仅来自 Metadata。

### T4 ChatOrchestrator：ReconcileIncompleteTasksAsync
- [ ] 实现：扫描消息，`Name=="task"` && status running/pending && `!GetOverview(id).HasActiveExecution` → `Status="cancelled"`, `Error=reason`；落盘；返回数。

### T5 EventStreamHandler：去写
- [ ] 删除 `HandleToolCall` 会话写入（保留指针绑定）、`HandleModeUpdate` 写入、`MarkIncompleteTasksCancelledAsync`、`ReconcileOrphanTaskCardsAsync`、`TryFillTaskFields*`、`FormatArgumentsJson`、`MapToolCallStatus`、`IsIncompleteTaskToolCall`、死代码读取方法/字段。
- [ ] 顶部 using 清理。

### T6 SessionWindow：改调服务端
- [ ] `OnCancelExecution` 删除 `MarkIncompleteTasksCancelledAsync` 调用。
- [ ] `ReconcileIncompleteTasksAsync` 改调 `ChatOrchestrator.ReconcileIncompleteTasksAsync`。

### T7 测试
- [ ] 删/改 `EventStreamHandlerTests` 写断言测试。
- [ ] 新增 `ChatEventTracker` Task* 映射测试、`TaskTool` metadata 测试、`ChatOrchestrator` reconcile 测试。

### T8 文档 + 回归
- [ ] 更新 `04-development-standards.md`（单写者契约）+ `12-*` release notes 或新增 release notes。
- [ ] 全量构建 0 error；三套测试回归。
