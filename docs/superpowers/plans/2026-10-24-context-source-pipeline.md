# 上下文源管道（Context Source Pipeline）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立一套通用的上下文源管道：初始渲染字节稳定（不破坏前缀缓存），运行时变化只产生增量叙述；并以它承载项目指令与技能清单，同时把技能状态收敛为单一真相源。

**Architecture:** 每种上下文内容实现一个 `IContextSource`（读值 + 两段渲染）。`ContextAssembler` 负责规范化哈希、diff、增量消息、会话级状态与生命周期；状态存 `SessionData.Metadata["context.baseline"]`。技能目录用不可变快照 + 单调版本 + 事件提供唯一真相源，管道只是它的渲染协议层。

**Tech Stack:** .NET 10、C#、`System.Text.Json`、`FileSystemWatcher`、xUnit v3 4.0、Moq 4.20、FluentAssertions 8.10、Blazor Server（WebUI）

**Spec:** `docs/superpowers/specs/2026-10-24-context-source-pipeline-design.md`

## Global Constraints

- 目标框架 `net10.0`；测试用 xUnit v3 + Moq + FluentAssertions；本环境 MTP 发现不到测试，统一 `dotnet build` + `dotnet vstest <dll> --TestCaseFilter:...`。
- `Seeing.Agent.Abstractions` 只放接口 / DTO / 枚举 / 常量，**零实现**（不得出现方法体、计算属性、`static` 辅助方法）。
- 能力包只能引用 `Abstractions` + `primitives` + `src/support`；不得引用 `Core` 具体类型。
- 工具 schema 仍只在 `SessionToolSchemaResolver` 计算；本计划不触碰工具暴露/权限/执行级可调用集。
- 模块在 `ActivateAsync` 挂载、`DeactivateAsync` 卸载；watcher 生命周期只由 `SkillsModule` 拥有。
- 禁止静默吞异常：失败必须记录日志并进入 `Issues` 或返回失败结果。
- C# 注释与提交信息用中文；日志用结构化占位符 `{Property}`。
- 不新增魔法字符串键：跨层传递一律用类型/强类型集合。
- 本期**不纳入**工具目录源；不得顺手改动 `ToolExposureEngine`、`TokenBudgetManager`、`ToolCatalogSectionContributor`。

## Review Focus

1. AGENTS.md 在读取瞬间被其它进程占用/无权限 —— 期望：保留上一次已叙述内容、不叙述"已删除"，下一次读取成功再恢复。
2. `SKILL.md` 或指令正文中出现 `{{timestamp}}`、`{{model}}` 字面量 —— 期望：原样进入提示词，不被变量替换，缓存不被逐请求击穿。
3. 会话经历压缩 / revert / fork —— 期望：内容不丢失、增量不重复；`Target=Message` 的初始渲染在压缩后重新可见。
4. 安装器写到一半（技能目录已建、`SKILL.md` 未就绪） —— 期望：装配不崩溃，该技能暂不可见，写完后的下一次装配出现。
5. 同一技能在用户级与项目级同时被禁用 —— 期望：启用操作后真正可用（清除所有层级的禁用项）。

---

## File Structure

**Abstractions（契约，零实现）**

| 文件 | 职责 |
|---|---|
| `src/abstractions/Seeing.Agent.Abstractions/Context/ContextSourceModels.cs` | `ContextSourceKey`、`ContextSourceScope`、`ContextSourceTarget`、`ContextValueState`、`ContextSourceValue` |
| `.../Context/IContextSource.cs` | 源契约 |
| `.../Context/IContextAssembler.cs` | 装配入口 + `ContextAssemblyMode`、`ContextAssemblyResult` |
| `.../Skills/ISkillCatalog.cs` | `SkillSource`、`SkillCatalogEntry`、`SkillConflict`、`SkillLoadIssue`、`SkillCatalogSnapshot`、`ISkillCatalog` |
| `.../Skills/ISkillWriter.cs` | `SkillDraft`、`SkillWriteResult`、`ISkillWriter` |
| `.../Skills/ISkillCatalogPin.cs` | 执行级钉住端口 |
| `src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs`（改） | 新增 `ContextBaseline = "context.baseline"` |

**Core（管道实现）**

| 文件 | 职责 |
|---|---|
| `src/spine/Seeing.Agent.Core/Context/CanonicalJson.cs` | 规范化 JSON + sha256 |
| `src/spine/Seeing.Agent.Core/Context/ContextBaselineState.cs` | 状态模型 + `PendingBaselineReset` 位 |
| `src/spine/Seeing.Agent.Core/Context/ContextBaselineStore.cs` | 读写 `SessionData.Metadata` |
| `src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs` | 装配、diff、增量消息、ledger、CAS |
| `src/spine/Seeing.Agent.Core/Context/Sources/IInstructionFileReader.cs` | 逐路径读取结果端口 |
| `src/spine/Seeing.Agent.Core/Context/Sources/InstructionFileReader.cs` | 实现（区分 Missing / Unreadable） |
| `src/spine/Seeing.Agent.Core/Context/Sources/ProjectInstructionsContextSource.cs` | 指令源（`Target=Message`） |
| `src/spine/Seeing.Agent.Core/Compression/CompressionService.cs`（改） | 压缩后置 `Compaction` 位 |
| `src/spine/Seeing.Agent.Core/Core/Prompts/PromptBuilder.cs`（改） | 变量替换后再追加基线 |
| `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`（改） | 透传基线 + 空系统提示词处理 |

**Skills（目录 + 源 + 写入）**

| 文件 | 职责 |
|---|---|
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillFileScanner.cs` | 扫盘、解析、冲突、问题 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogCore.cs` | `ISkillCatalog` 唯一实现 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogFormatter.cs` | 清单唯一格式化入口 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillWriteGate.cs` | `skill-state.json` 写闸 |
| `src/capabilities/Seeing.Agent.Skills/Watching/SkillRootWatcher.cs` | 多根 watcher + 去抖 |
| `src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs` | 技能源（`Target=System`） |
| `src/capabilities/Seeing.Agent.Skills/SkillWriter.cs` | `ISkillWriter` 实现 |
| `src/capabilities/Seeing.Agent.Skills/SkillManager.cs`（重写） | 只读门面 |
| `src/capabilities/Seeing.Agent.Skills/SkillLoader.cs`（改） | 单一幂等加载路径 |
| `src/capabilities/Seeing.Agent.Skills/SkillsModule.cs`（改） | 注册 catalog/writer/watcher/源 |

