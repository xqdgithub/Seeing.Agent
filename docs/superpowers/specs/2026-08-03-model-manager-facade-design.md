# Model Manager 门面与 Manager 架构原则

**日期:** 2026-08-03  
**状态:** 已审阅通过，待实施  
**范围:** Manager 架构原则 + 模型域首个落地样板

## 背景与问题

模型相关能力目前分散在多个组件，外部（App / Gateway / WebUI / Tools）自行拼装：

| 组件 | 职责碎片 |
|------|----------|
| `IModelConfigManager` | 目录 CRUD + `GetDefaultModel` |
| `IProviderManager` | Provider 配置与客户端；被会话路径错误注入 |
| `AgentSelectionResolver.ResolveModelId` | 默认模型优先级 |
| `AgentRuntimeManager.GetEffectiveModelIdAsync` | 另一套优先级（旁路） |
| `ExecutionJobService.TryBackfillSessionModelSelection` | App 层拆 provider 写 Session |
| `ModelRef.Parse/Format` | 被 UI/App/Tools 直接调用 |
| `Session.razor` | 本地 Parse、默认回填、目录首项回退 |

结果：新建会话默认模型修复需要改多处；Native/ACP 对 session modelRef 格式不一致；Provider 概念泄漏到执行链路。

参照已有的 `IMcpManager` 聚合门面，模型域应对齐「一域一门面」。

## 目标

1. 沉淀 **Manager 架构原则**，供后续域复用。
2. 以 **`IModelManager`** 作为模型域唯一对外入口（会话/执行路径）。
3. 跨层只传递 **modelRef** 字符串（`provider/model` 或裸 id）。
4. `SessionData.SelectedModel` 为唯一模型字段；**删除** `SelectedModelProvider`，不做历史数据兼容。

## 非目标

- 不做旧会话 `SelectedModelProvider` 读时升级或迁移。
- 不把 Provider CRUD 赶出管理页（Models / Settings 仍可用 `IProviderManager`）。
- 不让 `Seeing.Session` 依赖 `Seeing.Agent`。
- 本轮不顺带大改 Agent 选择、MCP、Scheduler 等其他域（仅定原则；模型为样板）。

---

## 1. Manager 架构原则

每个业务域对外只暴露一个 `IXxxManager` 门面。外部只依赖门面完成该域用例，不拼装内部协作对象。

| 规则 | 含义 |
|------|------|
| 一域一门面 | 如 Model → `IModelManager`；Agent 选择不塞进 Model |
| 对外按用例 | Resolve / Apply / Seed / List / GetDefault，而非暴露 Parse、ProviderKeys |
| 内部可拆类 | `ModelRef`、目录索引、Provider 客户端可内部协作；不作为外部必用依赖 |
| 分层依赖 | 会话/执行：只注入域 Manager。管理页：可额外用配置向 Manager（如 `IProviderManager`） |
| 稳定契约 | 跨层用稳定值语义（模型域 = modelRef） |
| 禁止旁路 | 禁止 App/UI 再写一套默认回填；一律调 Manager |

### Provider 边界（本设计选定）

- **会话 / 执行 / Gateway / Tools：** 不注入 `IProviderManager`，不调用 `ModelRef`。
- **管理页：** 可继续使用 `IProviderManager`（配置 CRUD、测连通）。
- **LlmService：** 可在 Core/Llm 内部使用 Provider 解析客户端；对编排层仍只收 modelRef。

---

## 2. `IModelManager` 对外 API

命名空间：`Seeing.Agent.Llm`。实现可基于现有 `ModelConfigManager` 演进，或新建 `ModelManager` 并内部复用配置逻辑。DI 注册 `IModelManager`；`IModelConfigManager` 过渡期可由同一实例实现，随后 Obsolete/删除。

### 2.1 目录与默认

- `GetModels()` / `GetModel(modelRef)` / `GetModelsByType(...)`
- `GetDefaultModel()` / `SetDefaultModelAsync(...)` / `CanSetAsDefaultModel(...)`
- CRUD：`Add/Update/Delete/SaveModelsAsync`（管理页可经同一门面或保留 Config 子集）

### 2.2 解析

- `ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName)`  
  优先级：`request > session > Agent.Model > DefaultModel`
- `ResolveAcpModel(string? requestModelRef, string? sessionModelRef)`  
  优先级：`request > session`（**不**回退 Native `DefaultModel` 或 Agent.Model）

### 2.3 会话

