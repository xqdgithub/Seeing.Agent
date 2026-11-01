# Agent 自动反思（Reflection）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在回合收尾后异步复盘，把可复用的流程性经验沉淀为技能，使 agent 越用越熟。

**Architecture:** 先补三处缺失的地基契约（执行结果聚合体、运行时等待、记忆注入管道），再新增 `Seeing.Agent.Reflection` 能力包。复盘以隔离子会话执行，工具面只读 + 只能创建技能，修改/删除只产出建议清单。

**Tech Stack:** .NET 10 / C#，xUnit 2.9 + FluentAssertions 6.12 + Moq 4.20，`System.Threading.Channels`，Microsoft.Data.Sqlite（仅 Memory 侧）

**Spec:** `docs/superpowers/specs/2026-10-31-agent-reflection-design.md`（v3）

## Global Constraints

- 目标框架 `net10.0`；`ImplicitUsings` / `Nullable` 均 enable。
- 能力包只允许引用 `Seeing.Agent.Abstractions`、`Seeing.Session`、`src/support/*`。禁止引用 Core / Hosting / 其他能力包。
- 禁止在 Core 新增工具或能力包引用。
- 工具 schema 唯一计算在 `SessionToolSchemaResolver`；本条不修改它。
- 配置节只写 `~/.seeing/*.json` 与 `{ws}/.seeing/*.json`，不进 `appsettings.json`。
- 私有字段 `_camelCase`，私有静态字段 `s_camelCase`；异步方法 `Async` 后缀。
- 所有注释、日志、用户可见文本使用**中文**。
- 日志用结构化占位符 `{PropertyName}`，禁止字符串拼接。
- 工具描述与参数 schema 必须满足 `docs/architecture/04-development-standards.md` §9 R1–R9：`Description` 首句说明用途；每个参数必须有非空 `description`；`required` 完整；`additionalProperties: false`；长度/范围约束用 `minLength`/`maxLength`/`minimum`/`maximum`。
- 测试运行一律用 VSTest（MTP 在本环境无法发现测试）：

```powershell
dotnet build Seeing.Agent.slnx
dotnet vstest tests/<proj>/bin/Debug/net10.0/<Proj>.dll --TestCaseFilter:"FullyQualifiedName~<Filter>"
```

- 每个任务结束必须 `git commit`，提交信息用中文描述。

## Review Focus

以下五类输入或条件，spec 隐含要求但单个任务的测试容易漏掉，最可能上线后咬人。每条已在对应任务里配了测试。

1. **命令短路批次被当成一次真实执行**（`/clear`、`/compact` 走同一个 `ExecutionRunner`，`Status` 也是 `Completed`）。期望：不计入复盘轮次、不计入 Memory 抽取节奏。→ Task 1、Task 8
2. **子代理会话的终态被当成顶层**（`explore`/`general` 各自跑一轮完整 `ExecutionRunner`）。期望：`IsTopLevel=false`，不触发复盘。→ Task 1、Task 3
3. **`read` 在受限 agent 下被文件权限门挡住**（`read` 映射到 `filesystem.read` → `PermissionKind.File`）。期望：复盘会话能真正读到工作区文件。→ Task 13
4. **复盘超时被内部安全超时吞掉**（`WaitForIdleAsync` 的 10 分钟上限是"记 Warning 后正常 return"）。期望：业务超时抛 `OperationCanceledException`，记为 timeout 而不是"跑完了但解析失败"。→ Task 2、Task 12
5. **复盘会话泄漏到用户视野**（组窗口、TUI 订阅、`task_status` 枚举、标题生成各一次 LLM 调用）。期望：四处全部排除，且不额外花钱。→ Task 4

---

## File Structure

### Phase 0 — 地基（可独立合入）

| 文件 | 职责 |
|---|---|
| `src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionOutcome.cs` | 新建。一轮执行的终态事实聚合体 |
| `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionOutcomeObserver.cs` | 新建。终态观察者端口 |
| `src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionRecord.cs` | 修改。加 `AgentName`/`AgentExecuted`/`TotalSteps`/`Usage`；删 `ProcessedMessageCount` |
| `src/abstractions/Seeing.Agent.Abstractions/Events/HostingLifecycleEvents.cs` | 修改。`ExecutionCompleteEvent` 携带 `ExecutionOutcome` |
| `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionRuntime.cs` | 修改。加 `DeliverAndAwaitAsync` |
| `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs` | 修改。捕获 `LoopCompleteEvent` 的 steps/usage/duration；组装 outcome；扇出观察者 |
| `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs` | 修改。实现 `DeliverAndAwaitAsync`（TCS 表） |
| `src/primitives/Seeing.Session/Core/SessionOrigin.cs` | 新建。会话用途正交维度 |
| `src/primitives/Seeing.Session/Core/SessionData.cs` | 修改。加 `Origin` 属性 + `Clone` 复制 |
| `src/primitives/Seeing.Session/Core/ISessionActivityTracker.cs` | 新建（从 Memory 下沉） |
| `src/primitives/Seeing.Session/Management/SessionActivityTracker.cs` | 新建（从 Memory 下沉） |
| `src/primitives/Seeing.Session/Management/ISessionGroupManager.cs` | 修改。加 `CreateInternalAsync` |
| `src/primitives/Seeing.Session/Management/SessionGroupManager.cs` | 修改。实现 `CreateInternalAsync` |
| `src/capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs` | 新建。记忆经 ContextSource 注入 |
| `src/capabilities/Seeing.Agent.Memory/Integration/MemoryRecallHandler.cs` | 删除 |
| `src/capabilities/Seeing.Agent.Memory/Core/*Token*.cs`、`Core/CostControl/DailyQuotaManager.cs` | 删除（零消费者的死账本） |
| `src/capabilities/Seeing.Agent.Memory/Integration/MemoryHookHandler.cs` | 修改。`AgentTurnMemoryHandler` 改实现 `IExecutionOutcomeObserver` |
| `src/spine/Seeing.Agent.Core/Configuration/AgentManager.cs` | **不修改**（原 T5「加 `!IsHidden`」已撤销，见 spec §3.5） |
| `src/hosting/Seeing.Agent.Hosting/Tools/TaskStatusTool.cs`、`TaskTool.cs` | 修改。排除 `Origin == Reflection` |
| `src/hosting/Seeing.Agent.Hosting/Tools/TuiSurfaceProvider.cs` | 修改。同上 |
| `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs` | 修改。`Origin == Reflection` 不生成标题 |

### Phase 1–3 — Reflection 包

| 文件 | 职责 |
|---|---|
| `src/capabilities/Seeing.Agent.Reflection/Seeing.Agent.Reflection.csproj` | 新建。依赖 Abstractions + Seeing.Session |
| `Configuration/ReflectionOptions.cs` | 配置模型 |
| `Configuration/ReflectionOptionsProvider.cs` | `IOptionsMonitor` + 持久化（照抄 `MemoryOptionsProvider`） |
| `Abstractions/IReflectionQueue.cs`、`IReflectionExecutor.cs`、`IReflectionJournal.cs` | 包内端口 |
| `Core/ReflectionQueue.cs` | 有界 Channel + 同会话去重 |
| `Core/ReflectionExecutor.cs` | 建内部会话 → 投递等待 → 收集建议 |
| `Core/ReflectionJournal.cs` | `runs/{date}/{executionId}.json` 落盘 |
| `Core/ExecutionUsageLedger.cs` | 日 token 账本，累加 `ExecutionOutcome.Usage` |
| `Core/Suggestions.cs` | 建议清单模型与解析 |
| `Core/Prompts.cs` | 策展提示词 + 反模式清单 |
| `Integration/ReflectionObserver.cs` | `IExecutionOutcomeObserver` 实现，门禁 + 计数 |
| `Integration/Tools/SkillCreateTool.cs` | 工具 id `skill_create` |
| `Background/ReflectionWorker.cs` | `BackgroundService` + `IModuleHostedService` |
| `ReflectionModule.cs` | `ISeeingModule`，id `reflection` |
| `Extensions/ReflectionServiceExtensions.cs` | `AddSeeingReflection(registry)` |