**Hosting / Scheduler / WebUI（接线）**

| 文件 | 职责 |
|---|---|
| `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`（改） | 装配点、scoped 钉住、基线透传 |
| `src/hosting/Seeing.Agent.Hosting/Execution/SkillCatalogPin.cs` | `ISkillCatalogPin` 实现 |
| `src/spine/Seeing.Agent.Core/Core/ToolManager.cs`（改） | `ExecuteAsync` 增加快照参数 |
| `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`、`HeartbeatJob.cs`（改） | 只读装配 |
| `samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor`、`SkillCreatePage.razor`、`SkillDetailPage.razor`（改） | 订阅事件、写服务 |

**Tests**

| 文件 | 覆盖 |
|---|---|
| `tests/spine/Seeing.Agent.Tests/Context/CanonicalJsonTests.cs` | 规范化与哈希 |
| `tests/spine/Seeing.Agent.Tests/Context/ContextBaselineStoreTests.cs` | 状态读写、版本、pending、ledger |
| `tests/spine/Seeing.Agent.Tests/Context/ContextAssemblerTests.cs` | diff/叙述/只读/字节稳定/并发 |
| `tests/spine/Seeing.Agent.Tests/Context/ProjectInstructionsContextSourceTests.cs` | 指令源 |
| `tests/spine/Seeing.Agent.Tests/Prompts/PromptBuilderBaselineTests.cs` | 基线追加时机 |
| `tests/capabilities/Seeing.Agent.Skills.Tests/SkillCatalogCoreTests.cs` | 目录核心 |
| `tests/capabilities/Seeing.Agent.Skills.Tests/SkillRootWatcherTests.cs` | watcher |
| `tests/capabilities/Seeing.Agent.Skills.Tests/SkillsContextSourceTests.cs` | 技能源 |
| `tests/capabilities/Seeing.Agent.Skills.Tests/SkillWriterTests.cs` | 写入收口 |

---

### Task 1: 规范化 JSON 与哈希

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Context/CanonicalJson.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Context/CanonicalJsonTests.cs`

**Interfaces:**
- Produces: `internal static class CanonicalJson`，成员 `static string Serialize(JsonElement value)`、`static string Hash(JsonElement value)`（返回 64 位小写 hex）。

- [ ] **Step 1: 写失败测试**

```csharp
public class CanonicalJsonTests
{
    // 属性顺序不影响结果
    [Fact] public void Serialize_ShouldIgnorePropertyOrder()
    {
        using var a = JsonDocument.Parse("""{"b":1,"a":2}""");
        using var b = JsonDocument.Parse("""{"a":2,"b":1}""");
        CanonicalJson.Serialize(a.RootElement).Should().Be(CanonicalJson.Serialize(b.RootElement));
        CanonicalJson.Serialize(a.RootElement).Should().Be("""{"a":2,"b":1}""");
    }

    // 空白不影响结果
    [Fact] public void Serialize_ShouldStripWhitespace()
    {
        using var d = JsonDocument.Parse("{\n  \"a\" : [ 1 , 2 ]\n}");
        CanonicalJson.Serialize(d.RootElement).Should().Be("""{"a":[1,2]}""");
    }

    // 已知向量：Hash 必须稳定（golden）
    [Fact] public void Hash_ShouldMatchGoldenVector()
    {
        using var d = JsonDocument.Parse("""{"a":[1,2],"b":"x"}""");
        CanonicalJson.Hash(d.RootElement).Should().Be("<TASK-1 运行时记录的实际值>");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 编译失败（`CanonicalJson` 不存在）

- [ ] **Step 3: 实现**

`Serialize`：对象按键名 ordinal 升序递归输出，数组保序，字符串用 `JsonEncodedText` 等价转义（输出 `"` `\` 控制字符的标准转义，非 ASCII 原样 UTF-8），数字用 `JsonElement.GetRawText()`（保留原始表示，避免精度改写），未定义类型抛 `NotSupportedException`。`Hash` = `SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value)))` 转小写 hex。

- [ ] **Step 4: 运行测试，把真实哈希写回 golden 断言**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~CanonicalJsonTests"`
Expected: 先因空 golden 失败；填入实际值后 3 个测试 PASS

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Context/CanonicalJson.cs tests/spine/Seeing.Agent.Tests/Context/CanonicalJsonTests.cs
git commit -m "feat(context): 规范化 JSON 与稳定哈希"
```

---

### Task 2: 契约 + 会话状态存储

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Context/ContextSourceModels.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Context/IContextSource.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Context/IContextAssembler.cs`
- Create: `src/spine/Seeing.Agent.Core/Context/ContextBaselineState.cs`
- Create: `src/spine/Seeing.Agent.Core/Context/ContextBaselineStore.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Context/ContextBaselineStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum ContextSourceScope { User, Project, Session }`、`enum ContextSourceTarget { System, Message }`、`enum ContextValueState { Initial, Value, Unavailable, Removed }`
  - `readonly record struct ContextSourceKey(string Value)`
  - `sealed record ContextSourceValue(ContextValueState State, JsonElement? Canonical, object? Payload)`
  - `interface IContextSource { ContextSourceKey Key { get; } ContextSourceScope Scope { get; } int ValueSchemaVersion { get; } ContextSourceTarget Target { get; } int MaxBaselineChars { get; } Task<ContextSourceValue> ReadAsync(CancellationToken ct = default); string RenderInitial(ContextSourceValue value); string? Render(ContextSourceValue previous, ContextSourceValue current); }`
  - `enum ContextAssemblyMode { Persist, ReadOnly }`
  - `sealed record ContextAssemblyResult(string SystemBaseline, string? HistoryBaseline, string? DeltaText, IReadOnlyList<string> ChangedSourceKeys, IReadOnlyList<string> SkippedSourceKeys, IReadOnlyList<string> Issues)`
  - `interface IContextAssembler { Task<ContextAssemblyResult> EnsureFreshAsync(SessionData session, ContextAssemblyMode mode, string? insertBeforeMessageId, IReadOnlySet<ContextSourceKey>? skip, CancellationToken ct = default); string BuildSystemBaseline(SessionData session); }`
  - `internal sealed class ContextBaselineState`：`int StateVersion`、`PendingBaselineReset Pending`、`Dictionary<string, ContextSourceState> Sources`；`[Flags] internal enum PendingBaselineReset { None = 0, Compaction = 1, WorkspaceChanged = 2 }`；`internal sealed class ContextSourceState`：`int ValueSchemaVersion`、`string BaselineText`、`string BaselineHash`、`JsonObject? BaselineValues`、`JsonObject? CurrentValues`、`string? BaselineMessageId`、`List<DeltaLedgerEntry> DeltaLedger`；`internal sealed record DeltaLedgerEntry(string MessageId, JsonObject Values)`
  - `internal sealed class ContextBaselineStore`：`static ContextBaselineState Load(SessionData session)`、`static void Save(SessionData session, ContextBaselineState state)`、`static void Clear(SessionData session)`

