# 通用后台活动体系 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立统一的后台活动控制面，让 `bash` 支持 `background=true`，并具备查询、等待、取消、级联中断与完成唤醒。

**Architecture:** 活动是控制面/观测面，执行仍由各自 launcher 负责；`IBackgroundActivityManager` 只暴露会话面（带 `requesterSessionId`），`IBackgroundActivityControl` 只给宿主与模块激活器。状态内存权威、终态原子落盘，完成通知经 outbox 合并投递。

**Tech Stack:** .NET 10、System.Text.Json、Microsoft.Extensions.DependencyInjection / Options / Logging、xUnit + Moq + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-20-background-activity-design.md`

## Global Constraints

- 不新增 `ToolCallStatus` 成员；原 tool call 终态仍是 `Success`。
- 能力包只能引用 `Abstractions` + 原语，**不得引用 `Seeing.Agent.Core`**。
- 新能力包必须登记 `BuiltInCapabilitySets` 与 `AddSeeingFullCapabilities`，否则模块永不 Activate。
- 文件写入走 `AtomicFileWrite` / `IPathWriteLock`；路径判定走 `PathBoundary`。
- 测试禁止固定时长等待；时间经 `TimeProvider` 注入。
- `activity.progress` 事件节流 ≥ 1s，且不进事件落盘白名单。
- `activity_output` 单页 ≤ 32KB，且声明 `output.skip=true`。
- launcher 不得引用 `ToolContext.CancellationToken` / `ToolContext.EventSink`。
- `IBackgroundActivityManager` 不得出现 `RegisterLauncher` / `UnregisterLauncherAsync` / `TrackAsync` / `StartAsSystemAsync` / `CancelBy*`。
- 会话面所有方法 `requesterSessionId` 不可空。
- 活动 id 不得经 `Metadata` 传递。

## Review Focus

- **重复后台化**：LLM 重试把 `npm run dev` 起两次造成端口冲突 → Task 13 断言 `DedupeKey` 生效。
- **宿主崩溃残留**：`bash -c "… &"` 的子进程在宿主退出后继续锁住 bin → Task 4 的 Job Object 测试。
- **连环唤醒**：5 个活动同时完成只应唤醒 1 次 → Task 11 的合并断言。
- **fork 丢关联**：会话 fork 后子会话看不到 `BackgroundActivityId` → Task 2 的 Clone 测试。
- **跨会话越权**：工具用他人 `OwnerSessionId` 启动或列出活动 → Task 7/14 的越权断言。

---

### Task 1: ExecutionId 贯通到工具层

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Agents/AgentContext.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolInvocation.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`（`ToolContext`）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:1008-1034`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatExecutionContext.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs:378`
- Modify: `src/capabilities/Seeing.Agent.Acp/Tools/AcpTool.cs:218`
- Modify: `src/spine/Seeing.Agent.Core/Core/ToolCallInvoker.cs:106`
- Modify: `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs:757-770`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/ToolContextExecutionIdTests.cs`

**Interfaces:**
- Produces: `AgentContext.ExecutionId` (string?)、`ToolInvocation.ExecutionId` (string?)、`ToolContext.ExecutionId` (string?)

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ExecuteAsync_ToolContext_ShouldCarryExecutionId()
{
    // 用假 ITool 捕获 ToolContext，经 ToolCallInvoker 传 executionId
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolContextExecutionIdTests"`
Expected: FAIL（`ExecutionId` 不存在）

- [ ] **Step 3: 实现贯通**

`ToolInvocation` 增加 `string? ExecutionId`（参数按注释"新增执行级事实加到这个记录里"）；`ToolContext` 增加 `string? ExecutionId`；`ChatExecutionContext` 增加 `string? ExecutionId`；`ExecutionRunner.BuildAgentContext` 接收 `record.ExecutionId` 填入；`AgentJob` 填 `$"sched:{jobId}:{runAt:O}"`；`AcpTool` 填 `context.ExecutionId`；`ToolCallInvoker` / `ToolManager` 逐层透传。修正 `ToolInvocation.cs:12` 注释为"唯一构造点是 ToolCallInvoker"。

- [ ] **Step 4: 运行确认通过**