### 测试

`tests/capabilities/Seeing.Agent.Reflection.Tests/`（新建，镜像 src 分类）；Phase 0 的测试落回各自既有的测试项目。

---

## Task Dependency Graph（并行编排依据）

```
Wave 1（无依赖，可全并行）
  T1 ExecutionOutcome 契约        T2 DeliverAndAwaitAsync      T4 SessionOrigin
                                                                 T6 activityTracker 下沉
Wave 2（依赖 T1）
  T3 ExecutionRunner 组装 outcome + 观察者扇出
  T7 Memory 死账本删除（依赖 T1 的 Usage 字段）
Wave 3（依赖 T1+T3）
  T8 AgentTurnMemoryHandler 改观察者
Wave 4
  T9 MemoryContextSource（依赖 T1；需 T3 稳定后可端到端验证）
Wave 5（依赖 T1/T2/T4/T6）
  T10 Reflection 包骨架 + observer + 队列 + worker
Wave 6（依赖 T10）
  T11 Prompts + Journal + UsageLedger        T13 SkillCreateTool（依赖 T10 的模块挂载点）
Wave 7（依赖 T11/T13）
  T12 ReflectionExecutor 端到端
Wave 8
  T14 组合根登记 + 守门测试 + E2E
```

**并行边界**：同 Wave 内的任务互不修改同一文件，可同时进行。Wave 之间只需等 **Interfaces** 契约落地，不必等实现全部完成——契约由上一 Wave 的任务在第一步就写好并提交。

---

### Task 1: `ExecutionOutcome` 与 `IExecutionOutcomeObserver`

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionOutcome.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionOutcomeObserver.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionRecord.cs`
- Test: `tests/abstractions/Seeing.Agent.Abstractions.Tests/Execution/ExecutionOutcomeTests.cs`

**Interfaces:**
- Consumes: `ExecutionStatus`（`Seeing.Agent.Abstractions.Execution`），`TokenUsage`（`Seeing.Agent.Abstractions.Llm`）
- Produces:
  - `record ExecutionOutcome { required string SessionId; required string ExecutionId; required ExecutionStatus Status; string? AgentName; bool AgentExecuted; bool IsTopLevel; string? ParentSessionId; int TotalSteps; TokenUsage? Usage; TimeSpan Duration; DateTime CompletedAt; }`
  - `interface IExecutionOutcomeObserver { bool ShouldObserve(ExecutionOutcome outcome) => true; Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default); }`
  - `ExecutionRecord` 新增 `AgentName`（`string?`）、`AgentExecuted`（`bool`）、`TotalSteps`（`int`）、`Usage`（`TokenUsage?`）；删除 `ProcessedMessageCount`

- [ ] **Step 1: 写失败测试**

```csharp
public class ExecutionOutcomeTests
{
    [Fact]
    public void ExecutionRecord_Should_Not_Expose_ProcessedMessageCount()
    {
        typeof(ExecutionRecord).GetProperty("ProcessedMessageCount").Should().BeNull();
    }

    [Fact]
    public void ExecutionRecord_Should_Default_AgentExecuted_To_False()
    {
        new ExecutionRecord().AgentExecuted.Should().BeFalse();
    }

    [Fact]
    public void ExecutionOutcome_Should_Expose_Usage_And_Steps()
    {
        var outcome = new ExecutionOutcome
        {
            SessionId = "s1", ExecutionId = "e1",
            Status = ExecutionStatus.Completed,
            AgentName = "build", AgentExecuted = true, IsTopLevel = true,
            TotalSteps = 5,
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };
        outcome.Usage!.TotalTokens.Should().Be(150);
        outcome.TotalSteps.Should().Be(5);
    }

    [Fact]
    public async Task Observer_Default_ShouldObserve_Should_Return_True()
    {
        var fake = new AllObservingObserver();
        fake.ShouldObserve(new ExecutionOutcome
        {
            SessionId = "s", ExecutionId = "e", Status = ExecutionStatus.Completed
        }).Should().BeTrue();
        await fake.OnExecutionCompletedAsync(new ExecutionOutcome
        {
            SessionId = "s", ExecutionId = "e", Status = ExecutionStatus.Completed
        });
        fake.Called.Should().BeTrue();
    }

