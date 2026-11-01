# Agent 自动反思总结（Reflection）设计 v3

- 日期：2026-11-01
- 状态：待审批（v3 为架构级重做；v1/v2 已废弃）
- 前置：本文档包含**必须先行完成的地基重构**（§3），Reflection 包本身建立在重构后的契约上
- 参考：Nous Research Hermes Agent background review（固定快照 `bf0203a`）

## 0. v3 为什么推倒重来

v1/v2 被两份独立架构审查判定为「补丁叠补丁」。核心批评我接受，并已逐条回源码核实：

**v2 的致命前提有三个是假的：**

| v2 的假设 | 实测事实 | 后果 |
|---|---|---|
| `ChatExecutionContext.IsTopLevel` 是「零 I/O 的现成事实」 | 唯一构造点 `ExecutionRunner.cs:504-518` **从不赋值**，恒为默认 `true`；唯一 `false` 赋值在 `CreateSubAgentContext`（`ChatExecutionContext.cs:123`），该方法**零调用者** | 「排除子代理」门禁永不生效，每个 `explore`/`general` 轮次都会入队 |
| 复盘写的记忆下一轮经 `MemoryRecallHandler` 自动注入 | `MemoryRecallHandler` 读 `userMessage`/`message`/`content`（`:40-42`），但 `ChatBeforeStart` 的两处触发（`LlmService.cs:330-338`、`:459-468`）只传 `modelId`/`provider`，`mutable` 为 null | handler 恒在 `:43` 提前返回；且即使写入 `Mutable["systemPrompt"]` 也无消费方（系统提示词走另一条 `LlmSystemPrompt` + `promptOutput`，`:511-530`）。**记忆自动注入是死路径** |
| `CachedInputTokens + CachedOutputTokens` 可作日账本累计源 | `BudgetUpdateHook.cs:85-86` 是**覆盖写**（最后一次 LLM 调用），且该 hook 仅在 TokenBudget 集成启用时注册 | 8 步复盘只记到最后一步，日预算形同虚设 |

**另有一处 v2 自身引入的设计 bug：**

`§5.5` 写了 `Deny(PermissionKind.File, "*", 100)`。但 `read` 经 `DefaultToolPermissionPolicy.cs:25` 映射为 `filesystem.read` → `PermissionKind.File`，而 `PermissionService.cs:168-170` 对**匹配到的 Deny 规则**直接短路硬拒。配套的 `Allow(File,"*")` 在步骤 3b 被忽略（资源类 kind 不走 Allow 短路，`:172-179`）。结果：**复盘会话能写技能、不能读文件**——「证据校验」全部失效。

**根本定性错误**：v2 把「接线」当「承载」。它往一个全仓零触发的 hook 契约里塞两个未类型化 bool，来绕过「一轮执行的结果事实没有一等载体」。仓库里同类证据：`HookDataContract.AgentBeforeInvoke.IsTopLevel` 早已声明 `Required: true`，同样从没人填过——**契约里写了，但没人填，所以没人发现它是假的**。

v3 的立场：**先把地基补对，再写包。** 不补地基的版本一律是补丁。

### 已确认的既有缺陷（与本次功能无关，但必须一起修）

1. `HookRegistry` 约 46 个点，**16 个在 `src` 内零引用**（`AgentBeforeInvoke`、`SubagentStarted/Completed`、全部 6 个 `Memory*`、`PermissionAsk/Denied`、`SessionCreated/Completed/Activated/…`、`ShellCommandExecuteBefore`、`ToolBatch*`）。
2. `ExecutionRecord.ProcessedMessageCount`（`ExecutionRecord.cs:63`）**全仓零赋值**。
3. `LoopCompleteEvent.TotalSteps`/`Usage`/`Duration`（`MessageEventTypes.cs:169-196`）在 `AgentExecutor.cs:445-452` 真实赋值，但 `ExecutionRunner.cs:309-336` 消费事件流时**只挑了 `Success`/`Error`，其余丢弃**。
4. `GetTaskableAgentsAsync`（`AgentManager.cs:156-166`）**不看 `IsHidden`**，所以 `build`（`Mode=All`）与 `summary`（`All` + `IsHidden`）都在 `task` 工具的可用子代理列表里。
5. `PermissionService.EvaluateSkillAsync`（`:66-73`）**零生产调用者**，`PermissionKind.Skill` 是死路径——写 `Allow(Skill,…)` 或 `Deny(Skill,…)` 都不产生任何效果。
6. Memory 的 `ITokenTracker` / `IQuotaManager`（`MemoryServiceExtensions.cs:135/143`）**零生产消费者**；其 `TokenUsage` record 与 `Abstractions.Llm.TokenUsage` 重名并存。

---

## 1. 意图与目标

`Seeing.Agent.Memory` 有「缓冲 → LLM 抽取 → 日记忆/升格」链路；`Seeing.Agent.Skills` 有目录、`ISkillWriter`、`SkillsContextSource`。**但两者之间没有闭环**：agent 干完活，没有机制判断「这轮有没有值得沉淀的流程性经验」，也不会把经验变成可复用技能。

Hermes 的关键结论：自学习不是「让模型顺便反思一下」，而是**三种不同权限、不同触发时机的职责**：

| 职责 | 触发 | 能写什么 |
|---|---|---|
| 前台 turn | 用户消息 | 正常干活 |
| **后台复盘（本文档范围）** | 回合收尾 + 计数到期 | 只在收尾后判断「有无可复用信息」，产出记忆与技能 |
| 周期整理（Curator，**排除**） | 7 天 / 空闲 | 技能生命周期、语义合并 |

### 目标

1. **闭环**：收尾后异步判断是否有可沉淀经验，产出物在下一轮自动可见（**依赖 §3.3 的记忆注入修复**）。
2. **零前台代价**：不阻塞、不改写已生成回答、不污染主会话 transcript；失败 best-effort。
3. **写入能力收敛**：自动路径**只有创建能力**；修改/删除只产出「建议清单」，由人执行。
4. **可追溯**：每次复盘产出什么、依据哪一轮，有落盘记录。
5. **防污染**：提示词含反模式清单（不该学什么）。
6. **地基正确**：执行结果、运行时等待、会话用途三者有一等契约，不靠字段拼接。

### 非目标

