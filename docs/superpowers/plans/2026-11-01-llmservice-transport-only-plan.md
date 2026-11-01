# LlmService 职责收缩与记忆注入重做 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `LlmService` 收敛为纯传输层（不再改写 prompt），并把记忆注入迁到 `IContextSource` 管道。

**Architecture:** A-1 阶段删除 `ChatBeforeStart` / `LlmSystemPrompt` 两个 prompt 改写钩子、把 `ChatRequest` 移进 attempt 循环、删掉失效的 `MemoryRecallHandler`；A-2 阶段新增 `IContextExecutionScope.UserQuery` 与 `MemoryContextSource`（`Target = Message`），让记忆召回每轮以一条消息进入历史。

**Tech Stack:** .NET 10 / C#，xUnit 2.9 + FluentAssertions 6.12 + Moq 4.20

**Spec:** `docs/superpowers/specs/2026-11-01-llmservice-responsibility-and-memory-contextsource-design.md`

> **v2 修订**：v1 经独立核查发现 12 处事实错误 + 2 个未决设计问题。已修正并采纳两项决策：`Target = Message`（不是 System）、失败/无命中都返回 `Unavailable`。

## Global Constraints

- 目标框架 `net10.0`；`ImplicitUsings` / `Nullable` 均 enable。
- 能力包只允许引用 `Seeing.Agent.Abstractions`、`Seeing.Session`、`src/support/*`。
- **所有注释、日志、用户可见文本必须用中文。**
- 私有字段 `_camelCase`；异步方法 `Async` 后缀；每个新增 public 成员有中文 `<summary>`。
- 日志用结构化占位符 `{PropertyName}`。
- **A-1 与 A-2 必须分开提交**；但 **T1 与 T3 必须合并成一个 commit**（见下方裁决）。
- 测试运行一律 VSTest（MTP 在本环境无法发现测试）：

```powershell
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/<proj>/bin/Debug/net10.0/<Proj>.dll --TestCaseFilter:"FullyQualifiedName~<Filter>"
```

- **测试判定必须校验「总计 N > 0」**（`--TestCaseFilter` 拼错会 0 用例执行且 exit 0，伪造全绿）。
- 变异验证必须 `--no-incremental`。

## 核查后的关键裁决

| # | 问题 | 裁决 |
|---|---|---|
| R1 | 删 `ChatBeforeStart` 会让 `MemoryRecallHandler.cs`、`HookManagerTests.cs`、`MemoryModuleHookLifecycleTests.cs` 三处编译失败，T1 单独提交必然红 | **T1 与 T3 合并为 T1（一个 commit）**。A-1/A-2 的分离不受影响 |
| R2 | `HookManagerTests` 用 `ChatBeforeStart` 测 Blocking 策略 | 改用 `ChatParams`（同为 Blocking，且保留） |
| R3 | `MemoryModuleHookLifecycleTests` 断言 `ChatBeforeStart` 计数 | 改为断言「模块激活后不再登记 chat 前置钩子」（`ChatBeforeStart` 点已不存在，用 `ChatParams` 计数替代） |
| R4 | `ContextAssembler` 对 System 源基线只建一次，之后只叙述 delta → 系统提示永久停在第一轮 | **改用 `Target = Message`**：每轮以消息形式进入历史，值变化时产生一条增量消息 |
| R5 | `RecallAsync` 无法区分「无命中」与「IO 失败」 | **失败/无命中都返回 `Unavailable`**，不叙述 `Removed`。登记为已知边界 |
| R6 | `TryAddScoped<IContextSource, T>()` 按 ServiceType 去重，已有源时整条跳过 | 改 `TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>())`（与 `SkillsModule.cs:113` 一致） |
| R7 | `IContextExecutionScope` 有 3 个实现（1 生产 + 2 测试 fake） | T4 必须同步改这 3 处，File Structure 已列 |
| R8 | `AgentExecutor.ExecuteAsync` 签名是 `(agent, messages, context, ct)` | T2 测试补全实参；构造范式抄 `AgentExecutorTurnRetryTests.cs:91-106` |
| R9 | `LlmService` 删两个块后仍写 `MaxTokens`/`Model`/`ThinkingEffort`/`Temperature` | **Produces 收窄为「不再改写 `SystemPrompt`」**，不是「零写入」 |
| R10 | 两条 `Not_Modify_Request_SystemPrompt` 用例在改前就通过（`LlmSystemPrompt` 零订阅） | 保留为**回归守门**，不参与 Step 2 的失败计数；Step 2 预期失败的是另两条 |
| R11 | `IHookManager` 无 `Registered` 成员 | 用自建 `RecordingHookManager`（照 `MemoryModuleHookLifecycleTests.cs:109` 的 private 版本） |
| R12 | `MaxInjectTokens*4`(3200) 与 `MaxBaselineChars`(8000) 冲突 | **统一为 `MaxBaselineChars = 8000`**，内部截断也用它；`MaxInjectTokens` 只用于召回条数预算 |

