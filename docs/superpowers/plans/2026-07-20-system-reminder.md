# System Reminder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现通用 System Reminder XML 信封（notice/task 分离），Session 持久化包裹全文；首批接入 Job/Cron、Heartbeat 与后台 Task 完成回调；WebUI 专用展示并可切换原文。

**Architecture:** 在 `Seeing.Agent/Core/Reminders` 提供纯静态 `Wrap`/`TryParse`；调用方在写入 Session / History 前 Wrap 裸文本；`Metadata.reminder=*` 供 UI 快速识别。配置层永不 Wrap。

**Tech Stack:** .NET 10 / C#、xUnit + FluentAssertions、Blazor WebUI 样本

**Spec:** `docs/superpowers/specs/2026-07-20-system-reminder-design.md`

## Global Constraints

- 配置 / 调度 JSON 只存裸 `taskBody`，禁止持久化包裹后的 XML
- Session / LLM History 存包裹后全文；`role` 仍为 `user`
- 转义：仅对 `taskBody` 将 `</` 替换为 `<\/`；Parse 取 `<task>` 内文本后再还原
- `Wrap` 时 `source`/`kind` 为空或空白 → `ArgumentException`；`TryParse` 失败返回 `false` 不抛
- **全程不 commit**（用户明确要求；跳过所有 git commit 步骤）

---

## File Structure

| 文件 | 职责 |
|------|------|
| `src/Seeing.Agent/Core/Reminders/SystemReminder.cs` | Tag / Source / Kind / MetadataKey 常量 |
| `src/Seeing.Agent/Core/Reminders/SystemReminderParts.cs` | 解析结果 record |
| `src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs` | source+kind → notice 文案 |
| `src/Seeing.Agent/Core/Reminders/SystemReminderRenderer.cs` | Wrap / TryParse / CreateUserMessage |
| `tests/Seeing.Agent.Tests/Core/Reminders/SystemReminderRendererTests.cs` | 往返与边界测试 |
| `src/Seeing.Agent.Scheduler/Jobs/AgentJob.cs` | History + Dispatch UserInput Wrap |
| `src/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs` | History + Dispatch UserInput Wrap |
| `src/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs` | 写入 Session 时打 reminder Metadata（Content 已由上游 Wrap） |
| `src/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs` | 同上 |
| `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` | InjectSynthetic 前 Wrap |
| `samples/.../Models/MessageViewModel.cs` | IsSystemReminder / Reminder* 字段 |
| `samples/.../Pages/Session.razor` | Session→ViewModel 解析 |
| `samples/.../Components/SystemReminderMessageItem.razor` | 专用气泡 + 原文切换 |
| `samples/.../Components/SystemReminderMessageItem.razor.css` | 样式 |
| `samples/.../Components/MessageList.razor` | user 分支分流 |

---

### Task 1: SystemReminderRenderer 核心（TDD）