- `GetSessionModelRef(SessionData session)` → 返回 `session.SelectedModel`（唯一字段）
- `ApplyModelToSession(SessionData session, string? modelRef)` → trim 后写入 `SelectedModel`；空白则清空。若能在目录中解析到规范键（`GetModel`），可写入目录键；否则原样保留（支持孤儿 / ACP 自由文本）
- `SeedSessionModel(SessionData session, string agentName)`  
  - Native：`ResolveNativeModel(null, null, agentName)` 非空则 Apply  
  - ACP：不写入 Native DefaultModel

> **规范化：** 不做「裸 id → 猜测 provider」的隐式魔法；仅当目录可唯一匹配时提升为目录键。Provider 拆分只发生在 Llm 调用客户端时的内部路径。

### 2.4 明确不对外

- `ModelRef.Parse` / `Format`
- known Providers 列表、拆出的 apiModelId / providerId（仅 Manager / LlmService 内部）

### 2.5 相邻边界

- `AgentSelectionResolver`：仅 `ResolveAgentIdAsync` / `ResolveAcpModeId`
- 删除 `ResolveModelId` / `ResolveAcpModelId`（迁入 `IModelManager`）
- `AgentRuntimeManager` 有效模型路径委托 `ResolveNativeModel`，或删除与主链脱节的旁路
- `TryBackfillSessionModelSelection` 的**模型**分支删除，改为 `ApplyModelToSession`；ACP mode 可留在 App 小方法

---

## 3. 数据模型与数据流

### 3.1 Session 存储

- `SessionData.SelectedModel`：存完整 modelRef。
- **删除** `SelectedModelProvider`（类型、Fork/Child 拷贝、UI、测试、钩子一并去掉）。
- `ISessionManager.SetModelAsync(sessionId, modelRef)`：单参数；去掉 `providerId`。
- Seeing.Session 不解析 provider；规范化/校验由 Agent 侧 `IModelManager` 在写入前完成。

### 3.2 数据流

```
seeing.json DefaultModel
        │
        ▼
  IModelManager.SeedSessionModel / Resolve*
        │
        ▼
  Session.SelectedModel (= modelRef)
        │
        ▼
  ChatOptions.ModelId (= modelRef)
        │
        ▼
  ResolveNative|Acp → RequestModelId → LlmService / ACP
```

- **新建会话：** ResolveAgent → SeedSessionModel → Save。  
- **提交执行：** 上游传 modelRef；BuildContext 只调 Resolve。  
- **ACP / Native：** session 入参统一为 `SelectedModel` 整串（修复 ACP 丢 provider 问题）。

---

## 4. 调用方收敛

| 调用方 | 目标状态 |
|--------|----------|
| `ChatOrchestrator` | 去掉 `IProviderManager`；Create 调 `SeedSessionModel` |
| `ExecutionJobService` | 无模型 TryBackfill；BuildContext 用 Resolve / GetSessionModelRef |
| `GatewaySessionResolver` / `GatewaySessionService` | 只注入 `IModelManager` |
| `Session.razor` / `SessionState` | 无 Provider 字段；无本地 Parse/Format；选择器绑 modelRef |
| `TaskTool`、TokenBudget | 读 `SelectedModel` 或 Manager |
| WebUI Models/Settings | 可继续用 `IProviderManager` + `IModelManager` 配置能力 |

---

## 5. 测试策略

- 单测集中在 `IModelManager`：Native 优先级、ACP 不回退 Default、Seed/Apply、无 Provider 字段。
- 删除所有 `SelectedModelProvider` 断言与测试夹具字段。
- 冒烟：新建会话 → UI 显示 `DefaultModel` → Submit 的 `RequestModelId` 与 session 一致。

---

## 6. 落地顺序

1. 定义 `IModelManager` + 实现 + 单测。  
2. `SessionData` 删除 `SelectedModelProvider`，全库编译驱动改调用点。  
3. 抽干 Resolver 模型方法、TryBackfill 模型分支、UI 分散逻辑。  
4. DI 切换；Obsolete/删除 `IModelConfigManager`（若已合并）。  

---

## 7. 成功标准

- 会话/执行路径无 `IProviderManager`、无 `ModelRef` 直接调用。  
- 新建 Native 会话的 `SelectedModel` 等于配置有效默认（Agent.Model 或 DefaultModel）。  
- ACP 不 seed、不 resolve Native DefaultModel。  
- 全库无 `SelectedModelProvider`。  
- 默认模型语义只存在于 `IModelManager` 一处实现。

## 8. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 已有会话文件含拆分字段 | 明确不做兼容；旧 Provider 字段反序列化忽略或丢弃，以 SelectedModel 为准；必要时用户重建会话 |
| `IModelConfigManager` 引用面广 | 同一实例双接口过渡，再删旧接口 |
| Llm 内部仍需拆 provider | 限制在 Llm 程序集内部，不泄漏到 App/UI |