## Review Focus

1. **同一 `ChatRequest` 被连调两次时 `SystemPrompt` 被修改**（重试路径）。期望：A-1 后 `LlmService` 不再写 `SystemPrompt`。→ Task 1
2. **`ChatParams` / `ChatHeaders` / `ChatAfterComplete` / `ChatOnError` 在删钩子后行为回归**。期望：完全不变。→ Task 1
3. **`UserQuery` 为空时记忆源崩溃或注入空分节**（纯系统批次）。期望：`Unavailable`，不注入。→ Task 3
4. **同一轮内多次装配导致记忆消息累积**（T9 三轮补丁的原始 bug）。期望：值不变 → 无新消息。→ Task 4
5. **召回失败被误叙述为"记忆已移除"**。期望：`Unavailable`，不叙述。→ Task 4

---

## File Structure

### A-1（T1 + T2）

| 文件 | 职责 |
|---|---|
| `src/spine/Seeing.Agent.Core/Llm/LlmService.cs` | 删两处 `ChatBeforeStart`（`:330-338`、`:459-468`）+ 两处 `LlmSystemPrompt`（`:379-394`、`:511-530`） |
| `src/abstractions/Seeing.Agent.Abstractions/Hooks/HookRegistry.cs` | 删 `ChatBeforeStart`（`:26-27`）、`LlmSystemPrompt`（`:148-149`） |
| `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs` | `request` 移进 attempt 循环（`:187`） |
| `src/capabilities/Seeing.Agent.Memory/Integration/MemoryRecallHandler.cs` | 删除 |
| `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs` | 删 `recall` 注册/撤销两行 |
| `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs` | 删 `TryAddSingleton<MemoryRecallHandler>()` |
| `tests/spine/Seeing.Agent.Tests/Hooks/HookManagerTests.cs:37,41,47` | `ChatBeforeStart` → `ChatParams` |
| `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryModuleHookLifecycleTests.cs:51,58,98` | 改断言 |
| `docs/architecture/24-llmservice-transport-only-release-notes.md` | 新建 |

### A-2（T3 + T4）

| 文件 | 职责 |
|---|---|
| `src/abstractions/Seeing.Agent.Abstractions/Context/IContextExecutionScope.cs` | 加 `UserQuery` |
| `src/spine/Seeing.Agent.Core/Context/ContextExecutionScope.cs` | 实现 `UserQuery` |
| `tests/spine/Seeing.Agent.Tests/Context/Fakes/SkillsContextSourceTests.cs:320` | fake 实现新属性 |
| `tests/spine/Seeing.Agent.Tests/Context/Fakes/ProjectInstructionsContextSourceTests.cs:367` | fake 实现新属性 |
| `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:224-228` | 填充 `UserQuery` |
| `src/capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs` | 新建 |
| `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs` | `TryAddEnumerable` 注册 |

**注**：`ContextExecutionScope` 与两个测试 fake 的确切路径以 `grep ": IContextExecutionScope"` 为准（核查报告给出的行号，实施时先确认）。