**Files:**
- Create: `src/Seeing.Agent/Core/Reminders/SystemReminder.cs`
- Create: `src/Seeing.Agent/Core/Reminders/SystemReminderParts.cs`
- Create: `src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs`
- Create: `src/Seeing.Agent/Core/Reminders/SystemReminderRenderer.cs`
- Test: `tests/Seeing.Agent.Tests/Core/Reminders/SystemReminderRendererTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `SystemReminderRenderer.Wrap(string taskBody, string source, string kind, string? taskId = null) -> string`
  - `SystemReminderRenderer.TryParse(string content, out SystemReminderParts parts) -> bool`
  - `SystemReminderRenderer.CreateUserMessage(...) -> SessionMessage`（Content 已 Wrap + Metadata）
  - `SystemReminderParts(Source, Kind, Notice, Task, TaskId?, Raw)`
  - 常量：`SystemReminder.Tag`, `Sources.Job/Task`, `Kinds.Cron/Heartbeat/Completed/Failed/Cancelled`, `MetadataKeys.Reminder/Source/Kind/TaskId`

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.Tests/Core/Reminders/SystemReminderRendererTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Core.Reminders;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Core.Reminders;

public class SystemReminderRendererTests
{
    [Fact]
    public void Wrap_Then_TryParse_RoundTrips_Task_Source_Kind_TaskId()
    {
        var wrapped = SystemReminderRenderer.Wrap(
            "每小时检查一次构建状态并汇总。",
            SystemReminder.Sources.Job,
            SystemReminder.Kinds.Cron);

        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Source.Should().Be("job");
        parts.Kind.Should().Be("cron");
        parts.Task.Should().Be("每小时检查一次构建状态并汇总。");
        parts.TaskId.Should().BeNull();
        parts.Notice.Should().NotBeNullOrWhiteSpace();
        parts.Raw.Should().Be(wrapped);
        wrapped.Should().Contain("<notice>");
        wrapped.Should().Contain("<task>");
        wrapped.Should().NotContain("每小时检查一次构建状态并汇总。\n</notice>");
    }

    [Fact]
    public void Wrap_WithTaskId_PreservesAttribute()
    {
        var wrapped = SystemReminderRenderer.Wrap(
            "done",
            SystemReminder.Sources.Task,
            SystemReminder.Kinds.Completed,
            taskId: "abc123");

        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.TaskId.Should().Be("abc123");
        wrapped.Should().Contain("task-id=\"abc123\"");
    }

    [Fact]
    public void Wrap_Escapes_ClosingSlash_In_TaskBody()
    {
        var body = "see </task> and </system-reminder> inside";
        var wrapped = SystemReminderRenderer.Wrap(body, "job", "cron");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Task.Should().Be(body);
        // 信封内原始序列含转义，避免提前闭合
        wrapped.Should().Contain("<\\/task>");
    }

    [Fact]
    public void Wrap_EmptyTaskBody_StillSucceeds()
    {
        var wrapped = SystemReminderRenderer.Wrap("", "job", "cron");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Task.Should().BeEmpty();
    }

    [Fact]
    public void Wrap_Throws_When_Source_Or_Kind_Blank()
    {
        var act1 = () => SystemReminderRenderer.Wrap("x", "", "cron");
        var act2 = () => SystemReminderRenderer.Wrap("x", "job", "  ");
        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParse_ReturnsFalse_For_PlainUserText()
    {
        SystemReminderRenderer.TryParse("hello user", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateUserMessage_Sets_Content_And_Metadata()
    {
        var msg = SystemReminderRenderer.CreateUserMessage(
            "prompt", SystemReminder.Sources.Job, SystemReminder.Kinds.Heartbeat);

        msg.Role.Should().Be(MessageRole.User);
        SystemReminderRenderer.TryParse(msg.Content, out _).Should().BeTrue();
        msg.Metadata.Should().ContainKey(SystemReminder.MetadataKeys.Reminder);
        msg.Metadata![SystemReminder.MetadataKeys.Source].Should().Be("job");
        msg.Metadata[SystemReminder.MetadataKeys.Kind].Should().Be("heartbeat");
    }

    [Fact]
    public void Notices_UnknownKind_Uses_Generic()
    {
        var wrapped = SystemReminderRenderer.Wrap("x", "custom", "weird");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Notice.Should().Contain("系统提醒");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SystemReminderRendererTests" --no-restore
```

Expected: 编译失败（命名空间/类型不存在）或测试失败。

- [ ] **Step 3: 实现核心类型**

`SystemReminder.cs`:

```csharp
namespace Seeing.Agent.Core.Reminders;

public static class SystemReminder
{
    public const string Tag = "system-reminder";

    public static class Sources
    {
        public const string Job = "job";
        public const string Task = "task";
    }

    public static class Kinds
    {
        public const string Cron = "cron";
        public const string Heartbeat = "heartbeat";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    public static class MetadataKeys
    {
        public const string Reminder = "reminder";
        public const string Source = "reminder_source";
        public const string Kind = "reminder_kind";
        public const string TaskId = "reminder_task_id";
    }
}
```

`SystemReminderParts.cs`:

```csharp
namespace Seeing.Agent.Core.Reminders;

public sealed record SystemReminderParts(
    string Source,
    string Kind,
    string Notice,
    string Task,
    string? TaskId = null,
    string Raw = "");
```

`SystemReminderNotices.cs`:

