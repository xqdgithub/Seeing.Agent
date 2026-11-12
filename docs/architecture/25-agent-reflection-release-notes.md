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
| `Core/ReflectionSuggestionsWriter.cs` | **复盘唯一的写盘出口**：按 Mode 把建议落成项目级技能，返回实际创建的名字 |
| `Core/ReflectionTurnCounter.cs` | 轮次计数（有界 LRU，上限 512） |
| `Integration/ReflectionAgentDefinition.cs` | `reflection` agent 定义（`Primary` + `IsHidden`，纯只读） |
| `Ui/ReflectionRunReader.cs` | 只读查询历史复盘记录（供 WebUI） |
| `Background/ReflectionWorker.cs` | `BackgroundService`：单循环排空队列 + 每 1 分钟触发一次空闲扫描 |
| `Background/ReflectionIdleScanner.cs` | 空闲扫描策略：结构/来源门禁、二次空闲确认、同空闲期冷却 |

### 2.1 触发

两条来源：

- **轮次触发**：顶层会话每完成 `TurnNudgeInterval`（默认 10）轮入队一次。
- **空闲触发**：`ReflectionWorker` 每 1 分钟扫 `ISessionActivityTracker.GetIdleSessions(IdleMinutes)`（默认 30 分钟）。

`ShouldObserve` 八重门禁（顺序即优先级）：

```
Enabled → Mode != Off → AgentExecuted → IsTopLevel
→ AgentName != "reflection" → IsAutomatableOrigin() → Status == Completed → SessionId 非空
```

**来源门禁用 `IsAutomatableOrigin()` 而非 `IsUserVisibleTaskOrigin()`**：
定时 / 心跳会话不出现在任务视图，但它们是真实的 agent 执行，可能产出可复用经验，
应当可复盘。两个谓词回答两个不同问题，不能合并：

| 谓词 | 语义 | 消费方 |
|---|---|---|
| `IsUserVisibleTaskOrigin()` | 用户是否该在任务列表看到 | `TaskTool` / `TaskStatusTool` / `TUI` / 记忆管道 |
| `IsAutomatableOrigin()` | 是否允许被自动分析（只排除复盘自身） | `ReflectionObserver` / `ReflectionIdleScanner` |

**空闲触发的活动时间戳来自主执行链**：`ExecutionRunner` 在终态无条件 `Touch`。
原先只有 `ChatMemoryHandler` 会 Touch，导致「记忆捕获关闭 → 空闲触发永久失效」的隐性耦合。

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
| `AllowedTools` | `read` / `grep` / `glob` / `skill` |
| `DeniedTools` | `task` / `write` / `edit` / `delete` |
| `PermissionRules` | `Allow(File, "*")` + `Deny(Shell, "*")` |

**纯只读**：复盘 agent **没有**任何写入通道。它的产出是结构化的建议 JSON，
技能落盘由宿主侧的 `ReflectionSuggestionsWriter` 在执行器内完成。

**为什么不用 `skill_create` 工具**（本设计曾采用该方案，已废弃）：

| 问题 | 说明 |
|---|---|
| 与提示词互斥 | `Prompts.ReflectionSystem` 要求输出 `{"suggestions":[...]}` 或 `{"action":"skip"}`，从未提过工具。模型只能在两种表达方式之间猜；猜错则最后一条 assistant 消息不是 JSON → `parse-failed` → 子会话按设计保留为孤儿 |
| 写入结果不可见 | 工具写盘对执行器透明，journal 的 `CreatedSkills` 只能是硬编码空数组，「这轮写入什么技能」永远查不到 |
| 需要写文件权限 | 只读分析任务被迫申请写权限，安全边界无法论证 |
| 白送一个全局工具 | `skill_create` 登记进 `Full` 能力集 → 任何 agent 都能调，属「工具受众维度缺失」的扩散 |

收进执行器后：写入结果与审计记录同源、agent 权限面收窄到完全只读、
工具目录回到 52 个无需维护新条目。

**关键不变量**：`PermissionRules` 里**不得**出现 `Deny(File, "*")`。`read` 工具映射到
`PermissionKind.File`，全局 Deny 会把 `read` 一起拒掉——这是设计评审抓出的 Review Focus #3。
写文件/执行命令的约束改由 `DeniedTools` 与 `Deny(Shell)` 表达。

`Mode = Primary` + `IsHidden` 的组合是刻意的：`Primary` 表达「不可被 task 委派」（可委派列表按 `Mode != Primary` 排除），`IsHidden` 表达「不显示在用户选择列表」。用 `SubAgent` + `IsHidden` 会污染可委派集合——`IsHidden` 的契约语义是「隐藏但仍可作为 SubAgent 使用」。

