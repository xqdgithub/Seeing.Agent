# 压缩失败落为会话系统消息

**日期:** 2026-08-20
**状态:** 已确认设计（待实施）

## 背景与问题

压缩请求失败（如 HTTP 502 Bad Gateway: 上游请求失败，即 LLM 上游调用失败）时，当前行为：

1. `LlmSummarizer` 抛 `HttpRequestException` → `CompressionService.CompressAsync` catch 转为 `CompressionOutcome { Success = false, ErrorMessage = ... }`
2. `CompactionRunner.RunAsync` 失败分支发布 `CompactionFailedEvent`（带 ErrorMessage），不抛异常
3. **服务端 `ChatEventTracker` 无 `CompactionFailedEvent` 分支** → 不写系统消息到会话（对比 Agent 错误 `ErrorEvent` 会写 `SystemMessage("错误: ...")` 持久化）
4. UI 端 `Session.razor` 的 `CompactionFailedEvent` 分支仅弹 `MessageService.Error` 瞬时 toast + 清进度条，**不重建时间线**

**结果**：用户看不到任何持久化提示；toast 一闪而过，刷新会话后无任何痕迹。

## 目标

- 压缩失败信息像 Agent 错误一样落为**会话系统消息**（持久化，刷新后可见）
- UI 立即可见该消息（时间线刷新）
- 统一覆盖手动 `/compact` 命令与自动阈值压缩两个入口

## 设计决策

### 决策 1：写系统消息 + 保留 toast

- 系统消息：会话内持久化记录（`SessionMessage.SystemMessage`），与现有 `"错误: {Message}"`、`"对话已取消: {Reason}"` 风格对齐
- toast：保留作为即时提醒（用户已确认）

### 决策 2：CompactionRunner 统一写入

在 `CompactionRunner.RunAsync` 失败分支写入，而非命令侧或 ChatEventTracker：

- **CompactionRunner 是手动/自动压缩的统一入口**（被 `BuiltInCommands.CompactHistory` 与 `ExecutionJobService` 自动压缩调用），一处写入两处生效
- ChatEventTracker 无效：命令执行在 `ProcessExecutionAsync` 的 `commandConsumedExecution` 分支，**早于 ChatEventTracker 创建**（ExecutionJobService.cs:431），命令发布的事件不经过它

## 变更明细

### 1. `src/Seeing.Agent.App/Execution/CompactionRunner.cs`

- 构造注入 `ISessionManager`（新签名 `(CompressionService, IExecutionEventPublisher, ISessionManager)`）
- `RunAsync` 失败分支，**先写会话再发布事件**（UI 读取已写入的 SessionData，与 Agent 事件"先投影再发布"一致）：

```csharp
else
{
    // 失败信息落为会话系统消息，持久化 + 通知（UI 时间线可见，刷新后保留）
    var session = _sessionManager.Get(sessionId);
    if (session != null)
    {
        var message = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
            ? "压缩失败"
            : $"压缩失败: {outcome.ErrorMessage}";
        session.Messages.Add(SessionMessage.SystemMessage(message));
        await _sessionManager.SaveAndNotifyAsync(sessionId);
    }
    _publisher.Publish(sessionId, new CompactionFailedEvent
    {
        SessionId = sessionId,
        ErrorMessage = outcome.ErrorMessage
    });
}
```

### 2. `samples/Seeing.Agent.WebUI/Pages/Session.razor`

`CompactionFailedEvent` 分支：保留 toast，增加时间线重建（与 `CompactionCompletedEvent` 分支对齐）：

```csharp
else if (evt is CompactionFailedEvent failedEvt)
{
    _compactionInProgress = false;
    _compactionProgress = null;
    _compactionReasoning = null;
    ResetTimelineFromSession();
    ReconcileTimelineFromSession();
    if (!string.IsNullOrEmpty(failedEvt.ErrorMessage))
    {
        MessageService.Error(string.Format("压缩失败: {0}", failedEvt.ErrorMessage));
    }
}
```

**原因**：UI 的 `OnSessionLifecycleEvent` 只处理 `Updated` 事件的 Title 更新，不刷新消息时间线；必须显式重建才能立即显示新写入的系统消息。

### 3. 测试

- `tests/Seeing.Agent.Tests/App/Execution/CompactionRunnerTests.cs`：
  - 新增：失败时写系统消息 + 调用 `SaveAndNotifyAsync`
  - 新增：成功时不写系统消息
  - 现有测试构造同步（补 mock `ISessionManager`）
- 同步其余 `CompactionRunner` 构造调用点（6 处测试）：
  - `CompactHistoryCommandTests.cs`
  - `ExecutionJobServicePermissionChannelTests.cs`
  - `ExecutionJobServiceInstructionTests.cs`
  - `ExecutionJobServiceCompactionTests.cs`
  - `ExecutionJobServiceCommandShortCircuitTests.cs`
  - `ExecutionJobServiceCancelTests.cs`
  - `ChatOrchestratorCreateSessionTests.cs`

## 边界与约束

- session 为 null 时跳过写入（`SaveAndNotifyAsync` 内部同样防御）
- 压缩失败时摘要未生成、session 消息结构未变，仅追加系统消息，不污染压缩后结构
- 宿主 `ProcessExecutionAsync` finally 的 `SaveAsync` 与 `SaveAndNotifyAsync` 幂等，不冲突
- 系统消息追加不影响消息真相模型（`GetActiveMessages` 正常返回）

## 不做（YAGNI）

- 不在 ChatEventTracker 加 `CompactionFailedEvent` 分支（命令事件流不经它，无效）
- 不做失败类型区分（502/超时/模型不可用）或重试 UI
- 不改变压缩成功路径（`CompactionCompletedEvent` 已触发时间线重建）
