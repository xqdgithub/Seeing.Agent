# Agent Todo 强制执行机制优化 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 通过 prompt 工程优化 + 代码级退出检查，使 build agent 在多步任务中能自觉使用 TodoWrite 工具跟踪进度，并确保“承诺即强制”——创建了 todo 就必须闭环。

**Architecture:** 提示词层面重写 build agent 任务管理段和 TodoWrite 工具描述（参考 OpenCode 模式）；代码层面在 AgentExecutor 循环中注入 SystemReminder（不碰 system prompt、缓存友好），退出时检查未完成 todo 并拦截一次。

**Tech Stack:** C# / .NET 10, Blazor (AntDesign), xUnit

---

## 文件结构

| 文件 | 职责 | 操作 |
|------|------|------|
| `src/Seeing.Agent/Core/Todo/TodoItem.cs` | `TodoStatus` enum（唯一定义）、`TodoItem`、`TodoList` | 修改 |
| `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs` | TodoWrite/TodoRead 工具实现 | 修改 |
| `src/Seeing.Agent/Core/Todo/TodoListExtensions.cs` | `TodoList` 查询/格式化扩展方法 | 创建 |
| `src/Seeing.Agent/Core/Reminders/SystemReminder.cs` | `Sources`、`Kinds` 常量 | 修改 |
| `src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs` | notice 文案匹配 | 修改 |
| `src/Seeing.Agent/Core/AgentExecutor.cs` | Agent 核心循环（注入 + 退出检查） | 修改 |
| `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | build agent 提示词 | 修改 |
| `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` | using 声明 | 修改 |
| `src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs` | ACP 事件映射 | 修改 |
| `samples/Seeing.Agent.WebUI/Models/TodoViewModel.cs` | 前端 Todo 视图模型 | 修改 |
| `samples/Seeing.Agent.WebUI/Components/TodoCard.razor` | Todo 卡片 UI 组件 | 修改 |
| `tests/Seeing.Agent.Tests/` | 单元测试 | 创建/修改 |

---

### Task 1: 统一 TodoStatus — Core.Todo 新增 Paused，删除 Tools.BuiltIn.Todo 重复定义

**Files:**
- Modify: `src/Seeing.Agent/Core/Todo/TodoItem.cs:8-17`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs:12-22,40-50,162-169,302`
- Modify: `src/Seeing.Agent/Core/Events/MessageEventTypes.cs:1`
- Modify: `src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs:194-201`

- [ ] **Step 1: Core.Todo.TodoStatus 新增 Paused**

```csharp
// src/Seeing.Agent/Core/Todo/TodoItem.cs:8-17 替换为：
public enum TodoStatus
{
    /// <summary>待处理</summary>
    Pending,
    /// <summary>进行中</summary>
    InProgress,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>已取消</summary>
    Cancelled,
    /// <summary>等待用户回复，暂停执行</summary>
    Paused
}
```

- [ ] **Step 2: 删除 TodoWriteTool.cs 中的 TodoStatus/TodoItem 重复定义，添加 using**

删除第 12-22 行的 `TodoStatus` enum、第 40-50 行的 `TodoItem` class 和第 27-35 行的 `TodoPriority` enum。在文件头部添加：

```csharp
using Seeing.Agent.Core.Todo;
```

- [ ] **Step 3: TodoWriteTool.TodoItem 改为引用 Core.Todo.TodoStatus**

第 46 行 `public TodoStatus Status` 现在引用 `Core.Todo.TodoStatus`，无需改动类型名。但 `TodoWriteTool` 自身的 `TodoItem` 类需要改为直接引用 `Core.Todo.TodoStatus`。

验证：文件内 `TodoStatus.` 引用全部来自 `Core.Todo.TodoStatus`。

- [ ] **Step 4: TodoWriteTool.ParseStatus 新增 "paused" 映射**

