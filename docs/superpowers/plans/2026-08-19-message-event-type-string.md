# 消息事件类型字符串化实施计划（子项目①）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `MessageEventType` 从封闭枚举改为开放的字符串常量（`const string`），使插件/扩展能发布自定义事件类型；同时消除 `AppEventType` 的 cast hack 与 `CommandResult`/`Navigate` 重复定义。

**Architecture:** `MessageEventType` 枚举 → 同名 `static class`（成员名不变、值变点分小写字符串），`IMessageEvent.Type` 属性改为 `string`。因 `const string` 是编译期常量、可直接用于 `switch` case 和 `=>` 返回，绝大多数引用点写法不变。`AppEventType` 枚举删除，5 处 cast 改字符串常量。前端 `EventStreamHandler` 唯一 cast case 需改。

**Tech Stack:** C# / .NET 10 / xUnit / FluentAssertions

**前置知识（执行者必读）：**
- 分层：`MessageEventTypes.cs` 在 `Seeing.Agent.Abstractions`（Abstractions 层）；`AppEvents.cs` 在 `Seeing.Agent.App`（App 层）。
- `AppEventType` 用 `(MessageEventType)AppEventType.X`（值 1000+）扩展枚举，本计划删除此 hack。
- 核心机制：`enum MessageEventType { LoopStart }` → `static class MessageEventType { public const string LoopStart = "loop.start"; }`。由于 `const string` 是编译期常量，`case MessageEventType.LoopStart:` 和 `Type => MessageEventType.LoopStart` 写法**不变**，只需把**属性/变量的类型标注**从 `MessageEventType` 改为 `string`。
- 注意：`MessageEventType` 类型名保留（枚举变静态类），避免改所有 `MessageEventType.X` 引用点。只需改显式类型标注处。
- 测试命令：`dotnet build Seeing.Agent.slnx`（全量编译）、`dotnet test tests/Seeing.Gateway.Tests`、`dotnet test tests/Seeing.Agent.Acp.Tests`、`dotnet test tests/Seeing.Agent.Tests`。

---

## 任务 1：`MessageEventType` 枚举改为静态字符串常量类

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`
- Test: `tests/Seeing.Agent.Tests/Events/MessageEventTypeStringTests.cs`（新建）

- [ ] **Step 1: 写失败测试**（验证枚举成员均映射为点分字符串、且类型是 string）

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class MessageEventTypeStringTests
{
    [Theory]
    [InlineData(MessageEventType.LoopStart, "loop.start")]
    [InlineData(MessageEventType.LoopComplete, "loop.complete")]
    [InlineData(MessageEventType.StreamStart, "stream.start")]
    [InlineData(MessageEventType.StreamDelta, "stream.delta")]
    [InlineData(MessageEventType.StreamComplete, "stream.complete")]
    [InlineData(MessageEventType.ToolCallPending, "tool.call.pending")]
    [InlineData(MessageEventType.ToolCallRunning, "tool.call.running")]
    [InlineData(MessageEventType.ToolCallComplete, "tool.call.complete")]
    [InlineData(MessageEventType.SubAgentStarted, "subagent.started")]
    [InlineData(MessageEventType.SubAgentCompleted, "subagent.completed")]
    [InlineData(MessageEventType.TaskStarted, "task.started")]
    [InlineData(MessageEventType.TaskProgress, "task.progress")]
    [InlineData(MessageEventType.TaskCompleted, "task.completed")]
    [InlineData(MessageEventType.TaskFailed, "task.failed")]
    [InlineData(MessageEventType.PermissionRequest, "permission.request")]
    [InlineData(MessageEventType.PermissionResponse, "permission.response")]
    [InlineData(MessageEventType.LoopCancelled, "loop.cancelled")]
    [InlineData(MessageEventType.Error, "error")]
    [InlineData(MessageEventType.CommandResult, "command.result")]
    [InlineData(MessageEventType.BudgetStatus, "budget.status")]
    [InlineData(MessageEventType.Compaction, "compaction")]
    [InlineData(MessageEventType.BudgetWarning, "budget.warning")]
    [InlineData(MessageEventType.Navigate, "navigate")]
    [InlineData(MessageEventType.TodoUpdate, "todo.update")]
    [InlineData(MessageEventType.ModeUpdate, "mode.update")]
    public void Constants_MapToDotNotationStrings(string value, string expected)
    {
        value.Should().Be(expected);
    }
}
```