Run: 同上。Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "refactor: 贯通 ExecutionId 到 ToolContext"
```

---

### Task 2: BackgroundActivityId 全链路投影

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResult.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`（`ToolCallEvent`）
- Modify: `src/spine/Seeing.Agent.Core/Core/ToolCallInvoker.cs`（4 处出口 + `BuildTerminalEvent:339`）
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:949`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs:126-174`
- Modify: `src/primitives/Seeing.Session/Core/SessionMessage.cs`（`SessionToolCall` + `Clone`）
- Modify: `samples/Seeing.Agent.WebUI/Components/.../ToolCallViewModel.cs:150-171`
- Modify: `samples/Seeing.Agent.Tui/Services/TuiEventInterpreter.cs:173-204`
- Modify: `samples/Seeing.Agent.Tui/Services/TuiViewState.cs:364-393`
- Modify: `src/gateway/Seeing.Gateway/Models/GatewayEventData.cs:6`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayEventMapper.cs:185-203`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/BackgroundActivityIdProjectionTests.cs`

**Interfaces:**
- Consumes: Task 1
- Produces: `ToolResult.BackgroundActivityId`、`ToolCallEvent.BackgroundActivityId`、`SessionToolCall.BackgroundActivityId`

- [ ] **Step 1: 写失败测试（含 fork）**

```csharp
[Fact]
public void ChatEventTracker_ShouldProjectBackgroundActivityId()
{
    // ToolCallEvent { BackgroundActivityId = "act_1" } → SessionToolCall.BackgroundActivityId == "act_1"
}

[Fact]
public void ForkedSession_ShouldKeepBackgroundActivityId()
{
    // SessionToolCall.Clone() 后字段保留
}
```

- [ ] **Step 2: 运行确认失败**

Run: `--TestCaseFilter:"FullyQualifiedName~BackgroundActivityIdProjectionTests"` → FAIL

- [ ] **Step 3: 加字段并逐段透传**

三处模型加 `string? BackgroundActivityId`；`ToolCallInvoker` 各出口从 result 赋值；`ChatEventTracker` 建对象/抄字段，落盘条件加 `backgroundActivityFilled`（`isNewToolCall || statusChanged || taskIdFilled || backgroundActivityFilled`）；`SessionToolCall.Clone()` 补拷贝；WebUI/TUI/Gateway 白名单加字段。

- [ ] **Step 4: 运行确认通过**

Run: 同上 + `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~SessionFork"` → PASS

- [ ] **Step 5: 提交**

```bash
git commit -am "feat: BackgroundActivityId 全链路投影"
```

---

### Task 3: InterruptAsync 签名与调用点语义

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionRuntime.cs:81`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs:1140-1160`
- Modify: `src/gateway/Seeing.Gateway.Client/InProcessSessionClient.cs:79`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewaySessionService.cs:52`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs:126`
- Modify: `samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs:679`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/InterruptSignatureTests.cs`

**Interfaces:**
- Produces: `InterruptAsync(string sessionId, CancellationToken ct = default, bool cancelBackgroundActivities = false)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task InterruptAsync_Default_ShouldNotCancelBackgroundActivities()
[Fact]
public async Task InterruptAsync_WithCascade_ShouldCancelByExecution()
```

- [ ] **Step 2: 运行确认失败**（签名不匹配）

- [ ] **Step 3: 改签名 + 修调用点**

按设计 §9.3 表：`InProcessSessionClient` / `GatewaySessionService` / `GatewayOrchestratorV2` 传 `true`；`SessionWindow.razor` / `InterruptThroughAsync` / `BrowserStreamEndpoints` 传 `false`。同步修 6 手写实现、3 Moq（显式传全三参）、1 反射契约测试。

- [ ] **Step 4: 运行全套测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
Expected: PASS（含既有 Interrupt 相关用例）

- [ ] **Step 5: 提交**

```bash
git commit -am "refactor: InterruptAsync 增加后台级联参数并固定各入口语义"
```

---

### Task 4: SubprocessScope 生命周期与 Job Object

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/SubprocessSpec.cs`
- Modify: `src/capabilities/Seeing.IO.Local/LocalSubprocessFactory.cs`
- Test: `tests/capabilities/Seeing.IO.Local.Tests/SubprocessLifecycleTests.cs`（Windows-only）