```csharp
// 第 163-170 行，switch 表达式新增：
private static TodoStatus ParseStatus(string status)
{
    return status.ToLowerInvariant() switch
    {
        "pending" => TodoStatus.Pending,
        "in_progress" => TodoStatus.InProgress,
        "completed" => TodoStatus.Completed,
        "cancelled" => TodoStatus.Cancelled,
        "paused" => TodoStatus.Paused,
        _ => TodoStatus.Pending
    };
}
```

- [ ] **Step 5: TodoWriteTool.ParametersSchema 的 status enum 新增 "paused"**

在 `BuildObjectSchema` 方法中（第 223 行），`status` 的 `enum` 数组新增 `"paused"`：

```csharp
["enum"] = new[] { "pending", "in_progress", "completed", "cancelled", "paused" },
```

- [ ] **Step 6: TodoReadTool 计数逻辑更新**

第 109 行和第 302 行，`t.Count(t => t.Status != TodoStatus.Completed)` 需要更新：`paused` 和 `cancelled` 不应计入待处理。改为：

```csharp
var pendingCount = todos.Count(t => t.Status == TodoStatus.Pending || t.Status == TodoStatus.InProgress);
```

- [ ] **Step 7: MessageEventTypes.cs using 替换**

第 1 行：

```csharp
// 替换
using Seeing.Agent.Tools.BuiltIn.Todo;
// 为
using Seeing.Agent.Core.Todo;
```

`TodoUpdateEvent` (第 489 行) 引用的 `TodoItem` 现在来自 `Core.Todo.TodoItem`。注意 `Tools.BuiltIn.Todo.TodoItem` 和 `Core.Todo.TodoItem` 结构不同（一个简化、一个完整含 Id/CreatedAt），需要确认 `TodoUpdateEvent.Todos` 使用 `Core.Todo.TodoItem` 后调用方仍兼容。

- [ ] **Step 8: AcpEventMapper.cs 更新引用**

第 5 行 `using Seeing.Agent.Tools.BuiltIn.Todo;` — 如果还引用了 `TodoItem` 或 `TodoPriority` 等类型，需要添加/替换为 `using Seeing.Agent.Core.Todo;`。

`MapPlanStatus()` 方法（第 194-201 行）保持逻辑不变，因为 `Core.Todo.TodoStatus` 值完全一致（0-3）：

```csharp
private static TodoStatus MapPlanStatus(string status) =>
    status?.ToLowerInvariant() switch
    {
        "completed" => TodoStatus.Completed,
        "in_progress" => TodoStatus.InProgress,
        "cancelled" => TodoStatus.Cancelled,
        _ => TodoStatus.Pending
    };
```

- [ ] **Step 9: 编译验证**

```bash
dotnet build
```
Expected: 无编译错误，所有 `TodoStatus` 引用解析到 `Core.Todo.TodoStatus`

- [ ] **Step 10: Commit**

```bash
git add src/Seeing.Agent/Core/Todo/TodoItem.cs src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs src/Seeing.Agent/Core/Events/MessageEventTypes.cs src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs
git commit -m "refactor: unify TodoStatus to Core.Todo, add Paused, remove duplicate enum

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 新增 TodoListExtensions 扩展方法

**Files:**
- Create: `src/Seeing.Agent/Core/Todo/TodoListExtensions.cs`

- [ ] **Step 1: 创建扩展方法文件**

```csharp
// src/Seeing.Agent/Core/Todo/TodoListExtensions.cs
using System.Text;

namespace Seeing.Agent.Core.Todo;

public static class TodoListExtensions
{
    public static bool IsEmpty(this TodoList list) =>
        list.Items.Count == 0;

    public static bool HasIncompletePendingOrInProgress(this TodoList list) =>
        list.Items.Any(t => t.Status is TodoStatus.Pending or TodoStatus.InProgress);

    public static bool HasPaused(this TodoList list) =>
        list.Items.Any(t => t.Status == TodoStatus.Paused);

