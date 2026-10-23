# 工具钉选与执行级可见性收口 —— 设计文档

**日期:** 2026-10-23
**状态:** 待审批
**范围:** 工具分层暴露的默认钉选 + 2026-10-23 架构评审整改（完整收口档 C）
**前置实现:** 执行级 `IExecutionToolScope`（已实现，未提交）

---

## 1. 背景

### 1.1 已修复的主问题

分层暴露下，`tool_search detail=full` 命中的工具原先要"下一次执行"才可用；模型在同一次执行内检索后立刻调用会被拒绝，并收到"请先检索"的错误指引，导致重复检索、轮次中断、用户被迫再发消息。

已落地的修法是**执行级可见性作用域**：

- 可调用 = 本轮内联集 ∪ 本执行已加载集；
- 加载只接受本轮结算为 `Deferred` 的工具（不绕过 Agent 白/黑名单）；
- **不改变 `tools[]`**（单轮 schema 不变、前缀缓存零额外失效）。

### 1.2 本次要解决的问题

独立架构评审结论：内核正确，但外围仍有补丁，并有 1 个 Critical 回归。

| 编号 | 问题 | 影响 |
|---|---|---|
| C1 | 默认钉选集写进 `SeeingAgentOptions` 初始化器，被 `MergeDeep` 当成"项目级已设置"，静默覆盖用户配置；`[]` 也无法清空 | 用户配置失效、承诺不可兑现 |
| I1 | 门闸用冻结快照，`tool_search` 每次现场重新结算 → 两份视图 | MCP 增删/热重载下行为不可解释 |
| I2 | 能力包直接改写执行期状态，无审计；写权限开放给所有工具 | 放行不可追溯，约束变君子协定 |
| I3 | `IToolManager` 契约未同步，只依赖 Abstractions 的宿主拿不到作用域 | 静默降级 |
| I4 | 模型可见的参数描述仍写"下一轮生效" | 直接抵消修复效果 |
| I5 | Scheduler 旁路从不 `PromotePending`（注释与代码不符） | cron 会话"下一次执行起内联"为假 |
| I7 | `IsCallable`→`SendsSchema` 改名后生产代码零调用 | 只付破坏性代价、无内部收益 |
| M* | 文案术语、加锁粒度、文档漂移、测试缺口 | 一致性与可维护性 |

### 1.3 已确认的产品选择

1. **钉选只保留一个默认集**：不做 tool-state、不做逐工具开关、不做 UI、不搞多层。
2. 默认集放组合层（Hosting），不写进配置初始化器（从根上消除 C1）。
3. 现有 `Tools.Exposure.PinnedTools`（默认 `[]`）保留为**显式覆盖**：非空则整体替换默认集。
4. `Agent.PinnedTools` 非空则整体覆盖（Agent 说了算）。
5. 本次按**完整收口（C）**实施；不包含真实 provider 联调。

---

## 2. 目标 / 非目标 / 反目标

**目标**

- 分层模式下默认钉住常用工具，`tools[]` 不再只剩 `tool_search`。
- 消除 C1 根因：配置层不再承载默认钉选集。
- 执行期状态迁移可审计（谁在何时放行）。
- 一次执行内"能搜到什么"与"能调什么"同源。
- Scheduler 旁路与主链对"发现提升"语义一致。
- 模型侧文案与执行行为完全一致。

**非目标**

- 不做逐工具钉选开关、不做 `tool-state.json` 扩展、不做工具页改动。
- 不做 Code Mode、不做轮内 schema 重算（`tools[]` 一次执行内恒定）。
- 不改权限判定链、审批通道、Hook 语义。
- 不做真实 provider 联调与收益数字回填。

**反目标（这样改就算失败）**

- 用户级配置被未显式设置的项目级值覆盖。
- 出现"检索了却调不了"或"没检索却调得了（且无法解释）"。
- 为修文案放宽门闸，或为修门闸引入第二套 schema 计算。

---

## 3. 现状与约束

**架构硬规则**（`AGENTS.md`）

- Core 瘦脊柱（零工具）；能力包只依赖 Abstractions + 原语。
- Abstractions **零实现**。
- schema 唯一计算点在 `SessionToolSchemaResolver`（Hosting）。
- 会话状态单一权威写入者。
- 工具调用并行执行，执行期共享状态必须线程安全。
- Scheduler 旁路直接调 `IAgentExecutor`，绕过 `ExecutionRunner`。

**当前实现（改动前）**