- [ ] **Step 1: 写失败测试**

```csharp
public class ContextBaselineStoreTests
{
    [Fact] public void SaveThenLoad_ShouldRoundTrip()
    {
        var s = new SessionData { Id = "s1" };
        var state = new ContextBaselineState();
        state.Pending = PendingBaselineReset.Compaction;
        state.Sources["k"] = new ContextSourceState { ValueSchemaVersion = 3, BaselineText = "X" };
        ContextBaselineStore.Save(s, state);

        var loaded = ContextBaselineStore.Load(s);
        loaded.Pending.Should().HaveFlag(PendingBaselineReset.Compaction);
        loaded.Sources["k"].ValueSchemaVersion.Should().Be(3);
        loaded.Sources["k"].BaselineText.Should().Be("X");
    }

    [Fact] public void Load_WithUnknownStateVersion_ShouldReturnEmpty()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = """{"stateVersion":999,"sources":{}}""";
        ContextBaselineStore.Load(s).Sources.Should().BeEmpty();
    }

    [Fact] public void Load_WithCorruptJson_ShouldReturnEmptyAndNotThrow()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = "{not json";
        ContextBaselineStore.Load(s).Sources.Should().BeEmpty();
    }

    [Fact] public void Clear_ShouldRemoveMetadataKey()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = "{}";
        ContextBaselineStore.Clear(s);
        s.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → `ContextBaselineStore` 不存在

- [ ] **Step 3: 实现契约与存储**

契约文件只放 `record`/`interface`/`enum`，无方法体。`ContextBaselineStore` 用 `JsonSerializerOptions { PropertyNamingPolicy = CamelCase }`；`Load` 捕获 `JsonException` 与版本不匹配 → 返回空状态并记 Debug 日志（**不得**返回 null）；`Save` 序列化到 `Metadata[SessionMetadataKeys.ContextBaseline]`；`Clear` 直接 `Remove`。`SessionMetadataKeys` 增加 `public const string ContextBaseline = "context.baseline";` 及注释"清空消息时移除；fork 不复制"。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContextBaselineStoreTests"`
Expected: 4 PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Context src/spine/Seeing.Agent.Core/Context src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs tests/spine/Seeing.Agent.Tests/Context/ContextBaselineStoreTests.cs
git commit -m "feat(context): 源契约与会话基线状态存储"
```

---

### Task 3: 装配器（diff / 叙述 / 只读 / 字节稳定）

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Context/ContextAssemblerTests.cs`

**Interfaces:**
- Consumes: Task 1 `CanonicalJson`、Task 2 契约与 `ContextBaselineStore`
- Produces: `public sealed class ContextAssembler : IContextAssembler`，构造函数 `(IEnumerable<IContextSource> sources, ISessionManager sessionManager, ILogger<ContextAssembler> logger)`；内部 `IContextAssembler.EnsureFreshAsync` / `BuildSystemBaseline`。

- [ ] **Step 1: 写失败测试（用测试内假源）**

```csharp
file sealed class FakeSource : IContextSource
{
    public ContextSourceKey Key => new("fake");
    public ContextSourceScope Scope => ContextSourceScope.Project;
    public int ValueSchemaVersion => 1;
    public ContextSourceTarget Target { get; init; } = ContextSourceTarget.System;
    public int MaxBaselineChars => 0;
    public ContextSourceValue Value { get; set; } = new(ContextValueState.Value, JsonDocument.Parse("""{"items":["a"]}""").RootElement, null);
    public string RenderInitial(ContextSourceValue v) => $"## Fake\n{v.Canonical}";
    public string? Render(ContextSourceValue p, ContextSourceValue c) => $"Fake changed: {c.Canonical}";
    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default) => Task.FromResult(Value);
}

[Fact] public async Task NoChange_ShouldNotEmitMessage()  // 无变化不叙述、不推进
[Fact] public async Task Changed_ShouldEmitExactlyOneMessage()  // 一条 <context-update>，插在锚点前
[Fact] public async Task RenderReturningNull_ShouldNotAdvanceCurrentValues()  // §8.6
[Fact] public async Task Unavailable_ShouldKeepPreviousValuesAndNotNarrate()
[Fact] public async Task Removed_ShouldNarrateMustNotBeUsed()
[Fact] public async Task BuildSystemBaseline_ShouldBeByteIdenticalAcrossCalls()  // 字节稳定
[Fact] public async Task ValueSchemaVersionMismatch_ShouldRebuildAndNarrateFull()  // 版本二分
[Fact] public async Task ReadOnlyMode_ShouldNotMutateSessionOrState()  // 旁路只读
[Fact] public async Task ConcurrentCalls_ShouldAdvanceOnce()  // 乐观并发
```