**Interfaces:**
- Produces: `SubprocessLifecycle { HostBound, Detached }`、`SubprocessSpec.Lifecycle`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Spec_DefaultLifecycle_ShouldBeHostBound()
[Fact]
public async Task HostBound_ShouldKillChildren_WhenHostJobCloses() // Windows-only，可 Skip
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

`SubprocessSpec` 加 `Lifecycle`（默认 `HostBound`）；`LocalSubprocessFactory` 对 `HostBound` 在 Windows 给每个进程创建独立 Job 并 assign（`KILL_ON_JOB_CLOSE`），失败记日志并降级；Unix 用 `setsid` / 进程组。**每进程一个 job，禁止共享。**

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat: SubprocessSpec 增加生命周期归属，HostBound 子进程随宿主回收"
```

---

### Task 5: LoopSkip 能力键

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolCapabilityKeys.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:681-707`
- Test: `tests/spine/Seeing.Agent.Tests/Tools/LoopSkipCapabilityTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Tool_WithLoopSkip_ShouldNotTriggerLoopDetection()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

加 `public const string LoopSkip = "loop.skip"`；`AgentExecutor` 在检测点用 **canonical 工具名** 查询能力，命中则**不调用** `RecordCallCore`（避免计数累积）。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat: 新增 loop.skip 能力键，轮询类工具豁免循环检测"
```

---

### Task 6: 删除 IsBackground 死字段

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Agents/AgentDefinition.cs:48`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Agents/AgentContext.cs:52`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatExecutionContext.cs:56,124`
- Modify: `src/capabilities/Seeing.Agent.Reflection/Integration/ReflectionAgentDefinition.cs:52`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs:385`
- Test: 编译即验证

- [ ] **Step 1: 删除 3 个声明与 3 处赋值**

- [ ] **Step 2: 构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error（若报错说明有隐藏读取点，停下来核查）

- [ ] **Step 3: 提交**

```bash
git commit -am "refactor: 删除 IsBackground 死字段，后台身份由活动 Origin 表达"
```

---

### Task 7: 契约层

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Background/BackgroundActivityEnums.cs`
- Create: `.../BackgroundActivitySnapshot.cs`
- Create: `.../BackgroundActivityRequests.cs`
- Create: `.../BackgroundActivityHandle.cs`
- Create: `.../IBackgroundActivityManager.cs`
- Create: `.../IBackgroundActivityControl.cs`
- Create: `.../IBackgroundActivityLauncher.cs`
- Create: `.../IBackgroundActivityReporter.cs`
- Create: `.../IBackgroundActivityOutbox.cs`
- Create: `.../IBackgroundActivityNotifier.cs`
- Create: `.../BackgroundActivityEvent.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/BackgroundActivityContractsTests.cs`

**Interfaces:**
- Produces: 设计 §6–§7 的全部类型与接口（签名以 spec 为准）

- [ ] **Step 1: 写契约测试**

```csharp
[Fact]
public void Manager_ShouldNotExposeSystemOperations()
{
    // 反射断言：RegisterLauncher / UnregisterLauncherAsync / TrackAsync /
    // StartAsSystemAsync / CancelBySessionAsync 不在 IBackgroundActivityManager 上
}

[Fact]
public void Manager_Methods_ShouldRequireNonOptionalRequesterSessionId()
{
    // requesterSessionId 参数无默认值、不可空
}
```

- [ ] **Step 2: 运行确认失败**（类型不存在）

- [ ] **Step 3: 按 spec §6/§7 实现全部类型**

`StartAsync` 签名 `(request, requesterSessionId, ct)`；系统方法只进 Control；`BackgroundActivityId` 不进 Metadata。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(abstractions): 后台活动契约"
```

---