    private sealed class AllObservingObserver : IExecutionOutcomeObserver
    {
        public bool Called { get; private set; }
        public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        { Called = true; return Task.CompletedTask; }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/abstractions/Seeing.Agent.Abstractions.Tests` → 编译失败（`ExecutionOutcome` 不存在、`IExecutionOutcomeObserver` 不存在）

- [ ] **Step 3: 创建契约**

`ExecutionOutcome.cs`：按 Interfaces 中的签名实现，每个属性带中文 `<summary>`。`IsTopLevel` 的注释必须写明「真值来源为 `SessionData.Kind`，不读 `ChatExecutionContext.IsTopLevel`（该字段从未被赋值）」。

`IExecutionOutcomeObserver.cs`：接口含 default 实现的 `ShouldObserve`。

`ExecutionRecord.cs`：加四个属性（带中文 `<summary>`），删 `ProcessedMessageCount`。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/abstractions/Seeing.Agent.Abstractions.Tests; dotnet vstest tests/abstractions/Seeing.Agent.Abstractions.Tests/bin/Debug/net10.0/Seeing.Agent.Abstractions.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionOutcome"` → 4 passed

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Execution/ tests/abstractions/Seeing.Agent.Abstractions.Tests/
git commit -m "feat(execution): 新增 ExecutionOutcome 聚合体与终态观察者端口"
```

---

### Task 2: `ISessionRuntime.DeliverAndAwaitAsync`

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Runtime/ISessionRuntime.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/DeliverAndAwaitTests.cs`

**Interfaces:**
- Consumes: `ExecutionOutcome`（Task 1）、`InboxMessage`、`InboxResult`
- Produces:
  - `Task<ExecutionOutcome> ISessionRuntime.DeliverAndAwaitAsync(InboxMessage message, CancellationToken ct = default)`
  - 语义：投递成功且该次执行进入终态后返回其 outcome；投递失败抛 `InvalidOperationException`（带 `InboxResult.Error`）；`ct` 取消或超时抛 `OperationCanceledException`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task DeliverAndAwait_Should_Return_Outcome_Of_That_Execution()
{
    var runtime = BuildRuntime();
    var outcome = await runtime.DeliverAndAwaitAsync(
        MakeMessage("ses_a", "hello"), CancellationToken.None);
    outcome.SessionId.Should().Be("ses_a");
    outcome.ExecutionId.Should().NotBeNullOrEmpty();
    outcome.Status.Should().Be(ExecutionStatus.Completed);
}

[Fact]
public async Task DeliverAndAwait_Should_Not_Be_Ambiguous_Across_Two_Deliveries()
{
    var runtime = BuildRuntime();
    var first = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_b", "one"), CancellationToken.None);
    var second = await runtime.DeliverAndAwaitAsync(MakeMessage("ses_b", "two"), CancellationToken.None);
    first.ExecutionId.Should().NotBe(second.ExecutionId);
}

[Fact]
public async Task DeliverAndAwait_Should_Throw_On_Cancellation()
{
    var runtime = BuildSlowRuntime();   // 执行体 sleep 5s
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var act = async () => await runtime.DeliverAndAwaitAsync(MakeMessage("ses_c", "slow"), cts.Token);
    await act.Should().ThrowAsync<OperationCanceledException>();
}

[Fact]
public async Task DeliverAndAwait_Should_Throw_When_Delivery_Fails()
{
    var runtime = BuildRuntime();
    var act = async () => await runtime.DeliverAndAwaitAsync(
        new InboxMessage { TargetSessionId = "", Sender = MakeSender(), Body = "x" }, CancellationToken.None);
    await act.Should().ThrowAsync<InvalidOperationException>();
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests` → 编译失败（`DeliverAndAwaitAsync` 不存在）

- [ ] **Step 3: 实现**

接口加方法（含中文 `<summary>` 说明与 `WaitForIdleAsync` 的区别：绑定 executionId、超时抛 OCE）。

`SessionRuntime` 实现要点：
- 字段：`private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecutionOutcome>> _awaiters = new();`
- `DeliverAsync` 已生成 executionId 并创建 `record`（见 `RunLoopAsync:288` 附近的 `record` 构造与 `record.ExecutionId`）。`DeliverAndAwaitAsync` 先 `CreateExecutionId()` 写入 `message` 侧不可行——改为：先登记一个 `TaskCompletionSource`，key 用本次投递后产生的 executionId。做法：`DeliverAsync` 之后从 `_states[sessionId].CurrentExecution?.ExecutionId` 读取；若为空（已在终态）则从 `_states[sessionId].LastExecution` 读。**这是竞态敏感点，实现时在 `RunLoopAsync` 的 `finally`（`:303-314`）里于 `state.LastExecution = record` 之后完成对应 TCS**。
- 取消：`ct.Register(() => tcs.TrySetCanceled())`；注册的 callback 必须 unregister，避免泄漏。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~DeliverAndAwait"` → 4 passed

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(runtime): 新增 DeliverAndAwaitAsync 消除程序等待的轮询歧义"
```

---

### Task 3: `ExecutionRunner` 组装 outcome 并扇出观察者

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:80-438`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/HostingLifecycleEvents.cs:68-78`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/ExecutionOutcomeAssemblyTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ExecutionOutcome`、`IExecutionOutcomeObserver`
- Produces: `ExecutionCompleteEvent.Outcome`（`ExecutionOutcome` 类型，`required`）

- [ ] **Step 1: 写失败测试（覆盖 Review Focus #1 #2）**

```csharp
[Fact]
public async Task RunOnce_Should_Report_AgentExecuted_True_For_Normal_Turn()
{
    var (runner, publisher) = BuildRunner(agent: "build");
    await runner.RunOnceAsync(Record("ses_1"), Batch("hello"), CancellationToken.None);
    var evt = publisher.Events.OfType<ExecutionCompleteEvent>().Single();
    evt.Outcome.AgentExecuted.Should().BeTrue();
    evt.Outcome.AgentName.Should().Be("build");
}

[Fact]
public async Task RunOnce_Should_Report_AgentExecuted_False_For_Command_Short_Circuit()
{
    var (runner, publisher) = BuildRunner(agent: "build");
    await runner.RunOnceAsync(Record("ses_2"), Batch("/clear"), CancellationToken.None);
    var evt = publisher.Events.OfType<ExecutionCompleteEvent>().Single();
    evt.Outcome.AgentExecuted.Should().BeFalse();
    evt.Outcome.AgentName.Should().BeNull();
}

[Fact]
public async Task RunOnce_Should_Report_IsTopLevel_False_For_SubAgent_Session()
{
    var (runner, publisher) = BuildRunner(agent: "explore",
        sessionKind: SessionKind.SubAgent, parentSessionId: "ses_root");
    await runner.RunOnceAsync(Record("ses_child"), Batch("do work"), CancellationToken.None);
    var evt = publisher.Events.OfType<ExecutionCompleteEvent>().Single();
    evt.Outcome.IsTopLevel.Should().BeFalse();
    evt.Outcome.ParentSessionId.Should().Be("ses_root");
}

[Fact]
public async Task RunOnce_Should_Capture_Usage_And_TotalSteps_From_LoopComplete()
{
    var (runner, publisher) = BuildRunner(agent: "build",
        loopComplete: new LoopCompleteEvent
        {
            SessionId = "ses_3", LoopId = "l1", TotalSteps = 7,
            Usage = new TokenUsage { InputTokens = 300, OutputTokens = 120 },
            Duration = TimeSpan.FromSeconds(3), Success = true
        });
    await runner.RunOnceAsync(Record("ses_3"), Batch("hello"), CancellationToken.None);
    var evt = publisher.Events.OfType<ExecutionCompleteEvent>().Single();
    evt.Outcome.TotalSteps.Should().Be(7);
    evt.Outcome.Usage!.TotalTokens.Should().Be(420);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionOutcomeAssembly"` → FAIL（`ExecutionCompleteEvent.Outcome` 不存在）

- [ ] **Step 3: 实现**

`HostingLifecycleEvents.cs`：`ExecutionCompleteEvent` 加 `public required ExecutionOutcome Outcome { get; init; }`。

`ExecutionRunner.RunOnceAsync`：
1. 在方法开头、`try` 之外声明承接变量：`string? executedAgentName = null; var agentExecuted = false; int totalSteps = 0; TokenUsage? usage = null; TimeSpan loopDuration = default;`
2. `:166` 拿到 `context` 后：`executedAgentName = context.Agent.Name;`
3. `:223` 进入 `if (!commandConsumedExecution)` 块后：`agentExecuted = true;`
4. `:317-326` 消费 `evt` 的循环里，对 `LoopCompleteEvent` 补捕获：`totalSteps += loopEnd.TotalSteps; usage = MergeUsage(usage, loopEnd.Usage); loopDuration += loopEnd.Duration;`（`MergeUsage` 逐字段相加，任一为 null 返回另一）
5. `finally` 中，落盘之后：从 `sessionManager.Get(record.SessionId)` 取 `Kind` 与父会话（`ISessionGroupManager.TryGetParent`），组装 `ExecutionOutcome`，写进 `ExecutionCompleteEvent`。
6. 扇出：`foreach (var o in scope.ServiceProvider.GetServices<IExecutionOutcomeObserver>())` 若 `o.ShouldObserve(outcome)` 则 `_ = o.OnExecutionCompletedAsync(outcome)`（cancellation 用 `CancellationToken.None`，异常各自 catch 并 `LogWarning`，不阻断其他观察者）。

**注意**：不要新增构造参数。`IExecutionOutcomeObserver` 从已有的 `_serviceProvider` scope 解析。

- [ ] **Step 4: 运行确认通过**

Run: 同上过滤器 → 4 passed。再跑全量 `ExecutionRunner` 相关：`--TestCaseFilter:"FullyQualifiedName~ExecutionRunner"` → 全绿（含既有 9 处构造点）。

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(execution): ExecutionCompleteEvent 携带 ExecutionOutcome 并扇出观察者"
```

---

### Task 4: `SessionOrigin` 正交维度

**Files:**
- Create: `src/primitives/Seeing.Session/Core/SessionOrigin.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs`（加属性 + `Clone()` 复制）
- Modify: `src/primitives/Seeing.Session/Management/ISessionGroupManager.cs`（加 `CreateInternalAsync`）
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Core/SessionOriginTests.cs`

**Interfaces:**
- Produces:
  - `enum SessionOrigin { User = 0, SubAgentTask = 1, Reflection = 2, Scheduler = 3, Heartbeat = 4 }`
  - `SessionData.Origin { get; set; }`（默认 `SessionOrigin.User`）
  - `Task<SessionData> ISessionGroupManager.CreateInternalAsync(string parentId, string agentName, string title, IReadOnlyList<SessionPermissionRule> snapshot, string? scenario, SessionOrigin origin, CancellationToken ct = default)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void SessionData_Should_Default_Origin_To_User()
    => new SessionData().Origin.Should().Be(SessionOrigin.User);

[Fact]
public void Clone_Should_Preserve_Origin()
{
    var s = new SessionData { Origin = SessionOrigin.Reflection };
    s.Clone().Origin.Should().Be(SessionOrigin.Reflection);
}

[Fact]
public async Task CreateInternalAsync_Should_Produce_SubAgent_With_Reflection_Origin()
{
    var (group, root) = await SeedRootAsync();
    var child = await group.CreateInternalAsync(
        root.Id, "reflection", "复盘", new List<SessionPermissionRule>(),
        null, SessionOrigin.Reflection);
    child.Kind.Should().Be(SessionKind.SubAgent);        // 满足组不变量
    child.Origin.Should().Be(SessionOrigin.Reflection);
}

[Fact]
public async Task CreateInternalAsync_Should_Not_Generate_Title()
{
    var (group, root) = await SeedRootAsync();
    var child = await group.CreateInternalAsync(root.Id, "reflection", "复盘",
        new List<SessionPermissionRule>(), null, SessionOrigin.Reflection);
    child.Title.Should().Be("复盘");   // 显式标题，非 "Session ses_..."
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/primitives/Seeing.Session.Tests` → 编译失败

- [ ] **Step 3: 实现**

`SessionOrigin.cs`：枚举 + 每个值中文 `<summary>`。

`SessionData`：在 `Kind` 属性后加 `Origin`。`Clone()`（`:457`）里复制 `Origin`。

`SessionGroupManager.CreateInternalAsync`：复制 `CreateChildAsync`（`:623-655`）的实现，差异为：`child.Origin = origin;`、`child.Title = title;`（不覆盖为默认标题）。**必须保持 `Kind = SessionKind.SubAgent`**，否则 `ValidateMemberInvariant`（`:961-986`）抛异常。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionOrigin"` → 4 passed

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(session): 新增 SessionOrigin 正交维度与 CreateInternalAsync"
```

---

### Task 5: ~~`GetTaskableAgentsAsync` 排除 `IsHidden`~~（已撤销，不执行）

**状态：作废（2026-11-01）。** 实现已提交 `69027046`，审查发现方向错误后 revert 为 `c8ac9e19`。

**撤销理由**：`AgentDefinition.cs:41` 的契约注释明确 `IsHidden` = 「不在用户选择列表显示，**但仍可作为 SubAgent 使用**」。隐藏 ≠ 不可委派。`general`（`SubAgent`+`IsHidden`）是该语义的正例，`TaskTool.cs:63` 直接举例「如 explore、general」。原提案会让内置可委派集合从 `{build, plan, explore, general}` 静默收缩为 `{build, plan, explore}`，并与 `AgentsPromptSectionContributor`（只看 `Mode == SubAgent`）的系统提示清单矛盾——把符合契约的行为改成三处口径不一致。

**对 `reflection` agent 的替代方案**：用 `Mode = AgentMode.Primary` + `IsHidden = true`（见 Task 10）。`Primary` 表达「不可委派」，`IsHidden` 表达「不显示在 UI」，两个维度各司其职。

**本任务不执行任何代码改动。** 保留此段仅为记录决策与回滚。

---

### Task 6: `ISessionActivityTracker` 下沉到 primitives

**Files:**
- Create: `src/primitives/Seeing.Session/Core/ISessionActivityTracker.cs`
- Create: `src/primitives/Seeing.Session/Management/SessionActivityTracker.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Core/SessionActivityTracker.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Abstractions/IMemoryEvolutionService.cs`（删除该接口定义）
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs:157`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionActivityTrackerTests.cs`

**Interfaces:**
- Produces: `interface ISessionActivityTracker { void Touch(string sessionId); IReadOnlyList<string> GetIdleSessions(TimeSpan idle); void Clear(string sessionId); }`（命名空间 `Seeing.Session.Core`）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void GetIdleSessions_Should_Return_Only_Sessions_Past_Threshold()
{
    var t = new SessionActivityTracker();
    t.Touch("a");
    Thread.Sleep(30);
    t.Touch("b");
    var idle = t.GetIdleSessions(TimeSpan.FromMilliseconds(20));
    idle.Should().Contain("a").And.NotContain("b");
}

[Fact]
public void Clear_Should_Remove_Session()
{
    var t = new SessionActivityTracker();
    t.Touch("a");
    t.Clear("a");
    t.GetIdleSessions(TimeSpan.Zero).Should().BeEmpty();
}
```

- [ ] **Step 2: 运行确认失败** → 编译失败（`SessionActivityTracker` 不在 `Seeing.Session` 命名空间）

- [ ] **Step 3: 实现**

把 `Memory/Core/SessionActivityTracker.cs` 的 24 行实现原样搬到 primitives，改命名空间为 `Seeing.Session.Management`（接口在 `Seeing.Session.Core`）。删除 Memory 侧文件与接口定义。`MemoryServiceExtensions.cs:157` 的 `TryAddSingleton<ISessionActivityTracker, SessionActivityTracker>()` 改为在 Session 注册处（`SessionServiceExtensions`）注册，Memory 侧不再注册。

- [ ] **Step 4: 运行确认通过 + 全仓编译**

Run: `dotnet build Seeing.Agent.slnx` → 0 error（验证无残留引用）

- [ ] **Step 5: 提交**

```bash
git commit -am "refactor(session): ISessionActivityTracker 下沉 primitives 消除跨包复制"
```

---

### Task 7: 删除 Memory 死账本

**Files:**
- Delete: `src/capabilities/Seeing.Agent.Memory/Abstractions/ITokenTracker.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Abstractions/IQuotaManager.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Core/CostControl/SqliteTokenTracker.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Core/CostControl/DailyQuotaManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs:129-149`
- Modify: `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs`（若有注册）
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/CostControl/CostLedgerRemovalTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ExecutionOutcome.Usage`（作为替代的数据源说明）
- Produces: 无（净删除）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Memory_Assembly_Should_Not_Define_TokenTracker() =>
    typeof(MemoryModule).Assembly.GetTypes()
        .Should().NotContain(t => t.Name is "ITokenTracker" or "IQuotaManager"
                                       or "SqliteTokenTracker" or "DailyQuotaManager");
```

- [ ] **Step 2: 运行确认失败** → FAIL（类型仍存在）

- [ ] **Step 3: 删除 + 清理 DI**

删除四个文件与 `MemoryServiceExtensions.cs:129-149` 的两段注册。若 `SqliteConnectionGate`/`SqliteConnectionOwner` 因此无消费者则保留（其他索引仍用）。

- [ ] **Step 4: 运行确认通过 + 全仓编译/测试**

Run: `dotnet build Seeing.Agent.slnx` → 0 error；`dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll` → 全绿

- [ ] **Step 5: 提交**

```bash
git commit -am "refactor(memory): 删除零消费者的 token 账本与配额实现"
```

---

### Task 8: `AgentTurnMemoryHandler` 改实现 `IExecutionOutcomeObserver`

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryHookHandler.cs:127-169`
- Modify: `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs:122-150`
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/AgentTurnObserverTests.cs`

**Interfaces:**
- Consumes: `ExecutionOutcome`、`IExecutionOutcomeObserver`（Task 1）
- Produces: `AgentTurnMemoryHandler : IExecutionOutcomeObserver`（不再是 `IHookHandler`）

- [ ] **Step 1: 写失败测试（覆盖 Review Focus #1 #2 的 Memory 侧）**

```csharp
[Theory]
// agentExecuted, isTopLevel, status, shouldFlush
[InlineData(true,  true,  ExecutionStatus.Completed, true)]
[InlineData(false, true,  ExecutionStatus.Completed, false)]  // 命令短路
[InlineData(true,  false, ExecutionStatus.Completed, false)]  // 子代理
[InlineData(true,  true,  ExecutionStatus.Failed,    false)]  // 失败
public void ShouldObserve_Should_Gate_On_ExecutionFacts(
    bool executed, bool topLevel, ExecutionStatus status, bool expected)
{
    var handler = BuildHandler();
    var outcome = new ExecutionOutcome
    { SessionId = "s", ExecutionId = "e", Status = status,
      AgentExecuted = executed, IsTopLevel = topLevel };
    handler.ShouldObserve(outcome).Should().Be(expected);
}

[Fact]
public async Task OnExecutionCompleted_Should_Trigger_Flush()
{
    var flush = new Mock<IMemoryFlushService>();
    var handler = BuildHandler(flush);
    await handler.OnExecutionCompletedAsync(new ExecutionOutcome
    { SessionId = "s", ExecutionId = "e", Status = ExecutionStatus.Completed,
      AgentExecuted = true, IsTopLevel = true });
    flush.Verify(f => f.TryFlushAfterTurn("s"), Times.Once);
}
```

- [ ] **Step 2: 运行确认失败** → FAIL（`AgentTurnMemoryHandler` 无 `ShouldObserve`）

- [ ] **Step 3: 实现**

类改为 `IExecutionOutcomeObserver`，删 `Spec`/`Priority`/`ExecuteAsync`。`ShouldObserve` 按上面测试的四条门禁。`OnExecutionCompletedAsync` 里保留原有 `ExtractEveryNTurns <= 0` 早退，然后 `_flush.TryFlushAfterTurn(outcome.SessionId)`。

`MemoryModule.RegisterHooks`（`:131-133`）删除 `hooks.Register(turn)`；`UnregisterHooks` 同步删除。改为在 DI 注册 `services.AddSingleton<IExecutionOutcomeObserver, AgentTurnMemoryHandler>()`（`MemoryServiceExtensions` 现有 `TryAddSingleton<AgentTurnMemoryHandler>()` 之后加）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentTurnObserver"` → 5 passed

- [ ] **Step 5: 提交**

```bash
git commit -am "refactor(memory): 轮次触发改为执行终态观察者，口径修正为顶层真实执行"
```

---

### Task 9: `MemoryContextSource` 并删除 `MemoryRecallHandler`

**Files:**
- Create: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryRecallHandler.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs:133/148`
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs:164`
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryContextSourceTests.cs`

**Interfaces:**
- Consumes: `IContextSource` / `IContextSourceContext`（读 `src/abstractions/Seeing.Agent.Abstractions/Context/ContextSourceModels.cs` 与 `src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs` 的既有实现作为范式）
- Produces: `MemoryContextSource : IContextSource`，`SourceKey` 常量 `ContextSourceKey.Memory`（若枚举存在则复用，否则新增字符串常量 `"memory"`），`Target = System`，`Scope = Project`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ContextSource_Should_Render_Recall_Hits_Into_System_Baseline()
{
    var recall = new Mock<IMemoryRecallService>();
    recall.Setup(r => r.RecallAsync("query", It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<SearchHit> { MakeHit("标题A", "内容A") });
    var source = new MemoryContextSource(recall.Object, Options());
    var text = await source.RenderAsync(MakeContext("query"));
    text.Should().Contain("标题A").And.Contain("内容A");
}

[Fact]
public async Task ContextSource_Should_Return_Empty_In_ToolsOnly_Mode()
{
    var source = new MemoryContextSource(Mock.Of<IMemoryRecallService>(), Options(MemoryRetrievalMode.ToolsOnly));
    (await source.RenderAsync(MakeContext("q"))).Should().BeEmpty();
}

[Fact]
public void MemoryModule_Should_Not_Register_MemoryRecallHandler()
{
    var module = typeof(MemoryModule).Assembly.GetTypes()
        .Should().NotContain(t => t.Name == "MemoryRecallHandler").Subject;
}
```

- [ ] **Step 2: 运行确认失败** → FAIL（`MemoryContextSource` 不存在）

- [ ] **Step 3: 实现**

照 `SkillsContextSource` 的形状实现：`SourceKey`、`Target = ContextTarget.System`、`Scope = ContextScope.Project`、`RenderAsync` 产出 `## Relevant memories` 分节（沿用原 handler 的 `- ({score}) {title}: {content}` 格式与 `MaxInjectTokens * 4` 字符预算），空结果返回空串（不渲染分节）。

`MemoryModule.RegisterHooks/UnregisterHooks` 删除 `MemoryRecallHandler` 两行；`MemoryServiceExtensions:164` 删除注册；DI 注册 `services.AddScoped<IContextSource, MemoryContextSource>()`。

- [ ] **Step 4: 运行确认通过 + 全仓编译**

Run: `dotnet build Seeing.Agent.slnx` → 0 error；Memory.Tests 全绿

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(memory): 记忆注入迁移到 ContextSource 管道，删除失效的 before_start 路径"
```

---

### Task 10: Reflection 包骨架（配置 + 模块 + observer + 队列 + worker）

**Files:**
- Create: `src/capabilities/Seeing.Agent.Reflection/Seeing.Agent.Reflection.csproj`
- Create: `src/capabilities/Seeing.Agent.Reflection/Configuration/ReflectionOptions.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Configuration/ReflectionOptionsProvider.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Abstractions/IReflectionQueue.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/ReflectionQueue.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/ReflectionRequest.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Integration/ReflectionObserver.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Background/ReflectionWorker.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/ReflectionModule.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Extensions/ReflectionServiceExtensions.cs`
- Add: `src/capabilities/Seeing.Agent.Reflection` 到 `Seeing.Agent.slnx`
- Test: `tests/capabilities/Seeing.Agent.Reflection.Tests/{Seeing.Agent.Reflection.Tests.csproj,Trigger/ReflectionObserverTests.cs,Module/ReflectionModuleTests.cs}`

**Interfaces:**
- Consumes: `ExecutionOutcome`/`IExecutionOutcomeObserver`（T1）、`SessionOrigin`（T4）、`ISessionActivityTracker`（T6）
- Produces:
  - `enum ReflectionMode { Off, SuggestOnly, ApplyCreate }`
  - `sealed class ReflectionOptions`（字段见 spec §4.9，默认 `Enabled=false`、`Mode=SuggestOnly`）
  - `record ReflectionRequest(string SessionId, string ExecutionId, ReflectionTriggerKind Kind, DateTimeOffset At)`
  - `enum ReflectionTriggerKind { TurnNudge, Idle, Manual }`
  - `interface IReflectionQueue { bool TryEnqueue(ReflectionRequest request); ValueTask<ReflectionRequest> DequeueAsync(CancellationToken ct); int PendingCount { get; } }`
  - `sealed class ReflectionObserver : IExecutionOutcomeObserver`

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
// enabled, executed, topLevel, status, agentName, origin, expected
[InlineData(true, true, true, ExecutionStatus.Completed, "build", SessionOrigin.User, true)]
[InlineData(false, true, true, ExecutionStatus.Completed, "build", SessionOrigin.User, false)]
[InlineData(true, false, true, ExecutionStatus.Completed, "build", SessionOrigin.User, false)]
[InlineData(true, true, false, ExecutionStatus.Completed, "explore", SessionOrigin.User, false)]
[InlineData(true, true, true, ExecutionStatus.Failed, "build", SessionOrigin.User, false)]
[InlineData(true, true, true, ExecutionStatus.Completed, "reflection", SessionOrigin.Reflection, false)]
public void ShouldObserve_Should_Gate_Correctly(
    bool enabled, bool executed, bool topLevel, ExecutionStatus status,
    string agent, SessionOrigin origin, bool expected)
{
    // ShouldObserve 是接口默认实现，只能经接口类型调用（具体类上调用会 CS1061）
    IExecutionOutcomeObserver o = BuildObserver(enabled);
    var outcome = MakeOutcome(executed, topLevel, status, agent) with { Origin = origin };
    o.ShouldObserve(outcome).Should().Be(expected);
}

[Fact]
public void Nudge_Should_Enqueue_Only_On_Threshold()
{
    var queue = new ReflectionQueue(32);
    var o = BuildObserver(enabled: true, interval: 3, queue);
    for (var i = 1; i <= 2; i++)
        o.OnExecutionCompletedAsync(MakeOutcome("s1"), default).GetAwaiter().GetResult();
    queue.PendingCount.Should().Be(0);
    o.OnExecutionCompletedAsync(MakeOutcome("s1"), default).GetAwaiter().GetResult();
    queue.PendingCount.Should().Be(1);
}

[Fact]
public async Task Queue_Should_Coalesce_Same_Session()
{
    var q = new ReflectionQueue(32);
    q.TryEnqueue(new ReflectionRequest("s1", "e1", ReflectionTriggerKind.TurnNudge, default));
    q.TryEnqueue(new ReflectionRequest("s1", "e2", ReflectionTriggerKind.TurnNudge, default));
    q.PendingCount.Should().Be(1);
    (await q.DequeueAsync(default)).ExecutionId.Should().Be("e2");  // 保留最新
}

[Fact]
public void Module_Should_Declare_Dependencies()
{
    var m = new ReflectionModule();
    m.Id.Should().Be("reflection");
    m.DependsOn.Should().Contain("skills");
    m.ProvidedTools.Should().Contain("skill_create");
}
```

- [ ] **Step 2: 运行确认失败** → 项目不存在，创建 csproj 后编译失败

- [ ] **Step 3: 实现**

csproj 只引用 `..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj` 与 `..\..\primitives\Seeing.Session\Seeing.Session.csproj`，外加 `Microsoft.Extensions.*` 包（照 `Seeing.Agent.Memory.csproj` 的包列表，去掉 Sqlite/YamlDotNet/System.Reactive）。

`ReflectionOptionsProvider` 照抄 `MemoryOptionsProvider` 的形状（`IOptionsMonitor` + `IMemoryOptionsStore` 对应物 + `Reload()`）。

`ReflectionQueue`：`Channel.CreateBounded<ReflectionRequest>(capacity)`、`SingleReader`、内部 `ConcurrentDictionary<string, string>` 记录「sessionId → 最新 executionId」用于合并。

`ReflectionObserver`：门禁直接读 `outcome.Origin` / `outcome.AgentExecuted` / `outcome.IsTopLevel` / `outcome.Status`（**全部已在 `ExecutionOutcome` 上，禁止回读会话**——契约 `IExecutionOutcomeObserver` 明文要求，且回读会被 5s 超时截断）。`ShouldObserve(outcome)` 单参实现即可，**不需要** `ShouldObserve(outcome, origin)` 双参重载。门禁通过后计数，到期 `Reset` + `TryEnqueue`。

`ReflectionWorker`：`BackgroundService + IModuleHostedService`，`ModuleId = "reflection"`，照抄 `MemoryEvolutionWorker` 的 `ModuleHostedRunGate` + `RegisterWake` + Deactivate 哨兵。两个循环：队列消费（调 `IReflectionExecutor`，本期为 no-op 桩）+ 1 分钟 tick 空闲扫描。

`ReflectionModule`：`ConfigureServices` 留空（DI 在 `ReflectionServiceExtensions`）；`ActivateAsync` 注册 agent（占位定义用 **`Mode = AgentMode.Primary` + `IsHidden = true`**——`Primary` 表达「不可被 task 委派」，`IsHidden` 表达「不显示在 UI 选择列表」，见 spec §3.5；`SystemPrompt` 用 `Prompts.ReflectionSystem`，T11 到位后替换）、`MarkActive()`；`DeactivateAsync` 对称。
`ReflectionObserver`：门禁直接读 `outcome.Origin` / `outcome.AgentExecuted` / `outcome.IsTopLevel` / `outcome.Status`（**全部已在 `ExecutionOutcome` 上，禁止回读会话**）。`ShouldObserve(outcome)` 是接口默认实现，**C# 只能经接口类型调用**——测试里必须声明为 `IExecutionOutcomeObserver`（`var o = new ReflectionObserver(); o.ShouldObserve(...)` 会 `CS1061`）；**不需要**双参重载。

`ReflectionServiceExtensions.AddSeeingReflection(registry)`：注册配置节 `Reflection`/`reflection.json`/`ConfigScope.Both`、`ReflectionOptionsProvider`、`IReflectionQueue` 单例、`IExecutionOutcomeObserver → ReflectionObserver`、`AddModuleHostedService<ReflectionWorker>`、`ISeeingModule → ReflectionModule`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build Seeing.Agent.slnx` → 0 error；`dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/... --TestCaseFilter:"FullyQualifiedName~Reflection"` → 全绿

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(reflection): 新增 Reflection 包骨架（配置/模块/观察者/队列/worker）"
```

---

### Task 11: 提示词、Journal、用量账本与建议模型

**Files:**
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/Prompts.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/Suggestions.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Abstractions/IReflectionJournal.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/ReflectionJournal.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/ExecutionUsageLedger.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Abstractions/IExecutionUsageLedger.cs`
- Test: `tests/capabilities/Seeing.Agent.Reflection.Tests/Core/{PromptsTests,SuggestionParsingTests,ReflectionJournalTests,UsageLedgerTests}.cs`

**Interfaces:**
- Produces:
  - `static class Prompts { public const string ReflectionSystem; public static string BuildUserPrompt(string transcript, string sourceSessionId); }`
  - `record ReflectionSuggestion(string Kind, string Target, string? SuggestedContent, string Reason, IReadOnlyList<string> Evidence)`
  - `record ReflectionRunRecord(string SessionId, string ExecutionId, ReflectionTriggerKind Kind, DateTimeOffset At, string Outcome, IReadOnlyList<string> CreatedSkills, IReadOnlyList<ReflectionSuggestion> Suggestions, int InputTokens, int OutputTokens)`
  - `interface IReflectionJournal { Task AppendAsync(ReflectionRunRecord record, CancellationToken ct = default); }`
  - `interface IExecutionUsageLedger { long UsedToday(); void Add(TokenUsage usage); }`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Prompts_Should_Contain_AntiPattern_List()
{
    Prompts.ReflectionSystem.Should()
        .Contain("环境").And.Contain("负面断言").And.Contain("能从代码")
        .And.Contain("一次性的").And.Contain("skip");
}

[Fact]
public void Prompts_Should_Not_ReList_Skill_Catalog()
{
    // 技能清单由 SkillsContextSource 渲染，本提示词不得重复列举
    Prompts.ReflectionSystem.Should().NotContain("## Skills");
}

[Fact]
public void ParseSuggestions_Should_Drop_Empty_Evidence()
{
    var json = """
    {"suggestions":[
      {"kind":"skill.update","target":"a","reason":"r","evidence":[]},
      {"kind":"skill.update","target":"b","reason":"r","evidence":["msg 3"]}
    ]}
    """;
    var r = Suggestions.Parse(json);
    r.Should().ContainSingle().Which.Target.Should().Be("b");
}

[Fact]
public void ParseSuggestions_Should_Handle_Skip_Action()
{
    Suggestions.Parse("""{"action":"skip"}""").Should().BeEmpty();
}

[Fact]
public async Task Journal_Should_Write_One_File_Per_Run()
{
    using var dir = new TempDir();
    var j = new ReflectionJournal(dir.Path);
    await j.AppendAsync(MakeRecord("e1"));
    Directory.GetFiles(dir.Path, "*.json", SearchOption.AllDirectories)
        .Should().ContainSingle().Which.Should().EndWith("e1.json");
}

[Fact]
public void Ledger_Should_Accumulate_Not_Overwrite()
{
    using var dir = new TempDir();
    var l = new ExecutionUsageLedger(dir.Path);
    l.Add(new TokenUsage { InputTokens = 100, OutputTokens = 50 });
    l.Add(new TokenUsage { InputTokens = 200, OutputTokens = 80 });
    l.UsedToday().Should().Be(430);
}
```

- [ ] **Step 2: 运行确认失败** → 编译失败

- [ ] **Step 3: 实现**

`Prompts.ReflectionSystem`：写三段（任务定义 / 反模式清单 / 写入纪律），**不得**含 `## Skills` 分节（技能清单由 ContextSource 注入）。必须包含 `{"action":"skip"}` 这一合法输出。反模式五条逐字体现到文本里（环境相关偶发失败、对工具的负面断言、一次性调试叙事、能从代码直接推导的事实、本次任务独有的具体值）。

`Suggestions.Parse`：容忍 ```json 围栏、从夹杂文本中提取 JSON、`evidence` 为空的条目整条丢弃、`action == "skip"` 返回空。

`ReflectionJournal`：路径 `{ProjectSeeingDirectory}/reflection/runs/{yyyyMMdd}/{executionId}.json`，`JsonSerializer` 缩进 + 中文不转义（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）。同 executionId 覆盖写。

`ExecutionUsageLedger`：路径 `{ProjectSeeingDirectory}/reflection/usage.json`，结构 `{ "date": "2026-11-01", "input": 0, "output": 0 }`，UTC 日切（日期不符则重置）。`Add` 累加，`UsedToday` 返回 `InputTokens + OutputTokens`。进程内加锁。

- [ ] **Step 4: 运行确认通过**

Run: `--TestCaseFilter:"FullyQualifiedName~Prompts|FullyQualifiedName~Suggestion|FullyQualifiedName~Journal|FullyQualifiedName~UsageLedger"` → 全绿

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(reflection): 策展提示词、建议解析、运行日志与用量账本"
```

---

### Task 12: `ReflectionExecutor` 端到端

**Files:**
- Create: `src/capabilities/Seeing.Agent.Reflection/Abstractions/IReflectionExecutor.cs`
- Create: `src/capabilities/Seeing.Agent.Reflection/Core/ReflectionExecutor.cs`
- Modify: `src/capabilities/Seeing.Agent.Reflection/Core/ReflectionQueue.cs`（若需 exposes 移除）
- Modify: `src/capabilities/Seeing.Agent.Reflection/Background/ReflectionWorker.cs`（接上真实执行）
- Test: `tests/capabilities/Seeing.Agent.Reflection.Tests/Core/ReflectionExecutorTests.cs`

**Interfaces:**
- Consumes: `ISessionManager`、`ISessionGroupManager.CreateInternalAsync`（T4）、`ISessionRuntime.DeliverAndAwaitAsync`（T2）、`IReflectionJournal`/`IExecutionUsageLedger`/`Prompts`/`Suggestions`（T11）、`ISkillWriter`
- Produces: `Task<ReflectionRunResult> IReflectionExecutor.RunAsync(ReflectionRequest request, CancellationToken ct = default)`；`record ReflectionRunResult(bool Executed, string? SkipReason, IReadOnlyList<string> CreatedSkills, int SuggestionCount)`

- [ ] **Step 1: 写失败测试（覆盖 Review Focus #4）**

```csharp
[Fact]
public async Task Run_Should_Build_Internal_Session_With_Reflection_Origin()
{
    var (exec, group, runtime) = Build();
    await exec.RunAsync(MakeRequest("ses_src"));
    group.CreatedInternal.Single().Origin.Should().Be(SessionOrigin.Reflection);
}

[Fact]
public async Task Run_Should_Return_Timeout_When_Business_Timeout_Hits()
{
    var (exec, _, runtime) = Build(deliverAndAwait: _ => throw new OperationCanceledException());
    var result = await exec.RunAsync(MakeRequest("ses_src"));
    result.Executed.Should().BeFalse();
    result.SkipReason.Should().Be("timeout");   // 不是 "parse-failed"
}

[Fact]
public async Task Run_Should_Record_Suggestions_To_Journal()
{
    var (exec, _, runtime) = Build(answerJson:
        """{"suggestions":[{"kind":"skill.update","target":"x","reason":"r","evidence":["m1"]}]}""");
    var result = await exec.RunAsync(MakeRequest("ses_src"));
    result.SuggestionCount.Should().Be(1);
    Directory.GetFiles(JournalDir, "*.json", SearchOption.AllDirectories).Should().NotBeEmpty();
}

[Fact]
public async Task Run_Should_Cleanup_Session_On_Success()
{
    var (exec, group, _) = Build();
    await exec.RunAsync(MakeRequest("ses_src"));
    group.RemovedSessions.Should().ContainSingle();
}
```

- [ ] **Step 2: 运行确认失败** → 编译失败

- [ ] **Step 3: 实现**

按 spec §4.3 的五个阶段实现：
1. `TryGetOrLoadAsync` 取源会话（**不得**用 `LoadAsync`），`GetActiveMessages()` 序列化 transcript，单条截断 `MaxToolResultChars`，总长截断 `MaxTranscriptChars`。
2. `CreateInternalAsync(... , SessionOrigin.Reflection)`。
3. `InboxMessage` 的 `ModelId = opts.Model ?? child.SelectedModel`；**自带** `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(RunTimeoutMinutes)`；`catch (OperationCanceledException)` → `SkipReason = "timeout"`（不要依赖 `DeliverAndAwaitAsync` 的内部超时）。
4. 成功则读子会话最后一条 assistant 消息 → `Suggestions.Parse`；`journal.AppendAsync` 写 `ReflectionRunRecord`（含 `outcome.Usage` 与 `ledger.Add`）。
5. 成功 → `RemoveSessionAsync(child.Id)` + `DeleteAsync(child.Id)`；失败保留。

`ReflectionWorker` 把桩替换为真实调用，并做 `MaxRunsPerHour` 滑动窗口 + `DailyTokenBudget` 检查（`ledger.UsedToday() >= budget` → skip `"quota"`）。

- [ ] **Step 4: 运行确认通过**

Run: `--TestCaseFilter:"FullyQualifiedName~ReflectionExecutor"` → 4 passed

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(reflection): 隔离子会话执行、超时与建议落盘"
```

---

### Task 13: `skill_create` 工具（含 Review Focus #3）

**Files:**
- Create: `src/capabilities/Seeing.Agent.Reflection/Integration/Tools/SkillCreateTool.cs`
- Modify: `src/capabilities/Seeing.Agent.Reflection/ReflectionModule.cs`（Activate 挂载 / Deactivate 撤销）
- Test: `tests/capabilities/Seeing.Agent.Reflection.Tests/Integration/SkillCreateToolTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Reflection.Tests/Integration/ReflectionAgentPermissionTests.cs`

**Interfaces:**
- Consumes: `ISkillWriter`、`ISkillCatalog`、`ReflectionOptions`
- Produces: `ToolBase` 子类，`Id => "skill_create"`，参数 `name`/`description`/`content`

- [ ] **Step 1: 写失败测试（覆盖 Review Focus #3）**

```csharp
[Fact]
public async Task ApplyCreate_Should_Write_Project_Level_With_Provenance()
{
    var (tool, writer) = Build(ReflectionMode.ApplyCreate);
    var r = await tool.ExecuteAsync(Args("my-flow", "描述", "正文"), Ctx("ses_src"));
    r.Success.Should().BeTrue();
    writer.LastCreate!.Level.Should().Be(ConfigLevel.Project);
    writer.LastCreate.Draft.Metadata!["distilled-by"].Should().Be("seeing-reflection");
    writer.LastCreate.Draft.Metadata["source-session"].Should().Be("ses_src");
}

[Fact]
public async Task ApplyCreate_Should_Refuse_Existing_Name()
{
    var (tool, writer) = Build(ReflectionMode.ApplyCreate, existing: "my-flow");
    var r = await tool.ExecuteAsync(Args("my-flow", "描述", "正文"), Ctx("s"));
    r.Success.Should().BeFalse();
    writer.CreateCalled.Should().BeFalse();
}

[Fact]
public async Task SuggestOnly_Should_Not_Write()
{
    var (tool, writer) = Build(ReflectionMode.SuggestOnly);
    var r = await tool.ExecuteAsync(Args("x", "d", "c"), Ctx("s"));
    r.Success.Should().BeTrue();
    writer.CreateCalled.Should().BeFalse();
    r.Content.Should().Contain("未写入");   // 明确告知未生效
}

[Fact]
public async Task Should_Accept_Platform_Legal_Name_With_Upper_And_Space()
{
    var (tool, _) = Build(ReflectionMode.SuggestOnly);
    var r = await tool.ExecuteAsync(Args("Self-Improving + Proactive Agent", "d", "c"), Ctx("s"));
    r.Success.Should().BeTrue();   // 平台允许大写/空格/加号
}

[Fact]
public async Task ReflectionAgent_Should_Be_Able_To_Read_Workspace_File()
{
    // Review Focus #3：read 映射 filesystem.read → PermissionKind.File
    var (agent, auth) = BuildReflectionAgent();
    var result = await auth.AuthorizeAsync(MakeReadResource("/ws/a.txt"), MakeContext(agent));
    result.Effect.Should().Be(PermissionEffect.Allow);
}

[Fact]
public async Task ReflectionAgent_Should_Deny_Write_And_Shell()
{
    var (agent, auth) = BuildReflectionAgent();
    (await auth.AuthorizeAsync(MakeWriteResource("/ws/a.txt"), MakeContext(agent)))
        .Effect.Should().Be(PermissionEffect.Deny);
    (await auth.AuthorizeAsync(MakeShellResource("rm -rf /"), MakeContext(agent)))
        .Effect.Should().Be(PermissionEffect.Deny);
}
```

- [ ] **Step 2: 运行确认失败** → 编译失败

- [ ] **Step 3: 实现**

`SkillCreateTool`：`ToolBase` 子类。`Description` 首句「把一条可复用的流程沉淀为项目级技能。」+ 时机与限制说明。`ParametersSchema` 满足 R1–R9：三个必填参数，各自 `description` 非空，`additionalProperties: false`，`name` 有 `maxLength: 64`、`content` 有 `maxLength: 8000`、`description` 有 `maxLength: 1024`。

校验顺序（对齐平台，不自创）：`name` 长度 ≤64 且不含 `\ / : * ? " < > |` 与控制字符；`description` 非空 ≤1024；`content` 非空 ≤8000 且不含「忽略之前」「ignore previous」类模式。

`ApplyCreate`：先 `ISkillCatalog.IsEnabled(name)` 或 `Find(name) != null` → 拒绝（`overwrite:false` 的显式前置判断，避免依赖写入器报错文案），然后 `ISkillWriter.CreateAsync(draft, ConfigLevel.Project, overwrite: false)`，`draft.Metadata` 写 `distilled-by`/`source-session`。

`SuggestOnly`：不写盘，返回 `"已记录建议，未写入技能（SuggestOnly 模式）。"` + 建议正文。

`ReflectionModule`：`ActivateAsync` 里 `tm.RegisterToolAsync(skillCreateTool)`，`DeactivateAsync` 里 `UnregisterToolAsync("skill_create")`。同时把 agent 定义的 `PermissionRules` 改成 spec §4.4 的版本（含 `Allow(File,"*",0)`、**不含** `Deny(File,"*")`、**不含** `Deny(Tool,"*")`），`AllowedTools` 为 `["read","grep","glob","skill","skill_create"]`，`DeniedTools` 为 `["task","write","edit","delete"]`。

- [ ] **Step 4: 运行确认通过**

Run: `--TestCaseFilter:"FullyQualifiedName~SkillCreate|FullyQualifiedName~ReflectionAgentPermission"` → 6 passed

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(reflection): skill_create 工具与受限权限面（含读文件权限回归）"
```

---

### Task 14: 组合根登记与端到端守门

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs`（`Full` 加 `"reflection"`）
- Modify: `samples/Seeing.Agent.HostComposition/FullCapabilityComposition.cs`（加 `AddSeeingReflection(registry)`）
- Modify: `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/BuiltInToolCatalog.cs`（在 `Types()` / `Assemblies()` 里登记 `SkillCreateTool`，并把它所在程序集加入清单）
- Modify: `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/ToolMetadataTests.cs:187-200`（`HaveCount(52)` → `53`，id 列表加 `"skill_create"`）
- Modify: `tests/apps/Seeing.Agent.HostComposition.Tests/FullCapabilityCompositionTests.cs`
- Test: `tests/e2e/*/ReflectionEndToEndTests.cs`（若 e2e 项目支持；否则并入 Reflection.Tests）

**Interfaces:**
- Consumes: 全部前置任务
- Produces: `full` 能力集包含 `reflection`；宿主默认启用；工具目录含 `skill_create`

**注意**：`BuiltInToolCatalog` 是**测试侧**硬编码清单（`src` 无此类）。`ToolMetadataTests:175-182` 断言「反射扫描到的具体 ITool 类型 == 清单」，所以新增工具必须同时登记类型，否则会报「清单与实际类型不一致」，而不是只改个数。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Full_CapabilitySet_Should_Include_Reflection()
    => BuiltInCapabilitySets.Full.Should().Contain("reflection");

[Fact]
public void Fully_Composed_Host_Should_Register_Reflection_Module()
    => BuildFullHost().GetServices<ISeeingModule>()
        .Should().Contain(m => m.Id == "reflection");

[Fact]
public void Tool_Catalog_Should_Include_SkillCreate()
    => BuiltInToolCatalog.All().Select(t => t.Id).Should().Contain("skill_create");
```

- [ ] **Step 2: 运行确认失败** → FAIL

- [ ] **Step 3: 实现 + 修正既有守门**

改两处登记点与工具计数断言。

- [ ] **Step 4: 全量验证**

```powershell
dotnet build Seeing.Agent.slnx
dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```

Expected: 0 error，全部 passed。

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(reflection): 登记 full 能力集与宿主组合根，补齐守门测试"
```

---

## 自检（已执行）

**Spec 覆盖**：§3.1→T1/T3；§3.2→T2；§3.3→T9；§3.4→T4（含三处视图过滤，见 §3.4 列出的 `TaskStatusTool`/`TaskTool`/`TuiSurfaceProvider`/标题生成，均落在 T4 的 Step 3 与 T3 的标题门禁）；§3.5→**已撤销（T5 作废，`reflection` agent 改用 `Primary`+`IsHidden`，落在 T10）**；§3.6→T6；§3.7→T7；§3.8→T8；§4.1–4.10→T10/T11/T12/T13/T14。

**类型一致性**：`ExecutionOutcome`/`IExecutionOutcomeObserver`（T1）在 T3/T8/T10/T12 复用同一签名；`SessionOrigin`（T4）在 T10/T12 复用；`ReflectionSuggestion`（T11）在 T12 复用；`IReflectionExecutor.RunAsync` 返回 `ReflectionRunResult` 在 T10（桩）与 T12（实现）一致。

**并行边界**：Wave 1 的**三个**任务互不改同一文件（T1 只动 Abstractions/Execution，T2 动 Runtime + SessionRuntime，T4 动 primitives/Session）。T6 因同改 `src/primitives/Seeing.Session/` 目录且与 T8/T9 共享 `MemoryServiceExtensions.cs`（DI 注册文件串行叠加），移到 Wave 1b 与 T4 串行。T5 已作废。

**未决项（留给 builder）**：`ExecutionOutcome` 放在 `ExecutionCompleteEvent` 内部还是并列字段；`CreateInternalAsync` 是否入独立 partition；journal 文件名冲突后缀；`SessionOrigin` 是否覆盖 Scheduler/Heartbeat 创建路径（本期只加枚举值）。