### 2.5 失败处理

**失败 / 超时一律清理子会话**，诊断信息落到 `runs/{date}/{executionId}.fail.json`：

| 字段 | 内容 |
|---|---|
| `SkipReason` | `timeout` / `failed` / `parse-failed` |
| `Transcript` | 复盘子会话的 assistant 文本（截断到 20,000 字符） |
| `Detail` | 异常消息或终态描述 |
| `ModelId` | 实际使用的模型 |

**为什么不再「保留子会话」**：原设计让失败会话留在 `.seeing/sessions/` 供诊断，
但没有任何回收机制——持续失败时每小时最多 6 个、一天 144 个孤儿会话。
诊断真正需要的只有「模型输出了什么」，把它落成文本后会话本身没有保留价值。

### 2.6 查看历史复盘

WebUI `/reflection` 页（工作区导航「复盘记录」）列出 `runs/` 下的全部记录：

- 汇总：记录数 / 失败数 / 产出建议数 / 创建技能数
- 列表：时间、触发方式、结果、建议数、技能数、Token、实际模型
- 展开：失败详情、建议清单（含依据）、本次写入的技能
- 可按「仅失败」过滤

这条出口是 `SuggestOnly` 模式的**唯一可见面**——没有它，用户开了 `Enabled=true`
只能得到硬盘上的 JSON，功能感知上等于不存在。

### 2.7 写入通道

`ReflectionSuggestionsWriter` 是复盘唯一的落盘路径，按 `Mode` 分流：

| Mode | 行为 |
|---|---|
| `Off` | 不写 |
| `SuggestOnly`（默认） | 不写。建议内容已完整保留在 journal 的 `Suggestions` 里 |
| `ApplyCreate` | 逐条写入项目级技能，返回实际创建的名字记进 journal |

逐条校验（对齐 `SkillWriter.TryValidateName` 与 `SkillFileScanner` 的既有口径，不自创）：
名称长度/非法字符、正文长度、提示注入片段、`ISkillCatalog.Find` 查重。
**单条失败不影响其余条目**——一条命名冲突不该让整批建议白跑。

写入的 frontmatter 带 `distilled-by = seeing-reflection` 与 `source-session = <源会话 ID>`，可追溯。

---

## 3. 配置

节名 `Reflection`，存放在 **独立文件 `reflection.json`**（`~/.seeing/reflection.json` 用户级 /
`{WorkspaceRoot}/.seeing/reflection.json` 项目级；文件根对象直接就是选项，不再包一层节名），
经 `IConfigSectionStore` 读取并在 `ConfigSectionRegistry` 登记（WebUI 配置页可见、纳入统一配置校验）。

> **不要**把配置写到 `appsettings.json` 的 `SeeingAgent:Reflection` 节——本仓能力包配置不走
> Microsoft 的 `IConfiguration` 管道，写在那里不会生效（这是审查抓出的原始缺陷，已修）。
>
> **也不要**写进 `seeing.json` 的 `SeeingAgent:Reflection`：`UnifiedConfigManager` 只把
> `FileName == "seeing.json"` 的节当嵌套节读，本节的 `FileName` 是 `reflection.json`，
> 写进 seeing.json 会被**完全忽略且无告警**。旧版本本文档错误地写成了 seeing.json
> （2026-11-12 修正），按旧文档配置的用户会以为「开了却没反应」。

用户级与项目级同时存在时按字段深度合并，项目级优先。

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

### 3.1 三条触发来源的差异

| 来源 | 门禁 |
|---|---|
| 轮次触发（`ReflectionObserver`） | 八重门禁全开（见 §2.1） |
| 空闲触发（`ReflectionWorker`） | 只读内存校验：`Kind == Root` + `IsUserVisibleTaskOrigin()` + 二次确认仍空闲 + 冷却去重 |
| 手动触发（`IReflectionManualTrigger`） | 模块激活 + `Mode != Off` + 会话在内存且为顶层、非复盘来源 + 队列未满。**只跳过 `Enabled`**（见下） |

空闲触发**不**走 `ReflectionObserver`，因此必须自带等价的来源门禁——否则会对子代理会话
甚至复盘会话自身做复盘。冷却键是「投递时的活动时刻」：用户一旦回到会话，
`Touch` 刷新时刻，天然允许下一次空闲复盘。

**手动触发为什么不看 `Enabled`**：`Enabled` 表达的是「要不要自动跑」，不是「这个能力是否可用」。
若手动也受它限制，用户为了验证功能就得先改配置再回来点，而「开启后没有任何即时反馈」正是
这个功能长期被认为是坏的直接原因。相反 `Mode == Off`、每日预算、每小时上限三关对三种来源
一视同仁——那是硬停与成本语义，不该被一次点击绕过。

