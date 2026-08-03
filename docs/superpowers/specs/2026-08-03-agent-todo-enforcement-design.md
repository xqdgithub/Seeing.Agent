# Agent Todo 强制执行机制优化

## 问题描述

内置 build agent 的提示词中明确要求「对于多步任务必须先创建 todo」，但实际运行中，只要用户不明确要求使用 todo 工具，agent 基本不会遵循该指令。

**根因分析**：

1. **提示词设计问题**：强制语气（「必须」「不可协商」）但没有解释「何时用/何时不用」；缺少具体示例；「简洁直接」的基调与「先规划再执行」矛盾
2. **TodoWrite 工具描述过短**：只有一句话简介，缺少使用指南
3. **无退出约束**：agent 创建了 todo 但不标记完成也可以正常退出，prompt 中的「终止条件」形同虚设

## 设计目标

- Prompt 层面：将错误率降至可接受范围
- 代码层面：承诺即强制——agent 创建了 todo 就必须闭环；未创建 todo 时温和提醒
- 不触碰 system prompt（保证 LLM 缓存命中），所有动态提醒走独立的 `<system-reminder>` 注入路径

## 架构概览

```
┌─────────────────────────────────────────────────┐
│                Prompt 层面                        │
│  ① 重写 build agent「任务管理」提示词              │
│  ② 增强 TodoWrite 工具描述                       │
├─────────────────────────────────────────────────┤
│                代码层面                           │
│  ③ 扩展 SystemReminder（新增 Agent source +      │
│     TodoEmpty / TodoIncomplete / TodoPaused kind）│
│  ④ AgentExecutor 注入与退出检查逻辑               │
│  ⑤ TodoStatus 新增 Paused 状态                   │
├─────────────────────────────────────────────────┤
│                UI 层面                            │
│  ⑥ Todo 渲染组件支持 paused 状态显示              │
└─────────────────────────────────────────────────┘
```

## 1. SystemReminder 扩展

### 1.1 新增 Source 和 Kind

`src/Seeing.Agent/Core/Reminders/SystemReminder.cs`：

```csharp
public static class Sources
{
    public const string Job = "job";
    public const string Task = "task";
    public const string Agent = "agent";  // 新增：Agent 循环内提醒
}

public static class Kinds
{
    // ... 保留现有
    public const string TodoEmpty = "todo_empty";          // 新增
    public const string TodoIncomplete = "todo_incomplete"; // 新增
    public const string TodoPaused = "todo_paused";        // 新增
}
```

### 1.2 新增 Notice 文案

`src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs`：

```csharp
(Agent, TodoEmpty) =>
    "你已执行多步操作但尚未创建 todo 列表。如果当前任务涉及 2 个或以上独立步骤，请使用 TodoWrite 工具规划。",
(Agent, TodoIncomplete) =>
    "你有未完成的 todo 任务。必须将所有 todo 标记为 completed、cancelled 或 paused 后才能结束。",
(Agent, TodoPaused) =>
    "你有处于暂停状态的任务。请检查并恢复需要继续的任务。",
```

### 1.3 注入方式

沿用现有 `SystemReminderRenderer.Wrap()` + 合成用户消息加入对话历史（与 TaskTool 一致），**不修改 system prompt**，保证缓存命中。

## 2. 统一 TodoStatus，删除 Tools.BuiltIn.Todo 中的重复定义

### 问题

项目中存在两套并行的 Todo 类型：

| 命名空间 | TodoStatus | TodoItem | 引用方 |
|----------|-----------|----------|--------|
| `Core.Todo` | ✅ 有（4 值） | ✅ 完整（含 Id/CreatedAt） | TodoManager、ITodoManager |
| `Tools.BuiltIn.Todo` | ⚠️ 重复（4 值） | ⚠️ 简化（Content/Status/Priority） | TodoWriteTool、TodoReadTool、AcpEventMapper、MessageEventTypes |

### 统一方案

删除 `Tools.BuiltIn.Todo.TodoStatus`，全部引用统一到 `Core.Todo.TodoStatus`。

### 2.1 Core.Todo.TodoStatus 新增 Paused

`src/Seeing.Agent/Core/Todo/TodoItem.cs`：

```csharp
public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled,
    Paused  // 新增：等待用户回复，暂停执行
}
```

### 2.2 删除 Tools.BuiltIn.Todo.TodoStatus