```csharp
namespace Seeing.Agent.Core.Reminders;

public static class SystemReminderNotices
{
    public static string Resolve(string source, string kind) => (source, kind) switch
    {
        (SystemReminder.Sources.Job, SystemReminder.Kinds.Cron) =>
            "定时任务已触发。以下内容是系统注入的指令，不是用户刚刚输入的消息。请按 <task> 中的文本执行。",
        (SystemReminder.Sources.Job, SystemReminder.Kinds.Heartbeat) =>
            "心跳任务已触发。以下内容是系统注入的指令，不是用户刚刚输入的消息。请按 <task> 中的文本执行。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Completed) =>
            "后台任务已完成。以下是任务结果摘要；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Failed) =>
            "后台任务失败。以下是错误摘要；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Cancelled) =>
            "后台任务已取消。以下是取消信息；请根据情况决定是否继续，这不是用户刚刚输入的消息。",
        _ => "系统提醒已注入。以下内容不是用户刚刚输入的消息。请按 <task> 中的文本处理。"
    };
}
```

`SystemReminderRenderer.cs`（完整实现要点）：

```csharp
using System.Text;
using System.Text.RegularExpressions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Reminders;

public static partial class SystemReminderRenderer
{
    private const string EscapeClose = "<\\/";

    public static string Wrap(string taskBody, string source, string kind, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source is required", nameof(source));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind is required", nameof(kind));

        taskBody ??= "";
        var notice = SystemReminderNotices.Resolve(source, kind);
        var escapedTask = EscapeTaskBody(taskBody);

        var sb = new StringBuilder();
        sb.Append('<').Append(SystemReminder.Tag);
        sb.Append(" source=\"").Append(source).Append('"');
        sb.Append(" kind=\"").Append(kind).Append('"');
        if (!string.IsNullOrEmpty(taskId))
            sb.Append(" task-id=\"").Append(taskId).Append('"');
        sb.AppendLine(">");
        sb.AppendLine("<notice>");
        sb.AppendLine(notice);
        sb.AppendLine("</notice>");
        sb.AppendLine("<task>");
        sb.AppendLine(escapedTask);
        sb.AppendLine("</task>");
        sb.Append("</").Append(SystemReminder.Tag).Append('>');
        return sb.ToString();
    }

    public static bool TryParse(string content, out SystemReminderParts parts)
    {
        parts = default!;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var match = ReminderRegex().Match(content);
        if (!match.Success)
            return false;

        var source = match.Groups["source"].Value;
        var kind = match.Groups["kind"].Value;
        var taskId = match.Groups["taskId"].Success ? match.Groups["taskId"].Value : null;
        var notice = match.Groups["notice"].Value.Trim('\r', '\n');
        var task = UnescapeTaskBody(match.Groups["task"].Value.Trim('\r', '\n'));

        parts = new SystemReminderParts(source, kind, notice, task, taskId, content);
        return true;
    }

    public static SessionMessage CreateUserMessage(
        string taskBody, string source, string kind, string? taskId = null)
    {
        var content = Wrap(taskBody, source, kind, taskId);
        return SessionMessage.UserMessage(content)
            .WithMetadata(SystemReminder.MetadataKeys.Reminder, true)
            .WithMetadata(SystemReminder.MetadataKeys.Source, source)
            .WithMetadata(SystemReminder.MetadataKeys.Kind, kind)
            .WithMetadata(SystemReminder.MetadataKeys.TaskId, taskId ?? "");
    }

    private static string EscapeTaskBody(string body) =>
        body.Replace("</", EscapeClose, StringComparison.Ordinal);

    private static string UnescapeTaskBody(string body) =>
        body.Replace(EscapeClose, "</", StringComparison.Ordinal);

    // 允许属性顺序固定为 source、kind、可选 task-id（实现可更宽松）
    [GeneratedRegex(
        @"^<system-reminder\s+source=""(?<source>[^""]+)""\s+kind=""(?<kind>[^""]+)""(?:\s+task-id=""(?<taskId>[^""]+)"")?\s*>\s*<notice>\r?\n?(?<notice>.*?)\r?\n?</notice>\s*<task>\r?\n?(?<task>.*?)\r?\n?</task>\s*</system-reminder>\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ReminderRegex();
}
```

