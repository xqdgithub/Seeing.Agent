# 子代理状态反馈优化实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 优化后台子任务的状态反馈机制，让父agent正确理解后台任务的工作机制

**Architecture:** 修改TaskStatusTool支持批量查询，修改TaskTool添加状态反馈，更新系统提示指导

**Tech Stack:** C#, .NET 10.0, xUnit

---

### Task 1: 修改TaskStatusTool参数schema和描述

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskStatusTool.cs:29-44`

- [ ] **Step 1: 修改参数schema**

```csharp
public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        task_id = new { type = "string", description = "任务 ID（可选，不传则返回当前会话所有子任务状态）" },
        wait = new { type = "boolean", description = "是否阻塞等待完成（默认 false，仅对单个任务有效）" },
        timeout_ms = new { type = "integer", description = "等待超时毫秒（默认 600000，正整数）" }
    },
    required = Array.Empty<string>()
});
```

- [ ] **Step 2: 修改工具描述**

```csharp
public override string Description =>
    "查询后台子任务状态。传入 task_id 查询单个任务；不传则返回当前会话所有子任务状态。wait=true 时可阻塞等待完成（仅对单个任务有效）。";
```

- [ ] **Step 3: 修改ExecuteAsync方法添加批量查询逻辑**

```csharp
public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
{
    var taskId = GetStringArgument(arguments, "task_id");
    var wait = GetBoolArgument(arguments, "wait") ?? false;
    var timeoutMs = 600000;
    if (arguments.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms) && ms > 0)
        timeoutMs = ms;

    // 运行期解析执行引擎，避免与 ToolManager 的构造期循环依赖
    var execService = context.Services?.GetService(typeof(ExecutionJobService)) as ExecutionJobService;
    if (execService == null)
        return Failure("执行引擎不可用，无法查询任务状态");

    // 如果没有传task_id，返回所有子任务状态
    if (string.IsNullOrEmpty(taskId))
    {
        return await GetChildTasksStatusAsync(context.SessionId, execService);
    }

    // 单个任务查询逻辑（保持原有逻辑）
    var overview = execService.GetOverview(taskId);
    var current = overview.CurrentExecution;

    // 无活跃/排队执行：回落到子会话消息摘要（兼容已完成落盘会话与旧后台任务）
    if (current == null && overview.QueueLength == 0)
        return await FallbackFromSessionAsync(taskId);

    if (wait)
    {
        // 取消优先：父 Loop 取消时 wait 立即返回，而非继续轮询到超时
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            await execService.WaitForExecutionAsync(current!.ExecutionId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (context.CancellationToken.IsCancellationRequested)
                return Failure("已取消");
            return Success("timeout", $"task_id: {taskId}\nstate: timeout");
        }

        current = execService.GetExecution(current!.ExecutionId);
        if (current == null)
            return await FallbackFromSessionAsync(taskId);
    }

    var state = MapExecutionState(current!.Status);
    var sb = new StringBuilder();
    sb.AppendLine($"task_id: {taskId}");
    sb.AppendLine($"state: {state}");

    var session = _sessionManager.Get(taskId) ?? await _sessionManager.LoadAsync(taskId);
    var allAssistants = session?.GetActiveMessages()
        .Where(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && !m.IsSummary)
        .Select(m => m.Content?.Trim())
        .Where(content => !string.IsNullOrEmpty(content));

    var result = string.Join("\n\n", allAssistants);
    if (current.Status == ExecutionStatus.Completed && !string.IsNullOrEmpty(result))
    {
        sb.AppendLine();
        sb.AppendLine("<task_result>");
        sb.AppendLine(result);
        sb.AppendLine("</task_result>");
    }
    else if (!string.IsNullOrEmpty(current.ErrorMessage))
    {
        sb.AppendLine();
        sb.AppendLine("<task_error>");
        sb.AppendLine(current.ErrorMessage);
        sb.AppendLine("</task_error>");
    }

    return Success(state, sb.ToString(), new Dictionary<string, object>
    {
        ["task_id"] = taskId,
        ["state"] = state
    });
}
```

- [ ] **Step 4: 添加GetChildTasksStatusAsync辅助方法**

```csharp
/// <summary>
/// 获取当前会话所有子任务的状态摘要
/// </summary>
private async Task<ToolResult> GetChildTasksStatusAsync(string parentSessionId, ExecutionJobService execService)
{
    var children = await _sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent);
    if (children == null || children.Count == 0)
    {
        return Success("no_tasks", "当前会话没有子任务");
    }

    var sb = new StringBuilder();
    sb.AppendLine("## 所有子任务状态");
    sb.AppendLine();

    var runningTasks = new List<(string Id, string Desc, string State)>();
    var completedTasks = new List<(string Id, string Desc, string State)>();
    var failedTasks = new List<(string Id, string Desc, string State)>();
    var cancelledTasks = new List<(string Id, string Desc, string State)>();

    foreach (var child in children)
    {
        var overview = execService.GetOverview(child.Id);
        var current = overview.CurrentExecution;
        
        string state;
        
        if (current == null && overview.QueueLength == 0)
        {
            state = child.Status switch
            {
                SessionStatus.Error => "error",
                SessionStatus.Active => "running",
                _ => "completed"
            };
        }
        else
        {
            state = current?.Status switch
            {
                ExecutionStatus.Pending => "running",
                ExecutionStatus.Queued => "running",
                ExecutionStatus.Running => "running",
                ExecutionStatus.Completed => "completed",
                ExecutionStatus.Failed => "error",
                ExecutionStatus.Cancelled => "cancelled",
                _ => "running"
            };
        }

        var taskDesc = child.Description ?? child.Id;
        var taskInfo = (child.Id, taskDesc, state);

        switch (state)
        {
            case "running":
                runningTasks.Add(taskInfo);
                break;
            case "completed":
                completedTasks.Add(taskInfo);
                break;
            case "error":
                failedTasks.Add(taskInfo);
                break;
            case "cancelled":
                cancelledTasks.Add(taskInfo);
                break;
        }
    }

    // 输出汇总
    sb.AppendLine($"总计: {children.Count} 个子任务");
    sb.AppendLine($"运行中: {runningTasks.Count}");
    sb.AppendLine($"已完成: {completedTasks.Count}");
    sb.AppendLine($"失败: {failedTasks.Count}");
    sb.AppendLine($"已取消: {cancelledTasks.Count}");
    sb.AppendLine();

    // 详细列表
    if (runningTasks.Count > 0)
    {
        sb.AppendLine("### 运行中");
        foreach (var task in runningTasks)
            sb.AppendLine($"- {task.Desc} (ID: {task.Id})");
        sb.AppendLine();
    }

    if (completedTasks.Count > 0)
    {
        sb.AppendLine("### 已完成");
        foreach (var task in completedTasks)
            sb.AppendLine($"- {task.Desc} (ID: {task.Id})");
        sb.AppendLine();
    }

    if (failedTasks.Count > 0)
    {
        sb.AppendLine("### 失败");
        foreach (var task in failedTasks)
            sb.AppendLine($"- {task.Desc} (ID: {task.Id})");
        sb.AppendLine();
    }

    if (cancelledTasks.Count > 0)
    {
        sb.AppendLine("### 已取消");
        foreach (var task in cancelledTasks)
            sb.AppendLine($"- {task.Desc} (ID: {task.Id})");
        sb.AppendLine();
    }

    // 所有任务完成提示
    if (runningTasks.Count == 0 && failedTasks.Count == 0 && cancelledTasks.Count == 0)
    {
        sb.AppendLine("✅ 所有子任务已完成，可以继续处理父任务。");
    }

    return Success("summary", sb.ToString(), new Dictionary<string, object>
    {
        ["total"] = children.Count,
        ["running"] = runningTasks.Count,
        ["completed"] = completedTasks.Count,
        ["failed"] = failedTasks.Count,
        ["cancelled"] = cancelledTasks.Count
    });
}
```

- [ ] **Step 5: 提交代码**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/Task/TaskStatusTool.cs
git commit -m "feat: TaskStatusTool支持批量查询所有子任务状态"
```