- 不做 Curator。
- 不做 WebUI 审批页。
- 不覆盖 Scheduler / Heartbeat 旁路（`AgentJob`/`HeartbeatJob` 直连 `IAgentExecutor`，不产生 `ExecutionRecord`、不写会话消息）。
- 不接管压缩。
- 不改 `SessionToolSchemaResolver` 的 schema 单一计算点、不改权限决策链。

### 反目标

- 每轮都复盘。
- 用未类型化字典在 hook 点之间传执行事实。
- 「建议」和「已应用」在日志里混淆。
- 复盘自己触发复盘。

---

## 2. 约束

按 `docs/architecture/04-development-standards.md` 与 `AGENTS.md`：

1. 能力包只依赖 **Abstractions + 原语 + support**。`Seeing.Session` 引用合规（先例：`Seeing.Agent.TokenBudget`、`Seeing.Agent.Acp` 的 csproj 均含）。
2. **区分两类端口**：宿主演进端口（执行结果、运行时等待）属于 `Abstractions`，必须新增；单功能私有端口不得进 Abstractions（否则成死契约）。v2 §3.2 把两者混为一谈，是它拒绝建 `ExecutionOutcome` 的借口——这条修正。
3. 工具注册走模块 `ActivateAsync` / `DeactivateAsync`。
4. 配置节走 `IConfigSectionRegistry`。
5. 后台服务用 `BackgroundService + IModuleHostedService` + `AddModuleHostedService<T>`。
6. 登记点四处：`BuiltInCapabilitySets.Full`、`FullCapabilityComposition`、`Seeing.Agent.slnx`（Folder）、对应守门测试。

---

## 3. 地基重构（Phase 0，先做，独立价值）

本节全部是**既有缺陷修复**，任何一项都可以单独合入并立即产生价值。Reflection 依赖它们。

### 3.1 执行结果聚合体

**问题**：一轮执行的事实（状态、agent 名、是否顶层、是否真跑了 agent、步数、用量、耗时）散落在 `ExecutionRunner.RunOnceAsync` 的局部变量里。`ExecutionCompleteEvent` 只有 `SessionId/ExecutionId/Status`（`HostingLifecycleEvents.cs:68`）。任何第二个消费方（成本统计、审计、Memory、UI 后处理）都要重新拼一遍。

**改法**：

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionOutcome.cs
/// <summary>
/// 一轮执行的终态事实聚合体：终态通知的唯一真相源。
/// 字段全部由 ExecutionRunner 在 finally 前填充，不得留恒默认值的必填项。
/// </summary>
public sealed record ExecutionOutcome
{
    public required string SessionId { get; init; }
    public required string ExecutionId { get; init; }
    public required ExecutionStatus Status { get; init; }

    /// <summary>本轮实际执行的 agent 名；命令短路时为 null。</summary>
    public string? AgentName { get; init; }

    /// <summary>本轮是否真的执行了 agent（命令短路 / 纯系统批次为 false）。</summary>
    public bool AgentExecuted { get; init; }

    /// <summary>是否顶层执行；由 SessionKind 判定，不依赖 ChatExecutionContext。</summary>
    public bool IsTopLevel { get; init; }

    public string? ParentSessionId { get; init; }
    public int TotalSteps { get; init; }
    public TokenUsage? Usage { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; }
}
```

配套改动：

- `ExecutionRecord` 增加 `AgentName` / `AgentExecuted` / `TotalSteps` / `Usage`；**删除 `ProcessedMessageCount`**（恒 0，无来源）。
- `ExecutionRunner` 在消费事件流处（`:307-336`）捕获 `LoopCompleteEvent.TotalSteps` / `Usage` / `Duration`（现在被丢弃）。
- `ExecutionCompleteEvent` 携带 `ExecutionOutcome`（或改为继承/组合），让终态事实有唯一出口。
- **`IsTopLevel` 的真值来源**：`session.Kind`（`SessionKind.SubAgent` → false）。这是结构性事实，不依赖任何未填充字段。

**归一化观察者**：

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionOutcomeObserver.cs
/// <summary>执行终态观察者。ExecutionRunner 在 finally 中对全部注册者扇出。</summary>
public interface IExecutionOutcomeObserver
{
    /// <summary>是否关心本次结果（便于按用途过滤，避免每轮都进入下游）。</summary>
    bool ShouldObserve(ExecutionOutcome outcome) => true;

    Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default);
}
```

`ExecutionRunner` 经 `IEnumerable<IExecutionOutcomeObserver>` 扇出，**不新增 hook、不拼字典、不加构造依赖**（`_eventPublisher` 已在，`ExecutionRunner.cs:52`）。

### 3.2 `ISessionRuntime.DeliverAndAwaitAsync`

**问题**：程序驱动的「等一次执行跑完」现在只能 `WaitForIdleAsync`（25ms 轮询，`SessionRuntime.cs:835`）+ `GetOverview().LastExecution` 猜。已有 **4 个调用点**在这么做：`TaskTool.cs:197`/`:265`、`TaskStatusTool.cs:85`、`Gateway/SessionEventPump.cs:111`。

两个具体缺陷：
- `WaitForIdleAsync` 的 10 分钟内部超时是**记 Warning 后正常 return**（`:829-833`），不抛异常。与业务超时撞值时会被误判为「跑完了」。
- 同一会话被投递两次时，它等到两轮都空闲，`LastExecution` 只剩后者——「这次执行的终态」无法回答。

**改法**：

```csharp
// ISessionRuntime 新增
/// <summary>投递并等待本次执行的终态；返回该次执行的 outcome。</summary>
/// <remarks>与 WaitForIdleAsync 的区别：绑定具体 executionId，多轮叠加时不歧义；超时抛 OCE。</remarks>
Task<ExecutionOutcome> DeliverAndAwaitAsync(InboxMessage message, CancellationToken ct = default);
```

实现：`SessionRuntime` 维护 `ConcurrentDictionary<string, TaskCompletionSource<ExecutionOutcome>>`（key = executionId），在记录进入终态时完成。`WaitForIdleAsync` 保留给「只是等空闲」的 UI/Gateway 场景。

**顺带收益**：4 个既有轮询调用点可逐步迁移。

### 3.3 记忆注入迁移到 ContextSource

**问题**：记忆自动注入是死路径（见 §0）。技能走 2026-10-24 的 ContextSource 管道，记忆走老的 `chat.before_start` 改 `systemPrompt`——而那条路径没有消费方。

