# Seeing.Agent 会话归属契约与子会话统一执行重构

**日期:** 2026-08-27
**状态:** 已实施（2026-08-27 完成）
**分支:** `master`（`fdd0437` + `7051c83` + `0708c53`）

---

## 1. 背景与动机

### 1.1 消息无强归属
`SessionMessage` 无 `SessionId` 字段，消息"属于哪个会话"仅靠物理列表位置隐式表达。Fork/拷贝场景无法追溯来源会话。

### 1.2 执行引擎与 App 层耦合
`ExecutionJobService` / `SessionExecutionQueue` / `ExecutionEventPublisher` 等执行引擎位于 `Seeing.Agent.App`（应用层），主库层的 `TaskTool` 无法直接使用，被迫依赖 `BackgroundTaskManager`（独立后台任务管理器）做桥接。

### 1.3 Task 事件体系过度设计
Task 事件（`TaskStartedEvent`/`TaskProgressEvent`/`TaskCompletedEvent`/`TaskFailedEvent`/`SubAgentEvent`）+ `TaskEventProjector` + `TaskSessionProjector` + `SessionStreamEventApplier` + `BackgroundTaskManager` 组成复杂的后台任务投影层。实际消费仅两处（TaskTool 内部状态 + UI Task 卡片），UI 已在 `OnAfterRenderAsync` 从工具调用事件归纳 Task 卡片，投影层成为孤儿代码。

### 1.4 切换会话不订阅执行流
`Session.razor.OnParametersSetAsync` 缺少对新会话的执行流订阅，切换到正在执行的子会话时无法实时渲染。

---

## 2. 目标

1. `SessionMessage.SessionId` 强归属（`session_id`），Fork/拷贝时改写
2. `SessionData.Messages` 封装为 `IReadOnlyList` + 统一编辑 API（补写/改写归属）
3. 执行引擎从 App 层下沉主库层（`Seeing.Agent.Execution`）
4. Task 事件体系与 BackgroundTaskManager 全部删除
5. TaskTool 改为创建普通子会话 + `SubmitAsync`（前台等待/后台监听）
6. 会话切换时订阅执行流，子会话实时渲染
7. Task 卡片由 UI 从工具调用事件归纳（无服务端投影）

---

## 3. 核心设计

### 3.1 SessionId 强归属

```csharp
// SessionMessage 新增
public string? SessionId { get; set; }  // 消息当前归属的会话

// SessionData.BelongsToSession 优先级
public bool BelongsToSession(string sessionId)
    => SessionId == sessionId  // 优先：强归属
    || SessionId == null && Messages.IndexOf(this) >= 0;  // 回退：旧数据兼容
```

### 3.2 Messages 统一编辑 API

```csharp
// IReadOnlyList + 公共 setter（JSON 兼容）
public IReadOnlyList<SessionMessage> Messages { get; set; }

// 统一编辑方法（内部同步锁 + 自动归属）
void AddMessage(SessionMessage msg, string? sessionId = null)
void AddMessages(IEnumerable<SessionMessage> msgs, string? sessionId = null)
void InsertMessage(int index, SessionMessage msg, string? sessionId = null)
void RemoveMessage(SessionMessage msg)
void RemoveLastMessage()
void RemoveMessages(Predicate<SessionMessage> match)
void ReplaceMessages(IEnumerable<SessionMessage> newMessages)  // 物化后替换
```

`AddMessage` / `InsertMessage` 自动注入 `SessionId`。所有外部写入必须走编辑 API，直接操作 `Messages` 列表的行为被编译期阻止。

### 3.3 执行引擎下沉

```
改造前：App 层 ExecutionJobService → BackgroundTaskManager 桥接 → TaskTool
改造后：主库 ExecutionJobService → TaskTool 直接调用
```

移动文件：
- `ExecutionJobService` / `SessionExecutionQueue` / `ExecutionEventPublisher` / `ChatEventTracker` / `CompactionRunner` 等 → `src/Seeing.Agent/Execution/`
- `ChatInput` / `ChatOptions` → `src/Seeing.Agent/Models/`
- `CommandResultEvent` / `SessionTitleChangedEvent` → `src/Seeing.Agent/Events/`