手动触发只做「入队 + `ReflectionWakeSignal.Pulse()`」，不阻塞页面。执行仍由唯一的后台循环完成，
避免 UI 请求与 Worker 形成两条并发执行路径。没有这条唤醒信号时，手动请求最长要等下一次
1 分钟 tick 才开始跑——用户点完看不到任何动静。

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

三个消费方共用该谓词：`TaskStatusTool.GetChildTasksStatusAsync`、`TaskTool.GetSubAgentStatusSummaryAsync`、
`TaskTool` 的 `task_id` 复用校验、`TuiSurfaceProvider`（递归组订阅）——共四处调用点。

**空闲触发的来源门禁在 `ReflectionIdleScanner` 侧独立实现**：它不走 `ReflectionObserver`，
因此必须自带 `Kind == Root` + `IsUserVisibleTaskOrigin()` 校验，否则会对子代理会话做复盘。
两者门禁是**两份实现**（观察者拿到终态事实，扫描器拿到会话对象，可用信息不同），
但表达的约束必须一致——改动任一处都要同步另一处。

**记忆管道同样需要来源门禁**：`ChatMemoryHandler` 消费 `ChatAfterComplete` 钩子，
原先不区分来源。复盘会话以 `Presentation = User` 投递，其 assistant 输出会被当作记忆候选，
而该会话随后被删除——条目既污染召回又查不到来源。现改为读内存会话，
`!IsUserVisibleTaskOrigin()` 时跳过；读不到会话时保守收录（不误杀尚未加载的用户会话）。

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
| `ISessionActivityTracker` 移至 `Seeing.Session` 命名空间，并新增 `TryGetLastActivity` | Memory 包外部消费者需改 `using`；自定义实现需补新成员 | 改用 `Seeing.Session.Core` / `Seeing.Session.Management` |
| `IReflectionExecutor.RunAsync` 返回 `Task<ReflectionRunResult>` | 仅包内 | 无（包为新增） |
| `AddSeeingReflection()` 增加可选 `IConfigSectionRegistry` 参数 | 宿主组合根 | 传入 registry 才能在 WebUI 看到配置节 |
| `ChatMemoryHandler` 构造增加可选 `ISessionManager` | 手工构造该 handler 的调用点 | 传 `null` 即保持旧行为（不做来源过滤） |
| 删除 `skill_create` 工具（工具目录 53 → 52） | 本仓无别的调用点 | 技能写入改由执行器内的 `ReflectionSuggestionsWriter` 完成 |

**不在本批次的破坏性变更**：`LlmService` 删除 `ChatBeforeStart` / `LlmSystemPrompt` 两钩子、`MemoryRecallHandler` 删除——见 [24 LlmService 纯传输层 Release Notes](24-llmservice-transport-only-release-notes.md)。

---

## 6. 已知边界（本期接受）

1. **`SessionForker` 不继承 `Origin`**：分叉出的会话回落 `User`，复盘会话若被分叉会「隐身失败」→ 潜在递归复盘（靠 `AgentName != "reflection"` 兜底）。
2. **写入的技能对所有 agent 可见**：复盘写出的项目级技能进入全局技能目录，没有「仅复盘可见」的隔离。这不影响正确性（技能本就是可复用的知识），但授权范围比必要的大。
3. **`ReflectionModule` 可选依赖静默降级**：容器未登记 `IAgentStore` 时模块照常激活，只是不注册 agent。**已补告警日志**说明缺哪个依赖，不再完全静默。
4. **`AddSeeingReflection` 的配置节存储缺失时退回默认值**：`IConfigSectionStore` 未注册（独立装配/单元测试）时 `Enabled` 恒为 `false`，即「不工作」。
5. **journal 写失败降级为 `SkipReason = "failed"`**：分析已跑完但结果未落盘，不报告成功。
6. **`SuggestionCount` 为 0 与「解析失败」不可区分**：`{"action":"skip"}` 与 JSON 畸形都产出空列表，前者记 `Executed = true`，后者记 `SkipReason = "parse-failed"`——但两者在 `ReflectionRunRecord.Suggestions` 里都为空。失败快照的 `Detail` 可帮助区分。
7. **`ExecutionUsageLedger` 单进程假设**：账本在锁内全量覆盖写；同一工作区两个宿主实例各写各的，后写者覆盖前者。预算是**按项目目录**分别计，切换工作区即重置。
8. **`ReflectionObserver` 先 Reset 后入队**：入队失败（队列满）时本轮触发额度已清零，该会话要再等 N 轮才可能触发。
9. **`ReflectionTurnCounter` 淘汰会让会话轮次归零**：上限 512，超限时淘汰最久未触发的会话，其触发点后移几轮。与「重启归零可接受」同类。
10. **`ReflectionJournal` 文件名截断到 100 字符 + 8 位哈希**：超出时不同 ID 仍可能因哈希碰撞而覆盖（概率可忽略）。