    public static string FormatBrief(this TodoList list)
    {
        if (list.Items.Count == 0)
            return "（无任务）";

        var sb = new StringBuilder();
        foreach (var item in list.Items.OrderBy(t => t.Status))
        {
            var statusMark = item.Status switch
            {
                TodoStatus.Completed => "[✔]",
                TodoStatus.InProgress => "[▶]",
                TodoStatus.Pending => "[ ]",
                TodoStatus.Cancelled => "[✗]",
                TodoStatus.Paused => "[⏸]",
                _ => "[ ]"
            };
            sb.AppendLine($"  {statusMark} {item.Content}");
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 编译通过

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Core/Todo/TodoListExtensions.cs
git commit -m "feat: add TodoList query and formatting extension methods

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: SystemReminder 扩展 — 新增 Agent source 和 Todo Kinds

**Files:**
- Modify: `src/Seeing.Agent/Core/Reminders/SystemReminder.cs`
- Modify: `src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs`

- [ ] **Step 1: SystemReminder.cs 新增常量**

```csharp
// Sources 类新增：
public const string Agent = "agent";

// Kinds 类新增：
public const string TodoEmpty = "todo_empty";
public const string TodoIncomplete = "todo_incomplete";
public const string TodoPaused = "todo_paused";
```

- [ ] **Step 2: SystemReminderNotices.cs 新增三条 notice**

```csharp
// Resolve 方法的 switch 表达式新增三条 case：
(SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoEmpty) =>
    "你已执行多步操作但尚未创建 todo 列表。如果当前任务涉及 2 个或以上独立步骤，请使用 TodoWrite 工具规划。",
(SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoIncomplete) =>
    "你有未完成的 todo 任务。必须将所有 todo 标记为 completed、cancelled 或 paused 后才能结束。",
(SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoPaused) =>
    "你有处于暂停状态的任务。请检查并恢复需要继续的任务。",
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 编译通过

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Core/Reminders/SystemReminder.cs src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs
git commit -m "feat: add Agent source and TodoEmpty/TodoIncomplete/TodoPaused kinds to SystemReminder

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: AgentExecutor — 注入与退出检查逻辑

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`

- [ ] **Step 1: 新增 Todo 辅助方法和状态字段**

在 `AgentExecutor` 类中添加字段和辅助方法：

```csharp
// 新增字段（在现有字段附近）
private bool _todoEmptyReminded;
private bool _incompleteReminded;
private int _totalToolCallsExecuted;

// 新增辅助方法：从 Session Context 加载 Todo 列表
private static TodoList LoadTodoList(Seeing.Session.Core.ISessionManager? sessionManager, string sessionId)
{
    var session = sessionManager?.Get(sessionId);
    var items = session?.GetContext<List<TodoItem>>(TodoWriteTool.TodoContextKey) 
        ?? new List<TodoItem>();
    return new TodoList { SessionId = sessionId, Items = items };
}

// 新增方法：构建 SystemReminder 注入消息
private static ChatMessage? BuildTodoReminderMessage(
    string kind, string taskBody, string? taskId = null)
{
    var content = SystemReminderRenderer.Wrap(
        taskBody,
        SystemReminder.Sources.Agent,
        kind,
        taskId);
    return new ChatMessage { Role = ChatRole.User, Content = content };
}
```

注意：`TodoWriteTool.TodoContextKey` 是 `private const`，需要改为 `public const` 或 `internal const`：

```csharp
// TodoWriteTool.cs 第 57 行
internal const string TodoContextKey = "todos";
```

- [ ] **Step 2: 循环开始 — 检测 paused todo（注入 TodoPaused）**

在 `ExecuteAsync` 的 `for` 循环体（`step` 循环）开头，`StreamStart` 事件之前添加：

```csharp
// === 循环开始检查：遗留 paused todo ===
if (step == 0 && _sessionManager != null && !string.IsNullOrEmpty(context.SessionId))
{
    var todoList = LoadTodoList(_sessionManager, context.SessionId);
    if (todoList.HasPaused())
    {
        var reminder = BuildTodoReminderMessage(
            SystemReminder.Kinds.TodoPaused,
            todoList.FormatBrief());
        if (reminder != null)
            messages.Add(reminder);
    }
}
```

- [ ] **Step 3: 每轮 LLM 调用前 — 检测 todo 是否为空（注入 TodoEmpty）**

在 `BuildRequestAsync` 调用之前添加：

```csharp
// === TodoEmpty 检查：已执行多步但未创建 todo ===
if (step >= 3 && _totalToolCallsExecuted >= 3 && !_todoEmptyReminded 
    && _sessionManager != null && !string.IsNullOrEmpty(context.SessionId))
{
    var todoList = LoadTodoList(_sessionManager, context.SessionId);
    if (todoList.IsEmpty())
    {
        var reminder = BuildTodoReminderMessage(
            SystemReminder.Kinds.TodoEmpty,
            "请评估当前任务是否需要使用 TodoWrite 规划。");
        if (reminder != null)
            messages.Add(reminder);
        _todoEmptyReminded = true;
    }
}
```

- [ ] **Step 4: 工具调用计数**

在 `ExecuteToolCallsAsync` 调用前添加：

```csharp
_totalToolCallsExecuted += assistantMessage.ToolCalls.Count;
```

- [ ] **Step 5: 退出检查 — 检测未完成 todo（注入 TodoIncomplete）**

在现有「无工具调用则结束」分支（第 351-364 行）替换为：

```csharp
// 无工具调用则检查 todo 状态
if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
{
    // === 退出检查：未完成 todo ===
    if (_sessionManager != null && !string.IsNullOrEmpty(context.SessionId))
    {
        var todoList = LoadTodoList(_sessionManager, context.SessionId);
        if (todoList.HasIncompletePendingOrInProgress())
        {
            if (!_incompleteReminded)
            {
                var reminder = BuildTodoReminderMessage(
                    SystemReminder.Kinds.TodoIncomplete,
                    todoList.FormatBrief());
                if (reminder != null)
                    messages.Add(reminder);
                _incompleteReminded = true;
                continue; // 不退出，再给一轮
            }
            // 第二次仍然未完成 → 信任 agent，允许退出
        }
    }
    
    // 发布 LoopComplete 事件（成功）
    yield return new LoopCompleteEvent
    {
        SessionId = context.SessionId,
        LoopId = loopId,
        TotalSteps = totalSteps,
        Duration = DateTime.Now - loopStartTime,
        Success = true,
        Usage = totalUsage
    };
    yield break;
}
```

- [ ] **Step 6: 编译验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 编译通过

- [ ] **Step 7: Commit**

```bash
git add src/Seeing.Agent/Core/AgentExecutor.cs src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs
git commit -m "feat: add todo reminder injection and exit check in AgentExecutor

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 单元测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Core/Todo/TodoListExtensionsTests.cs`
- Create: `tests/Seeing.Agent.Tests/Core/Reminders/SystemReminderNoticesTests.cs`
- Create: `tests/Seeing.Agent.Tests/Core/AgentExecutorTodoTests.cs`

- [ ] **Step 1: TodoListExtensions 单元测试**

```csharp
// tests/Seeing.Agent.Tests/Core/Todo/TodoListExtensionsTests.cs
using Seeing.Agent.Core.Todo;
using Xunit;

namespace Seeing.Agent.Tests.Core.Todo;

public class TodoListExtensionsTests
{
    [Fact]
    public void IsEmpty_ReturnsTrue_WhenNoItems()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem>() };
        Assert.True(list.IsEmpty());
    }

    [Fact]
    public void IsEmpty_ReturnsFalse_WhenHasItems()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "test", Status = TodoStatus.Pending }
        }};
        Assert.False(list.IsEmpty());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_DetectsPending()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "t1", Status = TodoStatus.Pending }
        }};
        Assert.True(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_DetectsInProgress()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "t1", Status = TodoStatus.InProgress }
        }};
        Assert.True(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_IgnoresCompletedCancelledPaused()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "t1", Status = TodoStatus.Completed },
            new() { Content = "t2", Status = TodoStatus.Cancelled },
            new() { Content = "t3", Status = TodoStatus.Paused }
        }};
        Assert.False(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasPaused_ReturnsTrue_WhenPausedExists()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "t1", Status = TodoStatus.Paused }
        }};
        Assert.True(list.HasPaused());
    }

    [Fact]
    public void HasPaused_ReturnsFalse_WhenNoPaused()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "t1", Status = TodoStatus.Pending }
        }};
        Assert.False(list.HasPaused());
    }

    [Fact]
    public void FormatBrief_IncludesAllStatusMarks()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> { 
            new() { Content = "pending task", Status = TodoStatus.Pending },
            new() { Content = "in progress task", Status = TodoStatus.InProgress },
            new() { Content = "done task", Status = TodoStatus.Completed },
            new() { Content = "cancelled task", Status = TodoStatus.Cancelled },
            new() { Content = "waiting task", Status = TodoStatus.Paused },
        }};
        var result = list.FormatBrief();
        Assert.Contains("[ ]", result);
        Assert.Contains("[▶]", result);
        Assert.Contains("[✔]", result);
        Assert.Contains("[✗]", result);
        Assert.Contains("[⏸]", result);
    }
}
```

- [ ] **Step 2: SystemReminderNotices 单元测试**

```csharp
// tests/Seeing.Agent.Tests/Core/Reminders/SystemReminderNoticesTests.cs
using Seeing.Agent.Core.Reminders;
using Xunit;

namespace Seeing.Agent.Tests.Core.Reminders;

public class SystemReminderNoticesTests
{
    [Fact]
    public void Resolve_Agent_TodoEmpty()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoEmpty);
        Assert.Contains("TodoWrite", notice);
    }

    [Fact]
    public void Resolve_Agent_TodoIncomplete()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoIncomplete);
        Assert.Contains("todo", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Agent_TodoPaused()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoPaused);
        Assert.Contains("暂停", notice);
    }

    [Fact]
    public void Resolve_Unknown_FallsBack()
    {
        var notice = SystemReminderNotices.Resolve("unknown", "unknown");
        Assert.NotEmpty(notice);
    }
}
```

- [ ] **Step 3: TodoWriteTool 解析 paused 状态测试**

```csharp
// tests/Seeing.Agent.Tests/Tools/TodoWriteToolTests.cs（追加到现有测试类或新建）
[Fact]
public void ParseStatus_RecognizesPaused()
{
    // 测试通过 System.Text.Json 反序列化 paused 字符串
    var status = TodoStatus.Paused;
    Assert.NotEqual(TodoStatus.Pending, status);
    Assert.NotEqual(TodoStatus.InProgress, status);
}
```

- [ ] **Step 4: 运行全部测试**

```bash
dotnet test
```
Expected: 所有新增和已有测试通过

- [ ] **Step 5: Commit**

```bash
git add tests/
git commit -m "test: add TodoListExtensions, SystemReminderNotices, and TodoWriteTool paused tests

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: 重写 Build Agent 提示词

**Files:**
- Modify: `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs:90-113`

- [ ] **Step 1: 替换「任务管理 **必须**（不可协商）」段**

将第 90-113 行的 `## 任务管理 **必须**（不可协商）` 整段替换为：

```csharp
## 任务管理

使用 TodoWrite/TodoRead 工具跟踪和规划任务。主动使用以展示进展给用户。

**何时使用：**
- 复杂多步任务（3 步以上）
- 用户明确提供了多个任务（编号或逗号分隔）
- 收到新指令后立即捕获为 todo

**何时不用：**
- 单个简单任务
- 纯问答 / 信息性请求

**任务状态：**
- pending → in_progress（一次只能一个）→ completed
- 需要等待用户回复时标记为 paused，获得回复后恢复
- 不需要的任务标记为 cancelled

**关键规则：**
- 完成后立即标记 completed，不要批量
- 发现新任务必须添加
- 结束前确保所有 todo 为 completed、cancelled 或 paused
- paused 的 todo 会在下次用户回复时提醒你继续

**示例：**
用户：「帮我添加深色模式并运行测试」
助手：先创建 todo：1. 添加深色模式切换 2. 更新组件 3. 运行测试。然后开始执行并逐步标记状态。

用户：「git status 做什么的？」
助手：显示工作区和暂存区状态。（不使用 todo）
""",
```

- [ ] **Step 2: 同时更新 general agent 的提示词（第 360-393 行）**

将 general agent 中「## 任务管理（不可协商）」段替换为类似内容（保持精简，general 是子代理）：

```csharp
## 任务管理

使用 TodoWrite 工具跟踪进度。对 3 步以上任务创建 todo，完成立即标记 completed。需要等待用户时标记 paused。结束时确保所有 todo 为 completed、cancelled 或 paused。
""",
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 语法无误，内联字符串未破坏编译

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs
git commit -m "feat: rewrite build and general agent task management prompts with examples and paused state

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 增强 TodoWrite 工具描述

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs:72-75`

- [ ] **Step 1: 替换 Description**

```csharp
public override string Description =>
    "使用此工具创建和管理结构化任务列表，帮助跟踪进度、组织复杂任务。" +
    "## 何时使用" +
    "- 复杂多步任务（3 步以上）" +
    "- 用户提供多个任务（编号或逗号分隔）" +
    "- 收到新指令后立即捕获为 todo" +
    "## 何时不用" +
    "- 单个简单任务" +
    "- 纯问答 / 信息性请求" +
    "- 可在 3 步内完成的小任务" +
    "## 任务状态" +
    "- pending: 未开始" +
    "- in_progress: 当前进行中（一次只能一个）" +
    "- completed: 已完成" +
    "- cancelled: 不需要了" +
    "- paused: 等待用户回复，暂停执行" +
    "## 规则" +
    "- 完成后立即标记 completed，不要批量" +
    "- 一次只保持一个 in_progress" +
    "- 需要等待用户时标记 paused" +
    "- 发现新任务立即添加" +
    "有疑问时，使用此工具。主动管理任务体现你的专注度。";
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 编译通过

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs
git commit -m "feat: enhance TodoWrite tool description with usage guide, examples, and paused state

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 8: WebUI Todo 渲染支持 paused 状态

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Models/TodoViewModel.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/TodoCard.razor`

- [ ] **Step 1: TodoViewModel.cs — TodoStatusViewModel 新增 Paused、更新 UI 属性**

`TodoStatusViewModel` enum 新增：

```csharp
Paused,  // 新增：等待用户回复
```

更新 UI 属性（switch 表达式新增 case）：

```csharp
// StatusIcon
TodoStatusViewModel.Paused => "⏸",

// StatusColor
TodoStatusViewModel.Paused => "var(--color-text-secondary)",

// StatusTagColor
TodoStatusViewModel.Paused => "warning",

// StatusText
TodoStatusViewModel.Paused => "暂停中",

// IsCurrentTask 不变（Paused 不是 InProgress，无需特殊处理）
```

更新 `ParseStatus` 方法（第 288-298 行）新增：

```csharp
"paused" => TodoStatusViewModel.Paused,
```

更新统计属性（`TodoListViewModel`）：

```csharp
public int PausedCount => Items.Count(i => i.Status == TodoStatusViewModel.Paused);
```

更新 `ProgressPercent` — paused 不计入分母：

```csharp
public double ProgressPercent => TotalCount > 0 
    ? Math.Round(CompletedCount * 100.0 / (TotalCount - CancelledCount - PausedCount), 1) 
    : 0;
```

- [ ] **Step 2: TodoCard.razor — 统计信息新增 paused、样式**

在统计信息区域（第 56-61 行 cancelled 统计之后）新增：

```razor
@if (TodoList.PausedCount > 0)
{
    <span class="todo-stat-item todo-stat-paused">
        ⏸ @(TodoList.PausedCount) 暂停中
    </span>
}
```

在 `GetItemClass` 方法中新增 paused 样式类：

```csharp
if (item.Status == TodoStatusViewModel.Paused)
    classes.Add("todo-item-paused");
```

- [ ] **Step 3: 添加 paused 的 CSS 样式**

在 WebUI 的 CSS 文件中（如 `wwwroot/css/app.css` 或组件级样式），新增：

```css
.todo-item-paused {
    opacity: 0.7;
    font-style: italic;
}

.todo-stat-paused {
    color: var(--color-text-secondary);
}
```

- [ ] **Step 4: 编译 + 运行验证**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 编译通过

手动启动 WebUI，触发 todo 创建场景，验证 paused 状态卡片正常渲染。

- [ ] **Step 5: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Models/TodoViewModel.cs samples/Seeing.Agent.WebUI/Components/TodoCard.razor
git commit -m "feat: add paused state support for TodoCard UI rendering

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 9: 集成验证

- [ ] **Step 1: 运行全套测试**

```bash
dotnet test
```
Expected: 全部通过

- [ ] **Step 2: 运行完整构建**

```bash
dotnet build
```
Expected: 无错误、无警告

- [ ] **Step 3: 手动验证 Todo 循环场景**

启动 WebUI：
```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

场景 A — 多步任务：发送「帮我给 src/SessionManager.cs 添加一个日志方法并写单元测试」
- 预期：agent 在几步后创建 todo；完成后标记所有 completed

场景 B — 暂停场景：发送「我想重构 UserService，你先给我分析一下现在的结构，等我确认后再动手」
- 预期：agent 分析完后将待实现的任务标记 paused，等下一轮用户确认后恢复

场景 C — 未完成退出：发送「帮我重构 UserService 和 AuthService」，agent 创建 todo 后尝试提前结束
- 预期：看到 TodoIncomplete reminder，agent 被拦截一次

场景 D — 简单问题：发送「ls 命令是做什么的」
- 预期：不使用 todo，直接回答，无干扰

---

## Self-Review

**1. Spec coverage check:**

| Spec 章节 | 对应 Task |
|-----------|----------|
| 1. SystemReminder 扩展 (1.1-1.3) | Task 3 |
| 2. 统一 TodoStatus (2.1-2.4) | Task 1 |
| 3. 注入时机与内容 (3.1-3.3) | Task 4 |
| 4. AgentExecutor 改动 (4.1-4.3) | Task 4 |
| 5. Build Agent 提示词改写 | Task 6 |
| 6. TodoWrite 工具描述增强 | Task 7 |
| 7. Todo 查询辅助方法 | Task 2 |
| 8. WebUI Todo 渲染 | Task 8 |
| 9. 测试 | Task 5 |

**2. Placeholder scan:** 无 TBD/TODO，所有步骤包含具体代码。

**3. Type consistency:**
- `TodoStatus` 统一来自 `Core.Todo`
- `TodoListExtensions` 方法名与 AgentExecutor 引用一致
- `SystemReminder.Kinds.TodoEmpty/TodoIncomplete/TodoPaused` 与 Notices 匹配
- `TodoContextKey` 已从 `private` 改为 `internal` 供 AgentExecutor 使用
- WebUI `TodoStatusViewModel.Paused` 整数值与后端 `Core.Todo.TodoStatus.Paused` 一致（均为 4）