DI 入口 `AddExecutionEngine()` 留在 App 层（应用层组装）。

### 3.4 子会话统一执行

TaskTool 改为：
1. 创建普通子会话（`SessionKind.SubAgent`）
2. `SubmitAsync` 提交到 `ExecutionJobService`
3. **前台模式**（小型任务）：`WaitForExecutionAsync` 等待结果
4. **后台模式**（大型任务）：注册 `TaskCompleted` 事件监听，完成时注入 synthetic 消息 + 唤醒父会话

删除 `BackgroundTaskManager`（5 文件）：`BackgroundTaskInfo`、`BackgroundTaskManager`、`BackgroundTaskProgress`、`IBackgroundTaskManager`、`IBackgroundTaskProgress`。

### 3.5 Task 事件删除

删除：
- 事件类型：`TaskStartedEvent`、`TaskProgressEvent`、`TaskCompletedEvent`、`TaskFailedEvent`、`SubAgentEvent`
- 常量：`MessageEventType.TaskStarted/TaskProgress/TaskCompleted/TaskFailed/SubAgent`
- 投影器：`ITaskEventProjector`、`TaskEventProjector`、`TaskProjectionContext`、`TaskSessionProjector`
- App 层：`SessionStreamEventApplier`、`AppEvents.Task*` 事件
- Gateway：`GatewayEventMapper` Task 映射、`GatewayEventData` Task 字段
- 测试：`TaskEventProjectorTests`、`TaskSessionProjectorTests`、`BackgroundTaskManagerTests`、`BackgroundTaskProgressTests`

UI Task 卡片改为从 `ToolCallStartedEvent` / `ToolCallCompletedEvent` 的 `ToolName == "task"` 归纳。

### 3.6 会话切换修复

`Session.razor.OnParametersSetAsync` 补充 `RestoreExecutionFromOverview(SessionId)` 调用，确保切换到正在执行的子会话时订阅执行流并恢复状态。

---

## 4. 关键修复

### 4.1 P0: ReplaceMessages 惰性序列自清空

```csharp
// 修复前：Clear + ToList 形成空引用循环
public void ReplaceMessages(IEnumerable<SessionMessage> newMessages)
{
    var snapshot = newMessages.ToList();  // 如果 newMessages 是 session.Messages.Take(n)，此时已清空
    _messages.Clear();
    // ...
}

// 修复后：先物化再操作
public void ReplaceMessages(IEnumerable<SessionMessage> newMessages)
{
    var materialized = newMessages.ToList();  // 先物化
    lock (_syncRoot) { _messages.Clear(); }
    foreach (var m in materialized) { _messages.Add(m); }
}
```

### 4.2 P1a: SessionForker 改写 SessionId

```csharp
// Fork 时改写克隆消息的 SessionId
clone.SessionId = forkedSession.Id;
```

### 4.3 P1b: 会话切换订阅执行流

```csharp
// OnParametersSetAsync 补充
if (SessionId != _lastRenderedSessionId)
{
    await RestoreExecutionFromOverview(SessionId);
}
```

---

## 5. 测试状态

| 项目 | 通过 | 总计 |
|------|------|------|
| Seeing.Session.Tests | 97 | 97 |
| Seeing.Agent.Tests | 765 | 765 |
| Seeing.Agent.WebUI.Tests | 69 | 69 |
| Seeing.Gateway.Tests | 31 | 31 |
| Seeing.Agent.Acp.Tests | 45 | 45 |
| **合计** | **1007** | **1007** |

---

## 6. 提交记录

| Commit | 内容 |
|--------|------|
| `fdd0437` | 主重构：会话归属契约、执行引擎下沉主库层、子会话统一执行、Task 事件删除 |
| `7051c83` | WebUI 修复：会话切换清理流式状态并校验消息归属，防止父子会话渲染串台 |
| `0708c53` | 测试修复：工具输出限制默认值与头尾预览测试期望更新 |
| `593442b` | 文档：更新解耦重构 spec，记录 Task 事件体系删除与会话归属重构 |