（注：`InlineData` 参数类型为 `string`，若枚举未改则编译失败——这本身就是失败信号。共 25 条 InlineData，与枚举成员一一对应。`SubAgentStarted`/`SubAgentCompleted` 带 `[Obsolete]`，仅产生 CS0618 警告，`Seeing.Agent.Tests` 工程未开 TreatWarningsAsErrors，可编译。）

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~MessageEventTypeStringTests"`
Expected: 编译失败（`MessageEventType` 是枚举，不能传 string 给 InlineData），或断言失败。

- [ ] **Step 3: 将枚举改为静态常量类**

编辑 `src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`：把 `public enum MessageEventType { LoopStart, ... }` 整体替换为：

```csharp
/// <summary>
/// 消息事件类型 - 开放的字符串标识（插件可自由扩展，无需修改本类）
/// </summary>
public static class MessageEventType
{
    /// <summary>Agent Loop 开始（一次完整对话循环开始）</summary>
    public const string LoopStart = "loop.start";

    /// <summary>Agent Loop 结束（一次完整对话循环结束）</summary>
    public const string LoopComplete = "loop.complete";

    /// <summary>流式开始（新轮次开始信号）</summary>
    public const string StreamStart = "stream.start";

    /// <summary>流式增量（实时渲染用）</summary>
    public const string StreamDelta = "stream.delta";

    /// <summary>流式结束（添加历史用）</summary>
    public const string StreamComplete = "stream.complete";

    /// <summary>工具调用请求（pending）</summary>
    public const string ToolCallPending = "tool.call.pending";

    /// <summary>工具执行中</summary>
    public const string ToolCallRunning = "tool.call.running";

    /// <summary>工具执行完成</summary>
    public const string ToolCallComplete = "tool.call.complete";

    /// <summary>子代理启动（废弃：使用 TaskStarted）</summary>
    [Obsolete("Use TaskStarted")]
    public const string SubAgentStarted = "subagent.started";

    /// <summary>子代理完成（废弃：使用 TaskCompleted）</summary>
    [Obsolete("Use TaskCompleted")]
    public const string SubAgentCompleted = "subagent.completed";

    /// <summary>子任务启动</summary>
    public const string TaskStarted = "task.started";

    /// <summary>子任务进度投影</summary>
    public const string TaskProgress = "task.progress";

    /// <summary>子任务完成</summary>
    public const string TaskCompleted = "task.completed";

    /// <summary>子任务失败</summary>
    public const string TaskFailed = "task.failed";

    /// <summary>权限请求（需要用户确认）</summary>
    public const string PermissionRequest = "permission.request";

    /// <summary>权限响应（用户确认/拒绝）</summary>
    public const string PermissionResponse = "permission.response";

    /// <summary>Loop 被取消</summary>
    public const string LoopCancelled = "loop.cancelled";

    /// <summary>错误</summary>
    public const string Error = "error";

    /// <summary>命令执行结果</summary>
    public const string CommandResult = "command.result";

    /// <summary>预算状态更新</summary>
    public const string BudgetStatus = "budget.status";

    /// <summary>压缩执行</summary>
    public const string Compaction = "compaction";

    /// <summary>预算警告</summary>
    public const string BudgetWarning = "budget.warning";

    /// <summary>导航请求</summary>
    public const string Navigate = "navigate";

    /// <summary>Todo 列表更新</summary>
    public const string TodoUpdate = "todo.update";

    /// <summary>会话模式更新</summary>
    public const string ModeUpdate = "mode.update";
}
```

（`SubAgentStarted`/`SubAgentCompleted` 为废弃成员，字符串化时保留 `[Obsolete]` 与字符串值。）

- [ ] **Step 4: 改 `IMessageEvent.Type` 为 `string`**

在同一个文件 `MessageEventTypes.cs` 中，`IMessageEvent` 接口的 `Type` 属性：

```csharp
/// <summary>事件类型（开放字符串标识）</summary>
string Type { get; }
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~MessageEventTypeStringTests"`
Expected: PASS（25 个 InlineData 全部匹配）。

- [ ] **Step 6: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs tests/Seeing.Agent.Tests/Events/MessageEventTypeStringTests.cs
git commit -m "refactor: MessageEventType 枚举改字符串常量类"
```

---

## 任务 2：修复 Abstractions 层所有事件 record 的 `Type` 类型标注

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`（所有 record 的 `public MessageEventType Type =>` 改 `public string Type =>`）

- [ ] **Step 1: 全量替换类型标注**

