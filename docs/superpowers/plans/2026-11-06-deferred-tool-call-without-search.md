# 延迟工具免检索直调 + 目录迁入上下文管道 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 deferred 工具无需先 `tool_search` 即可调用，参数错误时自动回灌完整定义，并把工具目录从系统提示词迁入上下文源管道，使 `tools[]` 与系统提示词在一轮会话内恒定。

**Architecture:** 三部分。Part 3 打通「定义 → 事件 → 会话消息 → 历史重建」的持久化链路；Part 1 把执行层二元 gate 拆成四分支并关闭 promotion；Part 2 新建 `ToolCatalogContextSource`（`Target = Message`）接管目录，删除系统提示词分节。三部分合起来让缓存从头到尾不失效。

**Tech Stack:** .NET 10 / C#，xUnit 2.9 + FluentAssertions 6.12 + Moq 4.20，Microsoft.Extensions.DependencyInjection，System.Text.Json。

**Spec:** `docs/superpowers/specs/2026-11-06-deferred-tool-call-without-search-design.md`

## Global Constraints

- 目标框架 `net10.0`；SDK 10.0.102。
- 构建：`dotnet build Seeing.Agent.slnx` 必须 0 error。
- 测试一律用 VSTest 跑已构建程序集（本环境 MTP 发现不到测试）：
  `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<Class>"`
- 测试禁用固定时长等待（`Task.Delay`/`Thread.Sleep`）。用条件轮询或 `TaskCompletionSource`。
- 注释、日志、文档文案一律用中文。
- 依赖方向：能力包只依赖 Abstractions + 原语；新类型落 Hosting 或 Core 时必须符合该层允许的依赖。
- 权限链不得改动：`IPermissionAuthorizer` / `DefaultToolPermissionPolicy` / 审批流程原样。
- `AgentExecutor` 不得写会话暴露态（单一权威写入者）。
- 新增持久化字段一律可空，旧数据反序列化为 null 不得抛异常。

## Review Focus

- **`SessionToolCall.Clone()` 漏拷新字段**：`Clone()` 是逐字段枚举（`SessionMessage.cs:156-184`）。漏了就「内存里对、落盘读回后丢」，纯内存比对测试发现不了，线上表现为前缀缓存照断。→ Task 3 的测试 11 走 `SaveAsync` + 读回。
- **失败有两种出口**：`ToolResult.Success == false` 与 `throw`（被 `catch` 兜住）。只改前者会让 `GitCommitTool` 这类抛 `JsonException` 的工具完全享受不到定义回灌。→ Task 5 测试 3。
- **大小写不一致**：结算 `Contains`/`SendsSchema` 是 OrdinalIgnoreCase，`ToolManager.GetTool`/`ExecuteAsync` 是序数敏感。模型写 `Read` 会穿过 B 分支后掉进裸「工具不存在: Read」。→ Task 4 测试 14。
- **纯噪声失败误附定义**：超时（`Metadata["timeout"]=true`）、重试耗尽（`Title="重试耗尽"`）、权限拒绝都会返回 `Success=false`，附 2–8KB 定义纯属浪费且误导。→ Task 5 测试 4/5/6。
- **已加载工具重复付定义**：`tool_search detail=full` 加载后工具在结算里仍是 `Deferred`，`SendsSchema` 依旧为 false。只看这一个条件会给刚拿到过定义的工具再附一次。→ Task 5 测试 8。
- **目录首条消息插在会话中段**：`Target = Message` 首次接入时会在历史中间插一条消息，那一次仍断缓存。这是可接受的一次性成本，但不得试图用「追加到末尾」规避（会破坏「初始在前、增量在后」的管道不变量）。→ Task 8 的 Review 说明。

---

### Task 1: `ToolResultFormatting` 支持 `<tool_definition>` 块

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResultFormatting.cs:30-60`
- Test: `tests/spine/Seeing.Agent.Tests/App/ToolResultFormattingTests.cs`

**Interfaces:**
- Consumes: 无（本任务自包含）
- Produces:
  - `public static string ToModelContent(bool succeeded, string? output, string? error, string? toolName = null, string? status = null, string? title = null, string? toolDefinition = null)`
  - 新增参数追加在末位且默认 `null` → 现有全部调用点零改动

- [ ] **Step 1: 写失败测试**

在 `ToolResultFormattingTests` 追加三条：

```csharp
[Fact]
public void Failure_WithToolDefinition_ShouldEmitDefinitionBlockAfterError()
{
    var content = ToolResultFormatting.ToModelContent(
        false, null, "缺少必需参数: taskType", "cron_create", "failed",
        toolDefinition: "{\"name\":\"cron_create\"}");

    var errorIndex = content.IndexOf("<error>", StringComparison.Ordinal);
    var definitionIndex = content.IndexOf("<tool_definition>", StringComparison.Ordinal);

    errorIndex.Should().BeGreaterThan(0);
    definitionIndex.Should().BeGreaterThan(errorIndex);
    content.Should().Contain("</tool_definition>");
    content.Should().Contain("{\"name\":\"cron_create\"}");
}

[Fact]
public void Failure_WithoutToolDefinition_ShouldBeByteIdenticalToLegacy()
{
    var legacy = ToolResultFormatting.ToModelContent(false, null, "boom", "t", "failed");
    var withNull = ToolResultFormatting.ToModelContent(false, null, "boom", "t", "failed", toolDefinition: null);

    withNull.Should().Be(legacy);
    withNull.Should().NotContain("<tool_definition>");
}

[Fact]
public void Failure_ToolDefinitionContainingClosingTag_ShouldBeEscaped()
{
    var content = ToolResultFormatting.ToModelContent(
        false, null, "boom", "t", "failed", toolDefinition: "{\"d\":\"a</b>\"}");

    content.Should().Contain("<\\/b>");
    // 必须只有一对闭合标签：块内不得出现未转义的 </
    content.Split("</tool_definition>").Should().HaveCount(2);
}