| 关注点 | 位置 | 现状 |
|---|---|---|
| 结算 | `SessionToolSchemaResolver.SettleAsync` | 产出 inline/deferred/目录/token |
| 钉选解析 | `ToolExposureConfigResolver.Resolve` | Agent 覆盖 → 全局 `Tools.Exposure.PinnedTools` |
| 门闸 | `AgentExecutor.ExecuteSingleToolCallAsync` | `toolScope.IsCallable(name)` |
| 作用域 | `ExecutionToolScope` | 种子=结算视图；`TryLoad` 仅接受 Deferred |
| 发现态 | `SessionToolExposureState` | 提升只在 `ExecutionRunner:448` |
| 启用/禁用 | `ToolManager` | `tool-state.json` 双层 `DisabledTools` |
| 检索 | `ToolSearchTool` | 每次调用现场 `ResolveAsync` 取语料；直接写 `ToolContext.ToolScope` |

---

## 4. 决策

### D1 钉选模型：一个默认集 + 两级显式覆盖

```
代码默认集（Hosting 常量）
  → config.PinnedTools 非空：整体替换
  → Agent.PinnedTools 非空：整体替换（最高）
DisabledTools 永远优先（被禁用的工具不内联）
```

- 默认集**不进入** `SeeingAgentOptions` 初始化器：配置里只存"用户显式写的清单"，默认 `[]` 表示未设置。
- 想完全不钉选：给 Agent 配 `PinnedTools: []`（空数组 = 显式覆盖为空）。
- 想换成另一组：在 `seeing.json` 写非空 `PinnedTools`。

**理由**

- 满足"常用工具默认被钉住"，同时不引入多层管理。
- 从根上消除 C1：`MergeDeep` 的"空集合=未设置"不再影响钉选（默认值在代码里）。

**备选与弃用**

- 默认集写进 options 初始化器：即 C1，弃用。
- tool-state.json + 逐工具开关 + 三层合并：用户明确判定过度设计，弃用。
- `PinCoreTools` / `UnpinnedTools`：同上，弃用。

### D2 默认集内容（19 个，顺序即内联顺序）

```
read, write, edit, glob, grep, bash, delete, open_file, add_workspace_path,
question, skill, task, todowrite,
webfetch, websearch, codesearch,
memory_search, memory_read, memory_write
```

`tool_search` 恒内联，不进默认集。

### D3 声明式"加载"上报（I2）

**选择**

- `ToolResult` 新增 `IReadOnlyList<string>? LoadedToolIds`。
- 新增能力键 `ToolCapabilityKeys.ExposureLoad = "exposure.load"`。
- 只有声明该能力的工具，其 `LoadedToolIds` 才会被执行器采纳。
- `AgentExecutor` 在工具返回后统一 `toolScope.TryLoad(...)`、记结构化日志、写入 `ToolCallEvent.Metadata["exposure.loaded"]`。
- 能力包不再直接写 `ToolContext.ToolScope`；该属性保留**只读**用途（判断宿主是否支持同执行放行，以决定文案）。

**理由**

- 状态迁移回到执行器（唯一所有者），可审计、可测试。
- 关闭"任意工具（含不可信 MCP 工具）都能提升可见性"的口子。
- 与"不用 `ToolResult.Metadata` 传执行控制"的约定不冲突：这是强类型声明，不是字符串指令。

**备选与弃用**

- 保留 `ToolScope.TryLoad` 直接写：不可审计、写权限过大，弃用。
- `ToolResult.Metadata["exposure.loaded"]`：字符串魔法，弃用。

### D4 单一结算快照（I1）

**选择**

- `IExecutionToolScope` 暴露 `IReadOnlyList<ExposedTool> DeferredTools`（种子即本轮结算的 deferred 集）。
- `ToolSearchTool` 语料优先取 `context.ToolScope?.DeferredTools`；仅当无作用域（非执行器宿主）才回退 `ISessionToolSchemaResolver.ResolveAsync`。
- `GetFullDefinition` 直接用条目携带的 `Schema`，不再回头查结算。

**理由**

- "能搜到什么"与"能调什么"同源，消除两份视图。
- 去掉每次检索的重复结算（既有已知边界 §6.12 一并消失）。

**备选与弃用**

- 把整个 `SessionToolSchemaResolution` 塞进 `ToolContext`：暴露面过大，弃用。

### D5 会话暴露状态的单一写入出口（I5）

**选择**

新增契约 `ISessionToolExposureWriter`（Abstractions）：

```csharp
void PromotePending(SessionData session);
Task<bool> PromotePendingAsync(string sessionId, CancellationToken ct = default); // 会话不存在 → false，不抛
void Commit(SessionData session, IReadOnlyList<string>? discovered, bool escalated,
            int schemaTokens, int catalogTokens);
```