在 `MessageEventTypes.cs` 中，所有事件 record（`LoopStartEvent`/`LoopCompleteEvent`/`StreamStartEvent`/`StreamDeltaEvent`/`StreamCompleteEvent`/`ToolCallEvent`/`SubAgentEvent`/`TaskStartedEvent`/`TaskProgressEvent`/`TaskCompletedEvent`/`TaskFailedEvent`/`ErrorEvent`/`PermissionRequestEvent`/`PermissionResponseEvent`/`LoopCancelledEvent`/`TodoUpdateEvent`/`ModeUpdateEvent`）中，把属性声明：

```csharp
public MessageEventType Type => MessageEventType.LoopStart;
```

改为：

```csharp
public string Type => MessageEventType.LoopStart;
```

（注意：仅属性声明的**返回类型** `MessageEventType` → `string`；`=> MessageEventType.LoopStart` 右侧不变。`ToolCallEvent` 与 `SubAgentEvent` 的 `public MessageEventType Type { get; init; }` 改为 `public string Type { get; init; } = string.Empty;`——`= string.Empty` 消除 CS8618。）

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/Seeing.Agent.Abstractions/Seeing.Agent.Abstractions.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs
git commit -m "refactor: IMessageEvent.Type 改 string，事件 record 类型标注同步"
```

---

## 任务 3：删除 `AppEventType`，修正 `AppEvents.cs`

**Files:**
- Modify: `src/Seeing.Agent.App/Events/AppEvents.cs`

- [ ] **Step 1: 删除 `AppEventType` 枚举并新增局部常量类**

编辑 `AppEvents.cs`：删除 `public enum AppEventType { ... }`（第 10-26 行），在其位置新增：

```csharp
/// <summary>
/// App 层事件类型字符串常量（不并入 Abstractions，保持 Abstractions 不感知 App 层应用事件）
/// </summary>
public static class AppEventTypeConstants
{
    /// <summary>Session 更新</summary>
    public const string SessionUpdated = "session.updated";

    /// <summary>Skill 内容展开</summary>
    public const string SkillContent = "skill.content";

    /// <summary>Session 标题变更</summary>
    public const string SessionTitleChanged = "session.title.changed";
}
```

- [ ] **Step 2: 替换 5 处 cast**

在 `AppEvents.cs` 中替换各 record 的 `Type` 属性：

| 原写法 | 新写法 |
|--------|--------|
| `public MessageEventType Type => (MessageEventType)AppEventType.SessionUpdated;` | `public string Type => AppEventTypeConstants.SessionUpdated;` |
| `public MessageEventType Type => (MessageEventType)AppEventType.SessionTitleChanged;` | `public string Type => AppEventTypeConstants.SessionTitleChanged;` |
| `public MessageEventType Type => (MessageEventType)AppEventType.CommandResult;` | `public string Type => MessageEventType.CommandResult;` |
| `public MessageEventType Type => (MessageEventType)AppEventType.Navigate;` | `public string Type => MessageEventType.Navigate;` |
| `public MessageEventType Type => (MessageEventType)AppEventType.SkillContent;` | `public string Type => AppEventTypeConstants.SkillContent;` |

（`CommandResultEvent`/`NavigateEvent` 复用 Abstractions 的 `MessageEventType.CommandResult`/`MessageEventType.Navigate`，统一重复定义。）

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/Seeing.Agent.App/Seeing.Agent.App.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 4: 提交**

```bash
git add src/Seeing.Agent.App/Events/AppEvents.cs
git commit -m "refactor: 删除 AppEventType 枚举，cast 改字符串常量"
```

---

## 任务 4：修正 `EventStreamHandler` 的 cast case

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`

- [ ] **Step 1: 替换 SkillContent cast case**

编辑 `EventStreamHandler.cs` 第 225 行：

```csharp
case (MessageEventType)AppEventType.SkillContent:
```

改为：

```csharp
case AppEventTypeConstants.SkillContent:
```

（需确认文件顶部已 `using Seeing.Agent.App.Events;`，若无则添加。）

其余 21 个 `case MessageEventType.X:`（第 150-233 行）**无需改动**——`const string` 是合法 switch case label。

