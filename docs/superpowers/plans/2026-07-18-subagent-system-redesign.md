# 子 Agent（Task）系统重设计 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Session-first 重构子 Agent：真实 Child Session、`task`/`task_status`、Task* 投影、前台+后台、WebUI Task 卡片。

**Architecture:** 删除 Executor `task` 特判；Child=`SessionKind.SubAgent`；`ITaskEventProjector` 降采样进父事件流；`IAgentLoopScheduler` Inject+idle resume；Job Id≡Child Id。规格：`docs/superpowers/specs/2026-07-18-subagent-system-redesign.md`。

**Tech Stack:** .NET 10 / C# / xUnit / FluentAssertions / Moq / Blazor WebUI

## Global Constraints

- `task_id` ≡ Child Session Id ≡ Job Id（禁止 `bg_*`）
- 权限派生对齐 OpenCode deriveSubagentSessionPermission（不做全量交集）
- Ask 全局串行；工具先 Pending/Running 再执行
- 投影唯一：`ITaskEventProjector` + 嵌套 yield
- Resume 权威在引擎 Scheduler
- **不要 git commit**（用户明确要求）
- plans/specs 在 gitignore；若需纳入版本库用 `git add -f`，但仍不 commit

## File Map

**Session:** `src/Seeing.Session/Core/SessionKind.cs`, `SessionPermissionRule.cs`, `SessionData.cs`, `ISessionManager.cs`, `Management/SessionManager.cs`, `SessionForker.cs`

**Agent:** `Core/Permission/SubagentPermissionDeriver.cs`, `SessionPermissionMapper.cs`, `Core/Events/MessageEventTypes.cs`, `ITaskEventProjector.cs`, `TaskEventProjector.cs`, `TaskProjectionContext.cs`, `Core/Scheduling/IAgentLoopScheduler.cs`, `AgentLoopScheduler.cs`, `Core/Background/*`, `Tools/BuiltIn/Task/TaskTool.cs`, `TaskStatusTool.cs`, `Tools/ToolInvoker.cs`, `Core/AgentExecutor.cs`, `Extensions/ServiceCollectionExtensions.cs`

**WebUI:** `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`, `Components/Messaging/TaskMessageComponent.razor`, `Models/Messaging/ContentBlock.cs`, `Rendering/RenderingServiceExtensions.cs`

**Tests:** `tests/Seeing.Session.Tests/Management/SessionChildTests.cs`, `tests/Seeing.Agent.Tests/Permission/SubagentPermissionDeriverTests.cs`, `Events/TaskEventProjectorTests.cs`, `Scheduling/AgentLoopSchedulerTests.cs`, `Tools/TaskToolTests.cs`, `TaskStatusToolTests.cs`, `Core/AgentExecutorToolOrderingTests.cs`, modify `Tools/AgentModeFilterTests.cs`

---

### Task 1: SessionKind + CreateChild

**Produces:** `SessionKind`; `CreateChildAsync`; `ListRootsAsync`; `ListChildrenAsync`

- [ ] **Step 1: 写失败测试** in `SessionChildTests.cs`: `CreateChildAsync_ShouldSetSubAgentKindAndEmptyHistory`; `ListRootsAsync_ShouldExcludeForkAndSubAgent`; `ListChildrenAsync_FilterByKind_ShouldDistinguishForkAndSubAgent`
- [ ] **Step 2:** `dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionChildTests"` → FAIL
- [ ] **Step 3:** Implement SessionKind (Root/Fork/SubAgent), SessionPermissionRule, SessionData fields, CreateChildAsync (no message copy), Fork→Kind=Fork, ListRoots/ListChildren
- [ ] **Step 4:** Same test → PASS
- [ ] **Step 5:** Skip commit（用户要求不 commit）

### Task 2: SubagentPermissionDeriver

**Produces:** `Derive(parentSessionRules, parentAgent, subagent)`

Algorithm: parent agent edit/write denies → parent session denies + directory → default deny todowrite/task unless sub allows. NO full intersect.

- [ ] **Step 1:** Tests for Plan edit deny; sub Allow task; parent session deny bash
- [ ] **Step 2:** filter SubagentPermissionDeriverTests → FAIL
- [ ] **Step 3:** Implement deriver + mapper
- [ ] **Step 4:** PASS
- [ ] **Step 5:** Skip commit

### Task 3: Task* events + Projector

**Produces:** TaskStarted/Progress/Completed/Failed; `ITaskEventProjector.Project`

- [ ] **Step 1:** ToolCallRunning→TaskProgress; StreamDelta not mirrored
- [ ] **Step 2-4:** Red → implement → green
- [ ] **Step 5:** Skip commit

### Task 4: LoopScheduler + Job Id unify

**Produces:** `InjectSyntheticAsync`; `TryResumeWhenIdleAsync`; `IsLoopBusy`; Job Id=TaskId (no bg_)

- [ ] **Step 1:** busy→resume false; StartAsync id==child id
- [ ] **Step 2-4:** Red → implement → green
- [ ] **Step 5:** Skip commit

### Task 5: TaskTool + TaskStatusTool

**Produces:** tools task/task_status; ToolContext.EmitAsync

- [ ] **Step 1:** Primary reject; background running+task_id; conflict; timeout
- [ ] **Step 2-4:** Rewrite TaskTool; add TaskStatusTool; Inject+Resume on complete
- [ ] **Step 5:** Skip commit

### Task 6: Executor cleanup + ordering + whitelist

- [ ] **Step 1:** Primary includes task; Pending before Complete
- [ ] **Step 2-4:** Delete HandleSubAgentCallAsync; Channel merge; Ask serial; whitelist
- [ ] **Step 5:** Skip commit

### Task 7: WebUI Task card

- [ ] **Step 1:** Handler unit test
- [ ] **Step 2-4:** HandleTask*; TaskMessageComponent; open child by task_id
- [ ] **Step 5:** WebUI smoke
- [ ] **Step 6:** Skip commit

### Task 8: Cleanup + regression

- [ ] **Step 1:** Run Session + Agent filtered tests → PASS
- [ ] **Step 2:** Checklist vs spec §12
- [ ] **Step 3:** Skip commit

## Spec Coverage

§4→T1/T4; §5→T2/T5/T6; §6→T3-T6; §7→T7; §8-10→T6-T8