断言要点：`DeltaText` 非空且 `session.Messages` 新增 1 条（`Role == User`，`Content` 含 `context-update` 与源键）；无变化时 `DeltaText` 为 null 且消息数不变；`Render` 返回 null 的源其 `CurrentValues` 保持旧值；`Unavailable` 时 `ChangedSourceKeys` 为空；`Removed` 文本包含 `must not be used`；连续两次 `BuildSystemBaseline` 字符串 `Should().Be` 相等；`ReadOnly` 后 `Metadata` 不含键且消息数不变。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → `ContextAssembler` 不存在

- [ ] **Step 3: 实现装配器**

顺序：`Scope` → `Key.Value`（ordinal）排序源；`ReadOnly` 模式跳过状态写入与消息插入（只返回渲染文本）。`Persist` 流程：先消费 `Pending` 位（`Compaction` → `BaselineValues := CurrentValues` 且清 `DeltaLedger`；`WorkspaceChanged` 同 `Compaction`），再读源、按 `Canonical` 算 `CanonicalJson.Hash` 与 `CurrentValues` 比对；`ValueSchemaVersion`/`StateVersion` 不匹配 → 丢弃该源状态重建；`Render` 返回 null → 不更新 `CurrentValues`；有变化 → 拼一条 `<context-update source="...">` 消息插在 `insertBeforeMessageId` 之前（锚点不存在则追加），并把 `(messageId, CurrentValues)` 追加进 `DeltaLedger`。`BuildSystemBaseline` 只拼接各源 `BaselineText`（按顺序，`\n\n` 分隔），**不读源**。并发用 `ISessionManager.UpdateSessionAsync` 的 CAS（构造参数增补 `ISessionManager`）；本任务内以 `SemaphoreSlim(1,1)` 兜底并在 Task 12 切到 CAS 时保留测试。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContextAssemblerTests"`
Expected: 9 PASS

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs tests/spine/Seeing.Agent.Tests/Context/ContextAssemblerTests.cs
git commit -m "feat(context): 装配器 diff 与增量叙述"
```

---

### Task 4: 提示词接线（基线在替换之后追加）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Prompts/PromptContext.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Agents/AgentContext.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`（`ToolContext`）
- Modify: `src/spine/Seeing.Agent.Core/Core/Prompts/PromptBuilder.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Prompts/PromptBuilderBaselineTests.cs`

**Interfaces:**
- Produces: `PromptContext.ContextBaseline`（`string?`）、`AgentContext.ContextBaseline`（`string?`）、`ToolContext.SkillSnapshot`（`SkillCatalogSnapshot?`，Task 7 提供类型；本任务先声明为 `object?` 会引入不一致，故 **Task 7 之后再改**，本任务只加 `ContextBaseline`）。

- [ ] **Step 1: 写失败测试**

```csharp
public class PromptBuilderBaselineTests
{
    // 基线必须在变量替换/Trim 之后追加（Review Focus #2）
    [Fact] public async Task Baseline_ShouldNotBeVariableSubstituted()
    {
        var ctx = new PromptContext {
            Agent = new AgentDefinition { SystemPrompt = "T {{timestamp}}" },
            Timestamp = DateTime.Now,
            ContextBaseline = "## Skills\nkeep {{timestamp}} literal" };
        var result = await new PromptBuilder([]).BuildAsync(ctx, default);
        result.Should().Contain("## Skills");
        result.Should().Contain("keep {{timestamp}} literal");
    }

    [Fact] public async Task Baseline_ShouldBeAppendedLast()
    {
        var ctx = new PromptContext { Agent = new AgentDefinition { SystemPrompt = "BODY" }, ContextBaseline = "TAIL" };
        (await new PromptBuilder([]).BuildAsync(ctx, default)).Should().EndWith("TAIL");
    }

    [Fact] public async Task EmptyAgentPrompt_ShouldUseBaselineAsSystemPrompt()
    {
        var ctx = new PromptContext { Agent = new AgentDefinition { SystemPrompt = "" }, ContextBaseline = "ONLY-BASELINE" };
        (await new PromptBuilder([]).BuildAsync(ctx, default)).Should().Be("ONLY-BASELINE");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → `PromptContext` 无 `ContextBaseline`

- [ ] **Step 3: 实现**

`PromptBuilder.BuildAsync`：`InjectSectionsAsync` → `Variables` 替换 → `ReplaceBuiltinVariables` → `Trim` → 若 `ContextBaseline` 非空则 `result + "\n\n" + baseline`（当 `result` 为空时直接返回 baseline）。`AgentExecutor.BuildRequestAsync`：去掉 `!string.IsNullOrEmpty(agent.SystemPrompt)` 对整块的守卫（改为总是调用 `BuildAsync`，由上面的空提示词分支兜底），并把 `context.ContextBaseline` 传入 `PromptContext`。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PromptBuilderBaselineTests"`
Expected: 3 PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Prompts/PromptContext.cs src/abstractions/Seeing.Agent.Abstractions/Agents/AgentContext.cs src/spine/Seeing.Agent.Core/Core/Prompts/PromptBuilder.cs src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs tests/spine/Seeing.Agent.Tests/Prompts/PromptBuilderBaselineTests.cs
git commit -m "feat(context): 基线在变量替换后追加（字节稳定）"
```

---

### Task 5: 项目指令源（逐路径读取 + 迁移适配）

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Context/Sources/IInstructionFileReader.cs`
- Create: `src/spine/Seeing.Agent.Core/Context/Sources/InstructionFileReader.cs`
- Create: `src/spine/Seeing.Agent.Core/Context/Sources/ProjectInstructionsContextSource.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Instructions/InstructionManager.cs`（改为薄适配器）
- Test: `tests/spine/Seeing.Agent.Tests/Context/ProjectInstructionsContextSourceTests.cs`

**Interfaces:**
- Consumes: `InstructionDiscovery`（复用扫描），`ProjectInstructionsRenderer`（复用信封）
- Produces:
  - `public enum InstructionFileReadStatus { Loaded, Missing, Unreadable }`
  - `public sealed record InstructionFileReadResult(string Path, InstructionFileReadStatus Status, string? Content, string? Error)`
  - `public interface IInstructionFileReader { Task<IReadOnlyList<InstructionFileReadResult>> ReadAsync(string cwd, string workspaceRoot, CancellationToken ct = default); }`
  - `public sealed class ProjectInstructionsContextSource : IContextSource`：`Key => new(typeof(ProjectInstructionsContextSource).FullName!)`、`Scope = Project`、`Target = Message`、`ValueSchemaVersion = 1`、`MaxBaselineChars = 32_000`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task UnreadableFile_ShouldReturnUnavailable_NotRemoved()  // Review Focus #1
{
    // 用 IInstructionFileReader 假实现返回 Unreadable，断言 ReadAsync 的 State 为 Unavailable
}