---

## Task Dependency Graph

```
T1 删钩子 + 删 MemoryRecallHandler + 修测试引用（必须合并）
T2 AgentExecutor request 生命周期（可与 T1 并行，改不同文件）
T1 → T3 UserQuery（改 Abstractions + 3 个实现）
T3 → T4 MemoryContextSource（改 Memory 包）
```

---

### Task 1: 删除 prompt 改写钩子 + 删除 MemoryRecallHandler（A-1 部分一）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Llm/LlmService.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Hooks/HookRegistry.cs`
- Delete: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryRecallHandler.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Modify: `tests/spine/Seeing.Agent.Tests/Hooks/HookManagerTests.cs`
- Modify: `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryModuleHookLifecycleTests.cs`
- Create: `docs/architecture/24-llmservice-transport-only-release-notes.md`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmTransportOnlyTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `LlmService` **不再改写 `ChatRequest.SystemPrompt`**（仍会写 `MaxTokens`/`Model`/`ThinkingEffort`/`Temperature`/`TopP`，这是传输层参数适配，保留）
  - `HookRegistry` 不再含 `ChatBeforeStart` / `LlmSystemPrompt` 字段
  - Memory 包不再含 `MemoryRecallHandler`

- [ ] **Step 1: 写失败测试**

```csharp
public class LlmTransportOnlyTests
{
    [Fact]
    public void HookRegistry_Should_Not_Define_Prompt_Rewriting_Hooks()
    {
        var names = typeof(HookRegistry)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.Name).ToList();
        names.Should().NotContain("ChatBeforeStart");
        names.Should().NotContain("LlmSystemPrompt");
    }

    [Fact]
    public async Task CompleteAsync_Should_Not_Trigger_Removed_Hooks()
    {
        var (llm, hooks) = BuildLlm();
        await llm.CompleteAsync("m1", MakeRequest(), "ses_1");
        hooks.TriggeredPoints.Should().NotContain("chat.before_start");
        hooks.TriggeredPoints.Should().NotContain("llm.system_prompt");
    }

    [Fact]
    public async Task CompleteAsync_Should_Still_Trigger_Transport_Hooks()
    {
        // Review Focus #2：保留的传输层钩子不变
        var (llm, hooks) = BuildLlm();
        await llm.CompleteAsync("m1", MakeRequest(), "ses_1");
        hooks.TriggeredPoints.Should().Contain("chat.params");
        hooks.TriggeredPoints.Should().Contain("chat.headers");
        hooks.TriggeredPoints.Should().Contain("chat.after_complete");
    }

    [Fact]
    public async Task CompleteAsync_Should_Not_Modify_SystemPrompt()
    {
        // 回归守门：改前就通过（旧 LlmSystemPrompt 零订阅），改后必须继续通过
        var (llm, _) = BuildLlm();
        var request = MakeRequest(systemPrompt: "原始提示");
        await llm.CompleteAsync("m1", request, "ses_1");
        request.SystemPrompt.Should().Be("原始提示");
    }

    [Fact]
    public async Task CompleteStreamAsync_Should_Not_Modify_SystemPrompt()
    {
        var (llm, _) = BuildLlm();
        var request = MakeRequest(systemPrompt: "原始提示");
        await foreach (var _ in llm.CompleteStreamAsync("m1", request, "ses_1")) { }
        request.SystemPrompt.Should().Be("原始提示");
    }

    [Fact]
    public void Memory_Assembly_Should_Not_Define_MemoryRecallHandler() =>
        typeof(MemoryModule).Assembly.GetTypes()
            .Should().NotContain(t => t.Name == "MemoryRecallHandler");
}
```

**`BuildLlm` 需要自建 `RecordingHookManager`**（`IHookManager` 无 `Registered` 成员）。可照抄 `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryModuleHookLifecycleTests.cs:109` 的 private 实现，暴露 `TriggeredPoints` 收集每次 `TriggerBlockingAsync` 的点名。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~LlmTransportOnly"`
Expected: **2 个 FAIL**（`HookRegistry_Should_Not_Define_Prompt_Rewriting_Hooks`、`CompleteAsync_Should_Not_Trigger_Removed_Hooks`），其余 4 个通过（见裁决 R10）。

