# 子代理状态反馈优化设计

**创建时间:** 2026-08-28
**状态:** 已批准
**目标:** 优化后台子任务的状态反馈机制，让父agent正确理解后台任务的工作机制

---

## 问题背景

### 当前问题
1. 后台任务本应异步执行，但父agent通过TaskStatus工具同步等待，失去了后台执行的意义
2. TaskStatus工具需要逐个查询子任务状态，效率低下
3. 父agent不了解系统提示通知机制，导致使用不当

### 问题影响
- 后台任务功能基本失效，父agent被阻塞
- 用户体验差，等待时间长
- 系统资源浪费

---

## 设计目标

1. **改进状态反馈**：在子代理完成时，提供所有子代理的状态摘要
2. **支持批量查询**：TaskStatus工具支持不传task_id查询所有子任务状态
3. **明确通知机制**：让父agent理解系统提示通知机制，避免同步等待
4. **完成状态提示**：当所有子任务完成时，明确提示可以继续处理父任务

---

## 技术方案

### 1. 修改TaskStatusTool参数schema

**当前实现：**
```csharp
public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        task_id = new { type = "string", description = "任务 ID（Child Session Id）" },
        wait = new { type = "boolean", description = "是否阻塞等待完成（默认 false）" },
        timeout_ms = new { type = "integer", description = "等待超时毫秒（默认 600000，正整数）" }
    },
    required = new[] { "task_id" }
});
```

**修改后：**
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

### 2. 修改TaskStatusTool描述

**当前实现：**
```csharp
public override string Description =>
    "查询后台子任务状态。传入 task 返回的 task_id。wait=true 时可阻塞等待完成。";
```

**修改后：**
```csharp
public override string Description =>
    "查询后台子任务状态。传入 task_id 查询单个任务；不传则返回当前会话所有子任务状态。wait=true 时可阻塞等待完成（仅对单个任务有效）。";
```

### 3. 修改ExecuteAsync方法

**核心逻辑变更：**
```csharp
public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
{
    var taskId = GetStringArgument(arguments, "task_id");
    var wait = GetBoolArgument(arguments, "wait") ?? false;
    var timeoutMs = 600000;
    if (arguments.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms) && ms > 0)
        timeoutMs = ms;

    var execService = context.Services?.GetService(typeof(ExecutionJobService)) as ExecutionJobService;
    if (execService == null)
        return Failure("执行引擎不可用，无法查询任务状态");

    // 如果没有传task_id，返回所有子任务状态
    if (string.IsNullOrEmpty(taskId))
    {
        return await GetChildTasksStatusAsync(context.SessionId, execService);
    }

    // 单个任务查询逻辑（保持原有逻辑）
    // ...
}
```

### 4. 添加批量查询辅助方法

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

### 5. 修改TaskTool后台任务完成通知

**当前实现：**
```csharp
case ExecutionStatus.Completed:
    var outputText = ReadChildResult(childId);
    var completedBody =
        $"Background task completed: {desc}\ntask_id: {childId}\nstate: completed\n\n<task_result>\n{outputText}\n</task_result>";
    // ...
```

**修改后：**
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
    // ...
```

### 6. 添加子代理状态摘要辅助方法

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

### 7. 修改TaskTool返回信息

**当前实现：**
```csharp
return Success(description, BuildOutput(session.Id, "running",
    "Background task started. Continue your current work and call task_status when you need the result."));
```

**修改后：**
```csharp
var outputMessage = $"后台任务已启动，任务ID: {session.Id}。" +
    $"任务完成时会通过系统提示通知您，无需轮询。" +
    $"如需主动检查状态，可调用task_status工具（不传task_id可查看所有子任务状态）。";
    
return Success(description, BuildOutput(session.Id, "running", outputMessage));
```

### 8. 在系统提示中添加指导

在主代理（build）的系统提示中添加后台任务处理指导：

```markdown
## 后台任务处理
- 使用task工具的background=true参数启动后台任务
- 后台任务完成时会通过系统提示通知，无需轮询
- 如需主动检查状态，可调用task_status工具（不传task_id可查看所有子任务状态）
- 避免同步等待后台任务，这会阻塞父会话
```

---

## 影响范围

### 修改文件
1. `src/Seeing.Agent/Tools/BuiltIn/Task/TaskStatusTool.cs` - 添加批量查询功能
2. `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` - 修改返回信息和完成通知
3. `src/Testing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` - 添加系统提示指导

### 兼容性
- 所有修改都是向后兼容的
- 不改变现有功能逻辑
- 只是增加新功能和改进用户界面

### 测试建议
1. 测试批量查询功能（无task_id参数）
2. 测试单个任务查询功能（有task_id参数）
3. 测试后台任务完成通知中的状态摘要
4. 测试所有任务完成时的提示信息
5. 测试系统提示是否正确显示

---

## 实施步骤

1. **Phase 1: 修改TaskStatusTool**
   - 修改参数schema，让task_id可选
   - 修改工具描述
   - 添加GetChildTasksStatusAsync辅助方法
   - 修改ExecuteAsync方法，添加批量查询逻辑

2. **Phase 2: 修改TaskTool**
   - 添加GetSubAgentStatusSummaryAsync辅助方法
   - 修改后台任务完成通知逻辑
   - 修改返回信息

3. **Phase 3: 更新系统提示**
   - 在build agent系统提示中添加后台任务处理指导

4. **Phase 4: 测试验证**
   - 测试各种场景（单任务、多任务、部分完成等）
   - 验证向后兼容性

---

## 验收标准

- [ ] TaskStatus工具支持不传task_id查询所有子任务状态
- [ ] 批量查询返回清晰的状态汇总信息
- [ ] 后台任务完成通知包含所有子代理状态
- [ ] 所有子任务完成时明确提示可以继续处理父任务
- [ ] 系统提示包含后台任务处理指导
- [ ] 所有修改向后兼容
- [ ] 测试覆盖各种场景