[Fact] public async Task MissingFile_ShouldProduceRemovalNarration()
[Fact] public async Task RenderInitial_ShouldMatchLegacyEnvelope()  // 与 ProjectInstructionsRenderer 输出一致
[Fact] public async Task Canonical_ShouldContainPathAndHash_NotContent()  // Payload 携带正文、Canonical 不含
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests` → 类型不存在

- [ ] **Step 3: 实现**

`InstructionFileReader`：遍历 `InstructionDiscovery` 给出的候选路径，逐个 `File.ReadAllTextAsync`；`FileNotFoundException`/`DirectoryNotFoundException` → `Missing`，其余 `IOException`/`UnauthorizedAccessException` → `Unreadable`（记录 warning，**不吞**）。源：全部 `Unreadable` → `Unavailable`；否则 `Canonical = [{path, sha256, mtime}]` 排序，`Payload = [{path, content}]`。`RenderInitial` 调 `ProjectInstructionsRenderer.Wrap(cwd, reason, files)`（reason 用 `ProjectInstructions.Reasons.Initial`）；`Render` 产出变更/删除叙述（删除文案必须含 `no longer applies`）。`InstructionManager` 保留接口签名，实现改为调用 `IContextAssembler` 后返回投影结果。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ProjectInstructionsContextSourceTests"`
Expected: 4 PASS

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Context/Sources src/spine/Seeing.Agent.Core/Core/Instructions/InstructionManager.cs tests/spine/Seeing.Agent.Tests/Context/ProjectInstructionsContextSourceTests.cs
git commit -m "feat(context): 项目指令源（区分缺失与不可读）"
```

---

### Task 6: 生命周期接线（压缩 / 清除 / fork / revert）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Compression/CompressionService.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs`（`ClearMessages`）
- Test: `tests/spine/Seeing.Agent.Tests/Context/ContextLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 2 `ContextBaselineState.Pending`、`PendingBaselineReset` 枚举（`[Flags]`：`Compaction`、`WorkspaceChanged`）
- Produces: `CompressionService.CompressAsync` 成功后在会话元数据里置 `Compaction` 位（不抛异常，失败仅告警）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Compaction_ShouldSetPendingFlag_Once()
[Fact] public async Task CompactionThenWorkspaceChange_ShouldKeepBothFlags()  // 位集合不互相覆盖
[Fact] public void ClearMessages_ShouldRemoveContextBaseline()
[Fact] public void Fork_ShouldNotCarryContextBaseline()  // SessionForker 只复制 InstructionFingerprints
[Fact] public async Task RevertBetweenTwoDeltas_ShouldNotRepeatNarration()  // 用 ledger 折叠
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests`

- [ ] **Step 3: 实现**

`CompressionService` 压缩成功分支后：`var state = ContextBaselineStore.Load(session); state.Pending |= PendingBaselineReset.Compaction; ContextBaselineStore.Save(session, state);`（`try/catch` 记 warning，不影响压缩）。`SessionData.ClearMessages()` 增加 `Metadata.Remove(SessionMetadataKeys.ContextBaseline);`（与 `InstructionFingerprints` 并列）。`SessionForker` 不改（它只复制指令指纹），测试锁定不复制。Revert 场景在 `ContextAssembler` 中按 `DeltaLedger` 中仍存活的最晚条目折叠 `CurrentValues`（Task 3 已实现保留，此处补测试）。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContextLifecycleTests"`
Expected: 5 PASS

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Compression/CompressionService.cs src/primitives/Seeing.Session/Core/SessionData.cs tests/spine/Seeing.Agent.Tests/Context/ContextLifecycleTests.cs
git commit -m "feat(context): 压缩/清除/fork/revert 生命周期接线"
```

---

### Task 7: 技能目录核心（快照 / 双通道 / 冲突 / 原子替换）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillCatalog.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillCatalogPin.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillFileScanner.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogCore.cs`
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillCatalogCoreTests.cs`

**Interfaces:**
- Consumes: `SkillManager.ParseSkillFile` 的解析逻辑（迁移到 `SkillFileScanner`）
- Produces:
  - `enum SkillSource { Registered, ProjectFile, UserFile }`
  - `sealed record SkillCatalogEntry(SkillInfo Info, SkillSource Source, string? ProviderId, string DirectoryPath)`
  - `sealed record SkillConflict(string Name, SkillSource Winner, string WinnerOrigin, SkillSource Loser, string LoserOrigin)`
  - `sealed record SkillLoadIssue(string Path, string Reason)`
  - `sealed record SkillCatalogSnapshot(long Version, IReadOnlyList<SkillCatalogEntry> Entries, IReadOnlySet<string> DisabledNames, IReadOnlyDictionary<string, ConfigLevel> DisabledScopes, IReadOnlyList<SkillConflict> Conflicts, IReadOnlyList<SkillLoadIssue> Issues)`
  - `interface ISkillCatalog { SkillCatalogSnapshot Current { get; } IDisposable Subscribe(Action<SkillCatalogSnapshot> onChanged); SkillCatalogEntry? Find(string name); bool IsEnabled(string name); Task<SkillCatalogSnapshot> EnsureFreshAsync(CancellationToken ct = default); Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default); Task<bool> RegisterAsync(SkillInfo info, string providerId, CancellationToken ct = default); Task<bool> UnregisterAsync(string name, CancellationToken ct = default); }`
  - `interface ISkillCatalogPin { SkillCatalogSnapshot Snapshot { get; } }`
  - `public sealed class SkillCatalogCore : ISkillCatalog`，构造函数 `(ILogger<SkillCatalogCore> logger, ISeeingDirectories? directories)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task RegisteredSkill_ShouldWinOverSameNameFile()      // 插件优先