### Task 8: Manager 状态机与取消树

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Background/BackgroundActivityManager.cs`
- Create: `.../BackgroundActivityRecord.cs`（内部可变态）
- Create: `.../BackgroundActivityOptions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Background/BackgroundActivityManagerTests.cs`

**Interfaces:**
- Consumes: Task 7
- Produces: `IBackgroundActivityManager` + `IBackgroundActivityControl` 实现

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void StartAsync_ShouldReturnRunningActivity()
[Fact] public void StartAsync_WithForeignRequester_ShouldThrow()
[Fact] public void Complete_After_Cancel_ShouldBeIgnored()   // CAS 先到先得
[Fact] public void List_ShouldOnlyReturnOwnSession()
[Fact] public async Task CancelAsync_ShouldCancelSubtree()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

内存 `ConcurrentDictionary`；每活动一个 `CancellationTokenSource`（链接 `TimeProvider` 超时与宿主停止）；`Running` 迁移限定 `Status == Pending`；终态 CAS；`Attributes` 不参与任何分支；`requesterSessionId` 校验按 §7.1 表。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(core): 后台活动 manager 状态机与取消树"
```

---

### Task 9: OutputStore

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Background/BackgroundActivityOutputStore.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Background/BackgroundActivityOutputStoreTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task AppendAsync_ShouldCapAtOutputLimit_AndMarkTruncated()
[Fact] public async Task ReadAsync_ShouldPageByOffset_AndNotExceed32KB()
[Fact] public async Task ReadAsync_WithForeignRequester_ShouldReturnEmptyPage()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

`FileStream.Append` + `IPathWriteLock`；尾部 64KB 内存缓冲；超限写 `[truncated]`；单页上限 32KB。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(core): 后台活动输出存储"
```

---

### Task 10: 快照落盘与孤儿扫描

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Background/BackgroundActivityStore.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Background/BackgroundActivityReconcileTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Running_ShouldWriteMinimalRecord()
[Fact] public async Task Reconcile_ShouldMarkDeadHostRunningAsInterrupted()
[Fact] public async Task Reconcile_ShouldNotTouchLiveHost()
[Fact] public async Task Reconcile_ShouldNotWriteOutbox()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

`{activityId}.json` 布局；`GetLock` 包住读改写，覆盖前重读校验 `Status`/`HostInstanceId`；`HostInstanceId = pid:启动时间`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(core): 活动快照落盘与孤儿收敛"
```

---

### Task 11: Outbox 与完成投递

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Background/BackgroundActivityOutbox.cs`
- Create: `.../BackgroundActivityNotificationDispatcher.cs`
- Create: `.../SessionRuntimeActivityNotifier.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Background/BackgroundActivityOutboxTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task PumpOnce_ShouldMergeFiveCompletionsIntoOneMessage()
[Fact] public async Task PumpOnce_Twice_ShouldNotRedeliver()
[Fact] public async Task PumpOnce_ShouldSkipObservedResults()
[Fact] public async Task PumpOnce_ShouldOnlyTakeOwnHostPending()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

批次先落盘 `digestId`；`PumpOnceAsync` 内部互斥闸；按会话串行；合并窗口用注入的 `TimeProvider`；`InboxMessage { Delivery = Wake, Presentation = System, SourceTag = "background.activity", IdempotencyKey = $"activity-digest:{digestId}" }`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(core): outbox 与完成通知合并投递"
```

---

### Task 12: process launcher

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Shell/ProcessBackgroundActivityLauncher.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Shell/ShellModule.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Shell.Tests/ProcessBackgroundActivityLauncherTests.cs`

**Interfaces:**
- Consumes: Task 7、Task 4
- Produces: `Kind = "process"`、`Capabilities = CanCancel | CanWait | CanStreamOutput | SignalsCompletion`

- [ ] **Step 1: 写可控 fake 与失败测试**

```csharp
[Fact] public async Task Launch_ShouldReturnImmediately_WhileProcessStillRunning()
[Fact] public async Task Cancel_ShouldKillProcessTree()
[Fact] public async Task Output_ShouldStreamToReporter()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

用 `IExecutionWorld.Subprocess.Start`（`SubprocessSpec.Lifecycle = HostBound`）；**不链接 `ToolContext.CancellationToken`**，只用 `ctx.Handle.CancellationToken`；stdout/stderr 逐行 `AppendOutputAsync`；退出后 `Complete`；启动失败 `Fail`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(shell): process 后台活动 launcher"
```

---