**改法**：

1. 新增 `MemoryContextSource : IContextSource`（`capabilities/Seeing.Agent.Memory`），`Target = System`，与 `SkillsContextSource` 同构：canonical 为召回结果的稳定序列化，基线字节稳定，变化只叙述增量。
2. **删除 `MemoryRecallHandler`** 及其注册（`MemoryModule.RegisterHooks:133/148`）。
3. `MemoryRetrievalMode.AutoInject` / `Both` 的语义此时才真正生效。

**为什么必须搭车修**：不修它，Reflection 目标 1「产出物下一轮自动可见」只剩技能半条腿，复盘写的记忆只能靠模型自己 `memory_search` 才有机会被看到。

### 3.4 会话用途正交维度

**问题**：`SessionKind` 只有 `Root`/`SubAgent`，且被**结构性**使用——`SessionGroupManager.ValidateMemberInvariant`（`:961-986`）强制 `Relation==Child ⇔ Kind==SubAgent`。所以**不能加 `SessionKind.Internal`**（会撞组不变量）。

现有「程序创建会话」的语义差异靠 Metadata 键散落打补丁：`origin_tool_call_id`、`control.*`、`inbox.*`、`context.*`，设计还想加 `reflection.origin`。

**改法**：在 `src/primitives/Seeing.Session/Core/` 加**正交**维度：

```csharp
/// <summary>会话来源/用途：与 SessionKind（结构语义）正交，用于视图过滤与统计口径。</summary>
public enum SessionOrigin
{
    User = 0,          // 用户直接发起
    SubAgentTask = 1,  // task 工具委派的子代理（现有行为）
    Reflection = 2,    // 复盘等平台内部执行
    Scheduler = 3,     // 计划任务派发
    Heartbeat = 4
}
```

`SessionData.Origin { get; set; } = SessionOrigin.User;`

`ISessionGroupManager` 增加 `CreateInternalAsync(parentId, agentName, title, snapshot, scenario, origin, ct)`：`Kind = SubAgent`（满足组不变量）、`Origin = Reflection`、**入独立 partition 或标记不参与任务视图**。

三个必须按 origin 过滤的消费方：
- `TaskStatusTool.GetChildTasksStatusAsync`（`:224-226`）——枚举全部子会话
- `TaskTool.GetSubAgentStatusSummaryAsync`（`:350-352`）
- `TuiSurfaceProvider`（`:146-157`）——递归把子会话加入 active group 订阅

同时修 `TaskTool.cs:117` 的 `Kind != SubAgent` 判定改为显式白名单（`Kind == SubAgent && Origin == SubAgentTask`）。

### 3.5 ~~`GetTaskableAgentsAsync` 补 `IsHidden` 过滤~~（已撤销，2026-11-01）

**原提案（错误，已回滚）**：`AgentManager.cs:159-165` 只过滤 `Disabled` / `Mode != Primary` / `Runtime != Native`，不看 `IsHidden`，判定 `build` 与 `summary` 出现在「可用子代理类型」列表里是 bug，建议加 `&& !a.IsHidden`。

**为什么错**：`AgentDefinition.cs:41` 的契约注释明确写 `IsHidden` = 「不在用户选择列表显示，**但仍可作为 SubAgent 使用**」。隐藏 ≠ 不可委派，这是两个正交语义：

| 维度 | 决定什么 | 判定点 |
|---|---|---|
| `IsHidden` | 是否出现在**用户可见的 agent 选择列表**（UI） | `GetPrimaryAgentsAsync` 的 `!IsHidden` |
| `Mode` | 是否可被 `task` 工具**委派** | `GetTaskableAgentsAsync` 的 `Mode != Primary` |

`general`（`SubAgent` + `IsHidden=true`）正是「隐藏但可委派」的正例，`TaskTool.cs:63` 的参数描述直接举例「如 explore、general」。原提案会让内置可委派集合从 `{build, plan, explore, general}` 静默收缩为 `{build, plan, explore}`，并与 `AgentsPromptSectionContributor`（只看 `Mode == SubAgent`）的系统提示清单矛盾——把一个符合契约的行为改成三处口径不一致。

**撤销记录**：实施时该任务（T5）已提交 `69027046`，审查发现后果后 revert 为 `c8ac9e19`。

**`reflection` agent 的正确模式**：`Mode = AgentMode.Primary` + `IsHidden = true`。

- `GetTaskableAgentsAsync`：按 `Mode != Primary` 排除 ✓（不可被 `task` 委派）
- `GetPrimaryAgentsAsync`（`:172-173`）：按 `!IsHidden` 排除 ✓（不出现在 UI 选择列表）
- `TaskTool.cs:92-93`：显式拒绝 `Mode == Primary` ✓（双保险）

这不是「两个无关开关的巧合」，而是两个维度各司其职：`Primary` 表达「不可作为子任务委派」，`IsHidden` 表达「不显示在 UI」。`reflection` 同时需要这两个否命题，所以同时设这两个值是正确的，不是绕过。

**遗留技术债（本期不修，已在 §9 登记）**：`GetSubAgentsAsync` / `GetAccessibleSubAgentsAsync` 零消费者且缺 `IsHidden` 过滤；`TaskTool.ExecuteAsync` 不校验 `IsHidden`。


### 3.6 会话活动追踪下沉

`ISessionActivityTracker`（`Memory/Abstractions/IMemoryEvolutionService.cs:10-15`）的签名 `Touch/GetIdleSessions/Clear` 与 Reflection 需求**逐字一致**，实现仅 24 行。因分层不可达而复制一份是典型的「因纪律而复制」。

**改法**：接口移到 `src/primitives/Seeing.Session/Core/ISessionActivityTracker.cs`，实现 `SessionActivityTracker` 同样下沉。Memory 与 Reflection 共用同一实例（DI 单例）。

### 3.7 成本账本：先修数据源，不建抽象

**问题**：现在**没有任何一处能回答「这轮花了多少 token」**。`ITokenBudgetManager` 是上下文窗口语义；`CachedInputTokens` 是单次覆盖值；Memory 的 `ITokenTracker`/`IQuotaManager` 是死代码。