### 6.1 已修复的边界（保留记录，避免回归）

| 原问题 | 修复 |
|---|---|
| `ReflectionTurnCounter` 字典无界增长（`Clear` 零调用） | 容量 512 + LRU 淘汰；`Reset` 保留触发时刻避免刚触发过的会话被立刻淘汰 |
| 三处来源门禁漂移（观察者允许定时/心跳复盘、扫描器不允许） | 拆成 `IsUserVisibleTaskOrigin`（视图）与 `IsAutomatableOrigin`（自动分析）两个谓词 |
| 失败会话永不清理（一天最多 144 个孤儿） | 失败/超时/解析失败统一落 `*.fail.json` 后清理子会话 |
| `Touch` 只在记忆钩子调用 → 记忆关闭时空闲触发失效 | `ExecutionRunner` 终态无条件 `Touch`；记忆侧保留 |
| `SuggestOnly` 建议无任何出口 | WebUI `/reflection` 只读页 |
| 复盘模型不可追溯 | `ResolveModelId` 三级回退，模型写进 journal 与页面 |
| journal 文件名无长度上限 | 截断到 100 + 8 位哈希 |
| Worker 停用不取消在途复盘（最长 10 分钟） | 独立 `_deactivationCts`，`Deactivate` 调 `CancelInFlight` |
| `TryEnqueue` 的 `false` 有二义性 | 改为 `ReflectionEnqueueResult` 枚举（`Enqueued`/`Updated`/`Dropped`/`Invalid`） |
| MCP `EnableServerAsync` 返回硬编码 `Connecting`（后台连接可能已完成） | 返回调用时刻的真实状态；测试改为断言「不再是 Disabled」 |

---

## 7. 验证

```powershell
dotnet build Seeing.Agent.slnx --no-incremental   # 0 error

# 全量（均为 VSTest，MTP 在本环境无法发现测试）
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll                              # 1297
dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll   # 108
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll           # 221
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll  # 274
dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll                       # 285
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll              # 75
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll  # 10
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll                         # 617
dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll                         # 84
```

总计 2966 用例，0 failed。

**变异验证**（每次都必须 `--no-incremental`，否则 MSBuild 复用旧产物会得假结论）：

| 变异 | 预期 | 实际 |
|---|---|---|
| `IsUserVisibleTaskOrigin` 改成 `== SubAgentTask`（白名单） | `SessionOriginExtensionsTests` 红 | 2 条红；另有 `TaskToolAutoApproveInheritanceTests` 3 条红 |
| 删除 `ShouldObserve` 的 `AgentName` 门禁 | `ShouldObserve_Should_Gate_Correctly` 红 | 1 条红 |
| 删除 `ShouldObserve` 的 `Mode == Off` 门禁 | `ShouldObserve_Should_Return_False_When_Mode_Is_Off` 红 | 1 条红 |
| 注释掉 `_ledger.Add` | `Run_Should_Record_Usage_To_Ledger` 红 | 该用例即为补此缺口而新增 |
| 去掉空闲扫描的冷却判断 | `Scan_Should_Not_Reenqueue_Same_Idle_Period` 红 | 1 条红 |
| 去掉 `ChatMemoryHandler` 的来源过滤 | `ExecuteAsync_When_Platform_Internal_Session_Should_Not_Buffer` 红 | 3 条红（三种内部来源） |
| 给 `reflection` agent 的 `AllowedTools` 塞入 `write` | `Agent_Should_Allow_Read_Tools` 红 | 1 条红（断言用 `BeEquivalentTo` 精确锁集合） |
| 去掉写入器的 `Mode != ApplyCreate` 短路 | `Apply_Should_Not_Write_In_SuggestOnly_Mode` 红 | 2 条红（SuggestOnly / Off） |
| 去掉轮次计数器的淘汰 | `Count_Should_Not_Exceed_Capacity` 红 | 2 条红（容量 + LRU） |
| `TryEnqueue` 队列满时返回 `Updated` 而非 `Dropped` | `TryEnqueue_Should_Report_Dropped_When_Full` 红 | 调用方将无法区分「正常去重」与「丢弃」 |

---

## 8. 下一步（未实现）

- **P4 观察期**：真实运行下的成本核对、建议质量抽检。
- **提案通道**：修改/删除已有技能走提案而非直接写（当前只支持新建）。
- **Layer 3 Curator**：定期梳理技能库（本期明确不做）。

---

## 9. 2026-11-12 可达性修复（「一直没被触发」）