- [ ] **Step 3: 实现（一次性做完，因为删字段必然连带多处编译失败）**

1. `LlmService.cs`：
   - 删非流式 `ChatBeforeStart` 调用（`:330-338`）
   - 删非流式 `LlmSystemPrompt` 整块（`:379-394`，含 `if (!string.IsNullOrEmpty(request.SystemPrompt))`）
   - 删流式 `ChatBeforeStart` 调用（`:459-468`）
   - 删流式 `LlmSystemPrompt` 整块（`:511-530`）
   - **不要动** `ChatParams` / `ChatHeaders` / `ChatMessage` / `ChatAfterComplete` / `ChatOnError`
2. `HookRegistry.cs`：删 `ChatBeforeStart`（`:26-27`）与 `LlmSystemPrompt`（`:148-149`）字段及注释
3. 删除 `MemoryRecallHandler.cs`
4. `MemoryModule.RegisterHooks` / `UnregisterHooks` 删 `recall` 两行
5. `MemoryServiceExtensions` 删 `TryAddSingleton<MemoryRecallHandler>()`
6. `HookManagerTests.cs:37,41,47`：`HookRegistry.ChatBeforeStart` → `HookRegistry.ChatParams`（同为 Blocking）
7. `MemoryModuleHookLifecycleTests.cs:51,58,98`：把 `ChatBeforeStart` 断言改为 `ChatParams`（模块激活后应登记 1 个），移除 `AddSingleton<MemoryRecallHandler>()` 两处
8. 写 release notes（见 Step 4）

- [ ] **Step 4: 写 release notes**

`docs/architecture/24-llmservice-transport-only-release-notes.md` 必须包含：
- **Breaking**：`HookRegistry.ChatBeforeStart` / `LlmSystemPrompt` 已删除
- **Breaking**：`Seeing.Agent.Memory` 的 `MemoryRecallHandler` 已删除
- **已知空窗**：A-1 → A-2 之间记忆注入完全不生效。这**不是回归**——`MemoryRecallHandler` 在此前也从未工作过（`ChatBeforeStart` 只传 `modelId`/`provider`，handler 恒提前返回）
- **不变**：`ChatParams` / `ChatHeaders` / `ChatMessage` / `ChatAfterComplete` / `ChatOnError` 行为不变

- [ ] **Step 5: 运行确认通过 + 全仓编译**

Run:
```
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~LlmTransportOnly"
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookManager"
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll
```
Expected: build 0 error；6 passed；HookManager 与 Memory 全量 0 failed；**每个都校验「总计 N > 0」**

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "refactor(llm): 删除 prompt 改写钩子，LlmService 收敛为纯传输层；移除失效的 MemoryRecallHandler"
```

---

### Task 2: `ChatRequest` 移进 attempt 循环（A-1 部分二）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:187`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/ExecutionRequestLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 1（`LlmService` 不再写 `SystemPrompt`）
- Produces: 每次 attempt 拿到全新 `ChatRequest` 实例

**构造范式**：抄 `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorTurnRetryTests.cs:91-106`（11 参数构造；`IModelManager.ResolveNativeModel` 必须返回非空）。该文件 `:23-29` 已有「首次抛 `LlmStreamingException`、第二次 yield 正常」的可复用实现。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Each_Retry_Attempt_Should_Get_A_Fresh_Request()
{
    var captured = new List<ChatRequest>();
    var executor = BuildExecutor(captured, failFirstAttempt: true);
    var agent = MakeAgent();

    await foreach (var _ in executor.ExecuteAsync(
        agent, new List<ChatMessage> { User("hi") }, MakeContext(), CancellationToken.None))
    { }

    captured.Should().HaveCount(2);
    captured[0].Should().NotBeSameAs(captured[1]);
}