---

### Task 2: 修改TaskTool添加状态反馈

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs:190-296`

- [ ] **Step 1: 添加GetSubAgentStatusSummaryAsync辅助方法**

```csharp
/// <summary>
/// 获取父会话的所有子代理状态摘要
/// </summary>
private async Task<string> GetSubAgentStatusSummaryAsync(string parentSessionId, string currentChildId)
{
    var children = await _sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent);
    if (children == null || children.Count <= 1)
        return string.Empty;

    var sb = new StringBuilder();
    sb.AppendLine("\n## 所有子代理状态");
    
    var allCompleted = true;
    var runningTasks = new List<string>();
    var completedTasks = new List<string>();
    var failedTasks = new List<string>();

    foreach (var child in children)
    {
        var overview = GetExecutionOverview(child.Id);
        var state = child.Status switch
        {
            SessionStatus.Error => "failed",
            SessionStatus.Active when overview.HasActiveExecution => "running",
            SessionStatus.Active => "completed",
            _ => "completed"
        };

        var taskDesc = child.Description ?? child.Id;
        switch (state)
        {
            case "running":
                runningTasks.Add(taskDesc);
                allCompleted = false;
                break;
            case "completed":
                completedTasks.Add(taskDesc);
                break;
            case "failed":
                failedTasks.Add(taskDesc);
                allCompleted = false;
                break;
        }
    }

    if (runningTasks.Count > 0)
    {
        sb.AppendLine($"运行中: {runningTasks.Count}");
        foreach (var task in runningTasks)
            sb.AppendLine($"  - {task}");
    }
    
    if (completedTasks.Count > 0)
    {
        sb.AppendLine($"已完成: {completedTasks.Count}");
        foreach (var task in completedTasks)
            sb.AppendLine($"  - {task}");
    }
    
    if (failedTasks.Count > 0)
    {
        sb.AppendLine($"失败: {failedTasks.Count}");
        foreach (var task in failedTasks)
            sb.AppendLine($"  - {task}");
    }

    if (allCompleted && children.Count > 1)
    {
        sb.AppendLine();
        sb.AppendLine("✅ 所有子代理任务已完成，可以继续处理父任务。");
    }

    return sb.ToString();
}
```

- [ ] **Step 2: 修改后台任务完成通知**

```csharp
case ExecutionStatus.Completed:
    var outputText = ReadChildResult(childId);
    var statusSummary = await GetSubAgentStatusSummaryAsync(parentSessionId, childId);
    
    var completedBody = $"后台任务已完成: {desc}\ntask_id: {childId}\nstate: completed";
    
    if (!string.IsNullOrEmpty(statusSummary))
    {
        completedBody += statusSummary;
    }
    
    completedBody += $"\n\n<task_result>\n{outputText}\n</task_result>";
    
    await _loopScheduler.InjectSyntheticAsync(
        parentSessionId,
        SystemReminderRenderer.Wrap(
            completedBody,
            SystemReminder.Sources.Task,
            SystemReminder.Kinds.Completed,
            taskId: childId),
        BuildReminderMeta(childId, "completed", SystemReminder.Kinds.Completed),
        CancellationToken.None);
    break;