**症状**：功能上线后从未产生任何 `reflection/runs` 记录，用户认为「从没被触发」。

**根因（四层叠加，按影响排序）**：

| # | 根因 | 表现 |
|---|---|---|
| 1 | 默认 `Enabled=false`，而 WebUI 里**没有任何开关** | 「复盘记录」页的空态文案指向系统设置，那里却没有这张卡 → 功能感知上不存在 |
| 2 | 本文档此前错误地写「配置存 `seeing.json`」，而代码注册的是 `FileName = "reflection.json"` | 按文档写进 `SeeingAgent:Reflection` 会被 `UnifiedConfigManager` **完全忽略且无告警** |
| 3 | 轮次（10 轮）+ 空闲（30 分钟且会话仍在内存）双门禁叠加 | 即使配对了，正常使用强度下也可能几小时不触发一次 |
| 4 | journal 的 `Outcome` 写的是 `outcome.Status.ToString()` → `"Completed"` | 记录页只映射 `suggested/created/skipped/failed`：显示英文，且「产出建议」统计恒为 0 |

**修复**：

1. **新增「复盘」设置卡**（`ReflectionSettingsCard`，ViewKey `settings.reflection`），由
   `ReflectionModule` **自己声明**（`Requires = ["reflection"]`）：模块停用后卡与页一起消失，
   不留点开即报错的空壳；宿主只在 `WebUiViews` 登记视图类型。
   内容：开关 / 模式 / 轮次间隔 / 空闲分钟 / 频率与预算上限 / 超时 / 模型 / 步数与截断 / **只读运行态**
   （模块是否激活、今日已用 Token、排队数、产物目录）/ **立即复盘**。
   保存固定写**用户级** `reflection.json`（这是个人偏好，不该随仓库分发被别人覆盖）。
2. **`Outcome` 改为语义值**（`ReflectionRunOutcomes`：`suggested` / `created` / `skipped`），
   与记录页的 `UiText.ReflectionOutcomeText` 映射对齐。
3. **新增 `IReflectionManualTrigger` + `ReflectionWakeSignal`**：手动触发只入队并唤醒后台，
   不阻塞页面；`ReflectionWorker` 的等待从 `PeriodicTimer` 改为「tick 或脉冲取先到者」。
   手动触发**跳过 `Enabled`**，但保留 `Mode == Off`、每日预算与每小时上限。
4. **本文档更正配置位置**（§3），并显式警告「写进 `seeing.json` 会被忽略」。
5. 「复盘记录」页空态文案改为指向「系统设置 → 复盘」，并在工具栏加直达按钮。

**新增/更新测试**：

| 测试 | 覆盖 |
|---|---|
| `ReflectionManualTriggerTests`（9 条） | 入队 / `Enabled=false` 仍可用 / `Mode=Off` 拒绝 / 模块未激活拒绝 / 子会话与复盘来源拒绝 / 会话不在内存拒绝 / 去重报 `Updated` / 队列满报 `Dropped` |
| `ReflectionWakeSignalTests`（4 条） | 超时返回 false / 脉冲立即返回 / 重复脉冲不叠加不抛 / 取消抛出 |
| `ReflectionExecutorTests`（+3 条） | `Outcome` 为 `suggested` / `skipped` / `created`，钉住「不再写执行状态」 |
| `ReflectionDependencyInjectionTests`（+2 条） | 设置卡两个端口可从真实容器解析 / 状态端口的四个事实接到底层服务 |
| `SettingsAndSlotMigrationTests`（改） | 设置卡 5 → 6 张；`settings.reflection` 归属 `reflection` |

**已知边界（新增）**：

- 手动触发的会话候选只取 `ISessionManager.List()`（内存中的会话）。磁盘上未加载的历史会话
  不列出——列出需要整盘加载，而用户真正想复盘的是刚聊过的那个。
- `MaxSteps` / `Temperature` / `SystemPrompt` 仍在模块 Activate 时烘进 agent 定义，
  改配置需重启（`§4.9` 的原有决策不变），卡片上已就地标注。

---

## 10. 2026-11-12 第二轮：复盘输入与空闲触发的正确性

§9 修的是「能不能被触发」，这一轮修的是「触发后拿到的东西对不对」。

### 10.1 空闲触发被记忆模块删掉了事实（功能级故障）

`ISessionActivityTracker` 存的是全进程共享的**事实**（会话最后活动时刻），
但两个消费方把「我处理过这段空闲期」也塞进了同一个对象：

| 消费方 | 默认阈值 | 原实现 |
|---|---|---|
| 记忆演化 `MemoryEvolutionWorker` | 15 分钟 | 处理完调 `_activity.Clear(sessionId)` ← **删事实** |
| 复盘 `ReflectionIdleScanner` | 30 分钟 | 只读 `GetIdleSessions(30min)` |