**改法（本期）**：
1. §3.1 已让 `ExecutionRecord.Usage` 有真实来源（`LoopCompleteEvent.Usage`）。
2. Reflection 的日账本直接累加 `ExecutionOutcome.Usage`，不再读 `CachedInputTokens`。
3. **删除 Memory 的 `ITokenTracker` / `IQuotaManager` / `SqliteTokenTracker` / `DailyQuotaManager`**（零消费者 + DTO 重名冲突）。这是净减。

`Abstractions/Cost/ITokenUsageLedger` **本期不建**——只有一个消费者时抽出来就是第二个死契约。等 WebUI 成本页或全局配额出现再说。这条与 v2 相反，是审查给的正确优先级。

### 3.8 Hook 体系的处理（关键取舍）

**不补 `agent.after_invoke` 触发。**

理由：`agent.after_invoke`、`SessionCompleted`、`ExecutionCompleteEvent` 三者语义重叠（都是「某轮/某会话结束了」），其中只有 `ExecutionCompleteEvent` 被真实使用（WebUI/TUI/Cli 共 8 处）。在没收敛的情况下补触发，等于给一个僵尸契约续命，并固化「未类型化字典传事实」的模式。

**Memory 的 `ExtractEveryNTurns` 怎么办**：`AgentTurnMemoryHandler`（`MemoryHookHandler.cs:130-169`）改为实现 `IExecutionOutcomeObserver`：

```csharp
public sealed class AgentTurnMemoryHandler : IExecutionOutcomeObserver
{
    public bool ShouldObserve(ExecutionOutcome o) =>
        o.AgentExecuted && o.IsTopLevel && o.Status == ExecutionStatus.Completed;

    public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct)
    {
        _flush.TryFlushAfterTurn(outcome.SessionId);
        return Task.CompletedTask;
    }
}
```

这样 `ExtractEveryNTurns` 真正生效，**且口径正确**（顶层 + 真实执行），而 `SessionMemoryBuffer.OnAgentTurnCompleted` 无条件 `TurnCount++`（`SessionMemoryBuffer.cs:48-69`）的问题一并被上游过滤解决。

**那 16 个零引用 hook 点**：本文档不处理，登记为独立技术债（见 §9.1）。除 `AgentAfterInvoke` 外不新增、不修、不删——避免范围爆炸。

---

## 4. Reflection 包设计

建立在 §3 的契约上。

### 4.1 包结构

```
src/capabilities/Seeing.Agent.Reflection/
├── Seeing.Agent.Reflection.csproj      # Abstractions + Seeing.Session
├── ReflectionModule.cs                 # ISeeingModule, id = "reflection"
├── Configuration/ReflectionOptions.cs
├── Abstractions/
│   ├── IReflectionQueue.cs
│   ├── IReflectionExecutor.cs
│   └── IReflectionJournal.cs
├── Core/
│   ├── ReflectionQueue.cs
│   ├── ReflectionExecutor.cs
│   ├── ReflectionJournal.cs            # runs/ 落盘
│   ├── Suggestions.cs                  # 「建议清单」模型与解析
│   └── Prompts.cs
├── Integration/
│   ├── ReflectionObserver.cs           # IExecutionOutcomeObserver
│   └── Tools/SkillCreateTool.cs
├── Background/ReflectionWorker.cs
└── Extensions/ReflectionServiceExtensions.cs
```

### 4.2 触发

`ReflectionObserver : IExecutionOutcomeObserver`：

```csharp
public bool ShouldObserve(ExecutionOutcome o) =>
    _options.CurrentValue.Enabled
    && _options.CurrentValue.Mode != ReflectionMode.Off
    && o.AgentExecuted                       // 排除命令短路
    && o.IsTopLevel                          // 排除子代理（真值来自 SessionKind）
    && o.AgentName != ReflectionAgentName    // 防递归第 1 重
    && o.Status == ExecutionStatus.Completed // 失败/中断不复盘
    && _sessions.IsReflectionSession(o.SessionId) == false;  // 防递归第 2 重

public Task OnExecutionCompletedAsync(ExecutionOutcome o, CancellationToken ct)
{
    if (_turns.Increment(o.SessionId) < _options.CurrentValue.TurnNudgeInterval)
        return Task.CompletedTask;
    _turns.Reset(o.SessionId);
    _queue.TryEnqueue(new ReflectionRequest(o.SessionId, o.ExecutionId, ReflectionTriggerKind.TurnNudge));
    return Task.CompletedTask;
}
```

**防递归三重**（v2 有两重是假的）：
1. `AgentName != "reflection"`
2. 源会话 `Origin != SessionOrigin.Reflection`
3. 复盘子会话的 `Origin == SessionOrigin.Reflection` → 其 `ExecutionOutcome.IsTopLevel` 应为 **true**（它是自己那轮的顶层），所以第 3 重必须靠 `Origin` 判定，不能靠 `IsTopLevel`。

**空闲触发**：`ReflectionWorker` 每 1 分钟 tick，`ISessionActivityTracker.GetIdleSessions(IdleMinutes)` → 入队 `Idle`。

**去重**：同会话队列中最多一条；已在执行则只更新「最新 executionId」。

### 4.3 执行

```
1. 取快照（只读）
   session = sessionManager.TryGetOrLoadAsync(sessionId)   // 绝不用 LoadAsync
   activeMessages = session.GetActiveMessages()
   → 紧凑 transcript，单条截断 MaxToolResultChars，总长 MaxTranscriptChars

2. 建内部会话
   child = groupManager.CreateInternalAsync(
              sourceSessionId, "reflection", $"{title} · 复盘",
              snapshot, parent?.Scenario, SessionOrigin.Reflection)

3. 投递并等待（用 §3.2 的新契约）
   outcome = runtime.DeliverAndAwaitAsync(new InboxMessage {
       TargetSessionId = child.Id,
       Sender = new InboxSender { Kind = InboxSenderKind.Session, Id = sourceSessionId },
       Body = BuildPrompt(transcript, skillCatalog, sourceSessionId),
       Delivery = InboxDelivery.Wake,
       Presentation = InboxPresentation.User,
       AgentId = "reflection",
       ModelId = opts.Model ?? child.SelectedModel
   }, linkedCt);                       // linkedCt = CancelAfter(RunTimeoutMinutes)

4. 收集
   outcome.Status != Completed → journal(skip)
   成功 → 读子会话最终 assistant 消息 → 解析建议清单 JSON
   记 runs/{date}/{executionId}.json

5. 清理
   成功 → groupManager.RemoveSessionAsync(child.Id) + sessionManager.DeleteAsync(child.Id)
   失败 → 保留供诊断（Origin 已隔离，不影响任务视图）
```