```

- [ ] **Step 3: 修改返回信息**

```csharp
var outputMessage = $"后台任务已启动，任务ID: {session.Id}。" +
    $"任务完成时会通过系统提示通知您，无需轮询。" +
    $"如需主动检查状态，可调用task_status工具（不传task_id可查看所有子任务状态）。";
    
return Success(description, BuildOutput(session.Id, "running", outputMessage));
```

- [ ] **Step 4: 提交代码**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs
git commit -m "feat: TaskTool添加子代理状态反馈和改进返回信息"
```

---

### Task 3: 更新系统提示

**Files:**
- Modify: `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs:46-72`

- [ ] **Step 1: 在build agent系统提示中添加后台任务处理指导**

```csharp
SystemPrompt = """
## 身份
- 你的名字是「小See」，一个智能助手。被问及身份时回答「小See」。

## 风格
- 简洁直接：除非用户要求，回答保持简短；简单问题用一个词或一句话回答
- 不使用表情符号，不说"我来…/让我…"之类的开场白，直接开始工作

## 工作原则
- 用户要求时才执行操作；被问"如何做"时先回答问题，不立即动手
- 修改代码前先理解文件约定并模仿现有模式，遵循安全最佳实践（不暴露或记录密钥）
- 完成文件编辑后直接停止，不解释做了什么

## 工具使用
- 多个独立请求可并行批量调用工具
- 优先使用专用工具而非 shell 命令（如用 Read/Edit 而非 cat/sed）
- 探索代码库时优先委托子代理

## 任务管理
- 3 步以上的多步任务用 TodoWrite 跟踪进度，完成立即标记 completed

## 后台任务处理
- 使用task工具的background=true参数启动后台任务
- 后台任务完成时会通过系统提示通知，无需轮询
- 如需主动检查状态，可调用task_status工具（不传task_id可查看所有子任务状态）
- 避免同步等待后台任务，这会阻塞父会话

## 代码引用
- 引用代码时用 `file_path:line_number` 格式

## 安全
- 不生成或猜测 URL（除非确定是编程用途）；无法帮助时简短说明并提供替代方案
""",
```

