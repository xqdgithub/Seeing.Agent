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

节名 `Reflection`，存放在 **`seeing.json`**（`~/.seeing/seeing.json` 用户级 / `.seeing/seeing.json` 项目级），
经 `IConfigSectionStore` 读取并在 `ConfigSectionRegistry` 登记（WebUI 配置页可见、纳入统一配置校验）。

> **不要**把配置写到 `appsettings.json` 的 `SeeingAgent:Reflection` 节——本仓能力包配置不走
> Microsoft 的 `IConfiguration` 管道，写在那里不会生效（这是审查抓出的原始缺陷，已修）。

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

### 3.1 两条触发来源的差异

| 来源 | 门禁 |
|---|---|
| 轮次触发（`ReflectionObserver`） | 八重门禁全开（见 §2.1） |
| 空闲触发（`ReflectionWorker`） | 只读内存校验：`Kind == Root` + `IsUserVisibleTaskOrigin()` + 二次确认仍空闲 + 冷却去重 |

空闲触发**不**走 `ReflectionObserver`，因此必须自带等价的来源门禁——否则会对子代理会话
甚至复盘会话自身做复盘。冷却键是「投递时的活动时刻」：用户一旦回到会话，
`Touch` 刷新时刻，天然允许下一次空闲复盘。

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
4. **`ReflectionWorker` 停用不取消在途复盘**：循环只用 `stoppingToken`，`IsActive=false` 只在下一次判循环条件时生效；单次 `RunAsync` 最长 `RunTimeoutMinutes`（10 分钟），停用后仍可能跑完当前这一次。
5. **`AddSeeingReflection` 的配置节存储缺失时退回默认值**：`IConfigSectionStore` 未注册（独立装配/单元测试）时 `Enabled` 恒为 `false`，即「不工作」。
6. **journal 写失败降级为 `SkipReason = "failed"`**：分析已跑完但结果未落盘，不报告成功。
7. **`SuggestionCount` 为 0 与「解析失败」不可区分**：`{"action":"skip"}` 与 JSON 畸形都产出空列表，前者记 `Executed = true`，后者记 `SkipReason = "parse-failed"`——但两者在 `ReflectionRunRecord.Suggestions` 里都为空。失败快照的 `Detail` 可帮助区分。
8. **`ExecutionUsageLedger` 单进程假设**：账本在锁内全量覆盖写；同一工作区两个宿主实例各写各的，后写者覆盖前者。预算是**按项目目录**分别计，切换工作区即重置。
9. **`TryEnqueue` 返回 `false` 有二义性**：既表示「同会话已存在、仅更新 executionId」，也表示「队列满被丢弃」。调用方只能记一条含糊日志。
10. **`ReflectionObserver` 先 Reset 后入队**：入队失败时本轮触发额度已清零，该会话要再等 N 轮才可能触发。
11. **`ReflectionTurnCounter` 淘汰会让会话轮次归零**：上限 512，超限时淘汰最久未触发的会话，其触发点后移几轮。与「重启归零可接受」同类。
12. **`ReflectionJournal` 文件名截断到 100 字符 + 8 位哈希**：超出时不同 ID 仍可能因哈希碰撞而覆盖（概率可忽略）。

---

## 7. 验证

```powershell
dotnet build Seeing.Agent.slnx --no-incremental   # 0 error

# 全量（均为 VSTest，MTP 在本环境无法发现测试）
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll                              # 1297
dotnet vstest tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll   # 105
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll           # 221
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll  # 274
dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll                       # 285
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll              # 75
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll  # 10
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll                         # 617
dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll                         # 84
```

总计 2963 用例，0 failed。

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

---

## 8. 下一步（未实现）

- **P4 观察期**：真实运行下的成本核对、建议质量抽检。
- **提案通道**：修改/删除已有技能走提案而非直接写（当前只支持新建）。
- **Layer 3 Curator**：定期梳理技能库（本期明确不做）。