**`opts.Model` 生效方式**：`CreateInternalAsync` 复制父会话 `SelectedModel`，所以必须显式在 `InboxMessage.ModelId` 上给出，或投递前写 `child.SelectedModel` 并 `SaveAsync`。v2 只在 `opts.Model` 非空时保存，默认路径下元数据不落盘——已修正。

**超时**：必须自带 `CancelAfter` 的 linked CTS 并捕获 `OCE`，**不能依赖** `WaitForIdleAsync` 的内部超时（它是「记 Warning 后正常 return」）。

**成本闸**：
- `AgentDefinition.MaxSteps`（默认 8）
- `RunTimeoutMinutes`（默认 10）
- `MaxRunsPerHour`（默认 6，进程内滑动窗口）
- `DailyTokenBudget`（默认 300k）——累加 `ExecutionOutcome.Usage`
- `Enabled = false` 默认

**注意 `Presentation = User` 的副作用**：`batch.HasUserInput = true` → `ExecutionRunner.cs:172-185` 会为复盘会话触发 `EnsureTitleFireAndForgetAsync`（**额外一次 LLM 调用**）。必须在 §3.4 的 `Origin` 上补一条门禁：`Origin == Reflection` 不生成标题。这是 v2 漏掉的成本闸。

### 4.4 `reflection` Agent 定义

```csharp
new AgentDefinition
{
    Name = "reflection",
    Description = "后台复盘代理（内部）。只在任务收尾后判断是否有值得沉淀的经验。",
    Mode = AgentMode.SubAgent,   // §3.5 修完 IsHidden 过滤后，SubAgent + IsHidden 足够
    IsNative = true,
    IsHidden = true,
    MaxSteps = opts.MaxSteps,
    Temperature = 0.2,

    AllowedTools = ["read", "grep", "glob", "skill", "skill_create"],
    DeniedTools  = ["task", "write", "edit", "delete"],
    // memory_write 不在列表里 —— 见 §4.7 写入通道说明

    PermissionDefaultEffect = PermissionEffect.Deny,
    PermissionRules =
    [
        PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0),
        PermissionRuleEntry.Allow(PermissionKind.Tool, "grep", 0),
        PermissionRuleEntry.Allow(PermissionKind.Tool, "glob", 0),
        PermissionRuleEntry.Allow(PermissionKind.Tool, "skill", 0),
        PermissionRuleEntry.Allow(PermissionKind.Tool, "skill_create", 0),
        PermissionRuleEntry.Allow(PermissionKind.File, "*", 0),   // 必须：read 的资源门
        PermissionRuleEntry.Deny(PermissionKind.Shell, "*", 100),
        PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
    ],
    SystemPrompt = Prompts.ReflectionSystem,
}
```

**权限配置的三个硬约束（v2 全错了）**：

1. **不能写 `Deny(Tool, "*", 100)`**：规则按 `Priority` 降序、同优先级 Deny 先于 Allow、**取第一个匹配**（`PermissionService.cs:513-517`）。通配 Deny(100) 会吞掉所有 Allow(0)。默认拒绝由 `PermissionDefaultEffect = Deny` 提供。
2. **不能写 `Deny(File, "*")`**：`read`/`grep`/`glob` 都映射为 `filesystem.read` → `PermissionKind.File`，匹配到的 Deny 在 `:168-170` 直接硬拒。必须写 `Allow(File, "*", 0)`（资源类 kind 的 Allow 在 3b 被忽略，但步骤 1 的工作区门是另一回事——**实测验证见 §6**）。
3. **`Allow/Deny(Skill, …)` 无效**：`EvaluateSkillAsync` 零调用者。`skill` 工具走普通 `tool.execute` 权限。

**`AgentMode.SubAgent` + `IsHidden` 的正确性依赖 §3.5**。若 §3.5 不做，则退回 `Primary + IsHidden`（v2 写法），并必须加守门测试。文档按 §3.5 已修的情况写。

### 4.5 工具

`skill_create`，**只有创建语义**：

```csharp
public override string Id => "skill_create";
// 参数：name / description / content
// Mode=SuggestOnly → 只写建议，不落盘
// Mode=ApplyCreate → ISkillWriter.CreateAsync(draft, ConfigLevel.Project, overwrite: false)
//                    Metadata["distilled-by"] = "seeing-reflection"
//                    Metadata["source-session"] = <源会话 id>
```

**工具描述必须遵守 `04 §9` R1–R9**：首句给用途、参数逐个写 `description`、声明 `additionalProperties: false`、`required` 完整、长度约束用 `minLength`/`maxLength`。v2 只列了参数名，会被 `ToolMetadataTests` 拦。

**名称校验必须对齐平台，不得自创**：`SkillWriter.TryValidateName`（`SkillWriter.cs:627-652`）允许大写与空格（现有技能 `Self-Improving + Proactive Agent` 合法），禁用 `\ / : * ? " < > |`。`SkillFileScanner.cs:74` 额外禁控制字符，**两侧本就不一致**——`skill_create` 取两者交集（更严），并在文档登记这个不一致作为独立债。「类级别命名」只经提示词表达，不做硬校验。

### 4.6 建议清单（不是"提案"）

**重要语义修正**：v2 把它叫 "Proposal" 并说「审批后应用」。这暗示自动路径**有修改能力**，只是被暂存。事实是：自动路径**根本没有修改能力**——它没有 update/delete 工具。所以：

- **自动路径只能创建新技能**（`skill_create`，`overwrite:false`）。
- **修改/删除只产出「建议清单」**，由人看后在 Skills 页面手动执行。
- 不存在「待应用的变更指令」，因此不存在「绕审批」问题——因为**没有可绕的审批路径**。

这是 security-by-design 而非 security-by-omission：即使将来有人加了 `skill_update` 工具，正确做法也是先给 `ISkillWriter` 加写门（`IAssetWriteGate`），而不是靠「不给工具」保证。

```csharp
// Core/Suggestions.cs
public sealed record ReflectionSuggestion(
    string Kind,                     // "skill.update" | "skill.delete"
    string Target,
    string? SuggestedContent,        // 建议的新正文（供参考，不自动应用）
    string Reason,
    IReadOnlyList<string> Evidence); // 必填，空则整条丢弃
```