[Fact]
public async Task Normal_Path_Should_Build_Request_Once()
{
    var captured = new List<ChatRequest>();
    var executor = BuildExecutor(captured, failFirstAttempt: false);
    var agent = MakeAgent();

    await foreach (var _ in executor.ExecuteAsync(
        agent, new List<ChatMessage> { User("hi") }, MakeContext(), CancellationToken.None))
    { }

    captured.Should().HaveCount(1);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `--TestCaseFilter:"FullyQualifiedName~ExecutionRequestLifecycle"`
Expected: `Each_Retry_Attempt_Should_Get_A_Fresh_Request` FAIL（两次同一实例）

- [ ] **Step 3: 实现**

把 `var request = await BuildRequestAsync(...)`（`:187`）从 `for (var attempt...)` **之前**移到**循环体第一行**。

- [ ] **Step 4: 运行确认通过 + 全量回归**

Run:
```
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```
Expected: 2 passed；全量 0 failed；**校验「总计 N > 0」**

- [ ] **Step 5: 提交**

```bash
git commit -am "fix(executor): ChatRequest 移进 attempt 循环，避免跨重试复用可变请求对象"
```

**A-1 到此完成。**

---

### Task 3: `IContextExecutionScope.UserQuery`（A-2 部分一）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Context/IContextExecutionScope.cs`
- Modify: `src/spine/Seeing.Agent.Core/Context/ContextExecutionScope.cs`（实现类，路径以 grep 为准）
- Modify: `tests/spine/Seeing.Agent.Tests/Context/Fakes/SkillsContextSourceTests.cs:320`
- Modify: `tests/spine/Seeing.Agent.Tests/Context/Fakes/ProjectInstructionsContextSourceTests.cs:367`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:224-228`
- Test: `tests/spine/Seeing.Agent.Tests/App/Execution/ExecutionScopeUserQueryTests.cs`

**Interfaces:**
- Consumes: `record.Input?.Text`（`ChatInput.Text`，**本批最后一条** user 文本，见 `SessionRuntime.cs:467`）
- Produces: `IContextExecutionScope.UserQuery { get; set; }`（`string?`）

**先做**：`grep ": IContextExecutionScope"` 确认全部 3 个实现的确切路径。Test fake 只需加自动属性即可。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ExecutionScope_Should_Carry_Last_User_Text_As_UserQuery()
{
    // 一批含两条用户消息时取最后一条
    var (runner, scope) = BuildRunnerWithBatch();
    await runner.RunOnceAsync(Record("ses_1"), Batch("第一条", "最后一条"), CancellationToken.None);
    scope.UserQuery.Should().Be("最后一条");
}

[Fact]
public async Task ExecutionScope_UserQuery_Should_Be_Empty_For_System_Only_Batch()
{
    var (runner, scaffold) = BuildRunnerWithBatch();
    await runner.RunOnceAsync(Record("ses_2"), SystemOnlyBatch(), CancellationToken.None);
    scaffold.Scope.UserQuery.Should().BeNullOrEmpty();
}
```

**`scope` 获取方式**：`ExecutionRunner` 内 `CreateScope()` 的作用域在 `RunOnceAsync` 返回后释放。测试须经 `AddStubContextPipeline` 注入的 `RecordingContextAssembler`（`tests/.../Context/Stubs/StubContextAssembler.cs:101-108` 的 `Scope` 属性）捕获。

- [ ] **Step 2: 运行确认失败** → FAIL（`UserQuery` 属性不存在，编译失败）

- [ ] **Step 3: 实现**

1. `IContextExecutionScope` 加：
   ```csharp
   /// <summary>
   /// 本轮执行的用户查询文本（本批最后一条用户输入）。
   /// <para>供「查询依赖」的上下文源使用（如记忆召回）。为空时源按不可用处理。</para>
   /// <para>由宿主在装配前填充一次，执行期内只读。</para>
   /// </summary>
   string? UserQuery { get; set; }
   ```
2. `ContextExecutionScope` 实现（`public string? UserQuery { get; set; }`）
3. 两个测试 fake 同步加自动属性
4. `ExecutionRunner.cs:224-228` 的 `executionScope` 赋值块内加：
   ```csharp
   executionScope.UserQuery = record.Input?.Text;
   ```

- [ ] **Step 4: 运行确认通过**

Run:
```
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionScopeUserQuery"
```
Expected: 2 passed；build 0 error

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(context): IContextExecutionScope 增加 UserQuery，供查询依赖的上下文源使用"
```

---

### Task 4: `MemoryContextSource`（A-2 部分二）

**Files:**
- Create: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryContextSourceTests.cs`

**Interfaces:**
- Consumes: `IContextExecutionScope.UserQuery`（T3）、`IMemoryRecallService.RecallAsync(string, CancellationToken) → Task<IReadOnlyList<SearchHit>>`、`MemoryOptions.Retrieval`、`MemoryModuleActivity.IsActive`
- Produces: `MemoryContextSource : IContextSource`

**参照**：`src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs`

**8 个必须实现的成员**：`Key` / `Scope` / **`ValueSchemaVersion`** / `Target` / `MaxBaselineChars` / `ReadAsync` / `RenderInitial` / `Render`

**关键约束（核查确认）**：
- `ContextSourceValue(ContextValueState, JsonElement?, object?)` 是 positional record
- **`Canonical` 必须是 JSON 对象**（数组会被装配器判为无效并跳过，`ContextAssembler.cs:261-275`）→ 用 `{"hits":[{"path","score"}]}`
- `Render(previous, current)` 的 **`previous.Payload` 恒为 null**（`ContextAssembler.cs:1012-1018`）→ 增量叙述只能依赖 `Canonical`
- 注册必须用 `TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>())`（裁决 R6）
- 截断上限统一用 `MaxBaselineChars = 8000`（裁决 R12）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ReadAsync_Should_Return_Unavailable_When_UserQuery_Empty()
{
    // Review Focus #3
    var source = Build(userQuery: null, hits: []);
    (await source.ReadAsync(default)).State.Should().Be(ContextValueState.Unavailable);
}

[Fact]
public async Task ReadAsync_Should_Return_Unavailable_When_No_Hits()
{
    // 裁决 R5：无命中也是 Unavailable（不叙述 Removed）
    var source = Build(userQuery: "问题", hits: []);
    (await source.ReadAsync(default)).State.Should().Be(ContextValueState.Unavailable);
}

[Fact]
public async Task ReadAsync_Should_Return_Unavailable_When_Module_Inactive()
{
    var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9)], active: false);
    (await source.ReadAsync(default)).State.Should().Be(ContextValueState.Unavailable);
}

