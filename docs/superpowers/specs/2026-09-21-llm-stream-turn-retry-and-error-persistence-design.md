# 流式 LLM 轮次重试与运行时错误不落消息 设计

**日期**：2026-09-21
**分支**：feature/modular-architecture
**状态**：设计草案 v1（待用户审阅）
**依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`05-extension-guide.md`
- 代码基线：
  - `src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs`
  - `src/spine/Seeing.Agent.Core/Llm/LlmService.cs`
  - `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`
  - `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs`
  - `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`
  - `src/primitives/Seeing.Session/Core/SessionData.cs`、`SessionMessage.cs`
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`、`Components/SessionWindow.razor`
  - `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`
  - `src/abstractions/Seeing.Agent.Abstractions/Llm/ProviderConfig.cs`、`LlmRetryPolicy.cs`

---

## 1. 问题与动机

### 1.1 现象

| # | 现象 | 证据 |
|---|------|------|
| P1 | 流式请求在 SSE 建立（HTTP 200）后被对端强断（`SocketException(10054)` → `IOException`），装饰器因"已产出 chunk 即不重试"直接抛错，本轮对话失败 | `RetryLlmClientDecorator.cs:118`；运行时日志：`ChatCompletions SSE 已建立` 之后无"流式重试"，直接 `流式聊天请求失败` |
| P2 | 传输层 `MaxRetries<=0` 被定义为"无限重试（仅受 budget 约束）"，日志 `Attempt=1/0` 易被误读为"0 次却重试" | `ProviderConfig.cs:85`、`LlmRetryPolicy.cs:42`、`RetryLlmClientDecorator.cs:88` |
| P3 | 运行时错误/取消被写成 `SystemMessage` 持久化进 `session.Messages`，下一轮 `BuildHistoryFromSession` 把它作为 `role=system` 消息喂给 LLM，污染上下文，并在 UI 显示系统气泡 | `ChatEventTracker.cs:147-153`、`ExecutionJobService.cs:1083-1150` |

### 1.2 根因

1. **重试职责错位**：传输装饰器（`ILlmClient`）在"已交付内容给上层"后无法回滚，因此它**不该**重试；而"可回滚的整轮重试"只有应用层（`AgentExecutor` 的单轮 LLM 调用）能做，当前缺失。
2. **事件契约缺"轮次重置"语义**：`StreamStartEvent` 只表达"新轮次"，未定义"同轮重发 = 重置"；消费端（投影/渲染）无法安全丢弃该轮已产出的部分内容。
3. **运行时观测与对话真相混用**：错误/取消属执行期观测，却被写入"喂给 LLM 的对话消息"通道。

### 1.3 参考实现

opencode（`packages/opencode/src/session/processor.ts`）把重试放在会话处理循环里整轮重开，已产出内容不清理而作为新 part 追加，错误挂在 `assistantMessage.error` 字段（不进 messages）。本设计**借鉴其分层位置**，但补上它缺失的"回滚"，避免重复输出。

---

## 2. 目标与非目标

### 2.1 目标

- **G1**：应用层（`AgentExecutor` 单轮）流式调用的**有界整轮重试**，重试时丢弃该轮已产出内容（回滚），对外表现为一次连续回复。
- **G2**：`StreamStartEvent` 具备**幂等重置契约**，投影层与 UI 可据此回滚已渲染/已暂存内容。
- **G3**：运行时错误/取消**不再写入 `session.Messages`**，改由事件与执行记录呈现；LLM 历史不携带它们。
- **G4**：明确并加固传输层重试边界（"仅未产出 chunk 前重试"），修正误导性日志。
- **G5**：保持 schema 单点、依赖方向（契约层 → 实现）与 Core 瘦脊柱原则。
- **G6**：重试等待期间 UI 显式可见（"X 秒后重试（第 K 次）"）并保持可取消，避免"卡死"错觉。

### 2.2 非目标

- **N1**：不实现协议层 SSE 断点续传（OpenAI Chat Completions 无 `Last-Event-ID` 类机制，做不到）。
- **N2**：不做 prompt 级续写（把部分输出作为 assistant 前缀重发）——留作 `ILlmTurnRetryPolicy` 的可选扩展，不进默认路径。
- **N3**：不重试/重放**已执行工具**的副作用。
- **N4**：不改动 Gateway/ACP 对外协议语义（仅新增可选字段，透传即可）。
- **N5**：不重构 `LlmService` 的请求构造（保持请求构造单点）。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| 轮次（Turn/Step） | `AgentExecutor` 循环内的一次 LLM 调用及其产出，由 `(LoopId, Step)` 唯一标识 |
| 传输重试 | `RetryLlmClientDecorator` 在 `ILlmClient` 层的透明重试，仅限尚未产出 chunk 的失败 |
| 轮次重试 | 应用层（`AgentExecutor`）对单轮 LLM 调用的整轮重开，可回滚该轮已产出 |
| 重置事件 | 同一 `(LoopId, Step)` 再次发出的 `StreamStartEvent`，语义为"丢弃该轮已产出、重开" |
| 运行时消息 | 错误/取消等执行期观测，不属于对话真相，不应进入 `Messages`/LLM 历史 |

---

## 4. 架构设计

### 4.1 分层职责

| 层 | 组件 | 负责 | 不负责 |
|----|------|------|--------|
| 传输层 | `RetryLlmClientDecorator`（`ILlmClientDecorator`） | 仅"尚未产出任何 chunk"前的透明重试（连接建立失败、HTTP 5xx/超时） | 已产出后的任何重试（无法回滚已交付内容） |
| 应用层 | `AgentExecutor` + `ILlmTurnRetryPolicy` | 单轮 LLM 调用的整轮重试决策与执行、该轮累积状态回滚 | 传输细节、prompt 续写（可选扩展） |
| 事件契约 | `StreamStartEvent`（+`Attempt`） | 定义轮次边界与幂等重置语义 | 承载业务数据 |
| 投影层 | `ChatEventTracker` + `SessionData` | 按确定性 Id 投影/覆盖/移除轮次消息 | 重试决策 |
| 历史构建 | `ExecutionJobService.BuildHistoryFromSession` | 只把"对话真相"消息喂给 LLM | 承载运行时观测 |
| 错误呈现 | `ErrorEvent` / `LoopComplete(false)` / 执行记录 | UI 与执行记录呈现错误 | 写入 `Messages` |

### 4.2 依赖方向

- `ILlmTurnRetryPolicy`、`LlmTurnRetryOptions`、`StreamStartEvent.Attempt` 的契约放 **Abstractions**（`Llm/`、`Events/`）。
- 默认实现在 **Core**（`Llm/`），随 `AddSeeingCore` 注册。
- 投影在 **Hosting**（`ChatEventTracker`）。
- 消费在 **WebUI / Gateway**，只读事件。
- 依赖保持向下；不新增能力包反向依赖。

---

## 5. 详细设计

### 5.1 事件契约：`StreamStartEvent.Attempt` + 幂等重置

`src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs:199`

```csharp
public record StreamStartEvent : IMessageEvent
{
    // ... 现有字段不变 ...
    /// <summary>该轮第几次尝试（从 1 开始）；>1 表示重试重开。</summary>
    public int Attempt { get; init; } = 1;
}
```

**契约**（写入 XML 注释，作为跨层约定）：

> 同一 `(LoopId, Step)` 多次发出 `StreamStartEvent` 表示该轮被重开。消费端必须将其视为**重置**：投影层移除该轮已产生的未完成 assistant 消息；UI 清空该轮已累积的渲染缓冲。`Attempt` 用于展示与诊断，不作为重置判定的唯一依据（判定依据是 `(LoopId, Step)` 重复）。

影响点：
- `Hosting/ChatEventTracker.cs:44`（重置投影，见 5.4）
- `WebUI/Services/EventStreamHandler.cs:372`（清空累积，见 5.7）
- `Gateway/Seeing.Gateway/Mapping/GatewayEventMapper.cs:114`、`Seeing.Agent.Gateway/Core/GatewayEventMapper.cs:37`（透传，可把 `Attempt` 放进 meta）
- `Acp/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs:75`（默认 `Attempt=1`，无改动）

### 5.2 应用层策略：`ILlmTurnRetryPolicy`

新增 `src/abstractions/Seeing.Agent.Abstractions/Llm/ILlmTurnRetryPolicy.cs`：

```csharp
public interface ILlmTurnRetryPolicy
{
    bool Enabled { get; }
    /// <summary>判定异常是否可做轮次重试（与传输层同一 IsRetryable 语义）。</summary>
    bool CanRetry(Exception ex, CancellationToken cancellationToken);
    /// <summary>第 attempt 次失败后的退避延迟（attempt 从 1 开始，指数退避封顶）。</summary>
    TimeSpan NextDelay(int attempt);
    /// <summary>
    /// 是否继续重试。默认不受次数/时长硬上限约束（Q1 决定：无限重试，仅用户取消与不可重试错误终止）；
    /// 保留可选上限字段以便运维收紧，默认关闭。
    /// </summary>
    bool ShouldRetry(int attempt, TimeSpan elapsed, TimeSpan nextDelay);
}
```

新增 `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmTurnRetryOptions.cs`：

| 字段 | 默认 | 说明 |
|------|------|------|
| `Enabled` | `true` | 应用层轮次重试开关 |
| `BaseDelayMs` | `500` | 退避基数 |
| `MaxDelayMs` | `10000` | 单次退避上限 |
| `MaxAttempts` | `0` | `0`=不限次数（Q1） |
| `TotalBudgetMs` | `0` | `0`=不限时长（Q1） |

默认实现 `src/spine/Seeing.Agent.Core/Llm/DefaultLlmTurnRetryPolicy.cs`：
- `CanRetry`：若异常是 `LlmException`（`LlmService` 包装后的 `LlmStreamingException`/`LlmConnectionException`/`LlmTimeoutException`），按其 `IsRetryable` 判定；否则退回 `LlmRetryPolicy.IsRetryable`（4xx → 否，`IOException`/`TimeoutException`/非用户取消的 `OCE` → 是）。**注意**：不能直接对 `LlmException` 用 `LlmRetryPolicy.IsRetryable`，否则对真实失败恒为 false。
- `NextDelay` 复用 `LlmRetryPolicy.ComputeDelay`（指数退避封顶）。
- `ShouldRetry` = `Enabled` 且（`MaxAttempts<=0 || attempt < MaxAttempts`）且（`TotalBudgetMs<=0 || elapsed+nextDelay <= Budget`）。默认两次上限判断均短路 → **无限重试**。

> **Q1 决策**：应用层重试不设次数/时长上限，退避指数封顶（`MaxDelayMs`），仅由**用户取消**（`effectiveToken`）或**不可重试错误**（4xx/上下文溢出等）终止。UI 需通过 `Attempt` 展示"重试中"，让用户明确可主动取消。

DI：`AddSeeingCore` 注册 `ILlmTurnRetryPolicy`（Singleton）与默认 `Options`。`AgentExecutor` 构造注入 `ILlmTurnRetryPolicy?`（可空；缺省时内部退回"只尝试一次"，保证既有无参行为兼容）。

### 5.3 `AgentExecutor` 接入（Core）

`AgentExecutor.cs:170-357` 的每轮结构改造为 attempt 循环：

```
for (step...) {
    for (attempt = 1; ; attempt++) {
        emit StreamStartEvent { LoopId, Step = step, Attempt = attempt }   // 重试时重发 = 重置
        reset per-turn accumulators: streamedContent/Reasoning/toolCalls/usage/lastUsage
        start llmChannel + llmTask (调用 _llm.CompleteStreamAsync)
        drain llmChannel → 累积 + 推送 StreamDeltaEvent
        await llmTask
        if (llmException == null) break;

        if (attempt < policy 允许 && policy.CanRetry(llmException, effectiveToken)) {
            var delay = policy.NextDelay(attempt);
            if (policy.ShouldRetry(attempt, elapsed, delay)) {
                elapsed += delay;
                await Task.Delay(delay, effectiveToken);
                continue;
            }
        }
        // 不可重试 / 耗尽 → 保持现有 ErrorEvent + LoopComplete(false) 路径
        break;
    }
    ...
}
```

要点：
- **重试安全性**：本 step 的工具只在 LLM 成功返回后、依据 `assistantMessage.ToolCalls` 执行（`AgentExecutor.cs:476`），因此**本轮 LLM 失败时本 step 的工具必然未执行**；重试不重放副作用。`history` 中上一 step 的 assistant/tool 消息保持不变。
- **回滚范围**：仅重置本轮累积变量与该轮事件产出；history 不动。
- **取消优先**：`effectiveToken` 取消时 `OCE` 不重试，走既有取消路径。
- **状态清理**：每次 attempt 前必须清空 `streamingContent`/`streamingReasoning`/`streamingReasoningSignature`/`accumulatedToolCalls`/`lastUsage`/`llmException`，避免重试内容与旧内容拼接。
- 现有 `llmException != null` 失败路径（`:363-397`）、空响应路径（`:399-422`）与成功路径（`:424+`）保持**不动**，位于 attempt 循环之后；耗尽时由其处理。
- **channel 完成语义**：`llmTask` 的异常分支必须 `llmChannel.Writer.Complete()`（不带 error）。若传 `Complete(ex)`，`ReadAllAsync` 会抛出该异常、中断 drain 并跳过重试判定（既有 `llmException` 分支因此形同死代码）。

### 5.4 投影层：`ChatEventTracker`

`ChatEventTracker.cs`

1. `StreamStartEvent` 分支（`:44-53`）：
   - 若当前轮已有正在累积的 assistant 消息（`_currentAssistantMessage`，其 Id 规则为 `{LoopId ?? sessionId}_step{Step}`，`:189`）→ 用 `session.RemoveMessage(_currentAssistantMessage)` 移除该未完成消息，避免重试残留旧气泡。
   - 兜底：若 `_currentAssistantMessage` 为空但 `session.Messages` 中仍存在同 Id 消息（如进程重载后投影重建），用 `session.RemoveMessages(m => m.Id == expectedId)` 移除。
   - 再执行现有"清引用"逻辑（`_currentAssistantMessage = null`、清 `_currentStep` 等）。
2. `ErrorEvent` 分支（`:147-149`）：**移除** `session.AddMessage(SystemMessage(...))`；**不写** `session.Context`（Q2：只靠 `ErrorEvent` + 执行记录）。
3. `LoopCancelledEvent` 分支（`:151-153`）：同上，移除 `SystemMessage` 持久化，不写 Context（Q2）。
4. `EnsureAssistantMessage`（`:182-200`）：加"同 Id 先移除"防御，保证同轮至多一条活跃 assistant 消息。
5. **错误文本的持久化真相源**：`ExecutionJobService.MarkSessionError` 写入 `SessionData.Metadata[SessionMetadataKeys.LastError]`（不进 `Messages`/LLM 历史），`ClearSessionError` 清除；`TaskStatusTool` 回落优先读取该键，保留存量 `"错误:"` 前缀兜底（避免 `task_status` 失去错误文本）。

> 注意：`SessionData` 已具备 `RemoveMessages`/`RemoveLastMessage`（`SessionData.cs:296-330`），无需新增 API。

### 5.5 历史构建：`BuildHistoryFromSession`

`ExecutionJobService.cs:1083`

- 由于 5.4 已移除运行时错误/取消的持久化，**新数据不再产生此类消息**；`BuildHistoryFromSession` 只需处理**存量数据兜底**（见 10）。
- 兜底规则：跳过 `role == system` 且内容以 `错误: ` / `对话已取消: ` 开头的消息（旧版 `ChatEventTracker` 的产物）。
- 为避免前缀匹配脆弱：同时支持显式标记——若 `Metadata` 含 `transient=true` 也跳过（预留：Q2 已决定新数据不产生此类消息，仅存量/未来使用）。
- 不得误伤刻意进入 LLM 的消息：`SystemReminder`（todo 提醒等）与 `schema_snapshot` 载体（已有 `IsSchemaSnapshotCarrierMessage` 过滤，`:1155`）保持原行为。

### 5.6 传输层边界（不变 + 加固）

`RetryLlmClientDecorator.cs`
- 保持 `:118` "已产出 chunk 即不重试"逻辑不变，补 XML 注释说明"回滚由应用层负责，传输层不得重试已交付流"。
- 日志可读性（`:86-89`、`:131-134`）：当 `MaxRetries <= 0` 时把 `{Max}` 渲染为 `∞`/`unlimited`，消除 `Attempt=1/0` 的误读。**不改** `MaxRetries<=0 = 无限` 的既有语义（有测试固化：`LlmRetryPolicyTests.ShouldRetry_UnlimitedRetries_UsesBudgetOnly`），仅澄清。

### 5.7 WebUI 适配

`samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`

- `HandleStreamStart`（`:372-389`）：当前条件 `evt.Step > 0 || _currentAssistantMessage == null` 在 **step=0 重试**时会漏清。改为"每次 `StreamStart` 都重置本轮渲染状态"：置空 `_currentAssistantMessage`、清 `_toolCallPositions`、清 `_accumulatedContent`/`_accumulatedReasoning`（后者当前只在 `HandleLoopStart` 清，`:346-348`）。正常 `step` 递增路径不受损。
- 错误展示：`EventStreamHandler` 记录 `LastError`（`HandleError` 由 `FormatErrorDetails` 赋值，`HandleLoopStart` 清除），`SessionWindow.razor` 以错误提示条呈现；不再依赖被移除的 `SystemMessage`。
- `SessionWindow.razor:1356`（用户取消写 `SystemMessage`）：**移除**本地写入（Q3），取消状态由 `SessionState.CompleteExecution()` 与 `LoopCancelledEvent` 表达。

### 5.8 重试等待的 UI 可见性

**问题**：无限重试 + 指数退避（单次最长 `MaxDelayMs`），等待期间若无反馈，用户会误以为卡死，且不知道可取消。

**事件契约**：新增 `LlmRetryScheduledEvent`（`src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`）

```csharp
public static class MessageEventType { /* ... */ public const string LlmRetry = "llm.retry"; }

