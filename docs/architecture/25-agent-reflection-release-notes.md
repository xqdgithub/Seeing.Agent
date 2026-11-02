# 25 Agent 自动反思（Reflection）Release Notes

> **实施日期：** 2026-11-02
> **规格：** [Agent 自动反思设计](../superpowers/specs/2026-10-31-agent-reflection-design.md)
> **实施计划：** [agent-reflection 计划](../superpowers/plans/2026-11-01-agent-reflection-plan.md)（Task 1–14）
> **适用范围：** 新增能力包 `Seeing.Agent.Reflection`；地基改动横跨 Abstractions / Session / Hosting / Memory

---

## 1. 一句话

Agent 在回合收尾后可以**异步复盘**这一轮的执行过程，把「下次遇到同类任务可复用」的流程性经验沉淀为项目级技能；复盘在隔离子会话中执行，默认**关闭**，开启后默认**只产出建议不写盘**。

---

## 2. 新增：`Seeing.Agent.Reflection` 能力包

模块 id `reflection`，已纳入 `BuiltInCapabilitySets.Full`，由 `AddSeeingFullCapabilities` 统一登记。

| 组件 | 职责 |
|---|---|
| `Integration/ReflectionObserver.cs` | `IExecutionOutcomeObserver` 实现：终态门禁 + 轮次计数 + 入队 |
| `Core/ReflectionQueue.cs` | 有界队列，同会话去重（保留最新 executionId） |
| `Core/ReflectionExecutor.cs` | 五阶段执行：门禁 → 快照 → 建内部会话 → 投递等待 → 收集清理 |
| `Core/ReflectionRateLimiter.cs` | `MaxRunsPerHour` 进程内滑动窗口 |
| `Core/ReflectionJournal.cs` | `runs/{yyyyMMdd}/{executionId}.json`，每 run 一文件 |
| `Core/ExecutionUsageLedger.cs` | `usage.json`，UTC 日切，累计 `ExecutionOutcome.Usage` |
| `Core/Prompts.cs` | 复盘系统提示词（任务定义 / 反模式清单 / 写入纪律）与用户提示词 |
| `Core/Suggestions.cs` | 建议清单 JSON 解析（容忍围栏、丢弃无 evidence/无 reason 条目） |
| `Integration/Tools/SkillCreateTool.cs` | 工具 id `skill_create` |
| `Integration/ReflectionAgentDefinition.cs` | `reflection` agent 定义（`Primary` + `IsHidden`） |
| `Background/ReflectionWorker.cs` | `BackgroundService`：单循环排空队列 + 每 1 分钟扫描空闲会话 |

### 2.1 触发

两条来源：

- **轮次触发**：顶层会话每完成 `TurnNudgeInterval`（默认 10）轮入队一次。
- **空闲触发**：`ReflectionWorker` 每 1 分钟扫 `ISessionActivityTracker.GetIdleSessions(IdleMinutes)`（默认 30 分钟）。

`ShouldObserve` 八重门禁（顺序即优先级）：

```
Enabled → Mode != Off → AgentExecuted → IsTopLevel
→ AgentName != "reflection" → Origin != Reflection → Status == Completed → SessionId 非空
```

前两条是配置闸，中间两条排除命令短路与子代理，第 5/6 条是**双重重递归防护**（见 §4.1），最后两条是数据有效性。

### 2.2 执行隔离

复盘会话经 `ISessionGroupManager.CreateInternalAsync(..., SessionOrigin.Reflection)` 创建：

- `Kind = SubAgent`（满足组不变量「Relation=Child ⇔ Kind=SubAgent」）
- `Origin = Reflection`（与 `Kind` 正交，用于视图过滤）
- 标题由调用方显式给定，**不触发标题生成**（见 §4.2）

成功后子会话被 `RemoveSessionAsync` + `DeleteAsync` 清理；失败/超时**保留**供诊断（`Origin` 已隔离，不会进任务视图）。

### 2.3 权限面

`reflection` agent：

| 项 | 值 |
|---|---|
| `Mode` | `Primary` + `IsHidden = true` |
| `AllowedTools` | `read` / `grep` / `glob` / `skill` / `skill_create` |
| `DeniedTools` | `task` / `write` / `edit` / `delete` |
| `PermissionRules` | `Allow(File, "*")` + `Deny(Shell, "*")` |