移除 `TodoWriteTool.cs` 第 12-22 行的重复 enum 定义。

### 2.3 引用方更新

`src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`：
- 删除 `TodoStatus` enum
- `TodoItem.Status` 类型改为 `Core.Todo.TodoStatus`
- 添加 `using Seeing.Agent.Core.Todo;`

`src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs`：
- `MapPlanStatus()` 引用改为 `Core.Todo.TodoStatus`
- 现有 `using Seeing.Agent.Tools.BuiltIn.Todo;` 已引了该命名空间，可能需要调整 using

`src/Seeing.Agent/Core/Events/MessageEventTypes.cs`：
- `using Seeing.Agent.Tools.BuiltIn.Todo;` 替换为 `using Seeing.Agent.Core.Todo;`

### 2.4 TodoManager 适配 Paused 状态

`TodoManager.UpdateStatusAsync()` 中 `Paused` 状态不应设置/清除 `CompletedAt`——当前逻辑已正确处理（只有 Completed/Cancelled 才设置完成时间，从这两个状态切换走才清除）。无需改动。

## 3. 注入时机与内容

核心原则：**只在有意义时注入，避免无效打扰**。

### 3.1 生命周期状态机

```
用户发消息 → [循环开始检查]
    |
    ├─ 有 paused todo? ──是──→ 注入 TodoPaused → 继续
    │
    ↓
[每轮 LLM 调用前]
    |
    ├─ step≥4 ∧ 累计工具调用≥3 ∧ todo 仍为空 ∧ 未提醒过?
    │   ──是──→ 注入 TodoEmpty（仅一次）→ 继续
    │
    ↓
[LLM 返回]
    |
    ├─ 有工具调用 → 继续循环
    ├─ 无工具调用 → [退出检查]
    │
    ↓
[退出检查]
    |
    ├─ 全部 ∈ {completed, cancelled, paused}? ──是──→ 退出 ✅
    ├─ 首次检测到 pending/in_progress? ──是──→ 注入 TodoIncomplete → 继续循环
    └─ 已提醒过一次? ──是──→ 信任 agent，退出 ✅
```

### 3.2 四种注入场景详情

| 场景 | 触发条件 | 注入内容 | 目的 |
|------|----------|----------|------|
| **TodoPaused** | 新循环开始，检测到 paused todo | 「你有 N 个暂停的任务，请恢复并完成」+ 列表 | 唤醒暂停任务 |
| **TodoEmpty** | step≥4 且累计≥3 次工具调用，todo 仍为空（每会话仅一次） | 「你已执行多步但未创建 todo，如有必要请使用 TodoWrite」 | 提醒规划 |
| **TodoIncomplete** | LLM 准备退出但有 pending/in_progress（首次拦截） | 「你有 N 个未完成任务，请标记 completed/cancelled/paused」+ 列表 | 防止遗弃 |
| **（不注入）** | step<4、简单对话、正常工具调用中、已提醒过 | — | 避免噪音 |

### 3.3 TodoEmpty 触发条件的合理性

- `step≥4`：给予 agent 充分时间判断任务复杂度
- `累计工具调用≥3`：确保确实是多步操作场景
- `仅一次`：避免反复骚扰

## 4. AgentExecutor 改动

### 4.1 循环开始

`ExecuteAsync` 方法中，在 `for` 循环体开始处：

```csharp
// 第一轮（step==0）：检查上一会话遗留的 paused todo
// 后续轮次：跳过（已在第一轮处理）
if (step == 0)
{
    var reminder = BuildLoopStartReminder(context.SessionId);
    if (reminder != null)
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = reminder });
}
```

### 4.2 每轮调用前（TodoEmpty 检查）

```csharp
// 在 BuildRequestAsync 之前
if (step >= 3 && totalToolCallsExecuted >= 3 && !todoEmptyReminded)
{
    var todos = await LoadTodos(context.SessionId);
    if (todos.IsEmpty)
    {
        var reminder = SystemReminderRenderer.Wrap(
            "当前任务可能较复杂，考虑使用 TodoWrite 规划。",
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoEmpty);
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = reminder });
        todoEmptyReminded = true;
    }
}
```

### 4.3 退出检查

在 `assistantMessage.ToolCalls == null || count == 0` 分支中：