注意：若 `Seeing.Agent` 未引用 `Seeing.Session`，则把 `CreateUserMessage` 放到 Scheduler/App 层，或确认已有 Session 包引用后再放此处。当前 `Seeing.Agent` 已依赖 Session（`AgentLoopScheduler` 使用 `SessionMessage`），可放在 Renderer。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SystemReminderRendererTests"
```

Expected: 全部 PASS。若 Regex 因换行/Trim 失败，收紧或放宽 `ReminderRegex` 直到往返稳定。

- [ ] **Step 5: Skip commit**（用户要求全程不 commit）

---

### Task 2: Scheduler Job 接入（AgentJob + HeartbeatJob + Dispatchers）

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`（History 与 Dispatch 的 UserInput）
- Modify: `src/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs`（同上）
- Modify: `src/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs`
- Modify: `src/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs`
- Test: `tests/Seeing.Agent.Scheduler.Tests/`（新增或扩展现有 Job 测试；若无现成夹具，至少在 Agent.Tests 加纯函数级「Wrap 后字符串形态」断言，并用手工检查调用点）

**Interfaces:**
- Consumes: `SystemReminderRenderer.Wrap` / `CreateUserMessage`
- Produces: Job History `ChatMessage.Content` 与 Dispatch `UserInput` 均为包裹文本；Session 落盘带 Metadata

- [ ] **Step 1: 改 AgentJob History**

在构建 `AgentContext.History` 处，将：

```csharp
new() { Role = ChatRole.User, Content = prompt }
```

改为：

```csharp
new()
{
    Role = ChatRole.User,
    Content = SystemReminderRenderer.Wrap(
        prompt,
        SystemReminder.Sources.Job,
        SystemReminder.Kinds.Cron)
}
```

`using Seeing.Agent.Core.Reminders;`

**不要**修改读取配置里的 `prompt` 存储逻辑。

- [ ] **Step 2: 改 AgentJob Dispatch UserInput**

在 `DispatchAsync(..., userInput, ...)` 调用前，若 `userInput` 来自配置裸 prompt，先 Wrap 再传入：

```csharp
var wrappedInput = string.IsNullOrEmpty(userInput)
    ? userInput
    : SystemReminderRenderer.Wrap(userInput, SystemReminder.Sources.Job, SystemReminder.Kinds.Cron);
await DispatchAsync(data, content, defaultSessionId, wrappedInput, ct);
```

（以文件内实际变量名为准；确保只 Wrap 一次，避免双重包裹。）

- [ ] **Step 3: 改 HeartbeatJob**

对 History 中 `Content = prompt` 与 `UserInput = queryText` 同样 Wrap，`kind = SystemReminder.Kinds.Heartbeat`。

- [ ] **Step 4: Dispatchers 写 Metadata**

`ScheduleDispatchers.cs` 与 `GatewayScheduleDispatcher.cs` 中：

```csharp
var userMessage = SessionMessage.UserMessage(request.UserInput);
```

改为：若 `TryParse(request.UserInput, out var parts)` 成功，则：

```csharp
var userMessage = SessionMessage.UserMessage(request.UserInput!)
    .WithMetadata(SystemReminder.MetadataKeys.Reminder, true)
    .WithMetadata(SystemReminder.MetadataKeys.Source, parts.Source)
    .WithMetadata(SystemReminder.MetadataKeys.Kind, parts.Kind);
if (!string.IsNullOrEmpty(parts.TaskId))
    userMessage = userMessage.WithMetadata(SystemReminder.MetadataKeys.TaskId, parts.TaskId);
```

否则保持原样（兼容非 reminder 的 UserInput）。

- [ ] **Step 5: 验证编译 + 针对性测试**

```bash
dotnet build src/Seeing.Agent.Scheduler
dotnet build src/Seeing.Agent.Gateway
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SystemReminderRendererTests"
```

Expected: 构建成功；Renderer 测试仍 PASS。

可选：在 `tests/Seeing.Agent.Scheduler.Tests` 增加断言「配置字符串不含 `<system-reminder>`，而执行路径 Wrap 结果含标签」——若缺少可测入口，用静态辅助抽取 Wrap 调用以便单测。

- [ ] **Step 6: Skip commit**

---

