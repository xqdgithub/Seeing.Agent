# 子代理结果完整返回设计

**创建时间:** 2026-08-28
**状态:** 已批准
**目标:** 解决子代理只返回最后一条消息的问题

## 问题描述

在 Seeing.Agent 项目中，当使用子代理（SubAgent）执行任务时，父代理只能获取子代理的最后一条 assistant 消息，而不是完整的执行记录。这导致：

1. 子代理执行多个步骤时，只返回最后一步的结果
2. 后台任务完成通知只显示摘要，完整内容无法获取
3. 父代理无法获取子代理的完整执行过程

### 复现场景

用户要求"同时委托两个 agent 分别从不同角度分析北京和天津的差异，最后汇总分析结果"。系统创建了两个后台任务，但完成后只返回了摘要信息，完整报告无法获取。

## 根本原因

`TaskTool.ReadChildResult` 方法使用 `LastOrDefault` 只获取最后一条 assistant 消息：

```csharp
private string ReadChildResult(string childId)
{
    var childSession = _sessionManager.Get(childId);
    var lastAssistant = childSession?.GetActiveMessages()
        .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                            && !m.IsSummary);
    var text = lastAssistant?.Content?.Trim();
    return string.IsNullOrEmpty(text) ? "子任务执行完成，无输出内容。" : text;
}
```

同样，`TaskStatusTool` 中的结果获取逻辑也有相同问题。

## 解决方案

修改 `TaskTool.ReadChildResult` 方法，返回所有 assistant 消息，而不是只返回最后一条。

### 修改 1：`TaskTool.ReadChildResult` 方法

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`

**修改前:**
```csharp
private string ReadChildResult(string childId)
{
    var childSession = _sessionManager.Get(childId);
    var lastAssistant = childSession?.GetActiveMessages()
        .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                            && !m.IsSummary);
    var text = lastAssistant?.Content?.Trim();
    return string.IsNullOrEmpty(text) ? "子任务执行完成，无输出内容。" : text;
}
```

**修改后:**
```csharp
private string ReadChildResult(string childId)
{
    var childSession = _sessionManager.Get(childId);
    var allAssistants = childSession?.GetActiveMessages()
        .Where(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && !m.IsSummary)
        .Select(m => m.Content?.Trim())
        .Where(content => !string.IsNullOrEmpty(content));
    
    var result = string.Join("\n\n", allAssistants);
    return string.IsNullOrEmpty(result) ? "子任务执行完成，无输出内容。" : result;
}
```

### 修改 2：`TaskStatusTool.ExecuteAsync` 方法

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Task/TaskStatusTool.cs`

**修改前 (第 96-107 行):**
```csharp
var lastAssistant = session?.GetActiveMessages()
    .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        && !m.IsSummary);

if (current.Status == ExecutionStatus.Completed && lastAssistant != null &&
    !string.IsNullOrEmpty(lastAssistant.Content))
{
    sb.AppendLine();
    sb.AppendLine("<task_result>");
    sb.AppendLine(lastAssistant.Content);
    sb.AppendLine("</task_result>");
}
```

**修改后:**
```csharp
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
```

### 修改 3：`TaskStatusTool.FallbackFromSessionAsync` 方法

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Task/TaskStatusTool.cs`

**修改前 (第 130-132 行):**
```csharp
var lastAssistant = activeMessages
    .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
```

**修改后:**
```csharp
var allAssistants = activeMessages
    .Where(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
    .Select(m => m.Content?.Trim())
    .Where(content => !string.IsNullOrEmpty(content));

var result = string.Join("\n\n", allAssistants);
```

然后更新后续的条件判断：
```csharp
if (!string.IsNullOrEmpty(result))
{
    sb.AppendLine();
    sb.AppendLine("<task_result>");
    sb.AppendLine(result);
    sb.AppendLine("</task_result>");
}
```

## 预期效果

修改后，子代理将返回所有 assistant 消息，而不是只返回最后一条。这将解决用户遇到的问题：

1. **子代理结果完整:** 父代理能获取子代理的完整执行记录
2. **后台任务结果完整:** 后台任务完成后，父任务能获取子代理的完整结果
3. **保持向后兼容:** 不影响现有功能，只是返回更多内容

## 影响范围

- `TaskTool.ReadChildResult` 方法
- `TaskStatusTool.ExecuteAsync` 方法
- `TaskStatusTool.FallbackFromSessionAsync` 方法

## 测试计划

1. 创建子代理执行多步骤任务，验证父代理能获取所有步骤的结果
2. 测试后台任务场景，验证后台任务完成后能获取完整结果
3. 测试空结果场景，验证兜底文案正常显示
4. 运行现有测试，确保不破坏现有功能

## 参考

- opencode 项目的子代理处理方式：通过数据库存储所有消息，支持获取完整执行记录
- Seeing.Agent 项目的 `SessionData.GetActiveMessages()` 方法：返回从最后一条摘要消息开始的所有消息