**关键不变量**：`PermissionRules` 里**不得**出现 `Deny(File, "*")`。`read` 工具映射到 `PermissionKind.File`，全局 Deny 会把 `read` 一起拒掉——这是设计评审抓出的 Review Focus #3。写文件/执行命令的约束改由 `DeniedTools` 与 `Deny(Shell)` 表达。

`Mode = Primary` + `IsHidden` 的组合是刻意的：`Primary` 表达「不可被 task 委派」（可委派列表按 `Mode != Primary` 排除），`IsHidden` 表达「不显示在用户选择列表」。用 `SubAgent` + `IsHidden` 会污染可委派集合——`IsHidden` 的契约语义是「隐藏但仍可作为 SubAgent 使用」。

---

## 3. 配置

节名 `SeeingAgent:Reflection`（`.seeing/seeing.json` / `~/.seeing/seeing.json`）：

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `false` | **默认关闭**。复盘会产生额外模型调用，必须由用户显式开启 |
| `Mode` | `SuggestOnly` | `Off` / `SuggestOnly`（只记录不写盘）/ `ApplyCreate`（直接写技能） |
| `TurnNudgeInterval` | `10` | 每 N 轮触发一次 |
| `IdleMinutes` | `30` | 空闲多久后触发 |
| `MaxRunsPerHour` | `6` | 进程内滑动窗口 |
| `DailyTokenBudget` | `300000` | 累加 `ExecutionOutcome.Usage`，超预算跳过 |
| `RunTimeoutMinutes` | `10` | 单次执行超时（自带 `CancelAfter`，不依赖运行时内部超时） |
| `MaxSteps` | `8` | 复盘子会话最大步数 |
| `Model` | `null` | 复盘子会话模型；null 时继承 |
| `QueueCapacity` | `32` | 队列容量，满则丢弃 |
| `MaxToolResultChars` | `4000` | transcript 中单条工具结果截断 |
| `MaxTranscriptChars` | `60000` | transcript 总长上限（从尾部保留最近的） |

---

## 4. 地基改动（横跨多个包）

### 4.1 `SessionOrigin` 正交维度

新增 `src/primitives/Seeing.Session/Core/SessionOrigin.cs`：

```csharp
public enum SessionOrigin { User = 0, SubAgentTask = 1, Reflection = 2, Scheduler = 3, Heartbeat = 4 }
```

`SessionData.Origin` 默认 `User`；存量会话缺该字段时反序列化为 `User`。

**视图过滤用排除式而非白名单**，收敛在 `SessionOriginExtensions.IsUserVisibleTaskOrigin()`：

```csharp
public static bool IsUserVisibleTaskOrigin(this SessionOrigin origin)
    => origin is SessionOrigin.User or SessionOrigin.SubAgentTask;
```

**为什么不能写成 `== SubAgentTask`**：`Origin` 是后加字段，升级前的历史子会话没有它 → 反序列化后是 `User`。白名单会让**所有存量 task 会话升级后从任务视图消失且无法 resume**。实测该写法让 `TaskToolAutoApproveInheritanceTests` 3 个用例失败。排除式只挡 `Reflection`/`Scheduler`/`Heartbeat`——它们必然是新代码创建的，不存在历史兼容问题。

三个消费方共用该谓词：`TaskStatusTool.GetChildTasksStatusAsync`、`TaskTool.GetSubAgentStatusSummaryAsync`、`TuiSurfaceProvider`（递归组订阅）。`TaskTool` 的 `task_id` 复用校验同样改为 `Kind == SubAgent && IsUserVisibleTaskOrigin()`。

### 4.2 标题生成门禁

`ExecutionRunner` 的 `EnsureTitleFireAndForgetAsync` 触发条件增加 `session.Origin == SessionOrigin.User`。

理由：复盘会话以 `Presentation = User` 投递会让 `batch.HasUserInput = true`，原本会为每个复盘会话额外触发一次标题 LLM 调用——这是纯粹的成本浪费（复盘会话的标题在创建时已显式指定）。

### 4.3 `ISessionActivityTracker` 下沉 primitives

从 `Seeing.Agent.Memory` 移到 `src/primitives/Seeing.Session/`：