落盘：`.seeing/reflection/runs/{yyyyMMdd}/{executionId}.json`（含 suggestions + 实际创建了哪些技能 + 用量）。

**不做 `ProposalStore`**（v2 的独立状态机 + 目录 + Pending/Applied/Rejected 是过度设计，且与权限系统的 `IPendingRequestManager` 语义不匹配——后者是在途 TCS 等待模型，有 5 分钟超时收敛）。

### 4.7 写入通道

**本期只有两个写入动作**：
1. `skill_create` → `ISkillWriter.CreateAsync`（新建）
2. 记忆 —— **不写**。

**为什么复盘不写记忆**：Memory 已有自己的抽取链路（`MemoryPipeline` → `daily/` → `MemoryEvolution` 升格 `digest/`），且 §3.3 修完后会自动注入。复盘再写一遍是**双轨重复**。v2 让复盘同时写记忆是错的——它既制造重复，又让「哪些记忆是复盘写的」无法追溯。

复盘的产出唯一通道是**技能**（程序性记忆），这也是 Hermes 的实质重点（tool 序列、验证步骤、踩坑规避）。用户偏好/项目约束交给 Memory 自己的抽取。

**遗留问题**：`skill_create` 对主 agent 可见（用户已裁定「不限制」）。风险见 §7.1。

### 4.8 提示词

`Prompts.ReflectionSystem` 三块：

**(a) 任务定义**：这轮任务是什么、交付了吗 → 有无可复用**流程**（命令序列、验证步骤、踩坑规避）→ 有无已有技能需修正（只能出建议）。

**(b) 反模式清单 —— 不学什么**：
- 环境相关偶发失败（网络抖动、端口占用）
- 对工具的负面断言
- 一次性调试叙事
- **能从代码直接推导的事实**（架构、路径、函数签名）
- 本次任务独有的具体值（bug 编号、临时文件名、个人路径）

**(c) 写入纪律**：
- 更新已有技能优先于新建；已有同类技能时只能出建议
- 仅类级别技能，禁止 `fix-bug-1234`
- 每条建议必带 `evidence`（消息序号 + 原文片段）
- 什么都不值得写时输出 `{"action":"skip"}`

**预注入**：紧凑 transcript + 技能清单。

**注意（v2 错误）**：不要自己拼技能清单——`SkillsContextSource` 已在上下文基线渲染 `## Skills` 分节（`SkillsContextSource.cs:69`）。复盘 prompt 再拼一份 = 同一事实两处渲染且格式不同，违反 `04 §5`「同一值重复渲染必须逐字节相等」。

正确做法：复盘会话走正常执行路径，`SkillsContextSource` 会自然把清单注入其系统提示词；`Prompts.ReflectionSystem` **不重复列举技能**，只说明「你的工具清单里有 `skill` 工具，可用它读取任何现有技能全文」。技能清单的可见性由 ContextSource 管道保证。

### 4.9 配置

节名 `Reflection`，文件 `reflection.json`，`ConfigScope.Both`：

```csharp
public sealed class ReflectionOptions
{
    public bool Enabled { get; set; } = false;
    public ReflectionMode Mode { get; set; } = ReflectionMode.SuggestOnly;

    public int TurnNudgeInterval { get; set; } = 10;
    public int IdleMinutes { get; set; } = 15;

    public int MaxSteps { get; set; } = 8;
    public int RunTimeoutMinutes { get; set; } = 10;
    public int MaxRunsPerHour { get; set; } = 6;
    public long DailyTokenBudget { get; set; } = 300_000;
    public int MaxTranscriptChars { get; set; } = 40_000;
    public int MaxToolResultChars { get; set; } = 800;

    public string? Model { get; set; }
}
```

**热重载的处理（v2 错误）**：`MaxSteps` / `Temperature` / `SystemPrompt` 在 `Activate` 时被烘进 `AgentDefinition`（`AgentStore` 只是 `ConcurrentDictionary`）。v2 用 `ReloadHandler` 重注册覆盖——这会**静默丢掉用户的 MD 覆盖**（`AgentManager.ApplyMdConfigToStoreAsync` 依赖 `_originalAgents` 备份，模块侧 `RegisterAsync` 绕过它），还会破坏 `AgentsBuiltInModule` 的 `ReferenceEquals` 归属判定（`AgentsBuiltInModule.cs:78`），导致 Deactivate 漏注销。

**本期决策**：**不承诺这三项热重载**。文档明确写「`MaxSteps`/`Temperature`/`SystemPrompt` 改动需重启生效」。`Model` 不进 `AgentDefinition`（走 `InboxMessage.ModelId`），所以 `Model` 是真正可热重载的。

这是比「加 ReloadHandler」更诚实的解——v2 把「配置说 8 步实际还是旧值」叫谎报，却没意识到 ReloadHandler 会制造另一种谎报。

### 4.10 模块集成

```csharp
public sealed class ReflectionModule : ISeeingModule
{
    public string Id => "reflection";
    public string ModuleId => Id;
    public IReadOnlyList<string> ProvidedTools => ["skill_create"];
    public IReadOnlyList<string> ProvidedSeams { get; } = [];
    public IReadOnlyList<string> DependsOn { get; } = ["skills"];
    // Activate: 注册 agent（幂等）→ 注册 IExecutionOutcomeObserver → 挂 skill_create → MarkActive
    // Deactivate: 对称撤销 + Unregister("reflection") + MarkInactiveAndWake
}
```

`ReflectionWorker` 经 `AddModuleHostedService<ReflectionWorker>()`，照抄 `MemoryEvolutionWorker` 的 `ModuleHostedRunGate` + `RegisterWake`。

**四处登记**（v2 漏了两处）：
1. `BuiltInCapabilitySets.Full` 加 `"reflection"`
2. `FullCapabilityComposition.AddSeeingFullCapabilities` 加 `AddSeeingReflection(registry)`
3. `Seeing.Agent.slnx` 的 Folder 登记项目（`05 §1.8`）
4. 守门测试：`BuiltInToolCatalog`（工具类型 + `ToolMetadataTests` 的 `ids.Should().HaveCount(52)` → 53）、`tests/apps/Seeing.Agent.HostComposition.Tests/FullCapabilityCompositionTests`