于是：会话空闲到 15 分钟时被记忆演化处理并把时间戳删掉；30 分钟时复盘的
`GetIdleSessions` 里根本没有这个会话。**空闲触发永久失效，无报错、无日志**。
本机 `memory.json` 的 `Evolution.IdleMinutes=1` 让这件事在 1 分钟后就发生。

**修复**：新增 `Seeing.Session.Management.IdlePeriodClaimStore`（primitives）——
按 `(sessionId, lastActivity)` 记录「本消费方处理过哪一段空闲期」。
- 两个消费方**各持一份实例**（共享会让一个把另一个的机会吃掉，比 `Clear` 更隐蔽）；
- 追踪器里的时间戳**只由主执行链 `Touch`**，消费方不得删除（会话真正删除时才 `Clear`）；
- `Release` 供「占了位但最终没处理」的路径（如队列满）撤销；`Prune` 防无界增长。

### 10.2 多模态内容在复盘里完全消失

`BuildTranscript` 只读 `SessionMessage.Content`。但多模态消息
（`UserMessageWithParts`）把正文放在 `Parts` 的 text 段里，`Content` 为空 ——
「用户贴图 + 打字说明」这类消息在复盘里变成一行**空白**，附件本身也完全不可见。

**修复**：
- `ExtractText`：`Content` 空时回落到 `Parts` 的 text 段（这是漏得最彻底的一处）；
- `AppendPartDigest`：非 text 段压成一行 `[附件] 图片:screenshot.png`。
  **只留类型 + 文件名**，不记 URL / base64 / 本地路径：二进制对分析无用，
  base64 会瞬间吃满 `MaxTranscriptChars` 把真正要复盘的对话挤掉。

### 10.3 产物目录不跟随工作区切换

`ReflectionStorageRoot` 原是构造时算好的不可变值（`internal record`）。切到工作区 B 后，
journal / 账本 / 记录页仍读写 A 的 `.seeing/reflection`：新工作区的复盘记录看不到、
预算也记在旧项目上。会话存储有 `SessionReloadHandler` 做同样的事，复盘没有等价物。

**修复**：
- `ReflectionStorageRoot` 改为**可变**单例（`Path` 每次现算 + `AlignToWorkspace` / `SetPath`）；
- `ReflectionJournal` / `ExecutionUsageLedger` / `ReflectionRunReader` 改为持有**持有者**而非当时的字符串；
  账本在目录变化时**丢弃内存累计并重新读盘**（预算应按项目分别计）；
- 新增 `ReflectionWorkspaceParticipant`（`IWorkspaceApplyParticipant`）：切换成功后对齐目录。

**新增测试**：

| 测试 | 覆盖 |
|---|---|
| `IdlePeriodClaimStoreTests`（7 条） | 登记 / 活动时刻变化解锁 / `Release` 可重试 / 空白 ID 忽略 / `Prune` / **两个消费方互不饿死** |
| `MemoryEvolutionActivityRetentionTests`（3 条） | 演化后共享时间戳仍在 / 同段空闲期只演化一次 / 新空闲期可再演化 |
| `ReflectionIdleSharingTests`（3 条） | 别的消费方处理过后扫描器仍能看到 / 扫描器不删事实 / 自己的冷却仍生效 |
| `ReflectionTranscriptTests`（8 条） | 工具调用压平 / `[tool:name]` / 多模态文本段 / 附件摘要 / 不内联 base64 / 两级截断 |
| `ReflectionStorageRootTests`（5 条） | 工作区对齐 / 幂等 / 未选定不覆盖 / journal 写新根 / 账本随目录重置 |

> `BuildTranscript` 是 `internal`，为此 `Seeing.Agent.Reflection.csproj` 加了
> `InternalsVisibleTo("Seeing.Agent.Reflection.Tests")`：它是「模型看到什么」的唯一真相源，
> 漏一项都不会报错，必须能逐条钉住。

**已知边界（本轮接受）**：

- `MemoryEvolutionWorker` 的空闲循环是私有实现（含 `Task.Delay(1min)`），测试里复刻了同一套判定顺序
  来验证去重契约，而不是驱动真实循环。worker 内部时序若被改写，这两处需同步。
- 手动触发仍只列内存中的会话（§9 已记）。

---

## 11. 2026-11-12 第三轮：记录页流程优化与技能层级决策

### 11.1 「在复盘中」的可观测性

原先用户点完「立即复盘」到记录出现之间是完全黑箱：分不清「在跑」「排队」「按钮没生效」。