- [ ] **Step 2: 构建 WebUI 验证**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs
git commit -m "refactor: EventStreamHandler SkillContent cast case 改字符串常量"
```

---

## 任务 5：修正 `AcpEventMapper`

**Files:**
- Modify: `src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs`

- [ ] **Step 1: 改 `MapToolProgressType` 返回类型 + `ToolCallEvent.Type` 赋值**

编辑 `AcpEventMapper.cs`：

```csharp
private static string MapToolProgressType(string status) =>
    status switch
    {
        "completed" or "failed" => MessageEventType.ToolCallComplete,
        "running" or "in_progress" => MessageEventType.ToolCallRunning,
        _ => MessageEventType.ToolCallPending
    };
```

（返回类型 `MessageEventType` → `string`，`=>` 右侧不变。）

`Map` 中第 28 行 `Type = MessageEventType.ToolCallPending,` 与第 42 行 `Type = MapToolProgressType(progress.Status),` **无需改动**（赋值目标 `ToolCallEvent.Type` 现为 string，`MessageEventType.ToolCallPending` 是 const string）。

- [ ] **Step 2: 构建 + 测试**

Run: `dotnet build src/Seeing.Agent.Acp/Seeing.Agent.Acp.csproj && dotnet test tests/Seeing.Agent.Acp.Tests`
Expected: BUILD SUCCEEDED；TEST PASS（若 `AcpEventMapperTests` 有类型标注需改，一并处理）。

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs
git commit -m "refactor: AcpEventMapper 事件类型字符串化"
```

---

## 任务 6：修正 `TaskEventProjector` / `AgentExecutor` / `BudgetEvents` / `ExecutionJobService`

**Files:**
- Modify: `src/Seeing.Agent/Core/Events/TaskEventProjector.cs`
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Events/BudgetEvents.cs`
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`

- [ ] **Step 1: `TaskEventProjector.cs`**

第 18-20 行 `MessageEventType.ToolCallPending => "tool_pending",` 等 switch 表达式——case 标签类型从枚举改 const string 后**无需改动**（编译期常量 switch 兼容）。若该文件存在 `MessageEventType` 类型标注（非 `case`），改为 `string`。构建验证：

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 2: `AgentExecutor.cs`**

第 568/611/654/689/712/729/743 行 `Type = MessageEventType.ToolCallPending,` 等赋值——`ToolCallEvent.Type` 现为 string，赋值右侧 const string 兼容，**无需改动**。仅当存在 `public MessageEventType Type` 类型标注时改 string。构建验证：

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 3: `BudgetEvents.cs`**

第 15/41/70 行 `public MessageEventType Type => MessageEventType.BudgetStatus;` 等 → `public string Type => MessageEventType.BudgetStatus;`（返回类型改 string，右侧不变）。