### 4.11 时序

```
用户发消息 → DeliverAsync → RunLoop → ExecutionRunner.RunOnceAsync
  → finally: 落盘 + 组装 ExecutionOutcome
             → Publish(ExecutionCompleteEvent{Outcome})
             → 扇出 IExecutionOutcomeObserver
                          ↓（异步，前台已返回）
  → ReflectionObserver: Enabled && AgentExecuted && IsTopLevel && Completed && 非复盘会话
                        && (AgentName != reflection) && 计数到期
  → ReflectionQueue.Enqueue
  → ReflectionWorker → ReflectionExecutor
      → 取 transcript（只读）
      → CreateInternalAsync(Origin=Reflection，不生成标题)
      → DeliverAndAwaitAsync（linked Ct，超时抛 OCE）
      → 解析建议清单 + 收集新建技能
      → journal(runs/{date}/{executionId}.json)
      → RemoveSessionAsync + DeleteAsync
下一轮 → SkillsContextSource 增量叙述新技能（已有）
         → MemoryContextSource 注入相关记忆（§3.3 新增）
```

---

## 5. 安全边界

| 风险 | 缓解 |
|---|---|
| 提示注入变永久学习 | 子会话工具面只有 `read/grep/glob/skill/skill_create`，无 shell、无文件写；`skill_create` 落盘前内容校验；`Metadata["source-session"]` 可追溯批量清理 |
| 技能污染 | 名称/长度对齐平台；content 拒绝「忽略之前的指令」类模式；`overwrite:false` 硬拒同名；Registered 通道只读 |
| 递归复盘 | 三重：`AgentName != reflection` + 源会话 `Origin != Reflection` + 子会话 `Origin == Reflection`（不依赖 `IsTopLevel`，因为子会话自己是顶层） |
| 成本失控 | `Enabled=false` + Nudge 间隔 + 每小时上限 + 日账本（读 `ExecutionOutcome.Usage`）+ `MaxSteps` + 超时 + **不生成标题**，七道闸 |
| 污染主会话 | 子会话独立 transcript；不写源会话消息 |
| 源会话被删 | `TryGetOrLoadAsync` 返 null → skip |
| 建议误以为已应用 | 建议只落 journal，绝不调用 `ISkillWriter.Update/Delete`；工具返回文案明确区分 |

### 5.1 已接受的残留风险（用户裁定）

**`skill_create` 不做可见范围限制。** 事实：`ToolManager.GetToolSchemasAsync`（`:104-116`）只按 `settledToolIds ∩ AllowedTools − DeniedTools` 过滤，不看模块、不看会话用途。`AllowedTools` 为空（`build`）即全开。因此：

- `build` 也能调 `skill_create`，**直接写永久技能**；
- 结合提示注入，可**跳过复盘通道全部门禁**（Nudge、`Enabled`、成本闸都不在路径上）；
- `ToolExposure` 的 auto 分层会让它更隐蔽：`skill_create` 可能只进 deferred 目录，模型 `tool_search` 一下就拿到。

**本期不缓解，明示接受。** 保留的防线是内容校验 + `overwrite:false` + 来源标记。

**架构建议（不属本期）**：这是工具模型缺**受众维度**，不是 reflection 的问题。`ITool` 加 `ToolAudience`（或经 `IToolCapabilities` 加 `audience` 键），在 `SessionToolSchemaResolver` 结算时按「当前 agent + 会话用途」过滤。改一次全局受益（`acp`/`systemone` 等系统工具同样受益）。

---

## 6. 测试策略

新建 `tests/capabilities/Seeing.Agent.Reflection.Tests`。

### Phase 0 的守门（必须先绿）

| 用例 | 覆盖 |
|---|---|
| `ExecutionOutcomeTests` | `AgentName`/`AgentExecuted`/`IsTopLevel` 取值正确；命令短路时 `AgentExecuted=false`；子代理会话 `IsTopLevel=false`（**用 `SessionKind` 验证，不依赖 `ChatExecutionContext`**） |
| `ExecutionOutcomeUsageTests` | `Usage`/`TotalSteps` 来自 `LoopCompleteEvent`，多步执行时为累计值（不是最后一次） |
| `DeliverAndAwaitTests` | 返回该次 executionId 的 outcome；连续两次投递不歧义；超时抛 `OperationCanceledException`（不是正常返回） |
| `MemoryContextSourceTests` | 召回结果进系统基线；基线字节稳定；变化只叙述增量；`ToolsOnly` 模式不注入 |
| `SessionOriginTests` | `CreateInternalAsync` 产出 `Kind=SubAgent` + `Origin=Reflection`；`TaskStatusTool`/`TaskTool`/`TuiSurfaceProvider` 不枚举它；不生成标题 |
| `GetTaskableAgentsTests` | `IsHidden` agent 不在可委派列表（`build`/`summary`/`reflection` 全排除） |

### Phase 1+ 的守门

| 用例 | 覆盖 |
|---|---|
| `ReflectionTriggerTests` | 子代理不触发；命令短路不触发；失败不触发；`Mode=Off` 不触发；计数到期归零；`agentName=reflection` 不触发 |
| `ReflectionRecursionTests` | 复盘子会话不再次入队（**关键回归**） |
| `ReflectionExecutorTests` | transcript 序列化含工具调用名；截断生效；`Origin` 标记；超时抛 OCE；成功后会话与组成员都被清理 |
| `ReflectionPermissionTests` | **`read` 在 reflection agent 下真的能读工作区文件**（v2 的 `Deny(File,"*")` bug 的回归守门）；`write`/`bash`/`task` 被拒 |
| `SkillCreateToolTests` | `SuggestOnly` 不落盘；`ApplyCreate` 走 `CreateAsync(Project)`；同名被拒；**平台合法名（含大写/空格）通过**；超长被拒；`distilled-by` 元数据写入 |
| `SkillCreateMetadataTests` | `04 §9` R1–R9：首句用途、参数 description 非空、`additionalProperties:false`、`required` 完整、长度约束 |
| `ReflectionOptionsTests` | 默认 `Enabled=false`；节名 `Reflection`；`ConfigScope.Both` |
| `ReflectionModuleTests` | Activate/Deactivate 对称；`IAgentStore` 无 `reflection`；两个 agent 列表都不含它 |

### 验证命令（MTP 在本环境无法发现测试，一律 VSTest）