- `IReflectionQueue.Snapshot()`：排队请求的只读快照（按 `At` 升序 = 谁等得最久）。
  刻意不暴露可变集合——那会破坏「字典有值 ⇔ 通道有槽」的不变量；
- `ReflectionRunningTracker`（进程内单槽）：Worker `TryDequeue` 后请求**已离开队列**，
  不单独登记就会出现「排队数把正在跑的也算进去」的错觉。`End` 按值比对清理，
  且放在 `finally` —— 异常/取消路径漏清会让 UI 永久显示「复盘中」；
- `IReflectionStatus` 扩为 `Queued` / `Running`；记录页 3 秒轮询**只刷这一段**，
  不重载记录列表（否则用户展开的详情会被反复重置）。

### 11.2 删除记录

`IReflectionJournal.DeleteAsync(path)` + `ReflectionRunOperations.DeleteAsync`：

- **路径边界走 `PathBoundary.IsStrictlyWithin(runs/)`**，不接受任何 UI 拼接。
  记录文件名是 `executionId` 消毒后的结果（含截断 + 哈希），没有稳定主键可依赖，
  因此把 `FilePath` 原样传回、由服务端校验后删除；
- 同 `executionId` 的 `<id>.json` 与 `<id>.fail.json` 视为一条记录**一起删**，
  否则列表会残留孤立失败快照，用户以为没删掉；
- **只删记录，不动技能**：技能是独立资产，其 frontmatter 已带 `source-session` 可追溯。
  级联删技能会让「记录页一次误操作」变成「技能库内容丢失」。

### 11.3 生成 SKILL

`ReflectionSuggestionsWriter.CreateFromSuggestionAsync(suggestion, sessionId, level)`：

- **不看 `ReflectionMode`**：模式约束的是「复盘跑完后自动写盘」这个自主行为，
  而这是用户对着具体建议的显式点击 —— 同「手动触发跳过 `Enabled`」的理由；
- 校验全部复用 `TryCreateAsync`（名称合法性 / 正文长度 / 提示注入 / 查重），不另开一套口径；
- 失败原因直接可展示（重名 / 非法字符 / 注入片段），因为用户就站在那条建议前面。

### 11.4 技能层级决策（关键）

**记录位置是项目级，不是用户级**——这一点常被误读：

| 对象 | 实际位置 | 理由 |
|---|---|---|
| 复盘记录（journal / usage / 记录页） | `{工作区}/.seeing/reflection/` | 与 session、预算同域；切项目天然隔离 |
| 复盘配置 | `~/.seeing/reflection.json` | 个人偏好，跨项目一致 |
| 自动写入的技能 | **项目级** | 结论来自本项目会话 |
| 手动生成的技能 | **默认项目级**，API 支持显式用户级 | 同上；跨项目复用走技能页晋升 |

**为什么不做「跨项目记录汇总 → 全局技能」（本次明确不做）**：

1. **跨项目读盘需要新子系统**：汇总 N 个项目的记录意味着读 `~/proj-a/.seeing/reflection` 等，
   而应用只知道当前工作区。没有「哪些项目参与汇总」的登记与授权，这就是一个绕过工作区边界的后台扫描器。
2. **提示词语义冲突**：复盘反模式清单第一条就是「环境相关的偶发失败」、第二条是「对工具的负面断言」——
   这些规则的存在理由正是「换个项目就会误导」。把它们制度化地推广到全局，方向相反。
3. **切入单元选错**：已有经过蒸馏、结构化、带 `evidence` 的产品——**技能**。
   让单位从「N 份原始记录」变成「1 个已验收的技能」，收益相近而代价低一个量级：
   不跨项目读盘、不写新提示词、天然幂等（同名判断）、可审计（`distilled-by` / `source-session`）。

**后续演进（未实现）**：技能页的「晋升 / 降级」——把某个项目级技能提升为用户级。
它是当前架构下的自然延伸，也是将来若要判断「是否值得做跨项目汇总」的证据来源：
如果同一技能在多个项目里反复长出来，那才是有数据支撑的 L3 需求
（原 §8 的「Layer 3 Curator」仍然不做）。

**新增测试**：

| 测试 | 覆盖 |
|---|---|
| `ReflectionRunOperationsTests`（11 条） | 删除单文件 / 配对失败快照一起删 / 拒绝 runs 外路径 / 拒绝 `..` 穿越 / 读取越界拒绝 / 序号越界 / **手动生成跳过模式门禁** / **默认项目级** / **显式用户级** |
| `ReflectionRunningStateTests`（6 条） | 排队快照排序 / 出队即离开 / 执行中登记与清理 / 按值清理 / 状态端口聚合 / **执行器抛异常时也要清空** |

---

## 12. 2026-11-12 第四轮：稳定 `RunId` 与模块禁用完整性