[Fact]
public async Task ReadAsync_Should_Return_Unavailable_In_ToolsOnly_Mode()
{
    var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9)], mode: MemoryRetrievalMode.ToolsOnly);
    (await source.ReadAsync(default)).State.Should().Be(ContextValueState.Unavailable);
}

[Fact]
public async Task ReadAsync_Should_Return_Value_With_Stable_Object_Canonical()
{
    var source = Build(userQuery: "问题", hits: [Hit("b.md", 0.9), Hit("a.md", 0.8)]);
    var v1 = await source.ReadAsync(default);
    var v2 = await source.ReadAsync(default);
    v1.State.Should().Be(ContextValueState.Value);

    var json = v1.Canonical!.Value;
    json.ValueKind.Should().Be(JsonValueKind.Object);          // 必须是对象
    v1.Canonical.Value.GetRawText().Should().Be(v2.Canonical!.Value.GetRawText());
    v1.Canonical.Value.GetRawText().Should().Contain("\"a.md\"").And.Contain("\"b.md\"");
    v1.Canonical.Value.GetRawText().Should().NotContain("内容");  // 不含正文
}

[Fact]
public async Task RenderInitial_Should_Produce_Relevant_Memories_Section()
{
    var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.92, "标题A", "内容A")]);
    var text = source.RenderInitial(await source.ReadAsync(default));
    text.Should().StartWith("## Relevant memories");
    text.Should().Contain("标题A");
}