```powershell
dotnet build Seeing.Agent.slnx
dotnet build tests/capabilities/Seeing.Agent.Reflection.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionRunner"
```

---

## 7. 分阶段

| 阶段 | 内容 | 独立价值 | 依赖 |
|---|---|---|---|
| **P0-a** | `ExecutionOutcome` + `ExecutionRecord` 扩字段（删 `ProcessedMessageCount`）+ `ExecutionCompleteEvent` 携带 + `IExecutionOutcomeObserver` 扇出 + 捕获 `LoopCompleteEvent.Usage/Steps` | 终态事实有唯一出口；成本统计/审计可接入 | 无 |
| **P0-b** | `ISessionRuntime.DeliverAndAwaitAsync` + TCS 表 | 收掉 4 处轮询 | 无 |
| **P0-c** | `MemoryContextSource` + 删 `MemoryRecallHandler` | 记忆自动注入真正生效 | 无 |
| **P0-d** | `SessionOrigin` + `CreateInternalAsync` + 三处视图过滤 + 不生成标题 | 内部会话不再污染任务视图 | 无 |
| **P0-e** | `GetTaskableAgentsAsync` 加 `IsHidden` 过滤 | 修 `build`/`summary` 误入委托列表 | 无 |
| **P0-f** | `ISessionActivityTracker` 下沉 primitives | 消除复制 | 无 |
| **P0-g** | 删 Memory 死账本（`ITokenTracker`/`IQuotaManager`/`SqliteTokenTracker`/`DailyQuotaManager`） | 净减代码 | P0-a |
| **P0-h** | `AgentTurnMemoryHandler` 改 `IExecutionOutcomeObserver` | `ExtractEveryNTurns` 真正生效且口径正确 | P0-a |
| **P1** | Reflection 包：配置 + 模块 + observer + 队列 + worker + 计数 + 递归防护（**不调模型**） | 触发链路可观测 | P0-a/d/f |
| **P2** | `reflection` agent + `ReflectionExecutor` + 提示词 + journal | `SuggestOnly` 产出建议清单 | P1, P0-b/c |
| **P3** | `skill_create` + `ApplyCreate` + R1–R9 描述 + 内容校验 | 新技能下一轮可见 | P2 |
| **P4** | 端到端观察 + 成本核对 + 质量抽检 | — | P3 |

**P0 各项可独立合入、可独立验收、对既有系统均为净收益。** 这比 v2「P0 只补一个 hook 触发」诚实得多——v2 的 P0 是「给僵尸契约续命」，v3 的 P0 是「补三个缺失的事实源」。

P1–P3 建议一个 PR。P4 是观察期。

---

## 8. 留给 builder 的决定

- 队列容量与限并发（建议 32 / 1）。
- `ExecutionOutcome` 是 `ExecutionCompleteEvent` 的字段还是独立事件（建议字段，避免双通道）。
- `CreateInternalAsync` 是入独立 partition 还是靠 `Origin` 标记（建议先 `Origin` 标记 + 三处过滤，partition 隔离留待需要时）。
- 建议清单文件名冲突后缀。
- `IReflectionJournal` 用 JSON 还是 JSONL（建议每 run 一 JSON）。
- `ExecutionOutcome.Usage` 在多步执行时取累计还是最后一次（**必须累计**，否则账本失真；按 `LoopCompleteEvent` 逐次累加）。
- `SessionOrigin` 是否也覆盖 Scheduler/Heartbeat（本期只加枚举值，不改它们的创建路径）。

---

## 9. 技术债登记（本期不修，但必须记录）

1. **Hook 体系未收敛**：46 个点、16 个零引用、payload 是未类型化字典、`FireAndForget` 用 `payload.CancellationToken`（恒 default）完全脱离执行生命周期。建议独立议题：收敛到「类型化事件 + 观察者」，删除无生产者的点。
2. **`PermissionKind.Skill` 是死路径**：`EvaluateSkillAsync` 零调用者。要么接上，要么删——不能让它继续假装存在。
3. **`SkillWriter.TryValidateName` 与 `SkillFileScanner` 的校验口径不一致**（后者额外禁控制字符）。
4. **`AgentDefinition` 无来源分层**：`AgentStore` 是字典，配置派生定义只能靠 `RegisterAsync` 覆盖，会绕过 MD 合并。正确解是 `IAgentStore` 支持 provider/lazy 定义（`RegisterProvider(name, Func<AgentDefinition>)`，`GetAgentAsync` 时物化，配置项自动热更新）。这也是 `summary`/`acp` 的公共解。
5. **`AgentDefinition` 热重载**：本期以「不承诺」回避，长期需 §9.4 的解。
6. **`AgentMode` 语义重叠**：`Mode` 同时承担「用户入口」和「可被 task 调用」。修 `IsHidden` 过滤后短期够用，长期需要正交维度。
7. **`ITokenUsageLedger` 缺失**：本期先修数据源（`ExecutionOutcome.Usage`）；出现第二个消费者（WebUI 成本页/全局配额）再抽。
8. **工具受众维度缺失**：`skill_create` 全局可见是这个缺口的表象，不是个例。
9. **`Memory` 的 `Retrieval.Mode` 默认 `Both` 但注入路径到 §3.3 才真正生效**——修复后需核对文档与 UI 描述。

---

## 10. 审批要点

本设计相对 v2 的三个根本变化，需要审批确认：

1. **接受 Phase 0 的前置重构**（7 项，其中 6 项是既有缺陷修复）。若不同意，Reflection 包无法正确实现——只能回到「补丁版」。
2. **接受「不补 `agent.after_invoke` 触发」**，改由 `IExecutionOutcomeObserver` 承载。这意味着 `HookRegistry` 里那 16 个零引用点继续存在（登记为债），但不再新增僵尸用途。
3. **接受「复盘不写记忆」**。产出唯一通道是技能；用户偏好/项目约束交给 Memory 自有抽取。这避免双轨重复和「哪些记忆是复盘写的」无法追溯。

仍需决策的：

- `skill_create` 的受众维度是否提前到本期（可关闭 §5.1 的残留风险）。
- Phase 0 是否拆分多次 PR 逐个验收（建议：是，每项独立）。
- 复盘会话是否需要在 UI 提供任何可见入口（当前设计：完全不可见，仅 journal 可查）。