### Task 13: bash background 参数

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Shell/BashTool.cs`
- Modify: `tests/capabilities/Seeing.Agent.Tools.Shell.Tests/BashToolTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Shell.Tests/BashToolBackgroundTests.cs`

**Interfaces:**
- Consumes: Task 7、Task 12
- Produces: `ToolResult.BackgroundActivityId`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task BackgroundTrue_ShouldReturnActivityId_AndNotWait()
[Fact] public async Task BackgroundTrue_WithSameDedupeKey_ShouldRejectSecond()
[Fact] public async Task BackgroundTrue_WhenManagerMissing_ShouldFail()
[Fact] public async Task BackgroundTrue_WhenModuleDisabled_ShouldFail()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

构造函数注入 `IBackgroundActivityManager`；`background=true` 时构造 `StartRequest`（`Payload` 放 command/workdir/timeout、`OwnerSessionId` 与 `OwnerExecutionId` 来自 `ToolContext`、`DedupeKey` 由本工具计算）并返回句柄；`timeout` 被 `activity_timeout` 取代。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(shell): bash 支持 background"
```

---

### Task 14: activity_* 工具包

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Background/Seeing.Agent.Tools.Background.csproj`
- Create: `.../BackgroundToolsModule.cs`
- Create: `.../ActivityStatusTool.cs`
- Create: `.../ActivityOutputTool.cs`
- Create: `.../ActivityWaitTool.cs`
- Create: `.../ActivityCancelTool.cs`
- Create: `.../ActivityListTool.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Background.Tests/`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Status_WithForeignSession_ShouldReturnNull()
[Fact] public async Task Output_ShouldPage() 
[Fact] public async Task Wait_ShouldReturnCallerCancelled_WhenTokenCancelled()
[Fact] public async Task Cancel_ShouldNotAffectOtherSession()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

模块 id `background`；只注入 `IBackgroundActivityManager`；`activity_output` 声明 `output.skip`；`activity_wait` 声明 `timeout.skip` + `loop.skip`；`activity_status` 声明 `loop.skip`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(background): activity_* 工具包"
```

---

### Task 15: 事件与 WebUI 内联卡片

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Background/BackgroundActivityEvent.cs`（若 Task 7 未含）
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:202`
- Modify: `samples/Seeing.Agent.WebUI/Components/.../ToolCallViewModel.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/EventStreamHandlerActivityTests.cs`（若无该测试项目则置于既有 WebUI 测试项目）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task ActivityCompleted_ShouldUpdateExistingCard_ByOriginToolCallId()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

`case ActivityEventTypes.*` → 按 `OriginToolCallId` 找卡片；无则追加；首屏调 `manager.List` 重建；取消按钮仅 `CanCancel`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(webui): 后台活动内联卡片"
```

---

### Task 16: 组合根登记

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（注册 Manager/Control/Outbox/Dispatcher）
- Modify: `samples/Seeing.Agent.HostComposition/FullCapabilityComposition.cs`
- Modify: `src/capabilities/.../BuiltInCapabilitySets.cs`
- Modify: `Seeing.Agent.slnx`
- Test: `tests/apps/Seeing.Agent.HostComposition.Tests/BackgroundCapabilityRegistrationTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void FullCapabilities_ShouldIncludeBackgroundModule()
[Fact] public void Manager_And_Control_ShouldBeRegistered()
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

Core 注册单例 + hosted dispatcher；能力包登记进 `Full`；解决方案加入新工程。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git commit -am "chore: 登记后台活动与 activity 工具能力"
```

---

## Self-Review

- **Spec coverage**：§6–§20 的每条要求都能落到 Task 1–16；§21 架构债明确不做；§22 留给 builder。
- **Step scan**：每步只有一个动作与可验证结果；未写实现体。
- **Type consistency**：`IBackgroundActivityManager` / `IBackgroundActivityControl` / `BackgroundActivityHandle` / `BackgroundActivityStartRequest` 在 Task 7 定义，Task 8–16 只引用。
- **Review Focus**：5 条各有对应断言（Task 13 / 4 / 11 / 2 / 7+14）。
- **Proportion**：计划长度约为 spec 的 1.2 倍，无大段实现代码。