- [ ] **Step 2: 提交代码**

```bash
git add src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs
git commit -m "feat: 在系统提示中添加后台任务处理指导"
```

---

### Task 4: 测试验证

**Files:**
- Create: `tests/Seeing.Agent.Tests/Tools/TaskStatusToolTests.cs`

- [ ] **Step 1: 编写批量查询测试**

```csharp
using Seeing.Agent.Tools.BuiltIn.SubTask;
using Seeing.Agent.Execution;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class TaskStatusToolTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ExecutionJobService> _execServiceMock;
    private readonly TaskStatusTool _tool;

    public TaskStatusToolTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _execServiceMock = new Mock<ExecutionJobService>();
        _tool = new TaskStatusTool(
            Mock.Of<ILogger<TaskStatusTool>>(),
            _sessionManagerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTaskId_ReturnsAllChildTasksStatus()
    {
        // Arrange
        var parentId = "parent-session-1";
        var children = new List<SessionData>
        {
            new() { Id = "child-1", Description = "任务1", Status = SessionStatus.Active },
            new() { Id = "child-2", Description = "任务2", Status = SessionStatus.Active }
        };

        _sessionManagerMock.Setup(x => x.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(children);

        // Act
        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), new ToolContext
        {
            SessionId = parentId,
            Services = Mock.Of<IServiceProvider>(x => x.GetService(typeof(ExecutionJobService)) == _execServiceMock.Object)
        });

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Contains("所有子任务状态", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WithTaskId_ReturnsSingleTaskStatus()
    {
        // Arrange
        var taskId = "child-session-1";
        var overview = new SessionExecutionOverview
        {
            CurrentExecution = new ExecutionRecord
            {
                ExecutionId = "exec-1",
                Status = ExecutionStatus.Completed
            }
        };

        _execServiceMock.Setup(x => x.GetOverview(taskId)).Returns(overview);

        // Act
        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { task_id = taskId }), new ToolContext
        {
            SessionId = "parent-session-1",
            Services = Mock.Of<IServiceProvider>(x => x.GetService(typeof(ExecutionJobService)) == _execServiceMock.Object)
        });

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Contains("completed", result.Output);
    }
}
```

- [ ] **Step 2: 运行测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "TaskStatusToolTests"
```

- [ ] **Step 3: 提交测试代码**

```bash
git add tests/Seeing.Agent.Tests/Tools/TaskStatusToolTests.cs
git commit -m "test: 添加TaskStatusTool批量查询测试"
```

---

### Task 5: 总体审查

- [ ] **Step 1: 构建项目**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 2: 运行所有测试**

```bash
dotnet test tests/Seeing.Agent.Tests
```

- [ ] **Step 3: 检查代码风格**

```bash
dotnet format --verify-no-changes
```

- [ ] **Step 4: 最终提交**

```bash
git add .
git commit -m "feat: 完成子代理状态反馈优化"
```