### Task 3: TaskTool 后台完成注入

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`（约 203–233 行 `InjectSyntheticAsync`）
- Test: `tests/Seeing.Agent.Tests/Tools/` 或现有 Task 相关测试中增加 Wrap 形态断言

**Interfaces:**
- Consumes: `SystemReminderRenderer.Wrap`
- Produces: 注入父会话的 synthetic user 消息 Content 为 reminder 信封；metadata 同时含原有 `task_id`/`state` 与 reminder 键

- [ ] **Step 1: 抽取 taskBody 构建，再 Wrap**

将三处 `InjectSyntheticAsync` 的明文改为：

```csharp
// completed
var taskBody =
    $"Background task completed: {desc}\ntask_id: {childId}\nstate: completed\n\n<task_result>\n{text}\n</task_result>";
var wrapped = SystemReminderRenderer.Wrap(
    taskBody,
    SystemReminder.Sources.Task,
    SystemReminder.Kinds.Completed,
    taskId: childId);
await _loopScheduler.InjectSyntheticAsync(
    parentSessionId,
    wrapped,
    new Dictionary<string, string>
    {
        ["task_id"] = childId,
        ["state"] = "completed",
        [SystemReminder.MetadataKeys.Reminder] = "true",
        [SystemReminder.MetadataKeys.Source] = SystemReminder.Sources.Task,
        [SystemReminder.MetadataKeys.Kind] = SystemReminder.Kinds.Completed,
        [SystemReminder.MetadataKeys.TaskId] = childId
    },
    ct);
```

`cancelled` → `Kinds.Cancelled`；`error` → `Kinds.Failed`。

注意：`InjectSyntheticAsync` 的 metadata 值类型是 `IDictionary<string, string>`，reminder 布尔用 `"true"` 字符串；UI 识别时同时接受 bool/`"true"`。

- [ ] **Step 2: 确认 AgentLoopScheduler 保留调用方 metadata**

已有实现会合并 metadata 并加 `synthetic=true`，无需改逻辑；确认不会覆盖 Content。

- [ ] **Step 3: 跑相关测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~Task"
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SystemReminder"
```

Expected: PASS；若有快照断言旧明文格式，更新为含 `<system-reminder`。

- [ ] **Step 4: Skip commit**

---

### Task 4: WebUI 专用展示 + 原文切换

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`（`ToViewModel` 一带）
- Create: `samples/Seeing.Agent.WebUI/Components/SystemReminderMessageItem.razor`
- Create: `samples/Seeing.Agent.WebUI/Components/SystemReminderMessageItem.razor.css`
- Modify: `samples/Seeing.Agent.WebUI/Components/MessageList.razor`
- 确认 WebUI 项目引用 `Seeing.Agent`（以便 `TryParse`）；若样本不宜引用核心包，可将解析逻辑复制为 WebUI 内轻量 parser，或引用已含类型的包——优先直接引用/传递已解析字段，避免样本再解析失败。

**Interfaces:**
- Consumes: `SystemReminderRenderer.TryParse`、Metadata keys
- Produces: `MessageViewModel.IsSystemReminder`、`ReminderSource`、`ReminderKind`、`ReminderTaskBody`、`ReminderRaw`

- [ ] **Step 1: 扩展 MessageViewModel**

```csharp
public bool IsSystemReminder { get; set; }
public string? ReminderSource { get; set; }
public string? ReminderKind { get; set; }
public string ReminderTaskBody { get; set; } = "";
public string ReminderRaw { get; set; } = "";
```

- [ ] **Step 2: Session.razor 转换时填充**

在设置 `IsCompactionSummary` 附近：

```csharp
var metaReminder = msg.Metadata != null
    && msg.Metadata.TryGetValue("reminder", out var remObj)
    && (remObj is true or "true");

if (SystemReminderRenderer.TryParse(msg.Content ?? "", out var reminderParts))
{
    viewModel.IsSystemReminder = true;
    viewModel.ReminderSource = reminderParts.Source;
    viewModel.ReminderKind = reminderParts.Kind;
    viewModel.ReminderTaskBody = reminderParts.Task;
    viewModel.ReminderRaw = reminderParts.Raw;
}
else if (metaReminder)
{
    // Metadata 声称是 reminder 但 Content 损坏 → 当普通 user（与设计一致）
    viewModel.IsSystemReminder = false;
}
```

需要 `using Seeing.Agent.Core.Reminders;`。若 WebUI 不能引用 Agent，则把 `TryParse` 抽到双方都引用的小库，或在 WebUI 做同等 Regex——优先引用 Agent。

- [ ] **Step 3: 新建 SystemReminderMessageItem.razor**

```razor
@using Seeing.Agent.WebUI.Models