[Fact] public async Task ConflictingFile_ShouldBeRecordedAndNotCallable()   // 冲突不静默
[Fact] public async Task Conflict_ShouldBeVisibleInSnapshot()
[Fact] public async Task FileRootPrecedence_ShouldBeExplicit()             // 五个根的顺序
[Fact] public async Task Refresh_ShouldSwapAtomically_NoEmptyWindow()      // 并发读永不空
[Fact] public async Task ConcurrentRefresh_ShouldRebuildOnce()             // 单飞
[Fact] public async Task Version_ShouldIncreaseMonotonically()
[Fact] public async Task ParseFailure_ShouldBecomeIssue()
[Fact] public async Task RegisteredSkill_WithDuplicateName_ShouldRejectSecond()
[Fact] public async Task DisabledScopes_ShouldReportLevel()                // 供 UI 显示
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests`

- [ ] **Step 3: 实现**

`SkillFileScanner`：按根顺序扫描 `SKILL.md`（`SearchOption.AllDirectories`），解析失败 → `SkillLoadIssue`（不抛），同名按根优先级取高者并记 `SkillConflict`。`SkillCatalogCore`：`_registered`（不可变字典）+ `_fileSkills`（不可变字典）+ `_snapshot`（`Volatile.Read/Write`）+ `SemaphoreSlim(1,1)`；`RefreshAsync` 构建新快照（`Version+1`）后原子替换，最后在锁外触发事件；`RegisterAsync` 同名已注册 → 返回 `false`；`UnregisterAsync` 缺失 → `false`。查询 (`Find`/`IsEnabled`) 读当前快照。`SkillCatalogEntry` 的 `DirectoryPath` 是构建时写入的数据字段（不是计算属性）；`SkillInfo.DirectoryPath` 的移除放在 Task 10 随门面重写一起做，避免本任务就打断现有消费者编译。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillCatalogCoreTests"`
Expected: 10 PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Skills src/capabilities/Seeing.Agent.Skills/Catalog tests/capabilities/Seeing.Agent.Skills.Tests/SkillCatalogCoreTests.cs
git commit -m "feat(skills): 目录核心（双通道/优先级/原子快照）"
```

---

### Task 8: watcher + 模块生命周期 + 加载统一 + 指纹

**Files:**
- Create: `src/capabilities/Seeing.Agent.Skills/Watching/SkillRootWatcher.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillsModule.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillLoader.cs`
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillRootWatcherTests.cs`

**Interfaces:**
- Consumes: Task 7 `ISkillCatalog`
- Produces: `public sealed class SkillRootWatcher : IDisposable`，构造函数 `(IReadOnlyList<string> roots, Func<CancellationToken, Task> onChange, ILogger logger, TimeSpan? debounce = null)`；`SkillsModule` 在 `ActivateAsync` 启动、`DeactivateAsync` 释放。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task CreateSkillFile_ShouldTriggerOneCallbackAfterDebounce()
[Fact] public async Task RapidWrites_ShouldCoalesceIntoOneCallback()
[Fact] public async Task DeleteSkillDirectory_ShouldTriggerCallback()
[Fact] public async Task MissingRoot_ShouldNotThrow_AndAttachWhenCreated()
[Fact] public async Task Dispose_ShouldStopCallbacks()
[Fact] public async Task WorkspaceSwitch_ShouldDropOldRoots()  // SkillLoader 幂等路径
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests`

- [ ] **Step 3: 实现**

每个根一个 `FileSystemWatcher`（`IncludeSubdirectories = true`），事件 → 400ms 去抖 → 单次回调；根不存在时监听最近存在祖先，检测到根创建后重挂。`SkillLoader.LoadAsync/ReloadAsync` 合并为：`ResetSearchDirectoriesToDefault` → 追加 `~/.seeing/skills` 与 `SkillsOptions.Paths` → `await catalog.RefreshAsync()`（不再 clear-then-fill、不再分两套逻辑）。`SkillsModule` 负责 watcher 启停与 `IContextSource`/`ISkillCatalog`/`ISkillWriter` 的 DI 注册。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillRootWatcherTests"`
Expected: 6 PASS（watcher 用真实延时，超时上限 5s）

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Skills/Watching src/capabilities/Seeing.Agent.Skills/SkillsModule.cs src/capabilities/Seeing.Agent.Skills/SkillLoader.cs tests/capabilities/Seeing.Agent.Skills.Tests/SkillRootWatcherTests.cs
git commit -m "feat(skills): watcher 与统一加载路径"
```

---

### Task 9: 技能源 + 格式化单点 + 执行级钉住

**Files:**
- Create: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogFormatter.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillTool.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/ToolManager.cs`（`ExecuteAsync` 增快照参数）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`（`ToolContext.SkillSnapshot`）
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillsContextSourceTests.cs`

**Interfaces:**
- Consumes: Task 7 `ISkillCatalog`/`ISkillCatalogPin`、Task 8 注册
- Produces:
  - `public static class SkillCatalogFormatter`：`static string RenderBaseline(IReadOnlyList<SkillCatalogEntry> entries)`、`static string RenderList(IReadOnlyList<SkillCatalogEntry> entries)`、`static string RenderDelta(prev, curr)`；所有渲染复用同一排序（name ordinal）
  - `public sealed class SkillsContextSource : IContextSource`：`Target = System`、`Scope = Project`、`ValueSchemaVersion = 1`、`MaxBaselineChars = 8_000`，构造注入 `ISkillCatalogPin`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Baseline_ShouldSortById()                          // 字节稳定前提