### 12.1 两个正在发生的数据缺陷（由「加稳定 ID」串出来）

引入 `RunId` 时发现两个既有缺陷，它们都不是「将来的债」而是**当前就在丢数据**：

| # | 缺陷 | 后果 |
|---|---|---|
| 1 | 文件名用 `ExecutionId`，而空闲/手动触发的 `ExecutionId` 是**空串** → `SanitizeStem("")` 返回 `"unknown"` | 同一天所有空闲/手动复盘写进 `runs/{date}/unknown.json`，**互相覆盖，只剩最后一条** |
| 2 | 成功记录用 `outcome.ExecutionId`（复盘子会话的执行 ID），失败快照用 `request.ExecutionId`（源会话的执行 ID）——**两者本就不同** | 成功记录与失败快照**永远配不上对**，上一轮的「配对删除」实际是空转 |

**修复**：`ReflectionRunRecord` / `ReflectionFailureRecord` 增加 `RunId`（形如
`run_20261112112834_ab12cd34`：时间前缀便于人工翻阅排序，随机后缀防同秒并发撞名）。
Executor 在进入主流程后生成一次，成功与失败路径共用同一个值。

### 12.2 删除改为按 ID

- `IReflectionJournal.DeleteAsync(runId)`：按 ID 定位、跨天目录查找（记录与日期无绑定关系），
  成功记录与失败快照一起删；
- **仍保留 `PathBoundary` 校验**：`RunId` 会被拼进文件名，即使已消毒也不假设「消毒永远正确」；
- `ReflectionRunSummary` 增加 `RunId`；旧记录（无该字段）**从文件名回退**——
  否则升级前的记录将永远无法按 ID 删除；
- `ReflectionJournal.SanitizeStem` 提为公开：读取侧与写入侧必须共用同一套命名规则，
  两边各写一份会在改名时出现「写进去读不出来」。

### 12.3 模块禁用的完整性核实

**结论：`reflection` 是标准 `ISeeingModule`，禁用后功能链路逐层受控**，但发现一处不对称。

| 关注点 | 禁用后 | 机制 |
|---|---|---|
| 后台循环 | 停 | `ModuleLifecycleManager` → `StopModuleHostedServices`；`ExecuteAsync` 循环条件含 `IsActive` |
| 队列消费 | 停 | `DrainQueueAsync` while 条件含 `IsActive` |
| 终态观察者 | 直接返回 | `OnExecutionCompletedAsync` 首行 `if (!_activity.IsActive) return` |
| 空闲扫描 | 跳过 | `RunIdleScan` 首行判 `IsActive` |
| 手动触发 | 拒绝 | `ReflectionManualTrigger` 首行判 `IsActive` |
| `/reflection` 页面 / 设置卡 | 隐藏 | `Requires=[reflection]` → `UiVisibility` 命中 `ModuleDisabled` |
| DI 单例 | 仍在容器 | 组装期注册、运行期门控（与 Memory/Scheduler 同构，**不是缺陷**） |
| `reflection` agent | ~~仍在 AgentStore~~ → **已对称注销** | 原实现只注册不注销 |

**为什么「注册后永不注销」不成立**：原注释的理由是「避免影响进行中的会话」，
但 `ModuleSettlementReloadHandler` 默认就在**在途执行结束后**才 Deactivate，
且在途轮次已在 `BuildExecutionContextAsync` 里解析过定义——注销不影响它。
残留的后果是 `AgentStore.Get("reflection")` 仍能取到定义（用户选择列表 / 可委派集合 /
默认 agent 解析三处都因 `IsHidden` / `Primary` 过滤掉了，所以是资源残留而非可见泄漏）。

**修复**：`DeactivateAsync` 对称注销，且带 `ReferenceEquals` 守卫——
用户可能在 MD 里覆盖同名定义，只有「仍是我注册的那个实例」才该被删
（与 `AgentsBuiltInModule.DeactivateAsync` 同形）。

**新增测试**（`ReflectionModuleDisableTests`，10 条）：停用置 `IsActive=false` /
**停用注销 agent** / **不误删用户同名覆盖** / 再启用可复活 /
模块未激活时终态回调不入队 / 手动触发被拒 / 空闲扫描配置闸 /
两条 UI 声明都归属自身。

**变异验证**：移除 `DeactivateAsync` 中的注销 → `Deactivate_Should_Unregister_Agent` 红。

**已知边界**：UI 可见性判定（`UiVisibility`）在脊柱层，本测试项目未引用 Core，
因此这里只断言「声明带归属」；`ModuleDisabled → 隐藏` 的判定由 `WebUI.Tests` 的
`SettingsAndSlotMigrationTests` 覆盖。