public record LlmRetryScheduledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LlmRetry;

    public int Step { get; init; }
    /// <summary>即将进行的第几次尝试（2、3…）。</summary>
    public int Attempt { get; init; }
    /// <summary>本次退避等待时长（毫秒），供 UI 倒计时。</summary>
    public int NextDelayMs { get; init; }
    /// <summary>失败简述（如"网络连接错误"）。</summary>
    public string? Reason { get; init; }
}
```

**产出**：`AgentExecutor` 在 `ShouldRetry` 判定为真、`Task.Delay` 之前 `writer.WriteAsync(new LlmRetryScheduledEvent{...})`（§5.3 循环内）。

**投影**：`ChatEventTracker` **不处理**该事件（不落 `Session`）；`ExecutionJobService.ShouldPersistEvent` 对其返回 `false`（`ExecutionJobService.cs:913`），仅经事件总线发布给 UI。

**UI**：
- `AgentLoopInfo`（`EventStreamHandler.cs:18`）新增 `RetryAttempt`/`RetryNextDelayMs`/`RetryReason`/`RetryAt`。
- `EventStreamHandler` 新增 `HandleLlmRetry`：更新上述字段并触发 `OnStateChanged`；清除时机为收到该轮 `StreamStart`、首个 `StreamDelta`、`StreamComplete`、`LoopComplete`、`LoopCancelled`、`Error`。
- 渲染：`EventStreamHandler` 暴露 `IsRetrying`/`RetryText`（按 `RetryAt + NextDelayMs` 计算倒计时）；`SessionWindow.razor` 在消息区末尾显示重试提示条（`retry-banner`），重试期间以 1s Timer 刷新倒计时。
- `SessionState.IsExecuting`（`SessionState.cs:149`）在重试期间**保持 true**，避免侧边栏/输入区误判为空闲。
- 取消：重试等待在 `Task.Delay(delay, effectiveToken)` 上进行，用户取消立即中断并走 `LoopCancelled` 路径；UI 取消按钮全程可用，无需新增链路。

**Gateway/ACP**：`llm.retry` 为新增可选事件，未识别渠道忽略即可（不阻断）。Gateway 映射为状态通知属后续增强，非本期必需。

**开关**：`LlmTurnRetryOptions.Enabled=false` 时不产生该事件。

---

## 6. 数据流

**正常一轮**
`LoopStart` → `StreamStart(step=0, attempt=1)` → `StreamDelta*` → `StreamComplete` →（有工具）`ToolCall*` → `StreamStart(step=1, attempt=1)` → …

**重试一轮（P1 场景）**
`StreamStart(step=0, attempt=1)` → `StreamDelta*` → 连接中断（`LlmStreamingException`）
→（应用层判定可重试）→ 丢弃本轮产出 → **`LlmRetryScheduled(attempt=2, nextDelayMs)`**（UI 显示"X 秒后重试（第 K 次）"，可取消）→ 退避等待
→ `StreamStart(step=0, attempt=2)`（投影移除同 Id 未完成消息、UI 清空重试态）
→ `StreamDelta*` → `StreamComplete`（最终仅一条该轮 assistant 消息）。

---

## 7. 配置与默认值

| 配置 | 层级 | 默认 | 备注 |
|------|------|------|------|
| `LlmTurnRetryOptions.Enabled` | 应用 | `true` | 可关闭 |
| `LlmTurnRetryOptions.MaxAttempts` | 应用 | `0`（不限，Q1） | 可选收紧 |
| `LlmTurnRetryOptions.TotalBudgetMs` | 应用 | `0`（不限，Q1） | 可选收紧 |
| `LlmTurnRetryOptions.BaseDelayMs/MaxDelayMs` | 应用 | `500/10000` | 退避指数封顶 |
| `ProviderConfig.MaxRetries` | 传输 | `0`（无限，仅 budget） | **语义不变**，仅修日志显示 |
| `ProviderConfig.RetryBaseDelayMs/RetryMaxDelayMs/RetryTotalBudgetMs` | 传输 | `500/10000/120000` | 不变 |

> 应用层与传输层**双层重试**的叠加需注意：传输层只处理"未产出前"失败并自带退避；应用层只在传输层最终抛出后介入，因此不会同时重试同一失败。

---

## 8. 边界与不变量

1. 同一 `(LoopId, Step)` 至多一条活跃 assistant 消息。
2. 运行时错误/取消不进入 `Messages` 与 LLM 历史。
3. 工具已执行后不得重试/回放（本设计中由"LLM 失败时工具未执行"天然保证）。
4. 每轮重试默认不设次数/时长硬上限（Q1），仅用户取消与不可重试错误终止；退避指数封顶。
5. 用户取消优先于重试。
6. schema 只在 `ExecutionJobService` 计算（本设计不触碰）。

---

## 9. 测试策略

| 层 | 测试 | 断言 |
|----|------|------|
| Core | `DefaultLlmTurnRetryPolicyTests` | 4xx 不重试；`IOException`/`Timeout` 重试；attempt 上限；budget 边界 |
| Core | `AgentExecutorTurnRetryTests` | mock `ILlmService`：首次抛 `LlmStreamingException` 后成功 → 仅一条该轮 assistant 内容（无重复）、发出两次 `StreamStart`（attempt=1/2）、`history` 无重复 |
| Core | 取消场景 | `effectiveToken` 取消不重试，走 `LoopCancelled` |
| Core | 重试可见性 | `LlmRetryScheduledEvent` 在 `Task.Delay` 前发出，字段含 `Attempt`/`NextDelayMs`/`Reason` |
| Hosting | `ChatEventTrackerTests` | 重发同 `(LoopId,Step)` `StreamStart` → 旧未完成 assistant 消息被移除；`ErrorEvent`/`LoopCancelledEvent` 不新增 `Messages` |
| Hosting | `BuildHistoryFromSessionTests` | 存量 `错误: ` / `对话已取消: ` system 消息被跳过；`transient=true` 标记被跳过；`SystemReminder` 不被误伤 |
| Core | `RetryLlmClientDecoratorTests`（既有） | 保持"已 yield 不重试"；`MaxRetries=0` 预算耗尽 |
| WebUI | `EventStreamHandler` 重置 | step=0 重试时累积缓冲被清空（若可行） |

---

## 10. 兼容性与迁移

- `StreamStartEvent.Attempt` 为可选新增字段，JSON 反序列化向后兼容；现有构造点无需改动。
- 存量会话中的 `错误: ` / `对话已取消: ` system 消息：由 `BuildHistoryFromSession` 前缀过滤兜底，不强制数据迁移（可选：会话加载时一次性清理或补打 `transient` 标记）。
- 应用层重试默认开启但**有界**；如需完全关闭可设 `Enabled=false`。
- `RetryLlmClientDecorator` 行为不变，仅日志与注释。

---

## 11. 并行实施拆分（供 writing-plans）

| 轨道 | 内容 | 依赖 | 涉及文件 | 可并行 |
|------|------|------|----------|--------|
| T1 | 错误/取消不落消息 + 历史过滤 | 无 | `ChatEventTracker.cs`、`ExecutionJobService.cs`、测试 | 是（与 T2 有同文件冲突，见下） |
| T2 | `StreamStartEvent.Attempt` + 幂等重置契约（事件 + 投影 + UI + Gateway 透传） | 无 | `MessageEventTypes.cs`、`ChatEventTracker.cs`、`EventStreamHandler.cs`、Gateway mappers、测试 | 是（`ChatEventTracker` 与 T1 冲突） |
| T3 | `ILlmTurnRetryPolicy` + Options + 默认实现 + DI | 仅依赖 T2 的事件字段（可先按接口开发） | `Abstractions/Llm/*`、`Core/Llm/DefaultLlmTurnRetryPolicy.cs`、`AddSeeingCore`、测试 | 是（与 T1/T2 无文件冲突） |
| T4 | `AgentExecutor` 接入轮次重试 | 依赖 T2 事件字段 + T3 策略 | `AgentExecutor.cs`、测试 | 否（依赖 T3） |
| T5 | 传输层边界注释 + 日志修正 + 测试加固 | 无 | `RetryLlmClientDecorator.cs`、`LlmRetryPolicyTests.cs` | 是 |
| T6 | 重试等待可见性：`LlmRetryScheduledEvent` + `EventStreamHandler` 状态 + 渲染倒计时 | 依赖 T2/T3 | `MessageEventTypes.cs`、`AgentExecutor.cs`、`EventStreamHandler.cs`、`MessageRenderPipeline.cs`、`SessionState.cs`、测试 | 否（依赖 T3/T4） |

**冲突点**：T1 与 T2 都修改 `ChatEventTracker.cs`。建议将 `ChatEventTracker` 的两处改动（StreamStart 重置 + ErrorEvent/LoopCancelled 不落消息）**合并为同一轨道 T1'**，或由同一执行者顺序完成；`BuildHistoryFromSession` 独立到 T1b。

**最大化并行方案**（3 个 Agent 并发）：
- Agent A：T1'（`ChatEventTracker` 全部改动 + 测试）
- Agent B：T2 的事件契约与消费端（`MessageEventTypes.cs`、WebUI、Gateway）+ T3（策略层）
- Agent C：T1b（`BuildHistoryFromSession` 过滤）+ T5（传输层加固）
- 收尾（串行）：T4 `AgentExecutor` 接入 + T6 重试可见性（依赖 A 的事件语义 + B 的策略 + T4 的事件产出）

---

## 12. 开放问题（待确认）

- **Q1（已决）**：应用层**无限重试**、不设次数/时长上限；仅用户取消与不可重试错误终止；退避指数封顶。
- **Q2（已决）**：错误/取消只靠 `ErrorEvent` + 执行记录，**不写** `session.Context`。
- **Q3（已决）**：`SessionWindow.razor:1356` 的本地取消 `SystemMessage` **一并移除**。
- **Q4（已决）**：取消**保持现状**（`ExecutionStatus.Cancelled`），不额外写 `ErrorMessage`。