[Fact] public async Task Baseline_ShouldSkipEntriesWithoutDescription()
[Fact] public async Task Delta_ShouldNarrateAddRenameDescriptionAndRemoval()
[Fact] public async Task Removal_ShouldSayMustNotBeUsed()
[Fact] public async Task Baseline_ShouldNotDuplicateSkillsHeading()         // 单一 ## Skills 区块
[Fact] public async Task SkillTool_ShouldResolveFromPinnedSnapshot()        // 安装中途删除仍可加载
[Fact] public async Task Formatter_ShouldBeSingleSource_ForListAndErrors()
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests`

- [ ] **Step 3: 实现**

`SkillCatalogFormatter` 是唯一清单文本出口（基线、`/skill` 列表、`/skills`、`SkillTool` 错误提示共用）。`SkillsContextSource.ReadAsync` 只读 `ISkillCatalogPin.Snapshot`（**不读 `Current`**）。`SkillTool` 优先 `context.SkillSnapshot`，为空才回退 `_skillManager`（仅兼容旧宿主）。`ToolManager.ExecuteAsync` 末尾增加可选参数 `SkillCatalogSnapshot? skillSnapshot = null`，构造 `ToolContext` 时赋值；`ToolContext.WithCancellationToken` 走 `MemberwiseClone` 自动透传。`SkillManager` 不再实现 `IPromptSectionContributor`（从 DI 注册中移除），基线拥有整个 `## Skills` 区块。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillsContextSourceTests"`
Expected: 7 PASS

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogFormatter.cs src/capabilities/Seeing.Agent.Skills/Sources src/capabilities/Seeing.Agent.Skills/SkillTool.cs src/spine/Seeing.Agent.Core/Core/ToolManager.cs src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs tests/capabilities/Seeing.Agent.Skills.Tests/SkillsContextSourceTests.cs
git commit -m "feat(skills): 技能源与执行级快照钉住"
```

---

### Task 10: 只读门面 + 命令/ACP 事件同步

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/Commands/SkillCommands.cs`
- Modify: `src/capabilities/Seeing.Agent.Acp/AcpModule.cs`
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillManagerFacadeTests.cs`

**Interfaces:**
- Consumes: Task 7 `ISkillCatalog`
- Produces: `ISkillManager` 仅保留只读成员；写面（`AddSearchDirectory`、`DiscoverSkillsAsync`、`RegisterEmbeddedSkill`、`Unregister`）加 `[Obsolete("请使用 ISkillCatalog", error: true)]`；`DynamicSkillCommand` 构造只接收 `(ISessionManager, string skillName)`，执行时按名从 catalog 解析。`SkillInfo.DirectoryPath` 在本任务移除，消费者改用 `SkillCatalogEntry.DirectoryPath` 或 `Path.GetDirectoryName(info.Location)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void ISkillManager_ShouldExposeNoWritableMembers()   // 反射断言写面为 obsolete(error)
[Fact] public async Task DynamicCommand_ShouldResolveByNameAtExecution()  // 不持有 Snapshot 副本
[Fact] public async Task DeletedSkillCommand_ShouldBeUnregistered_OnCatalogEvent()
[Fact] public async Task DisabledSkillCommand_ShouldAlsoBeUnregistered()  // 修掉现存 bug
[Fact] public async Task AcpCommands_ShouldFollowCatalogEvents()
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests`

- [ ] **Step 3: 实现**

`SkillManager` 变成 `ISkillCatalog` 的只读投影（无字典）。命令注册改为 `SkillLoader`/`SkillsModule` 订阅 `ISkillCatalog.Subscribe`，按名称 diff 调 `ICommandRegistry.Register/Unregister`；注销使用 `GetAllSkillInfosIncludingDisabled` 的名称集合（修复"被禁用技能命令注销不掉"）。`AcpModule.ActivateAsync` 同样订阅、`DeactivateAsync` 取消订阅。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillManagerFacadeTests"`
Expected: 5 PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillManager.cs src/capabilities/Seeing.Agent.Skills src/capabilities/Seeing.Agent.Acp/AcpModule.cs tests/capabilities/Seeing.Agent.Skills.Tests/SkillManagerFacadeTests.cs
git commit -m "refactor(skills): 只读门面与命令事件同步"
```

---

### Task 11: 写入收口（writer + 层级 + UI）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillWriter.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/SkillWriter.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillWriteGate.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/SkillCreatePage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/SkillDetailPage.razor`
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillWriterTests.cs`

**Interfaces:**
- Consumes: Task 7 `ISkillCatalog`
- Produces: `ISkillWriter`（`CreateAsync` / `WriteMarkdownAsync` / `UpdateAsync` / `DeleteAsync` / `InstallZipAsync(name, Stream, ConfigLevel, bool overwrite, string? subdirectoryPrefix, CancellationToken)` / `SetEnabledAsync(name, bool, ConfigLevel, CancellationToken)`），`SkillWriteResult(bool Success, string? Error, SkillCatalogEntry? Entry)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Create_ShouldWriteAtomicallyAndRefresh()          // 返回时快照已含新技能
[Fact] public async Task Create_OnRegisteredSkill_ShouldFail()            // 插件技能只读
[Fact] public async Task Create_WithPathTraversalName_ShouldFail()        // ../ 拒绝
[Fact] public async Task WriteMarkdown_ShouldPreserveUnknownFrontmatter() // 在线安装分支
[Fact] public async Task InstallZip_WithSubdirectoryPrefix_ShouldStripRepoRoot()
[Fact] public async Task SetEnabled_ShouldClearAllLevels()                // Review Focus #5
[Fact] public async Task ConcurrentToggles_ShouldSerialize()              // skill-state 写闸
[Fact] public async Task FailedWrite_ShouldNotLeaveTempDirectory()
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests`

- [ ] **Step 3: 实现**

写目录：临时目录（同根下 `<name>.tmp-<guid>`）→ 成功后 `Directory.Move`；失败 `finally` 清理。`SetEnabledAsync`：启用时从**用户级与项目级**两个禁用集合移除该名；禁用时只写指定层级，并更新 `DisabledScopes`。`SkillWriteGate` 用 `SemaphoreSlim(1,1)` 串行化 `skill-state.json` 写入。UI：`SkillsPage` 订阅 `ISkillCatalog.Subscribe`，`Dispose` 取消；"刷新"调 `RefreshAsync`；展示 `Conflicts`/`Issues`/`Source`。`SkillCreatePage`/`SkillDetailPage` 改为调 `ISkillWriter`，删除页面内的 `Directory.CreateDirectory`/`File.WriteAllText`。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillWriterTests"`
Expected: 8 PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillWriter.cs src/capabilities/Seeing.Agent.Skills/SkillWriter.cs src/capabilities/Seeing.Agent.Skills/Catalog/SkillWriteGate.cs samples/Seeing.Agent.WebUI/Pages tests/capabilities/Seeing.Agent.Skills.Tests/SkillWriterTests.cs
git commit -m "feat(skills): 写入收口与 UI 订阅"
```

---

### Task 12: ExecutionRunner 接线 + 旁路只读

**Files:**
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/SkillCatalogPin.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/App/ExecutionRunnerContextAssemblyTests.cs`