- Hosting 提供实现（内部调用 `session.ToolExposure.*`）。
- `ExecutionRunner` 轮首经该端口 `PromotePending` + `Commit`（已有会话实例，用同步重载）。
- Scheduler 共享的 `AgentJob.ResolveToolSchemasAsync` 在解析前调 `PromotePendingAsync(sessionId)`；会话不存在/加载失败返回 false 并跳过，保持"旁路不阻断任务"的既有语义。
- `IToolDiscoveryStore.Add` 保持写 `Pending`。
- 源码不变量测试的允许写入清单改为"端口实现 + 原语 + discovery store"。

**持久化责任（显式决定）**

提升**不强制落盘**：主链由会话运行时的既有保存路径负责；旁路不额外 `SaveAsync`。提升是幂等的（`Discovered ∪ Pending`，清空 Pending），重启后从旧文件再提升一次结果相同，不会产生错误状态。

**理由**

- 写入点从"主链一处 + 旁路零处"收敛为一个实现。
- 修正"注释说会提升、代码没提升"的缺陷。

**备选与弃用**

- Scheduler 直接 `session.ToolExposure.PromotePending()`：新增写入点，弃用。
- 端口内强制 `SaveAsync`：与事件驱动保存重复，弃用。

### D6 `IToolManager` 契约补齐（I3）

**选择**

`IToolManager.ExecuteAsync` 追加两个可选参数：

```csharp
Task<ToolResult> ExecuteAsync(
    ToolCall toolCall,
    string sessionId = "",
    CancellationToken cancellationToken = default,
    Func<IMessageEvent, ValueTask>? emitAsync = null,
    IPermissionAuthorizer? permissionAuthorizer = null,
    AgentDefinition? agent = null,
    IExecutionToolScope? toolScope = null);
```

**理由**

- 只依赖 Abstractions 的宿主也能拿到同执行放行能力，不再静默降级。
- 不引入参数对象（用户要求降低复杂度）；实现方仅 `ToolManager`。
- 进破坏性清单。

### D7 `SendsSchema`（I7）

- 只保留 `SendsSchema`；`IsCallable` **直接删除**，不提供兼容别名（用户明确要求不做兼容）。
- 进破坏性清单。

### D8 文案与术语统一（I4/M*）

统一为：

- **本次执行内可调用**（同一轮用户输入内的后续 step）
- **下一次执行起进入 `tools[]`**

需要改的位置：

- `ToolSearchTool`：类注释、`detail` 参数 description、结果正文（三态）；
- `ToolExposureEngine`：目录开场白改为"**未加载前**不能直接调用"，并声明"未展示 ≠ 缺失"；
- `AgentExecutor`：拒绝文案；
- `IToolDiscoveryStore` / `SessionToolDiscoveryStore` / `SessionToolExposureState` 注释。

### D9 收益数字

- 删除文档中未验证的"降幅 ≥60%"。
- 增加确定性估算测试：默认集 19 项 vs 全量工具，`CharBasedTokenCounter` 计算 `SchemaTokens` 对照；真实降幅不写承诺。

### D10 破坏性清单

| 变更 | 处理 |
|---|---|
| 删除 `Tools.Exposure.PinnedTools` 的"默认 19 项"（改为默认 `[]` + 代码默认集） | 文档更新；旧配置若写了非空列表则成为显式覆盖（行为兼容） |
| `IToolManager.ExecuteAsync` 追加可选参数 | 破坏性清单 |
| `IToolSchemaResolution.IsCallable` 删除（保留 `SendsSchema`） | 破坏性清单；不做兼容别名 |
| `IExecutionToolScope` 新增 `DeferredTools` | 破坏性清单（接口成员） |
| `ToolResult` 新增 `LoadedToolIds`、`ToolCapabilityKeys.ExposureLoad` | 增量，非破坏 |

---

## 5. 关键流程

### 5.1 一次执行

```
ExecutionRunner（或 Scheduler helper）
  → exposureWriter.PromotePending(session)           // Scheduler 用 async 重载
  → resolver.ResolveAsync(session, agent)
       ├─ 结算（模块/场景/禁用/Agent 过滤）
       ├─ 钉选：Agent ?? (config 非空 ? config : 默认集)，再过滤禁用
       └─ ToolExposureEngine.Compute → inline / deferred / catalog
  → exposureWriter.Commit(discovered, escalated, schemaTokens, catalogTokens)
  → AgentExecutor.ExecuteAsync
       ├─ 创建 ExecutionToolScope(settlement)   ← 唯一执行期状态
       ├─ 每步：BuildRequest（tools[] 恒定）
       ├─ 工具执行前：toolScope.IsCallable ?
       ├─ 工具返回后：result.LoadedToolIds（仅 exposure.load）→ toolScope.TryLoad
       │                + 日志 + ToolCallEvent.Metadata["exposure.loaded"]
       └─ tool_search：语料 = toolScope.DeferredTools（无作用域才回退 resolver）
```