Run: `dotnet build src/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 4: `ExecutionJobService.cs`**

第 1052/1065 行 `public MessageEventType Type => MessageEventType.LoopStart;` → `public string Type => MessageEventType.LoopStart;`（返回类型改 string）。

Run: `dotnet build src/Seeing.Agent.App/Seeing.Agent.App.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Core/Events/TaskEventProjector.cs src/Seeing.Agent/Core/AgentExecutor.cs src/Seeing.Agent.TokenBudget/Events/BudgetEvents.cs src/Seeing.Agent.App/Execution/ExecutionJobService.cs
git commit -m "refactor: 事件类型字符串化——TaskEventProjector/AgentExecutor/BudgetEvents/ExecutionJobService"
```

---

## 任务 7：修正 `GatewayEventMapper` / `GatewayEvent`

**Files:**
- Modify: `src/Seeing.Gateway/Mapping/GatewayEventMapper.cs`
- Modify: `src/Seeing.Gateway/Models/GatewayEvent.cs`

- [ ] **Step 1: `GatewayEventMapper.cs`**

- 第 85 行 `SourceType = MessageEventType.PermissionRequest.ToString(),` → `SourceType = MessageEventType.PermissionRequest,`（const string 无需 ToString；`SourceType` 已是 `string?`）。
- 第 101 行 `SourceType = source.Type.ToString()` → `SourceType = source.Type,`（`source.Type` 现为 string）。
- 其余 `source switch` 的 `WithMeta` 调用不受影响。

- [ ] **Step 2: `GatewayEvent.cs`**

第 48 行注释提及 `MessageEventType`，无代码改动（`SourceType` 已是 `string?`）。仅当存在类型标注时改。

- [ ] **Step 3: 构建 + 修正测试 `nameof` 断言与 `MessageEventType` 类型标注**

Run: `dotnet build src/Seeing.Gateway/Seeing.Gateway.csproj`
Expected: BUILD SUCCEEDED。

编辑 `tests/Seeing.Gateway.Tests/Mapping/GatewayEventMapperTests.cs`：

**3a. 三处 `nameof` 断言**（第 36/55/319 行）：
```csharp
result.SourceType.Should().Be(nameof(MessageEventType.LoopStart));
```
改为：
```csharp
result.SourceType.Should().Be(MessageEventType.LoopStart);
```
（第 55 行 `StreamStart`、第 319 行 `PermissionRequest` 同理。需确认测试文件已 `using Seeing.Agent.Abstractions.Events;`。）

**3b.（阻断）理论测试参数类型**：`Map_ToolCallEvents_ShouldMapToContentInProgress`（第 160-165 行）的参数 `MessageEventType eventType` 改为 `string eventType`（枚举变静态类后 `MessageEventType` 不能作参数类型，CS0721）。第 171 行 `Type = eventType` 赋值不变（`ToolCallEvent.Type` 已是 string）；第 160-162 行的 `[InlineData]` 传 `MessageEventType.ToolCallPending` 等本身即合法 const string 特性参数，无需改动。

```csharp
[Theory]
[InlineData(MessageEventType.ToolCallPending)]
[InlineData(MessageEventType.ToolCallRunning)]
[InlineData(MessageEventType.ToolCallComplete)]
public void Map_ToolCallEvents_ShouldMapToContentInProgress(string eventType, ...)
```

- [ ] **Step 4: SourceType 消费者审计与对齐**

`src/Seeing.Gateway/Channels/GatewayAssistantReplyCollector.cs` 第 15 行：
```csharp
public const string LoopCompleteSourceType = "LoopComplete";
```
（第 72 行据此匹配 `GatewayEvent.SourceType`。）此常量与 `WithMeta` 产出的 `"LoopComplete"`（现改后为 `"loop.complete"`）强耦合，必须同步对齐：
```csharp
public const string LoopCompleteSourceType = MessageEventType.LoopComplete;
```
（若 `MessageEventType.LoopComplete` 不可在 const 处引用，则直接写 `"loop.complete"`。）

Run: `rg -n "LoopCompleteSourceType|SourceType" src/Seeing.Gateway src/Seeing.Agent.Gateway samples --glob "*.cs"` 审计所有 SourceType 消费者，确认无其它枚举名耦合。

- [ ] **Step 5: 测试验证**

Run: `dotnet test tests/Seeing.Gateway.Tests`
Expected: TEST PASS。

- [ ] **Step 6: 提交**

```bash
git add src/Seeing.Gateway tests/Seeing.Gateway.Tests
git commit -m "refactor: GatewayEventMapper 事件类型字符串化 + 修正 nameof/类型标注 + SourceType 消费者对齐"
```

---

## 已知不一致（本子项目不统一，记录在案）

- **ChatEventMapper 下划线风格**：`src/Seeing.Agent.Gateway/Core/GatewayEventMapper.cs`（服务器主路径）硬编码 `"loop_complete"`/`"stream_delta"`/`"permission_request"` 等下划线 SourceType，与本子项目产出的点分风格（`"loop.complete"`…）并存于线上。本子项目**不对齐**（避免扩大改动范围），后续子项目或专项任务统一。
- **`GatewayWsFrameSerializerTests.cs:20`**：测试 fixture `SourceType = "StreamDelta"` 为旧枚举名风格，纯测试数据不阻塞，可顺手改 `"stream.delta"`（可选）。
- **`src/Seeing.Gateway/README.md:55,118`**：文档仍描述 `MessageEventType` 为原始类型，纯文档，不阻塞，后续随文档任务更新。

---

## 任务 8：全量构建 + 测试 + 提交

- [ ] **Step 1: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: BUILD SUCCEEDED。若有残留 `MessageEventType` 类型标注编译错误，逐个改为 `string`（`case MessageEventType.X` 与 `=> MessageEventType.X` 写法保留）。

- [ ] **Step 2: 全量测试**

Run: `dotnet test Seeing.Agent.slnx`
Expected: TEST PASS。

- [ ] **Step 3: 提交**

```bash
git add -A
git commit -m "feat: 事件类型字符串化完成（子项目①）"
```

---

## 验收标准

- `MessageEventType` 是 `static class`（非枚举），`IMessageEvent.Type` 是 `string`。
- `AppEventType` 枚举已删除，无 `(MessageEventType)AppEventType.X` cast。
- 插件可通过实现 `IMessageEvent` 返回任意自定义字符串发布事件。
- 全量 `dotnet build` 与 `dotnet test` 通过。