[Fact]
public void Success_WithToolDefinition_ShouldIgnoreDefinition()
{
    var content = ToolResultFormatting.ToModelContent(
        true, "结果", null, "t", "success", toolDefinition: "{\"x\":1}");

    content.Should().Be("结果");
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolResultFormattingTests"`
Expected: 编译失败（`ToModelContent` 无 `toolDefinition` 参数）。

- [ ] **Step 3: 实现**

签名加末位参数 `string? toolDefinition = null`。成功分支（`if (succeeded) return output ?? string.Empty;`）**不变**，直接忽略该参数。

失败分支在 `sb.Append("<error>")...AppendLine("</error>")`（`:57`）之后、`sb.Append("</tool_result>")`（`:58`）之前插入：

```csharp
if (!string.IsNullOrEmpty(toolDefinition))
{
    sb.AppendLine("<tool_definition>");
    sb.Append(EscapeBody(toolDefinition)).AppendLine();
    sb.AppendLine("</tool_definition>");
}
```

复用已有私有 `EscapeBody`（`:78`）——它把 `</` 转成 `<\/`，与 `<error>` 正文同一套转义。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolResultFormattingTests"`
Expected: 全部 PASS（含既有 7 条）。

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResultFormatting.cs tests/spine/Seeing.Agent.Tests/App/ToolResultFormattingTests.cs
git commit -m "feat(tools): ToolResultFormatting 支持 <tool_definition> 块"
```

---

### Task 2: `ToolCallEvent.ToolDefinition` 与 `ModelContent` 透传

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs:290-341`
- Test: `tests/spine/Seeing.Agent.Tests/App/ToolCallEventModelContentTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `ToModelContent(..., string? toolDefinition = null)`
- Produces:
  - `ToolCallEvent.ToolDefinition`：`public string? ToolDefinition { get; init; }`（可空）
  - `ToolCallEvent.ModelContent` 在失败时把该值传给 `ToModelContent`

- [ ] **Step 1: 写失败测试**

新建 `tests/spine/Seeing.Agent.Tests/App/ToolCallEventModelContentTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ToolCallEventModelContentTests
{
    [Fact]
    public void FailedEvent_WithToolDefinition_ShouldIncludeDefinition()
    {
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        };

        evt.ModelContent.Should().Contain("<tool_definition>");
        evt.ModelContent.Should().Contain("cron_create");
    }

    [Fact]
    public void FailedEvent_WithoutToolDefinition_ShouldBeIdenticalToLegacy()
    {
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "read",
            Status = ToolCallStatus.Failed,
            Error = "boom"
        };

        evt.ModelContent.Should().NotContain("<tool_definition>");
        evt.ModelContent.Should().Contain("boom");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolCallEventModelContentTests"`
Expected: 编译失败（`ToolCallEvent` 无 `ToolDefinition` 属性）。

- [ ] **Step 3: 实现**

在 `ToolCallEvent` 里 `TurnDirectiveReason`（`:328`）之后加属性，并在 `ModelContent`（`:334-340`）末尾补实参：

```csharp
/// <summary>
/// 附带的工具完整定义（JSON）。仅当该工具本轮未下发 schema、且本次失败属参数类错误时填充；
/// 用于让模型在一次往返内修正参数，无需先 tool_search。
/// </summary>
public string? ToolDefinition { get; init; }
```

```csharp
public string ModelContent => ToolResultFormatting.ToModelContent(
    Status == ToolCallStatus.Success,
    Output,
    Error,
    ToolName,
    Status.ToString().ToLowerInvariant(),
    Title,
    ToolDefinition);
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolCallEventModelContentTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs tests/spine/Seeing.Agent.Tests/App/ToolCallEventModelContentTests.cs
git commit -m "feat(events): ToolCallEvent 承载 ToolDefinition 并透传给模型内容"
```

---

### Task 3: 定义持久化链路（`SessionToolCall` + Clone + 映射 + 历史重建）

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionMessage.cs:93-137`（字段）、`:156-184`（`Clone()`）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs:112-165`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:1071-1088`
- Test: `tests/primitives/Seeing.Session.Tests/SessionToolCallCloneTests.cs`（新建）
- Test: `tests/spine/Seeing.Agent.Tests/App/ToolDefinitionPersistenceTests.cs`（新建）

**Interfaces:**
- Consumes: Task 2 的 `ToolCallEvent.ToolDefinition`
- Produces:
  - `SessionToolCall.ToolDefinition`：`public string? ToolDefinition { get; set; }`，JSON 名 `tool_definition`
  - `SessionToolCall.Clone()` 拷贝该字段
  - `ChatEventTracker.ApplyEvent` 在 `ToolCallEvent` 分支写入该字段
  - `ExecutionRunner.BuildHistoryFromSession` 重建 tool 消息时传 `tc.ToolDefinition`

- [ ] **Step 1: 写失败测试（Clone 层）**

新建 `tests/primitives/Seeing.Session.Tests/SessionToolCallCloneTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests;

public class SessionToolCallCloneTests
{
    [Fact]
    public void Clone_ShouldCopyToolDefinition()
    {
        var original = new SessionToolCall
        {
            Id = "c1",
            Name = "cron_create",
            Status = "failed",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        };

        original.Clone().ToolDefinition.Should().Be("{\"name\":\"cron_create\"}");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/primitives/Seeing.Session.Tests && dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolCallCloneTests"`
Expected: 编译失败（无 `ToolDefinition`）。

- [ ] **Step 3: 加字段与 Clone 拷贝**

`SessionToolCall` 内 `TaskSteps`（`:153`）之前加：

```csharp
/// <summary>
/// 附带的工具完整定义（JSON）。仅当该工具本轮未下发 schema、且失败属参数类错误时填充。
/// 持久化是必需的：跨轮历史重建必须与本轮产出一致，否则前缀缓存失效。
/// </summary>
[JsonPropertyName("tool_definition")]
public string? ToolDefinition { get; set; }
```

`Clone()`（`:158-183`）里补 `ToolDefinition = ToolDefinition,`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build tests/primitives/Seeing.Session.Tests && dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolCallCloneTests"`
Expected: PASS

- [ ] **Step 5: 写失败测试（映射 + 历史重建 + 落盘往返）**

新建 `tests/spine/Seeing.Agent.Tests/App/ToolDefinitionPersistenceTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ToolDefinitionPersistenceTests
{
    [Fact]
    public void ApplyEvent_FailedToolCallWithDefinition_ShouldStoreOnSessionToolCall()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        });

        var call = session.GetActiveMessages()
            .SelectMany(m => m.ToolCalls ?? ImmutableList<SessionToolCall>.Empty)
            .Single(t => t.Id == "c1");

        call.ToolDefinition.Should().Be("{\"name\":\"cron_create\"}");
    }

    [Fact]
    public void BuildHistoryFromSession_ShouldEmitDefinitionBlock()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = ImmutableList.Create(new SessionToolCall
            {
                Id = "c1",
                Name = "cron_create",
                Status = "failed",
                Error = "缺少必需参数: taskType",
                ToolDefinition = "{\"name\":\"cron_create\"}"
            })
        });

        var history = ExecutionRunner.BuildHistoryFromSession(session);
        var tool = history.Single(m => m.Role == ChatRole.Tool);

        tool.Content.Should().Contain("<tool_definition>");
        tool.Content.Should().Contain("cron_create");
    }

    [Fact]
    public void LiveContent_AndRebuiltContent_ShouldBeByteIdentical()
    {
        const string def = "{\"name\":\"cron_create\",\"parameters\":{\"type\":\"object\"}}";
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = def
        };

        var session = SessionData.Create();
        new ChatEventTracker().ApplyEvent(session, evt);

        var rebuilt = ExecutionRunner.BuildHistoryFromSession(session)
            .Single(m => m.Role == ChatRole.Tool);

        rebuilt.Content.Should().Be(evt.ModelContent);
    }
}
```

- [ ] **Step 6: 运行确认失败**

Run: Build + vstest 过滤 `ToolDefinitionPersistenceTests`
Expected: 前两条 FAIL（`ApplyEvent` 未写字段、历史重建未传参），第三条 FAIL。

- [ ] **Step 7: 实现映射与历史重建**

`ChatEventTracker` 的 `ToolCallEvent` 分支里，`if (toolCall.Duration is { } duration) ...`（`:154-155`）之后加：

```csharp
if (toolCall.ToolDefinition != null)
    existing.ToolDefinition = toolCall.ToolDefinition;
```

`ExecutionRunner.BuildHistoryFromSession`（`:1079-1085`）的 `ToModelContent` 调用补末位实参：

```csharp
Content = ToolResultFormatting.ToModelContent(
    string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase),
    tc.Result,
    tc.Error,
    tc.Name,
    tc.Status,
    tc.Title,
    tc.ToolDefinition)
```

- [ ] **Step 8: 运行确认通过**

Run: Build + vstest 过滤 `ToolDefinitionPersistenceTests` 与 `ExecutionRunnerBuildHistoryTests`
Expected: 全 PASS。

- [ ] **Step 9: 提交**

```bash
git add src/primitives/Seeing.Session/Core/SessionMessage.cs src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs tests/primitives/Seeing.Session.Tests/SessionToolCallCloneTests.cs tests/spine/Seeing.Agent.Tests/App/ToolDefinitionPersistenceTests.cs
git commit -m "feat(session): ToolDefinition 落盘并贯通历史重建"
```

---

### Task 4: 四分支可见集 gate

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:891-906`
- Test: `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolVisibilityTests.cs`

**Interfaces:**
- Consumes: `SessionToolSchemaResolution.Contains(name)` / `.HasSearchTool`；`ToolManager.GetRegisteredTools()` / `.IsToolEnabled(id)`
- Produces:
  - `private string ResolveVisibilityRejection(string name, AgentContext context)` — 生成三种文案（实例方法：要用 `_tools`）
  - `private IReadOnlyDictionary<string, object> BuildVisibilityMetadata(string name)` — 写 `visibility.reason`
  - `private ITool? GetRegisteredToolIgnoreCase(string name)` — 序数不敏感注册表查找
  - `BuildTerminalEvent(...)` 追加末位可选参数 `IReadOnlyDictionary<string, object>? metadata = null`
  - 判定顺序：`Contains` → `IsToolEnabled` → 注册表（ignoreCase）→ 未找到

- [ ] **Step 1: 改写受影响的既有测试**

在 `AgentExecutorToolVisibilityTests`：

1. `ExecuteAsync_DeferredTool_ShouldRejectAndPointToSearch`（`:42`）改名为 `ExecuteAsync_DeferredTool_ShouldBeAllowed`，断言改：

```csharp
var complete = events.OfType<ToolCallEvent>()
    .Single(e => e.Type == MessageEventType.ToolCallComplete);
complete.Status.Should().NotBe(ToolCallStatus.Rejected,
    "deferred 工具免检索直调：已下发目录签名，无需先 tool_search");
```

2. `ExecuteAsync_ToolAbsentFromSettlement_ShouldRejectWithoutSearchHint`（`:109`）保留（工具未注册 → D 分支不提示检索这条语义未变，会在 Step 3 后由 `HasSearchTool=false` 满足），但把断言改为：

```csharp
complete.Status.Should().Be(ToolCallStatus.Rejected);
complete.Metadata!["visibility.reason"].Should().Be("not_found");
```

> 注意：该测试的 settlement 里没有 `tool_search`，所以 D 分支不出现检索提示——这正是它原本要守的性质。

3. `ExecuteAsync_LoadDeclaredWithoutCapability_ShouldBeIgnored`（`:86`）**会失去测意**（hidden_tool 放行后无法用它证明加载声明被忽略）。改为用「不在结算里的工具」当探针：

```csharp
[Fact]
public async Task ExecuteAsync_LoadDeclaredWithoutCapability_ShouldNotMakeUnsettledToolCallable()
{
    // 未声明 exposure.load 的工具不得解锁其他工具（防止第三方工具绕过“先检索”约定）。
    var llm = CreateScriptedLlm(ToolIdConventions.SearchToolId, "never_exposed_tool");
    var executor = await CreateExecutorWithToolsAsync(
        llm, new UndeclaredSearchTool(), new NamedTool("never_exposed_tool"));
    var settlement = BuildSettlement(
        (ToolIdConventions.SearchToolId, ToolExposureMode.Inline),
        ("hidden_tool", ToolExposureMode.Deferred));

    var events = await RunAsync(executor, settlement, maxSteps: 3);

    events.OfType<ToolCallEvent>()
        .Where(e => e.Type == MessageEventType.ToolCallComplete)
        .Single(e => e.ToolName == "never_exposed_tool")
        .Status.Should().Be(ToolCallStatus.Rejected);
}
```

同时 `UndeclaredSearchTool` 的 `LoadedToolIds` 改为 `["never_exposed_tool"]`（两处：`UndeclaredSearchTool` 与 `FalseCapabilitySearchTool` 的 `hidden_tool` 引用）。

4. `ExecuteAsync_LoadDeclaredWithExplicitFalseCapability_ShouldBeIgnored`（`:140`）同样把探针换成 `never_exposed_tool`，`settlement` 保持不变。

- [ ] **Step 2: 追加新性质测试**

先补一个能真正禁用工具的构造器（`IsToolEnabled` 只读禁用集，未注册的工具它也返回 true，因此 C1 分支必须用带 workspace 的真实 `ToolManager` 才测得出来）：

```csharp
/// <summary>带 workspace 的 ToolManager：SetToolEnabledAsync 需要它落 tool-state.json。</summary>
private static async Task<AgentExecutor> CreateExecutorWithDisabledToolAsync(string toolId)
{
    var temp = Path.Combine(Path.GetTempPath(), "visibility-" + Guid.NewGuid().ToString("N"));
    var userSeeing = Path.Combine(temp, "user", ".seeing");
    var projectSeeing = Path.Combine(temp, "project", ".seeing");
    Directory.CreateDirectory(userSeeing);
    Directory.CreateDirectory(projectSeeing);

    var workspace = new Mock<IWorkspaceProvider>();
    workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
    workspace.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

    var hookManager = new HookManager(NullLogger<HookManager>.Instance);
    var toolManager = new ToolManager(
        NullLogger<ToolManager>.Instance, hookManager, workspace: workspace.Object);

    await toolManager.RegisterToolAsync(new NamedTool(toolId));
    await toolManager.SetToolEnabledAsync(toolId, enabled: false);

    var llm = CreateScriptedLlm(toolId);
    return CreateExecutorCore(llm, toolManager, hookManager);
}
```

追加四条：

```csharp
[Fact]
public async Task ExecuteAsync_DisabledTool_ShouldRejectAsDisabled()
{
    var executor = await CreateExecutorWithDisabledToolAsync("disabled_tool");
    var settlement = BuildSettlement(("visible_tool", ToolExposureMode.Inline));

    var events = await RunAsync(executor, settlement);

    var complete = events.OfType<ToolCallEvent>()
        .Single(e => e.Type == MessageEventType.ToolCallComplete);
    complete.Status.Should().Be(ToolCallStatus.Rejected);
    complete.Metadata!["visibility.reason"].Should().Be("disabled");
    complete.Error.Should().Contain("已被禁用");
    complete.Error.Should().NotContain(ToolIdConventions.SearchToolId,
        "禁用与检索无关，提示检索只会让模型反复空搜");
}

[Fact]
public async Task ExecuteAsync_AgentFilteredTool_ShouldRejectWithoutSearchHint()
{
    // 已注册但不在本轮结算（Agent 白名单/模块过滤）：提示检索是误导。
    var executor = await CreateExecutorWithToolsAsync(
        CreateScriptedLlm("filtered_tool"), new NamedTool("filtered_tool"));
    var settlement = BuildSettlement(("visible_tool", ToolExposureMode.Inline));

    var events = await RunAsync(executor, settlement);

    var complete = events.OfType<ToolCallEvent>()
        .Single(e => e.Type == MessageEventType.ToolCallComplete);
    complete.Status.Should().Be(ToolCallStatus.Rejected);
    complete.Metadata!["visibility.reason"].Should().Be("not_in_settlement");
    complete.Error.Should().Contain("不是检索可以解决的问题");
    complete.Error.Should().NotContain(ToolIdConventions.SearchToolId);
}

[Fact]
public async Task ExecuteAsync_CaseInsensitiveDeferredCall_ShouldNotFallToNotFound()
{
    // 结算用 OrdinalIgnoreCase，ToolManager 用序数敏感：模型写 Read 不得落到“工具不存在”。
    var executor = CreateExecutor("Read");
    var settlement = BuildSettlement(("read", ToolExposureMode.Deferred));

    var events = await RunAsync(executor, settlement);

    var complete = events.OfType<ToolCallEvent>()
        .Single(e => e.Type == MessageEventType.ToolCallComplete);
    complete.Status.Should().NotBe(ToolCallStatus.Rejected);
}

[Fact]
public async Task ExecuteAsync_DeferredToolWithoutSearchTool_ShouldStillBeAllowed()
{
    var executor = CreateExecutor("hidden_tool");
    var settlement = BuildSettlement(("hidden_tool", ToolExposureMode.Deferred));

    var events = await RunAsync(executor, settlement);

    events.OfType<ToolCallEvent>()
        .Single(e => e.Type == MessageEventType.ToolCallComplete)
        .Status.Should().NotBe(ToolCallStatus.Rejected);
}
```

- [ ] **Step 3: 运行确认失败**

Run: Build + vstest 过滤 `AgentExecutorToolVisibilityTests`
Expected: `ShouldBeAllowed`、大小写不敏感、无检索入口三条 FAIL（旧 gate 会把它们拒掉）。

- [ ] **Step 4: 实现 gate**

把 `AgentExecutor.cs:891-906` 整块替换为：

```csharp
// ========== 可见集校验 ==========
// 只拦“不在本会话结算内”的工具（被禁用 / Agent 过滤 / 模块未挂载 / 名字不存在）。
// 结算内即放行：deferred（未下发 schema）也能直接调用——目录已给出参数签名，
// 参数错误由 Task 5 的定义回灌兜住。加载（TryLoad）只是审计，不参与能否调用。
if (context.ToolSettlement is { } settlement
    && !settlement.Contains(name))
{
    var reason = ResolveVisibilityRejection(name, context);
    _logger.LogWarning(
        "[AgentExecutor] 拒绝了结算外工具的调用: {ToolName} (enabled={Enabled}, registered={Registered})",
        name,
        _tools.IsToolEnabled(name),
        GetRegisteredToolIgnoreCase(name) is not null);
    return BuildTerminalEvent(
        tc, name, context, loopId, ToolCallStatus.Rejected, reason,
        BuildVisibilityMetadata(name));
}
```

`BuildTerminalEvent` 增加可选参数（`:851-870`）：

```csharp
private static ToolCallEvent BuildTerminalEvent(
    ToolCall tc, string name, AgentContext context, string loopId,
    ToolCallStatus status, string error,
    IReadOnlyDictionary<string, object>? metadata = null)
{
    return new ToolCallEvent
    {
        SessionId = context.SessionId,
        LoopId = loopId,
        Type = MessageEventType.ToolCallComplete,
        ToolCallId = tc.Id,
        ToolName = name,
        Arguments = ParseArguments(tc.Function?.Arguments),
        Status = status,
        Error = error,
        Metadata = metadata
    };
}
```

新增三个私有方法：

```csharp
/// <summary>按 OrdinalIgnoreCase 在注册表中查找（ToolManager 的查找是序数敏感的）。</summary>
private ITool? GetRegisteredToolIgnoreCase(string name) =>
    _tools.GetRegisteredTools()
        .FirstOrDefault(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));

/// <summary>结算外工具的三种拒绝文案：禁用 / 被过滤 / 不存在。</summary>
private string ResolveVisibilityRejection(string name, AgentContext context)
{
    if (!_tools.IsToolEnabled(name))
        return $"工具 '{name}' 已被禁用；可在工具设置中启用后重试。";

    if (GetRegisteredToolIgnoreCase(name) is not null)
        return $"工具 '{name}' 在当前 Agent 或会话中不可用；" +
               "这不是检索可以解决的问题，请直接使用已提供的工具完成任务。";

    var hint = context.ToolSettlement?.HasSearchTool == true
        ? $"\n可用 {ToolIdConventions.SearchToolId} 按关键词检索你要的能力（例如 query=\"删除文件\"）。"
        : string.Empty;
    return $"无此工具 '{name}'。{hint}";
}

private IReadOnlyDictionary<string, object> BuildVisibilityMetadata(string name) =>
    new Dictionary<string, object>
    {
        ["visibility.reason"] = !_tools.IsToolEnabled(name) ? "disabled"
            : GetRegisteredToolIgnoreCase(name) is not null ? "not_in_settlement"
            : "not_found"
    };
```

- [ ] **Step 5: 运行确认通过**

Run: Build + vstest 过滤 `AgentExecutorToolVisibilityTests` 与 `ExecutionToolScopeTests`
Expected: 全 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolVisibilityTests.cs
git commit -m "feat(executor): 四分支可见集 gate——结算内工具免检索直调"
```

---

### Task 5: 参数类失败时回灌完整定义

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:927-1033`
- Test: `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolDefinitionTests.cs`（新建）

**Interfaces:**
- Consumes: Task 2/3 的 `ToolCallEvent.ToolDefinition`；`SessionToolSchemaResolution.Tools`（`ExposedTool.Schema` 为 `FunctionToolSchema`）；`IExecutionToolScope.IsCallable`
- Produces:
  - `private const int MaxDefinitionChars = 8192;`
  - `private string? ResolveFailureDefinition(string name, AgentContext context, IExecutionToolScope? toolScope)`
  - `private static bool IsParamLikeFailure(ToolResult result)`

- [ ] **Step 1: 写失败测试**

新建 `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolDefinitionTests.cs`。测试脚手架复用 `AgentExecutorToolVisibilityTests` 的构造方式（`ToolManager` + `HookManager` + Mock LLM 脚本），新增：

```csharp
// 1. 普通失败（Success=false）→ 带定义
[Fact] public async Task DeferredTool_Failure_ShouldAttachDefinition()

// 2. 抛异常（JsonException）→ 带定义
[Fact] public async Task DeferredTool_ExceptionFailure_ShouldAttachDefinition()

// 3. 超时（Metadata["timeout"]=true）→ 不带
[Fact] public async Task TimeoutFailure_ShouldNotAttachDefinition()

// 4. 重试耗尽（Title="重试耗尽"）→ 不带
[Fact] public async Task RetryExhausted_ShouldNotAttachDefinition()

// 5. 内联工具失败 → 不带
[Fact] public async Task InlineTool_Failure_ShouldNotAttachDefinition()

// 6. 已加载工具失败（SendsSchema=false 但 IsCallable=true）→ 不带
[Fact] public async Task LoadedTool_Failure_ShouldNotAttachDefinition()

// 7. 超过 8192 字符 → 不带，改提示 tool_search
[Fact] public async Task OversizedDefinition_ShouldFallbackToSearchHint()
```

断言要点（每条都直接检查 `ToolCallEvent.ToolDefinition`）：

```csharp
var complete = events.OfType<ToolCallEvent>()
    .Single(e => e.Type == MessageEventType.ToolCallComplete);
complete.Status.Should().Be(ToolCallStatus.Failed);
complete.ToolDefinition.Should().NotBeNull();
complete.ToolDefinition!.Should().Contain("cron_create");   // 或对应工具名
complete.ToolDefinition.Should().Contain("parameters");
```

第 7 条断言 `complete.ToolDefinition.Should().BeNull()` 且 `complete.Error.Should().Contain(ToolIdConventions.SearchToolId)`。

第 6 条需要一个声明 `exposure.load` 的检索工具（复用 `DeclaringSearchTool`），脚本 LLM 先调 search 再调 target，`maxSteps: 3`。

- [ ] **Step 2: 运行确认失败**

Run: Build + vstest 过滤 `AgentExecutorToolDefinitionTests`
Expected: 全部 FAIL（`ToolDefinition` 恒为 null）。

- [ ] **Step 3: 实现**

在 `ExecuteSingleToolCallAsync` 里，**权限判定之前**先算出判定所需的两个事实（`:891` 的 gate 之后、`:908` 的 `EvaluatePermissionAsync` 之前）：

```csharp
// 是否需要在本轮失败时回灌定义：本轮未下发 schema 且本执行内未加载过。
// 两个条件都要：detail=full 加载后工具仍是 Deferred，只看 SendsSchema 会重复附定义。
var needsDefinitionOnFailure =
    context.ToolSettlement is { } gateSettlement
    && !gateSettlement.SendsSchema(name)
    && !(toolScope?.IsCallable(name) ?? false);
```

在 `catch (OperationCanceledException)` 之后、最终返回值之前，对**两条失败出口**统一切换为：

```csharp
// 出口 1：异常兜住（GitCommitTool 等抛 JsonException 的工具走这里）
// 出口 2：result.Success == false
```

把 `:956-972` 的 `catch (Exception ex)` 返回改为：

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "[AgentExecutor] 工具执行异常: {ToolName}", name);
    var definition = needsDefinitionOnFailure
        ? ResolveFailureDefinition(name, context)
        : null;
    return new ToolCallEvent
    {
        SessionId = context.SessionId,
        LoopId = loopId,
        Type = MessageEventType.ToolCallComplete,
        ToolCallId = tc.Id,
        ToolName = name,
        Arguments = arguments,
        Status = ToolCallStatus.Failed,
        Error = ex.Message,
        Duration = DateTime.Now - startTime,
        ToolDefinition = definition
    };
}
```

`!result.Success` 的两个返回（`:980-995` 取消分支不变，`:997-1013` 失败分支）只在失败分支加：

```csharp
var definition = needsDefinitionOnFailure && IsParamLikeFailure(result)
    ? ResolveFailureDefinition(name, context)
    : null;
```

并在该 `ToolCallEvent` 初始化器里加 `ToolDefinition = definition`。

新增两个方法：

```csharp
/// <summary>回灌定义的大小上限；超过则不附，改提示用 tool_search 获取。</summary>
private const int MaxDefinitionChars = 8192;

/// <summary>
/// 判断失败是否属“参数类”（缺参/类型错/枚举值非法）。
/// 超时、重试耗尽、权限拒绝这些失败附定义没有帮助，只会白付 2–8KB。
/// </summary>
private static bool IsParamLikeFailure(ToolResult result)
{
    if (result.Success)
        return false;
    if (result.Metadata?.ContainsKey("timeout") == true)
        return false;
    if (string.Equals(result.Title, "重试耗尽", StringComparison.Ordinal))
        return false;
    if (string.Equals(result.Error, "Permission denied", StringComparison.Ordinal))
        return false;
    return true;
}
```

超限时**不得**静默丢弃定义——必须把回退提示落在 `Error` 上，否则模型既拿不到定义也不知道为什么。`!result.Success` 失败分支（`:997-1013`）改为：

```csharp
var definition = needsDefinitionOnFailure && IsParamLikeFailure(result)
    ? ResolveFailureDefinition(name, context)
    : null;

// 需要回灌但取不到（>8192 字符）：把回退提示并进错误文本，不留空承诺。
var errorText = result.Error;
if (needsDefinitionOnFailure && IsParamLikeFailure(result) && definition is null)
{
    errorText = $"{result.Error}\n\n该工具定义过大（超过 {MaxDefinitionChars} 字符），" +
                $"请用 {ToolIdConventions.SearchToolId} query=\"{name}\" detail=full 获取。";
}
```

`ResolveFailureDefinition` 超限返回 `null`（不是占位文本），让上面的分支统一决定文案：

```csharp
/// <summary>
/// 从本轮冻结快照取该工具的完整定义（不重新结算）。
/// 超过 <see cref="MaxDefinitionChars"/> 时返回 null——由调用方改写成检索提示。
/// </summary>
private string? ResolveFailureDefinition(string name, AgentContext context)
{
    var exposed = context.ToolSettlement?.Tools.FirstOrDefault(
        t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
    if (exposed?.Schema is null)
        return null;

    var json = JsonSerializer.Serialize(exposed.Schema);
    return json.Length <= MaxDefinitionChars ? json : null;
}
```

- [ ] **Step 4: 运行确认通过**

Run: Build + vstest 过滤 `AgentExecutorToolDefinitionTests` 与 `AgentExecutorToolVisibilityTests`
Expected: 全 PASS。

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolDefinitionTests.cs
git commit -m "feat(executor): 参数类失败回灌工具完整定义"
```

---

### Task 6: `PromoteOnSearch` 开关（默认关闭）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs:195-221`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureConfigResolver.cs:15-21`、`:105-111`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs:169-179`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `ToolExposureOptions.PromoteOnSearch`：`public bool PromoteOnSearch { get; set; } = false;`
  - `EffectiveExposureConfig` 追加末位成员 `bool PromoteOnSearch`
  - `ToolExposureEngine.ResolveMode` 仅在 `PromoteOnSearch == true` 时把 `discovered` 视为 Inline

- [ ] **Step 1: 写失败测试**

在 `ToolExposureEngineTests` 追加：

```csharp
[Fact]
public void Discovered_ShouldStayDeferred_WhenPromoteOnSearchDisabled()
{
    var tools = new[] { Schema("a"), Schema("tool_search") };

    var result = ToolExposureEngine.Compute(
        Input(tools, Config(ToolExposurePolicyMode.Tiered, promoteOnSearch: false), discovered: ["a"]),
        Counter);

    result.Tools.Single(t => t.Id == "a").Mode.Should().Be(ToolExposureMode.Deferred,
        "promotion 关闭后 tools[] 恒定，发现态不影响暴露模式");
}

[Fact]
public void Discovered_ShouldBecomeInline_WhenPromoteOnSearchEnabled()
{
    var tools = new[] { Schema("a"), Schema("tool_search") };

    var result = ToolExposureEngine.Compute(
        Input(tools, Config(ToolExposurePolicyMode.Tiered, promoteOnSearch: true), discovered: ["a"]),
        Counter);

    result.Tools.Single(t => t.Id == "a").Mode.Should().Be(ToolExposureMode.Inline);
}

[Fact]
public void ToolsArray_ShouldBeStableAcrossRounds_WhenPromoteOnSearchDisabled()
{
    var tools = new[] { Schema("a"), Schema("b"), Schema("tool_search") };
    var config = Config(ToolExposurePolicyMode.Tiered, promoteOnSearch: false);

    var round1 = ToolExposureEngine.Compute(Input(tools, config, discovered: []), Counter);
    var round2 = ToolExposureEngine.Compute(Input(tools, config, discovered: ["a"]), Counter);

    round1.Tools.Select(t => t.Id).Should().Equal(round2.Tools.Select(t => t.Id));
    round1.CatalogText.Should().Be(round2.CatalogText);
}
```

同时把既有 `Discovered_ShouldBePrunedBySessionAvailableIds`（`:162`）的 `Input(...)` 调用补 `promoteOnSearch: true`——它断言的是 promotion 行为，需显式开启。

`Config(...)` 与 `Input(...)` 两个 helper 需要新增 `bool promoteOnSearch = false` 参数并透传。

- [ ] **Step 2: 运行确认失败**

Run: Build + vstest 过滤 `ToolExposureEngineTests`
Expected: 编译失败（helper 无 `promoteOnSearch` 形参）。

- [ ] **Step 3: 实现**

`SeeingAgentOptions.Tools.Exposure` 加：

```csharp
/// <summary>
/// tool_search 命中后是否在下一次执行把该工具提升为内联（进 tools[]）。
/// <para>
/// 默认 false：提升会让 tools[] 内部插入，破坏前缀缓存，且每轮常驻重发 schema。
/// 免检索直调上线后，提升带来的“跨轮可调用”已由直调本身提供，因此默认关闭。
/// 需要旧行为时置 true。
/// </para>
/// </summary>
public bool PromoteOnSearch { get; set; }
```

`EffectiveExposureConfig` 记录末尾加 `bool PromoteOnSearch`；`Resolve` 的 `new EffectiveExposureConfig(...)` 末尾补 `exposure.PromoteOnSearch`。

`ToolExposureEngine.ResolveMode` 签名加 `bool promoteOnSearch`，调用点（`:95`）传 `cfg.PromoteOnSearch`：

```csharp
if (IsSearchTool(id) || pinned.Contains(id))
    return ToolExposureMode.Inline;

// 默认不提升：发现态只用于观测，不改变本轮暴露模式（tools[] 恒定）。
if (promoteOnSearch && discovered.Contains(id))
    return ToolExposureMode.Inline;

return ToolExposureMode.Deferred;
```

- [ ] **Step 4: 修既有测试（默认关闭会改变它们的前提）**

`Config(...)` helper（`:68-75`）末尾补 `bool promoteOnSearch = false` 并作为 `EffectiveExposureConfig` 第 7 个构造实参。

`EffectiveExposureConfig` 记录新增末位成员后，**全部 `new EffectiveExposureConfig(...)` 调用点都要补实参**（`ToolExposureConfigResolver.cs:105`、`ToolExposureEngineTests.cs:43`、`:75`）。逐一检查：

```bash
rg -n "new EffectiveExposureConfig" src tests
```

`TieredLayout_ShouldInlinePinnedDiscoveredAndSearchTool`（`:99`）断言 `b`（discovered）为 Inline——这是 promotion 行为，必须显式开启：把它的 `Config(...)` 调用改为 `Config(ToolExposurePolicyMode.Tiered, pinned: ["a"], promoteOnSearch: true)`，并在测试里加一行注释说明原因。

`Discovered_ShouldBePrunedBySessionAvailableIds`（`:162`）同样补 `promoteOnSearch: true`。

- [ ] **Step 5: 运行确认通过**

Run: Build + vstest 过滤 `ToolExposureEngineTests`、`SessionToolSchemaResolverExposureTests`
Expected: 全 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureConfigResolver.cs src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs
git commit -m "feat(exposure): PromoteOnSearch 开关，默认关闭以保住前缀缓存"
```

---

### Task 7: `IContextExecutionScope.ToolSettlement` 与宿主接线

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Context/IContextExecutionScope.cs:16-39`
- Modify: `src/spine/Seeing.Agent.Core/Context/ContextExecutionScope.cs:17-33`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:227-235`
- Test: `tests/spine/Seeing.Agent.Tests/Context/ContextExecutionScopeTests.cs`（新建）

**Interfaces:**
- Consumes: `SessionToolSchemaResolution`（`Abstractions.Execution`）
- Produces:
  - `SessionToolSchemaResolution? ToolSettlement { get; set; }` 加入 `IContextExecutionScope`
  - `ContextExecutionScope.ToolSettlement` 实现
  - `ExecutionRunner` 在 `:235` 之后填充 `executionScope.ToolSettlement = context.ToolSettlement;`

- [ ] **Step 1: 写失败测试**

新建 `tests/spine/Seeing.Agent.Tests/Context/ContextExecutionScopeTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Context;
using Xunit;

namespace Seeing.Agent.Tests.Context;

public class ContextExecutionScopeTests
{
    [Fact]
    public void ToolSettlement_ShouldRoundTrip()
    {
        var scope = new ContextExecutionScope();
        scope.ToolSettlement.Should().BeNull();

        scope.ToolSettlement = SessionToolSchemaResolution.Empty;

        scope.ToolSettlement.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: Build + vstest 过滤 `ContextExecutionScopeTests`
Expected: 编译失败（无 `ToolSettlement`）。

- [ ] **Step 3: 实现**

`IContextExecutionScope` 加（放在 `UserQuery` 之后）：

```csharp
/// <summary>
/// 本轮执行的工具暴露结算视图（宿主在装配前填充）。
/// <para>
/// 工具目录源与工具看到同一份，执行期间不漂移。宿主未接入结算时为 null
/// （工具目录源按不可用处理，保留上次已叙述内容，不叙述「目录已删除」）。
/// </para>
/// </summary>
SessionToolSchemaResolution? ToolSettlement { get; set; }
```

需要 `using Seeing.Agent.Abstractions.Execution;`。

`ContextExecutionScope` 加对应属性（含 `using Seeing.Agent.Abstractions.Execution;`）。

`ExecutionRunner` 在 `executionScope.UserQuery = record.Input?.Text;`（`:235`）之后加：

```csharp
executionScope.ToolSettlement = context.ToolSettlement;
```

- [ ] **Step 4: 运行确认通过**

Run: Build + vstest 过滤 `ContextExecutionScopeTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Context/IContextExecutionScope.cs src/spine/Seeing.Agent.Core/Context/ContextExecutionScope.cs src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs tests/spine/Seeing.Agent.Tests/Context/ContextExecutionScopeTests.cs
git commit -m "feat(context): 执行级作用域携带工具结算视图"
```

---

### Task 8: `ToolCatalogContextSource` 与增量渲染器

**Files:**
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolCatalogDeltaRenderer.cs`
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolCatalogContextSource.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolCatalogDeltaRendererTests.cs`（新建）
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolCatalogContextSourceTests.cs`（新建）

**Interfaces:**
- Consumes: Task 7 的 `IContextExecutionScope.ToolSettlement`；`SessionToolSchemaResolution.CatalogText` / `.Tools` / `.DeferredToolCount`
- Produces:
  - `internal static class ToolCatalogDeltaRenderer`
    - `public static string Render(JsonElement? previousCanonical, JsonElement? currentCanonical)` — 无变化返回 `string.Empty`
    - `public const string RemovedPhrase = "已不在目录中"`（与 `SkillCatalogFormatter` 同约定，便于模型识别移除）
  - `public sealed class ToolCatalogContextSource : IContextSource, IContextSourceMessageMetadata`
    - `Key => new(typeof(ToolCatalogContextSource).FullName!)`
    - `Scope => ContextSourceScope.Session`
    - `Target => ContextSourceTarget.Message`
    - `ValueSchemaVersion => 1`
    - `MaxBaselineChars => 0`
    - canonical 形如 `{"namespaces":[{"name":"mcp__docs","tools":["mcp__docs__a","mcp__docs__b"]}]}`

- [ ] **Step 1: 写 DeltaRenderer 失败测试**

新建 `ToolCatalogDeltaRendererTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolCatalogDeltaRendererTests
{
    private static JsonElement Canonical(params (string Ns, string[] Ids)[] groups) =>
        JsonSerializer.SerializeToElement(new
        {
            namespaces = groups.Select(g => new { name = g.Ns, tools = g.Ids }).ToList()
        });

    [Fact]
    public void AddedTools_ShouldBeNarrated()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("mcp__docs", ["a"])),
            Canonical(("mcp__docs", ["a", "b"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("+1");
        text.Should().Contain("b");
    }

    [Fact]
    public void RemovedTools_ShouldNarrateWithRemovedPhrase()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("mcp__docs", ["a", "b"])),
            Canonical(("mcp__docs", ["a"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("-1");
        text.Should().Contain("b");
        text.Should().Contain(ToolCatalogDeltaRenderer.RemovedPhrase);
    }

    [Fact]
    public void NewNamespace_ShouldBeFullyNarrated()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("通用", ["read"])),
            Canonical(("通用", ["read"]), ("mcp__docs", ["x", "y"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("x");
        text.Should().Contain("y");
    }

    [Fact]
    public void NoChange_ShouldReturnEmpty()
    {
        var canonical = Canonical(("通用", ["read"]));

        ToolCatalogDeltaRenderer.Render(canonical, canonical).Should().BeEmpty();
    }

    [Fact]
    public void SameCanonical_ShouldRenderByteIdentically()
    {
        var previous = Canonical(("通用", ["read"]));
        var current = Canonical(("通用", ["read", "write"]));

        ToolCatalogDeltaRenderer.Render(previous, current)
            .Should().Be(ToolCatalogDeltaRenderer.Render(previous, current));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: Build + vstest 过滤 `ToolCatalogDeltaRendererTests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现 `ToolCatalogDeltaRenderer`**

规则（`Render` 内部）：

1. `previousCanonical` 为 null → 视为空目录（全量新增）。
2. 解析两边为 `Dictionary<string, SortedSet<string>>`（命名空间 → 工具 id 集），排序一律 `StringComparer.Ordinal`。
3. 对每个命名空间按 ordinal 遍历：新增集非空 → `### {ns} (+{n})  {id1}, {id2}`；移除集非空 → `### {ns} (-{n})  {ids}（{RemovedPhrase}）`。
4. 命名空间整体新增 → 输出全部工具 id。
5. 命名空间整体消失 → 输出 `### {ns} (-{n}) {ids}（{RemovedPhrase}）`。
6. 无任何差异 → `string.Empty`。

渲染文本首行固定 `工具目录已变化：`，其余用 `'\n'`（显式 LF，不随平台变化——字节稳定要求）。

- [ ] **Step 4: 运行确认通过**

Run: Build + vstest 过滤 `ToolCatalogDeltaRendererTests`
Expected: 全 PASS。

- [ ] **Step 5: 写 ContextSource 失败测试**

新建 `ToolCatalogContextSourceTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolCatalogContextSourceTests
{
    // 需要替身：测试内定义 FakeExecutionScope 实现 IContextExecutionScope
    private sealed class FakeScope : IContextExecutionScope
    {
        public string SessionId { get; set; } = "s1";
        public string Cwd { get; set; } = ".";
        public string WorkspaceRoot { get; set; } = ".";
        public Seeing.Agent.Abstractions.Skills.SkillCatalogSnapshot? SkillSnapshot { get; set; }
        public string? UserQuery { get; set; }
        public SessionToolSchemaResolution? ToolSettlement { get; set; }
    }

    [Fact]
    public async Task MissingSettlement_ShouldBeUnavailable_NotRemoved()
    {
        var source = new ToolCatalogContextSource(new FakeScope());

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Unavailable,
            "结算缺失属宿主接线问题，不得叙述为目录已删除");
    }

    [Fact]
    public async Task Target_ShouldBeMessage_SoCatalogNeverEntersSystemPrompt()
    {
        new ToolCatalogContextSource(new FakeScope()).Target
            .Should().Be(ContextSourceTarget.Message);
    }

    [Fact]
    public async Task Canonical_ShouldContainNamespacesAndIds_NotDescriptions()
    {
        var scope = new FakeScope
        {
            ToolSettlement = BuildSettlement(("mcp__docs", "mcp__docs__x"))
        };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        var json = value.Canonical!.Value.GetRawText();
        json.Should().Contain("mcp__docs");
        json.Should().Contain("mcp__docs__x");
        json.Should().NotContain("desc", "描述可能被 tool.definition Hook 改写，放进 canonical 会造成无关叙述");
    }

    [Fact]
    public async Task EmptyDeferred_ShouldBeRemoved()
    {
        var scope = new FakeScope { ToolSettlement = BuildSettlement() };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Removed);
    }
}
```

`BuildSettlement(params (string Ns, string Id)[] tools)` 用 `ExposedTool` 构造（`Schema` 用 `FunctionToolSchema`，`Mode = ToolExposureMode.Deferred`），`CatalogText` 给非空字符串。

- [ ] **Step 6: 运行确认失败**

Run: Build + vstest 过滤 `ToolCatalogContextSourceTests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 7: 实现 `ToolCatalogContextSource`**

- `ReadAsync`：`_executionScope.ToolSettlement` 为 null → `Unavailable`；`DeferredToolCount == 0` → `Removed`；否则 `Value`，canonical 按 `ExposedTool.Namespace` 分组（空命名空间记为 `"通用"`）、工具 id 按 ordinal 排序；payload 为 `settlement.CatalogText`。
- `RenderInitial(value)`：返回 `value.Payload as string ?? string.Empty`。
- `Render(previous, current)`：`current.State == Removed` 时把 current canonical 当作空；委托 `ToolCatalogDeltaRenderer.Render(previous.Canonical, currentCanonical)`；空串返回 `null`（不推进叙述）。
- `GetMessageMetadata`：返回 `new Dictionary<string, object> { ["tool_catalog"] = true }`（供 UI 折叠）。

- [ ] **Step 8: 运行确认通过**

Run: Build + vstest 过滤 `ToolCatalogContextSourceTests` 与 `ToolCatalogDeltaRendererTests`
Expected: 全 PASS。

- [ ] **Step 9: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolCatalogDeltaRenderer.cs src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolCatalogContextSource.cs tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolCatalogDeltaRendererTests.cs tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolCatalogContextSourceTests.cs
git commit -m "feat(context): 工具目录上下文源与增量渲染器"
```

---

### Task 9: 接线并移除系统提示词落点

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/HostingServiceCollectionExtensions.cs:96-97`
- Delete: `src/hosting/Seeing.Agent.Hosting/Prompts/ToolCatalogSectionContributor.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:598-600`
- Delete: `tests/hosting/Seeing.Agent.Hosting.Tests/Prompts/ToolCatalogSectionContributorTests.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Prompts/PromptSectionNames.cs:20-23`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolCatalogContextSourceTests.cs`（追加 DI 断言）

**Interfaces:**
- Consumes: Task 8 的 `ToolCatalogContextSource`
- Produces:
  - DI：`services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, ToolCatalogContextSource>())`
  - `PromptSectionNames.ToolCatalog` 常量删除（唯一消费点随之移除）
  - `AgentExecutor` 不再写 `promptContext.Sections[...]`

- [ ] **Step 1: 写失败测试（DI 注册）**

在 `ToolCatalogContextSourceTests` 追加：

```csharp
[Fact]
public void ExecutionEngineRegistration_ShouldRegisterToolCatalogAsContextSource()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddExecutionEngine();

    services.Should().Contain(
        d => d.ServiceType == typeof(IContextSource)
             && d.ImplementationType == typeof(ToolCatalogContextSource),
        "工具目录必须以上下文源注册；注册到 AddExecutionEngine（而非 AddSessionHosting），"
        + "因为 ToolSettlement / ToolExposure 全部在该方法内装配");
}
```

> 用 `ServiceDescriptor` 断言，不 `BuildServiceProvider`：`AddExecutionEngine` 依赖外部已注册的 `ISessionManager` 等，独立构建容器会炸。真正的 DI 连通性由 `HostComposition` 的既有守门测试覆盖（`tests/apps/Seeing.Agent.HostComposition.Tests`）。

- [ ] **Step 2: 运行确认失败**

Run: Build + vstest 过滤 `ToolCatalogContextSourceTests`
Expected: FAIL（未注册）。

- [ ] **Step 3: 接线**

`HostingServiceCollectionExtensions.cs:96-97` 整块替换（**注册位置是 `AddExecutionEngine`，不是 `AddSessionHosting`**——`ToolSettlement`、`ToolExposureConfigResolver`、`ISessionToolSchemaResolver` 全部在本方法内装配）：

```csharp
// 工具目录改为上下文源（Target = Message）：目录不再进系统提示词，
// 运行时变化走增量叙述消息，保住系统提示词与前缀缓存。
services.TryAddEnumerable(
    ServiceDescriptor.Scoped<Seeing.Agent.Abstractions.Context.IContextSource,
        Seeing.Agent.Hosting.Execution.ToolExposure.ToolCatalogContextSource>());
```

需要文件顶部 `using Microsoft.Extensions.DependencyInjection.Extensions;`（`TryAddEnumerable`）与 `Microsoft.Extensions.DependencyInjection`（`ServiceDescriptor`）——该文件已有 `AddSingleton` 调用，命名空间大概率已在。

- [ ] **Step 4: 删除旧落点**

删除 `ToolCatalogSectionContributor.cs` 与对应测试文件。删除 `AgentExecutor.cs:598-600`：

```csharp
// 工具目录正文（结算时渲染，string-only 携带）经通用 Sections 槽交给 ToolCatalog 分节贡献者
if (!string.IsNullOrWhiteSpace(settlement?.CatalogText))
    promptContext.Sections[PromptSectionNames.ToolCatalog] = settlement.CatalogText;
```

删除 `PromptSectionNames.ToolCatalog`（`:20-23`）——`ToolCatalogSectionContributor` 是唯一消费者（已确认 grep 仅剩 `AgentExecutor.cs:600`、该贡献者自身、该贡献者的测试）。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error（若仍有引用会在此暴露）。

Run: Build + vstest 过滤 `ToolCatalogContextSourceTests`
Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add -A src/hosting/Seeing.Agent.Hosting src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs src/abstractions/Seeing.Agent.Abstractions/Prompts/PromptSectionNames.cs tests/hosting/Seeing.Agent.Hosting.Tests
git commit -m "feat(context): 工具目录迁入上下文源管道，移除系统提示词分节"
```

---

### Task 10: 目录开场白与检索文案改写

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs:295-297`
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs:51-55`、`:208-212`
- Modify: `src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs:63`、`:330`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs:190-206`
- Test: `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs`

**Interfaces:**
- Consumes: Task 4/5/6 的新语义
- Produces: 无新增 API，仅文案

- [ ] **Step 1: 改写目录断言**

`ToolExposureEngineTests.Catalog_ShouldStateSameRequestCallabilityAndCompleteness`（`:190`）改为：

```csharp
[Fact]
public void Catalog_ShouldStateDirectCallabilityAndCompleteness()
{
    var tools = new[] { Schema("read"), Schema("tool_search") };

    var result = ToolExposureEngine.Compute(
        Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);

    result.CatalogText.Should().NotBeNull();
    // 目录承诺必须与执行层一致：deferred 工具可直接调用。
    result.CatalogText!.Should().Contain("可以直接调用");
    result.CatalogText.Should().NotContain("未加载前");
    result.CatalogText.Should().NotContain("先调用 tool_search 检索");
    // 未展示的条目必须显式说明仍然存在、仍可检索。
    result.CatalogText.Should().Contain("未展示");
}
```

- [ ] **Step 2: 追加检索文案断言，并修正会被旧文案绑死的既有断言**

`ToolSearchToolTests` 里以下既有断言直接检查旧承诺，**必须同步改写**，否则改了文案就会红：

| 位置 | 旧断言 | 改为 |
|---|---|---|
| `:165` | `Output` 含 `"本次执行内可直接调用"` | 含 `"已返回完整定义，可直接调用"` |
| `:341-354` `ParameterDescription_ShouldUseSameExecutionTerminology` | `detail` 参数描述含 `"本次执行内"`、不含 `"下一轮"` | 只断言不含 `"下一轮"` 与 `"自动内联"`；`detail` 描述改为「full=返回完整定义」 |
| `:357-368` `FullDetail_ShouldStateSameExecutionCallability` | 含 `"本次执行内可直接调用"` | 含 `"可直接调用"`；`LoadedToolIds` 断言保留（审计仍需） |
| `:403-419` `DetailFull_WithoutExecutionScope_ShouldPromiseOnlyLaterExecutions` | 含 `"下一次执行起"` | **删除该测试**：promotion 默认关闭后「下一次执行起内联」不再是真话，该测试守的是一条已不存在的承诺 |

另追加两条（`CreateTool(params ExposedTool[] deferred)` 是既有 helper，必须给它一条语料）：

```csharp
[Fact]
public void Description_ShouldNotPromiseAutoInlining()
{
    var tool = CreateTool(Entry("file_read"));

    tool.Description.Should().NotContain("自动内联");
    tool.Description.Should().NotContain("下一次执行起");
    tool.Description.Should().Contain("查找");
}

[Fact]
public async Task FullDetailResult_ShouldNotPromiseNextRoundInlining()
{
    var tool = CreateTool(Entry("file_read"));

    var result = await tool.ExecuteAsync(
        Args(new { query = "file_read", detail = "full" }), _context);

    result.Output.Should().NotContain("自动内联");
    result.Output.Should().NotContain("下一次执行起");
    result.Output.Should().Contain("可直接调用");
}
```

- [ ] **Step 3: 运行确认失败**

Run: Build + 两个测试程序集过滤
Expected: FAIL（旧文案仍在）。

- [ ] **Step 4: 改写文案**

`ToolExposureEngine.BuildCatalog` 的 `intro`（`:295-297`）：

```csharp
var intro = searchAvailable
    ? "以下工具当前未内联进本次请求的 schema，但可以直接调用——已给出参数名与必填标记（? 为可选），按签名传参即可。若参数不符，错误信息会附带完整定义供你修正。需要提前查看完整定义时用 tool_search（可按 namespace、关键词与 detail 收窄）。目录只展示部分条目，未展示的工具同样存在、同样可检索，不代表缺失。"
    : "以下工具当前未内联进本次请求的 schema，但可以直接调用——已给出参数名与必填标记（? 为可选），按签名传参即可。若参数不符，错误信息会附带完整定义供你修正。目录只展示部分条目，未展示的工具同样存在，不代表缺失。";
```

`ToolSearchTool.Description`（`:51-55`）：

```csharp
public override string Description =>
    "按关键词查找工具（不确定工具名时用）。\n\n" +
    "- detail：name 仅 id、summary 默认（id+签名+摘要）、full 返回完整定义。\n" +
    "- 目录里已给出参数签名的工具可直接调用，无需先检索。\n" +
    "- 分页：limit 与 offset；query 省略则浏览全部目录。";
```

`ToolSearchTool` 命中行（`:208-212`）：把三态文案里的「下一次执行起自动内联」删掉，统一为：

```csharp
sb.Append(discovered
    ? "\n   已返回完整定义，可直接调用："
    : "\n   警告：发现态登记失败（会话不可用），该工具本次及后续请求均不可直接调用:");
```

同时把 `detail` 参数描述（`ToolSearchTool.cs:80`）里的「（下一次执行起自动内联，每页最多 3 条）」改为「（每页最多 3 条）」——它是模型实际读到的 schema 文本，保留旧承诺等于自相矛盾。

> `discovered` 变量与 `LoadedToolIds` 声明保留（审计与观测仍需），仅改文案。

`BuiltInAgents.cs:63` 改为：

```
- 目录中已给出参数签名的工具可直接调用；只有不确定工具名时才用 `tool_search` 查找。
```

`BuiltInAgents.cs:330` 里 `需要的工具不在工具清单里时先用 tool_search 检索。` 改为同一句。

- [ ] **Step 5: 运行确认通过**

Run: Build + 两个测试程序集过滤；再跑 `PromptRoutingTests`
Expected: 全 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs
git commit -m "docs(prompt): 目录与检索文案对齐免检索直调语义"
```

---

### Task 11: 架构文档修正与全量验证

**Files:**
- Modify: `docs/architecture/19-tool-context-budgeting-release-notes.md` §4
- Modify: `docs/architecture/21-context-source-pipeline-release-notes.md` §8.3
- Test: 全量

**Interfaces:**
- Consumes: 全部前序任务
- Produces: 无代码变更

- [ ] **Step 1: 修正 19 §4 的矛盾表述**

把「执行级可调用集」与「exposure ≠ permission」两行改为统一表述：

```markdown
- **执行级放行集**：可调用 = **本会话结算内**（`Tools` 全集，含 Inline 与 Deferred）。
  分层只决定「是否随请求下发 schema」，**不决定能否调用**；deferred 工具可直接调用。
  参数错误时执行器把该工具的完整定义回灌进失败结果（`ToolCallEvent.ToolDefinition`），
  模型一次往返即可修正。`IExecutionToolScope.IsCallable` 不再参与能否调用的判定，
  仅用于判断「本次是否需要回灌定义」与审计。
- **exposure ≠ permission**：分层只决定「是否进本轮 schema / 目录」；调用许可恒走
  `IPermissionAuthorizer` 决策链；目录正文不得含允许/拒绝裁决。
```

- [ ] **Step 2: 修正 21 §8.3**

原文「本期不含工具目录源」改为：

```markdown
3. **工具目录源已接入**（2026-11-06）：`ToolCatalogContextSource`（`Target = Message`、
   `Scope = Session`）承担目录初始渲染与增量叙述；系统提示词分节
   `ToolCatalogSectionContributor` 已删除。目录正文仍由 `ToolExposureEngine.BuildCatalog`
   单点渲染（经执行级 `IContextExecutionScope.ToolSettlement.CatalogText` 传递），
   canonical 只含命名空间与工具 id，不含描述（描述可能被 `tool.definition` Hook 改写）。
```

- [ ] **Step 3: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error。

- [ ] **Step 4: 全量测试**

Run（逐个程序集）：

```bash
dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests && dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet build tests/primitives/Seeing.Session.Tests && dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll
dotnet build tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.ToolDiscovery.Tests.dll
```

Expected: 全绿。基线已知失败为 `InstructionManagerTests.InjectIfNeededAsync_FirstDiscovery`（读真实 `~/.agents/AGENTS.md`，环境依赖）与 `SidebarShutdown_ShouldReserveCollapseTriggerSpace`（WebUI CSS 契约），两者与本变更无关。

- [ ] **Step 5: 提交**

```bash
git add docs/architecture
git commit -m "docs: 修正 exposure/permission 表述并记录工具目录源接入"
```

---

## 附录：验证清单（供最终评审）

| 验收项 | 对应任务 / 测试 |
|---|---|
| deferred 直调成功且 `ChatRequest.Tools` 不含该工具 | Task 4 测试（`ShouldBeAllowed`） |
| 参数错误时下一轮能从 `<tool_definition>` 修正 | Task 5 测试 1/2 |
| 连续两轮 `tools[]` 完全一致 | Task 6 `ToolsArray_ShouldBeStableAcrossRounds` |
| 目录变化产生 `context.update` 消息、系统提示词不变 | Task 8 `Target_ShouldBeMessage...` + Task 9 |
| 历史重建与本轮产出逐字节相同，且经 SaveAsync 读回仍相同 | Task 3 `LiveContent_AndRebuiltContent_ShouldBeByteIdentical` + `Clone_ShouldCopyToolDefinition` |
| C1/C2/D 文案互不混用；C2 不含 `tool_search` | Task 4 新增测试 |
| 大小写不敏感不落入 not_found | Task 4 `CaseInsensitiveDeferredCall...` |
| 噪声失败不附定义 | Task 5 测试 3/4/5 |
| 已加载工具不重复付定义 | Task 5 测试 6 |
| 超限定义回退检索提示 | Task 5 测试 7 |
| 构建 0 error、相关程序集全绿 | Task 11 |