**Interfaces:**
- Consumes: Task 3 `IContextAssembler`、Task 7 `ISkillCatalog`、Task 9 `ToolContext.SkillSnapshot`
- Produces: `internal sealed class SkillCatalogPin : ISkillCatalogPin`（scoped）；`ExecutionRunner` 在 `!commandConsumedExecution` 分支内、`BuildHistoryFromSession` 之前调用装配；`AgentContext.ContextBaseline` 与 `SkillSnapshot` 在 `BuildAgentContext` 处赋值。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Assembly_ShouldRunAfterCommands_BeforeHistory()
[Fact] public async Task Assembly_ShouldPassPinnedSnapshotToTools()        // 同一对象
[Fact] public async Task ShortCircuitCommand_ShouldNotAssemble()           // /clear /compact
[Fact] public async Task SettlementFailure_ShouldNotNarrate()              // 行为变更
[Fact] public async Task SchedulerBypass_ShouldUseReadOnlyMode()           // 不推进状态
[Fact] public async Task HeartbeatBypass_ShouldNotEmitDeltaMessage()
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests`

- [ ] **Step 3: 实现**

`ExecutionRunner`：`EnsureFreshAsync(session, Persist, batch.FirstUserMessageId, skip: SkipInstructionInject ? 指令源键 : null)` 放在 `ExecutionRunner.cs:227-230` 位置；把结果写入 `AgentContext`（`ContextBaseline` 用 `BuildSystemBaseline(session)`，`SkillSnapshot` 取 pin）。scoped `SkillCatalogPin` 在 `CreateScope` 后注册（`services.AddScoped<ISkillCatalogPin>(...)`，实现返回捕获的快照）。Scheduler/Heartbeat：构造 `AgentContext` 前调用 `EnsureFreshAsync(session, ReadOnly, null, null)`，并把返回的 `SystemBaseline` 赋给 `ContextBaseline`；**不保存会话**。

- [ ] **Step 4: 运行测试（含既有 ExecutionRunner 测试）**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionRunner"`
Expected: 新增 6 PASS；既有 `ExecutionRunnerInstructionTests` 等按 Task 5 的适配器语义更新后全部 PASS

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution src/capabilities/Seeing.Agent.Scheduler/Jobs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Tests/App/ExecutionRunnerContextAssemblyTests.cs
git commit -m "feat(context): 主链装配与旁路只读模式"
```

---

### Task 13: 清理、文档与全量验证

**Files:**
- Delete: `src/spine/Seeing.Agent.Core/Core/Prompts/DynamicPromptBuilder.cs`
- Modify: `docs/architecture/README.md`、`docs/architecture/04-development-standards.md`、`AGENTS.md`
- Create: `docs/architecture/21-context-source-pipeline-release-notes.md`
- Test: 无新增单测；以全量构建 + 全量测试为验证

- [ ] **Step 1: 删除死代码并确认无引用**

Run: `rg "DynamicPromptBuilder|InstructionFingerprints|embedded://" src samples tests`
Expected: 仅剩文档与迁移说明中的提及；代码中无引用

- [ ] **Step 2: 写文档**

Release Notes 必须包含：行为变更表（压缩后指令重新可见、安装即时生效、插件优先、`ISkillManager` 写面 obsolete、`SetEnabledAsync` 需显式层级、`SkillInfo.DirectoryPath` 移除、结算失败不叙述）；反模式新增三条（多渲染点、路径前缀嗅探、绕过 catalog 写状态）；`AGENTS.md` WHERE TO LOOK 增加"上下文源管道"一行。

- [ ] **Step 3: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error

- [ ] **Step 4: 全量测试**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
Run: `dotnet build tests/capabilities/Seeing.Agent.Skills.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Skills.Tests/bin/Debug/net10.0/Seeing.Agent.Skills.Tests.dll`
Expected: 全绿（watcher 用例允许 5s 内完成）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "docs(context): 发布说明、反模式与索引"
```

---

## Self-Review

**Spec coverage**

| Spec 章节 | 任务 |
|---|---|
| §5 契约 | Task 2、7、11 |
| §6 状态模型（canonical / payload / ledger） | Task 2、3 |
| §7 装配与时序 | Task 3、12 |
| §8 不变量（字节稳定、版本二分、截断、三态、单飞、钉住） | Task 1、3、4、9 |
| §9 生命周期 | Task 6、12 |
| §10.1 项目指令源 | Task 5 |
| §10.2 技能源 | Task 9 |
| §11 技能目录 + 写入收口 | Task 7、8、10、11 |
| §12 提示词接线 | Task 4、12 |
| §13 删除清单 | Task 13（`DynamicPromptBuilder`、`embedded://`、UI 缓存） |
| §14 测试计划 | 各任务 Step 1 + Task 13 全量 |
| §15 行为变更 | Task 13 Release Notes |
| Review Focus 1–5 | Task 5（不可读）、Task 4（字面量）、Task 6（压缩/revert/fork）、Task 7（半写目录 → `SkillLoadIssue`）、Task 11（双层级禁用） |

**类型一致性**：`ContextSourceValue` 三参在 Task 2/3/5/9 一致；`SkillCatalogEntry` 四参（含 `DirectoryPath`）在 Task 7/9/11 一致；`ISkillWriter` 六方法在 Task 11 定义、UI 与测试使用；`IContextAssembler.EnsureFreshAsync` 五参在 Task 2/3/12 一致。`ToolContext.SkillSnapshot` 在 Task 9 引入、Task 12 使用。

**已知边界**：watcher 用真实延时（仓库无虚拟时钟包）；同秒同长度编辑可能延迟一次装配（§11.3 已记录）；`/help` 等短路命令不再注入指令（无模型调用，无影响）。