[Fact]
public void Render_Should_Return_Null_When_Canonical_Unchanged()
{
    // Review Focus #4：值不变不叙述
    var source = Build(userQuery: "问题", hits: []);
    var v = BuildValue(("a.md", 0.9));
    source.Render(v, v).Should().BeNull();
}

[Fact]
public void Render_Should_Narrate_When_Canonical_Changed()
{
    var source = Build(userQuery: "问题", hits: []);
    source.Render(BuildValue(("a.md", 0.9)), BuildValue(("b.md", 0.7)))
        .Should().Contain("b.md");
}

[Fact]
public void Target_Should_Be_Message()
{
    // 裁决 R4
    Build(userQuery: null, hits: []).Target.Should().Be(ContextSourceTarget.Message);
}
```

- [ ] **Step 2: 运行确认失败** → 编译失败（`MemoryContextSource` 不存在）

- [ ] **Step 3: 实现**

`ReadAsync` 顺序：
1. `!_activity.IsActive` → `Unavailable`
2. `Retrieval.Mode == ToolsOnly` → `Unavailable`
3. `_scope.UserQuery` 为空 → `Unavailable`
4. `RecallAsync(query)` → **无命中也返回 `Unavailable`**（裁决 R5；`Removed` 不用，因为无法区分失败与无命中）
5. 有命中 → `Value`，`Canonical = {"hits":[{"path":"...","score":0.92}]}` 按 path ordinal 排序，不含正文；`Payload` = 命中条目（含正文）

`RenderInitial`：渲染 `## Relevant memories` + `- (score) 标题: 摘要`（摘要截断 240 字符），总长超 `MaxBaselineChars`(8000) 截断。

`Render(previous, current)`：比较 `Canonical` 的 raw text；相同 → `null`；不同 → 叙述新增/消失的条目（**只能读 `Canonical`，不能读 `Payload`**）。

`MemoryServiceExtensions` 注册：
```csharp
services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>());
```

- [ ] **Step 4: 运行确认通过**

Run:
```
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll --TestCaseFilter:"FullyQualifiedName~MemoryContextSource"
```
Expected: 9 passed；**校验「总计 9 > 0」**

- [ ] **Step 5: 提交**

```bash
git commit -am "feat(memory): 新增 MemoryContextSource，记忆召回以 Message 目标进入历史"
```

---

### Task 5: 接线验收 —— 累积与增量守门

**Files:**
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/MemoryContextSourceAccumulationTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `MemoryContextSource`、`IContextAssembler`
- Produces: 无（纯验收）

**必须用 `ContextAssemblyMode.Persist`**（`ReadOnly` 下 `DeltaText` 恒 null，无判别力）。
**构造方式**：直接 `new ContextAssembler(sources, ...)`（照 `tests/.../Context/ContextAssemblerTests.cs:20-28`），不要走 DI——走 DI 会让 Step 2 因注册问题失败，失去 TDD 序列。

- [ ] **Step 1: 写测试**

```csharp
[Fact]
public async Task Repeated_Assembly_With_Same_Hits_Should_Not_Add_Message()
{
    // Review Focus #4：T9 三轮补丁的原始 bug
    var (assembler, session, recall) = BuildWithMemorySource(
        hits: [Hit("a.md", 0.9, "标题A", "内容A")]);

    var r1 = await assembler.EnsureFreshAsync(session, ContextAssemblyMode.Persist, null, null);
    var before = session.Messages.Count;

    var r2 = await assembler.EnsureFreshAsync(session, ContextAssemblyMode.Persist, null, null);

    r1.SystemBaseline.Should().Contain("## Relevant memories");   // Message 目标：基线为空，内容在消息里
    session.Messages.Count.Should().Be(before, "值未变不应新增消息");
    r2.DeltaText.Should().BeNull();
}

[Fact]
public async Task Changed_Hits_Should_Add_Exactly_One_Message()
{
    var (assembler, session, recall) = BuildWithMemorySource(hits: [Hit("a.md", 0.9)]);
    await assembler.EnsureFreshAsync(session, ContextAssemblyMode.Persist, null, null);
    var before = session.Messages.Count;

    recall.SetHits([Hit("b.md", 0.7)]);
    var r2 = await assembler.EnsureFreshAsync(session, ContextAssemblyMode.Persist, null, null);

    session.Messages.Count.Should().Be(before + 1, "值变化应恰好新增一条");
    r2.DeltaText.Should().NotBeNull();
}
```