<div class="system-reminder-message">
    <div class="system-reminder-header">
        <span class="system-reminder-badge">系统提醒</span>
        <span class="system-reminder-meta">@Message.ReminderSource · @Message.ReminderKind</span>
        <button type="button" class="system-reminder-toggle" @onclick="Toggle">
            @(_showRaw ? "摘要" : "原文")
        </button>
    </div>
    <div class="system-reminder-body">
        @if (_showRaw)
        {
            <pre class="system-reminder-raw">@Message.ReminderRaw</pre>
        }
        else
        {
            <div class="system-reminder-task">
                @(string.IsNullOrEmpty(Message.ReminderTaskBody) ? "（无任务正文）" : Message.ReminderTaskBody)
            </div>
        }
    </div>
</div>

@code {
    [Parameter] public MessageViewModel Message { get; set; } = default!;
    private bool _showRaw;

    private void Toggle() => _showRaw = !_showRaw;
}
```

CSS（左侧色条 + 浅底，与用户气泡区分）：

```css
.system-reminder-message {
    margin: 8px 0;
    padding: 10px 12px;
    border-left: 3px solid var(--ant-primary-color, #1677ff);
    background: var(--ant-color-fill-quaternary, rgba(0, 0, 0, 0.04));
    border-radius: 0 6px 6px 0;
}
.system-reminder-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 12px;
    opacity: 0.85;
}
.system-reminder-badge {
    font-weight: 600;
}
.system-reminder-toggle {
    margin-left: auto;
    border: none;
    background: transparent;
    color: var(--ant-primary-color, #1677ff);
    cursor: pointer;
    font-size: 12px;
}
.system-reminder-raw {
    white-space: pre-wrap;
    margin: 0;
    font-size: 12px;
}
.system-reminder-task {
    white-space: pre-wrap;
}
```

- [ ] **Step 4: MessageList 分流**

将 `message.Role == "user"` 分支改为：

```razor
@if (message.Role == "user")
{
    @if (message.IsSystemReminder)
    {
        <SystemReminderMessageItem @key="message.Id" Message="@message" />
    }
    else
    {
        <ChatMessageItem @key="message.Id"
                         Message="@message"
                         OnToolClick="@OnToolClickHandler" />
    }
}
```

- [ ] **Step 5: 构建 WebUI**

```bash
dotnet build samples/Seeing.Agent.WebUI
```

Expected: 成功。手动：注入一条 reminder Session 消息，确认默认显示 task 正文、点「原文」显示完整 XML。

- [ ] **Step 6: Skip commit**

---

### Task 5: 回归与规格核对

**Files:** 无新文件；跑测试 + 对照 spec

- [ ] **Step 1: 全量相关测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SystemReminder|FullyQualifiedName~Task"
dotnet build Seeing.Agent.slnx
```

Expected: 构建成功；相关测试 PASS。

- [ ] **Step 2: Spec 覆盖检查（人工）**

对照 `docs/superpowers/specs/2026-07-20-system-reminder-design.md`：

| Spec 项 | Task |
|---------|------|
| XML + notice/task 分离 | Task 1 |
| 配置不存包裹 | Task 2（未改配置存储） |
| Session 存包裹 | Task 2/3 |
| Job cron/heartbeat | Task 2 |
| Task completed/failed/cancelled | Task 3 |
| UI 专用样式 + 原文切换 | Task 4 |
| 转义 `</` | Task 1 |
| 不做 Gateway 出站剥标签 / 多语言 notice | 确认未实现 |

- [ ] **Step 3: Skip commit**

---

## Self-Review (plan author)

1. **Spec coverage:** 通用 Renderer、Job/Heartbeat、Task 注入、WebUI、转义、Metadata 均有对应 Task；YAGNI 项未纳入。
2. **Placeholders:** 无 TBD；正则若过严可在 Task 1 Step 4 调整。
3. **Types:** `SystemReminderParts` / `Wrap` / `TryParse` / MetadataKeys 在后续 Task 与 Task 1 一致；Task 增加 `cancelled` kind（与 TaskTool 三态对齐，属 spec `failed|completed` 的合理扩展）。
4. **Commits:** 全部标记 Skip，符合用户「全程不 commit」。