```csharp
var todos = await LoadTodos(context.SessionId);
if (todos.HasIncompletePendingOrInProgress)
{
    if (!incompleteReminded)
    {
        var reminder = SystemReminderRenderer.Wrap(
            FormatTodoListBrief(todos),
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoIncomplete);
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = reminder });
        incompleteReminded = true;
        continue; // 不退出，再给一轮
    }
    // 第二次仍然未完成 → 信任 agent，允许退出
}
```

## 5. Build Agent 提示词改写

`src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs`，替换「任务管理 **必须**（不可协商）」整段：

```markdown
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
```

### 变化总结

| 原来 | 现在 |
|------|------|
| 强制语气「必须」「不可协商」 | 指导语气「主动使用」「何时用/何时不用」 |
| 无示例 | 正反各一个示例 |
| 无 paused | 明确 paused 用法 |
| 只说工具名 | 说明状态流转规则 |

## 6. TodoWrite 工具描述增强

`src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`：

```csharp
public override string Description =>
    "使用此工具创建和管理结构化任务列表，帮助跟踪进度、组织复杂任务。" +
    "" +
    "## 何时使用" +
    "- 复杂多步任务（3 步以上）" +
    "- 用户提供多个任务（编号或逗号分隔）" +
    "- 收到新指令后立即捕获为 todo" +
    "" +
    "## 何时不用" +
    "- 单个简单任务" +
    "- 纯问答 / 信息性请求" +
    "- 可在 3 步内完成的小任务" +
    "" +
    "## 任务状态" +
    "- pending: 未开始" +
    "- in_progress: 当前进行中（一次只能一个）" +
    "- completed: 已完成" +
    "- cancelled: 不需要了" +
    "- paused: 等待用户回复，暂停执行" +
    "" +
    "## 规则" +
    "- 完成后立即标记 completed，不要批量" +
    "- 一次只保持一个 in_progress" +
    "- 需要等待用户时标记 paused" +
    "- 发现新任务立即添加" +
    "" +
    "有疑问时，使用此工具。主动管理任务体现你的专注度。";
```

### ParametersSchema 同步更新

`status` enum 数组新增 `"paused"`。

## 7. Todo 查询辅助方法

在 `TodoManager` 或独立的 TodoList 扩展中新增：

```csharp
// src/Seeing.Agent/Core/Todo/TodoListExtensions.cs
using Seeing.Agent.Core.Todo;

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
        // 返回简短列表文本，用于 system-reminder task body
    }
}
```

## 8. WebUI Todo 渲染

`paused` 状态需要视觉展示。需定位前端 Todo 渲染组件并添加 paused 状态的样式（如灰色斜体 + 暂停图标）。

## 9. 测试

- `AgentExecutor` 单元测试：退出检查逻辑（各状态组合）
- `SystemReminderNotices` 测试：新增 notice 文案
- `TodoWriteTool` 测试：paused 状态解析
- 集成测试：模拟多步任务场景，验证 reminder 注入时机

## 10. 涉及文件汇总

| # | 文件 | 改动 |
|---|------|------|
| 1 | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | 重写 build agent 提示词「任务管理」段 |
| 2 | `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs` | 增强 Description；删除重复 TodoStatus enum；引用统一到 Core.Todo.TodoStatus；ParseStatus 支持 "paused"；ParametersSchema 更新 |
| 3 | `src/Seeing.Agent/Core/Todo/TodoItem.cs` | TodoStatus enum 新增 Paused |
| 4 | `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` | using 替换为 Core.Todo |
| 5 | `src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs` | MapPlanStatus 引用 Core.Todo.TodoStatus，可能需要更新 using |
| 6 | `src/Seeing.Agent/Core/Reminders/SystemReminder.cs` | Sources 新增 Agent；Kinds 新增 TodoEmpty / TodoIncomplete / TodoPaused |
| 7 | `src/Seeing.Agent/Core/Reminders/SystemReminderNotices.cs` | 新增三条 notice 文案 |
| 8 | `src/Seeing.Agent/Core/AgentExecutor.cs` | 循环开始检测 paused；每轮 TodoEmpty 检查；退出前 todo 完成检查 |
| 9 | `src/Seeing.Agent/Core/Todo/TodoListExtensions.cs` | 新增 TodoList 查询/格式化扩展方法 |
| 10 | `samples/Seeing.Agent.WebUI` | Todo 渲染组件支持 paused 状态 |
| 11 | `tests/` | TodoReminder 相关测试 |