### 5.2 钉选生效矩阵

| 场景 | 结果 |
|---|---|
| 无任何配置 | 默认集（19 项）钉住 |
| `config.PinnedTools` 非空 | 整体替换默认集 |
| `Agent.PinnedTools` 非空 | 整体覆盖以上（空数组 = 不钉） |
| 工具被禁用 | 不内联（禁用优先） |
| 配置写了不存在的 id | 忽略（引擎对缺失 id no-op） |
| `PolicyMode=All` | 全内联，钉选无意义 |

---

## 6. 测试计划

**钉选**

- 默认集：19 项、顺序稳定、无 `tool_search`。
- `config.PinnedTools` 非空 → 替换；空 → 默认集。
- Agent 非空（含空数组）→ 整体覆盖。
- 禁用优先于钉选。
- **C1 回归**：`SeeingAgentOptions.Tools.Exposure.PinnedTools` 默认必须是空数组（防止默认值回流到配置层）。

**执行级作用域**

- 已有：内联/延迟/防越权/并发/幂等。
- 新增：`DeferredTools` 与结算一致；加载后 `LoadedIds` 单调。

**执行器**

- 真实 `ToolSearchTool` × 真实 `ExecutionToolScope` 全链路（检索 → 声明 → 应用 → 下一步放行）。
- `discoveryStore.Add == false` → 不声明、文案为失败。
- 未声明 `exposure.load` 的工具返回 `LoadedToolIds` → 不生效。
- 加载后仍走权限审批（Deny 用例）。
- 同执行内两次请求 `tools[]` 逐字节一致（缓存承诺）。

**Scheduler**

- `AgentJob.ResolveToolSchemasAsync` 前提升 Pending；下次执行工具已内联。
- 会话不存在 → 不抛、任务继续。
- 旁路不写度量、不改升级位。

**不变量**

- 会话暴露状态写入仅在端口实现/原语/discovery store。
- `AgentExecutor` 不引用 `ToolExposure`/`SessionData`（行为测试为主，源码扫描为辅）。
- schema 仍只在 resolver 计算。

---

## 7. 风险与已知边界

1. **provider 兼容性（中风险）**：模型调用"未在 `tools[]` 内"的函数名，部分 OpenAI 兼容网关/本地模板可能拒绝，或模型不服从。本次不做真实联调，只交付手工验证清单；失败时的 Plan B 是"轮内重算 schema"（需放弃单轮 schema 不变并接受缓存失效）。
2. **Scheduler 与用户轮并发**：以"同会话串行"为前提；并发执行同会话属既有边界。
3. **默认集 token 成本**：19 项估算（含 `bash`/`edit`/`task`/`todowrite` 等大 schema）约 6–8k；相对全量 15–35k 仍有收益，但不承诺具体百分比。
4. **不钉选的全局关闭**：`seeing.json` 的空 `PinnedTools` == 未设置，无法全局关闭默认集；需在 Agent 上配 `PinnedTools: []`。这是刻意取舍（避免再引入开关层）。
5. **钉选与 EscalateSchemaTokens**：阈值按全部候选工具估算，与钉选数量无直接交互（维持现状）。

---

## 8. 实施顺序

1. 钉选：Hosting 默认集常量 + `ToolExposureConfigResolver` 解析（config 非空覆盖）+ `SessionToolSchemaResolver` 过滤禁用 + C1 回归测试。
2. 声明式加载：`ToolResult.LoadedToolIds` + `ToolCapabilityKeys.ExposureLoad` + 执行器应用/日志/Metadata + `ToolSearchTool` 改造。
3. 单一快照：`IExecutionToolScope.DeferredTools` + `ToolSearchTool` 优先使用 + 回退路径。
4. 写出口端口：`ISessionToolExposureWriter` + `ExecutionRunner` 接线 + Scheduler 提升 + 不变量更新。
5. `IToolManager` 契约 + `[Obsolete] IsCallable`。
6. 文案统一 + 文档更新 + 收益数字删除。
7. 全量构建与测试回归。

---

## 9. 明确留给实现者

- 结构化日志字段命名与级别。
- `DeferredTools` 的线程安全实现细节（不可变快照即可）。
- 测试夹具复用方式。
- 手工联调清单的具体用例组织。
- 是否给 `ExecutionToolScope` 增加调试用 `ToString`。