- 接口：`Seeing.Session.Core.ISessionActivityTracker`
- 实现：`Seeing.Session.Management.SessionActivityTracker`

DI 注册从 `MemoryServiceExtensions` 移到 `SessionServiceExtensions` / `AddSeeingCore`（`TryAddSingleton` 保证全进程单实例）。理由：复盘触发与记忆抽取共用同一个活跃度事实，各持一份会导致空闲判定分裂。

### 4.4 `ExecutionOutcome` 携带 `Origin`

`ExecutionOutcome.Origin` 由 `ExecutionRunner.ResolveSessionRelation` 从内存中的 `SessionData` 一次读出（与 `IsTopLevel`/`ParentSessionId` 同源）。观察者因此不必回读会话——`IExecutionOutcomeObserver` 的契约明确禁止磁盘 I/O。

---

## 5. 破坏性变更

| 变更 | 影响 | 迁移 |
|---|---|---|
| `ISessionActivityTracker` 移至 `Seeing.Session` 命名空间 | Memory 包外部消费者需改 `using` | 改用 `Seeing.Session.Core` / `Seeing.Session.Management` |
| `IReflectionExecutor.RunAsync` 返回 `Task<ReflectionRunResult>` | 仅包内 | 无（包为新增） |
| `AddSeeingReflection(registry)` 挂载 `ReflectionModule` | 宿主组合根 | 使用 `AddSeeingFullCapabilities` 的宿主自动获得 |

**不在本批次的破坏性变更**：`LlmService` 删除 `ChatBeforeStart` / `LlmSystemPrompt` 两钩子、`MemoryRecallHandler` 删除——见 [24 LlmService 纯传输层 Release Notes](24-llmservice-transport-only-release-notes.md)。

---

## 6. 已知边界（本期接受）

1. **`SessionForker` 不继承 `Origin`**：分叉出的会话回落 `User`，复盘会话若被分叉会「隐身失败」→ 潜在递归复盘（靠 `AgentName != "reflection"` 兜底）。
2. **`skill_create` 无可见范围限制**：复盘写出的技能对所有 agent 可见。这是「工具受众维度缺失」的直接后果。
3. **`ReflectionModule` 可选依赖静默降级**：容器未登记 `IToolManager` / `IAgentStore` 时模块照常激活，只是不挂工具与 agent。好处是宿主不会因可选能力缺失起不来，代价是配置错误不报错。
4. **`ReflectionWorker` 停用响应最坏延迟 1 分钟**（`PeriodicTimer` tick 周期）。
5. **`AddSeeingReflection(IConfiguration)` 的 `TryAddSingleton(configuration)`**：若 DI 中已有不同实例则静默忽略。
6. **journal 写失败降级为 `SkipReason = "failed"`**：分析已跑完但结果未落盘，不报告成功。
7. **`SuggestionCount` 为 0 与「解析失败」不可区分**：`{"action":"skip"}` 与 JSON 畸形都产出空列表，前者记 `Executed = true`，后者记 `SkipReason = "parse-failed"`——但两者在 `ReflectionRunRecord.Suggestions` 里都为空。

---

## 7. 验证

```powershell
dotnet build Seeing.Agent.slnx --no-incremental   # 0 error

# 全量（均为 VSTest，MTP 在本环境无法发现测试）
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll                              # 1297
dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll   # 67
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll           # 217
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll  # 274
dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll                       # 281
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll              # 75
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll  # 10
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll                         # 617
dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll                         # 84
```

总计 2922 用例，0 failed。

**变异验证**：把 `IsUserVisibleTaskOrigin` 改成 `== SubAgentTask` 后，`SessionOriginExtensionsTests` 2 条用例变红；`TaskToolAutoApproveInheritanceTests` 3 条变红——证明存量兼容守门有效。

---

## 8. 下一步（未实现）

- **P4 观察期**：真实运行下的成本核对、建议质量抽检。
- **提案通道**：修改/删除已有技能走提案而非直接写（当前只支持新建）。
- **Layer 3 Curator**：定期梳理技能库（本期明确不做）。
- **工具受众维度**：让 `skill_create` 只对 `reflection` agent 可见——需先补 `AgentDefinition` 的工具受众声明能力。