**注意**：`Message` 目标下 `SystemBaseline` 不含记忆内容（它进消息）。第一条断言若失败，先确认 `Target` 是否真的是 `Message`，再调整断言——**不要**改成断言消息内容以外的东西来让测试通过。

- [ ] **Step 2: 运行确认**

若两条都通过 → **说明测试无判别力**（`BuildWithMemorySource` 可能没真正接入源），必须排查后再继续。
若第 1 条失败（消息累积）→ 说明 `Canonical` 不稳定或装配层有问题，修根因，**不要**加去重逻辑。

- [ ] **Step 3: 全量验证**

```powershell
dotnet build Seeing.Agent.slnx --no-incremental
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll
```

Expected: 全部 0 failed；**每个都校验「总计 N > 0」**

- [ ] **Step 4: 提交**

```bash
git commit -am "test(memory): 记忆注入累积与增量守门"
```

---

## 已知边界（写进 release notes，不在本期修）

1. **Scheduler / Heartbeat 旁路不填 `UserQuery`**（`SchedulerContextAssembly.cs:79-85` 只填 3 个字段）→ 定时任务里记忆**永不注入**。若要覆盖需另改 Scheduler 包。
2. **`Message` 目标每轮值变化都会新增一条消息** → 长会话中记忆消息会累积稀释历史。这是 `Target = Message` 的固有语义（用户已裁决采用）。缓解方向：召回结果在同 turn 内稳定（`UserQuery` 固定），跨 turn 才变化。
3. **`RecallAsync` 无法区分失败与无命中** → 都返回 `Unavailable`，表现为"本轮无记忆注入"。可观测信号：`RecallAsync` 内部已有 Warning 日志。
4. **继续类命令的批次**（`shouldContinue = true`，如 `/skill`）装配仍会发生，`UserQuery` = 命令原文 → 召回可能不相关。可接受（命令批次罕见且影响小）。

## 自检

**Spec 覆盖**：
- §4.1（删两个 hook）→ T1
- §4.2（`UserQuery` + 迁 ContextSource）→ T3 / T4
- §4.3（`MemoryContextSource`）→ T4（**已改 `Target = Message`**）
- §4.4（`request` 生命周期）→ T2
- §4.5（A-1/A-2 分开）→ T1+T2 是 A-1，T3+T4+T5 是 A-2
- §6 验收 → T1（不再写 `SystemPrompt`、保留钩子不变）、T3（`UserQuery`）、T4（`Unavailable` 各种场景、稳定 Canonical）、T5（不累积 + 恰好一条）

**Review Focus 落位**：#1→T1、#2→T1、#3→T3+T4、#4→T4+T5、#5→T4。

**类型一致性**：`IContextExecutionScope.UserQuery`（T3）在 T4 消费；`ContextValueState` 枚举值在 T4/T5 一致；`ContextSourceTarget.Message` 在 T4 设置、T5 验证。

**未决项（留给 builder）**：
- T1 的 `RecordingHookManager`：照 `MemoryModuleHookLifecycleTests.cs:109` 的 private 版抄一份到 `Seeing.Agent.Tests`
- T2 的 `BuildExecutor`：抄 `AgentExecutorTurnRetryTests.cs:91-106`
- T4 的 `Render` 叙述措辞：spec 只要求「变化时叙述」，具体文案由 builder 定
- T5 的 `BuildWithMemorySource`：直接 `new ContextAssembler`，源列表 = `[memoryContextSource]`
