# 全系统设计审查与整改 Release Notes

**日期:** 2026-11-07
**范围:** `src` 全域（约 13.4 万行 / 1147 个 .cs）+ `samples`
**审查方式:** 五域并行深审（Core / LLM / 会话与执行管线 / UI 宿主 / 能力包与网关），关键结论逐条回代码验证

## 一句话

骨架（模块结算、`SeeingJson` 单一序列化、schema 单点、`DeliverAsync` 唯一驱动、事件恰一次）立得住；失控的是**横切关注点各抄一遍**。本轮把三处「唯一真相」重新收敛（路径边界、终态组装、能力判定），并清掉一批死代码，同时修复六个「停用无效」的生命周期缺口。

## 一、重复实现的收敛（本轮核心）

### 1.1 路径边界：5 份 → 1 份

**新建** `src/abstractions/Seeing.Agent.Abstractions/Paths/PathBoundary.cs`（纯静态，无状态）。

**替换的 5 份**：

| 位置 | 原问题 |
|---|---|
| `Core/Permission/PathSafety.cs` | 含 realpath，逐字复制自下一行 |
| `Tools.FileSystem/FileSystemHelper.cs` | 与上者逐行同构 |
| `Skills/SkillWriter.cs` | 内联前缀比较，**无 realpath** |
| `Skills/Catalog/SkillCatalogCore.cs` | 内联前缀比较，**无 realpath** |
| `Acp/Filesystem/AcpFileSystemBridge.cs` | 纯字符串前缀，**无 realpath，且完全绕开 `IWorkspacePathGate`** |

**安全影响（本轮唯一的安全修复）**：后三处不做符号链接真实路径解析，`root/evil -> C:\Windows` 这类链接可以写出/读取工作区外文件，使 `Workspace.RestrictToWorkspace` 在 ACP 与技能路径上形同不存在。现统一 fail-closed。

**为什么放 Abstractions**：它是无状态纯函数，与既有 `ToolResultFormatting` / `ModelCapabilityFillEmpty` 同属「纯函数豁免清单」；所有调用方本就引用 Abstractions，零新增依赖边。

**测试**：`PathBoundaryTests`（10 例）锁定相等/子目录/父目录/`..` 逃逸/同前缀兄弟目录/空值/**符号链接越界**。无符号链接权限时跳过而非误报通过。

### 1.2 终态组装：2 份 → 1 份

**新建** `Hosting/Execution/ExecutionOutcomeFactory.cs`。

删除了 `SessionRuntime.BuildOutcome` + `ResolveSessionRelation`（约 90 行）与 `ExecutionRunner` 的同名两份（约 40 行）——它们逐字相同，但 `Duration` 口径已分叉（墙钟 vs 模型循环累计），同名字段在不同通道含义不同，消费方只能靠读注释判断。

**现在**：`duration` 是显式可选参数，两个调用方各自传入。工厂类注释写明了两种口径的语义与各自的用途。

### 1.3 模型能力判定：2 份 → 1 份

**新建** `Core/Llm/ModelCapabilityPredicate.cs`（纯静态）。

`ModelConfigManager`（设为默认模型时的写入校验）与 `LlmRouter`（每次路由第 3 层）原本各有一份「模型类型 → 能力」投影 + 语音模态消歧。两份若漂移，就会出现「设置时通过、运行时被 Router 拒绝」的静默错配——只在运行期暴露。

`LlmRouter` 保留分层错误文案（诊断价值），只把判定核心委托出去；删除了重复的 `AudioModality` 常量。

## 二、生命周期缺口（「停用无效」六处）

| 缺口 | 症状 | 修复 |
|---|---|---|
| `ReflectionModule.DeactivateAsync` 漏 `_ui?.Unregister` | 停用后侧栏「复盘记录」**永久消失** | 与其余 6 个模块对齐，补注销 |
| `FileWatcherPool` 停用即 `Dispose` 单例 | 停用再启用后文件变更**静默停更** | 新增 `StopAll()`（清空但保留可复用），`Dispose` 才置终态；`Subscribe` 加 `ObjectDisposedException` 显式失败 |
| `GatewayHostedService` 不查模块结算 | `secure` 档禁用 `gateway` 后**端口仍监听** | 加 `IModuleCatalog.IsEnabled("gateway")` 门控（`_modules` 为 null 时放行，兼容既有测试） |
| `ChannelDeliveryScheduleDispatcher` 同理 | `comms` 停用后**出站仍执行** | 加 `comms` 门控 |
| `SchedulerOptionsMonitorAdapter.OnChange` 恒返回 `null` | 改 `scheduler.json` 后运行参数**静默不生效** | `ISchedulerOptionsProvider` 增 `OnChange`；适配器真转发；`Reload` 锁外通知、监听器异常隔离 |
| `HookManager.AddHandler` 无去重 | 同一处理器重复接线导致 Hook 双执行（如预算双计） | 按 (钩子点, 处理器实例) 去重；多点处理器不受影响 |

## 三、执行期正确性

### 3.1 `LlmService` 兜底分支误标不可重试

流式路径的兜底 `catch` 硬编码 `IsRetryable = false`。传输层重试耗尽后原样抛出的 `HttpRequestException`（5xx / 429）会走到这里，被标成不可重试后 `DefaultLlmTurnRetryPolicy.CanRetry` 直接返回 false ——**「传输层耗尽后由应用层接管」的链路在此断掉**。

现改为 `LlmRetryPolicy.IsRetryable(capturedException, ...)` 重算。

> **撤回的一项改动（记录以免重犯）**：曾把 `ProviderConfig.MaxRetries` 默认值从 `0`（不限）改为 `3`，理由是「两层都无界会长时间卡死」。审查发现前提不成立——`RetryTotalBudgetMs` 默认 **120 秒**，旧语义的「无限重试」本就受预算约束；且 `LlmTurnRetryOptions` 类注释明确写着「**Q1 决策：默认无限重试**」。该改动会削减既有用户的重试能力，**已完整回滚**。

### 3.2 待办投影断裂（用户可见缺陷）

`EventStreamHandler` 把 `todowrite` 输出写进自己的私有 `CurrentTodoList`，而渲染读的是 `SessionState.CurrentTodoList`——后者除「清空会话」时赋 `null` 外**无人赋值**，面板恒空。

该面板（`TodoPanel.razor` + `session-todo-panel`，240px 侧栏）已按产品决策**整体移除**：内联 `TodoCard`（附着在 `todowrite` 工具调用消息上）已足够。

**已知边界**：ACP 透传的 plan 更新走 `TodoUpdateEvent`（非工具调用），该路径下待办当前无卡片可依附，仅存在于 Agent 侧。`HandleTodoUpdate` 保留为标注空实现，分派点亦有注释说明「有意不投影」；将来若需可见，正确做法是在时间线新增独立条目类型，**而不是**恢复侧边栏。

### 3.3 会话组归档（H1）

`SessionGroupManager.ArchiveGroupAsync` 两个真实缺陷：

| 缺陷 | 后果 | 修复 |
|---|---|---|
| 无组锁 | 「读成员快照 → 逐个归档」期间成员增删/恢复/新执行启动无协调 → **半归档**，`ListAnchorSummariesAsync` 与 `ListArchivedAsync` 给出矛盾视图 | `AcquireLockAsync(_groupLocks, ...)` 覆盖整个周期 |
| 部分失败静默返回成功 | 2 个成员只成功 1 个仍返回 `true`，调用方误认为组已归档 | 任一失败即抛 `InvalidOperationException`（含成功数/总数、失败成员 id、可重试提示），组关系保留供重试 |

**测试**：新增 `SessionGroupArchiveFailureTests`（部分失败抛异常且保组、全成功仍返回 true）。为此给 `SessionGroupTestHarness` 增加 `decorateSessionStore` 参数（与既有 `decorateGroupStore` 对称）。

**影响面**：`SessionDirectory.ArchiveAsync`（异常上抛给用户，正确）与 `SessionArchivalScanService`（已有 try/catch，安全）。

## 四、大文件拆分（不做职责重划，只做同程序集内切块）

四个文件从「上帝对象」降为「内聚核心 + 若干专职类」，**公开 API 全部未变**。

| 文件 | 原 → 现 | 抽出的类 |
|---|---|---|
| `Core/AgentExecutor.cs` | 1301 → 1025 | `ToolCallInvoker`（单工具执行全流程，四条终态出口）、`ToolVisibilityGate`（纯静态可见性裁决 + 定义回灌） |
| `Llm/ModelConfigManager.cs` | 1369 → 1329 | `ModelCapabilityPredicate`（见 1.3） |
| `Execution/SessionRuntime.cs` | 1449 → 1375 | `ExecutionOutcomeFactory`（见 1.2） |
| `Execution/ExecutionRunner.cs` | 1235 → 1190 | `SessionHistoryBuilder`（历史重建 + 三类过滤） |

`ExecutionRunner.BuildHistoryFromSession` 保留 9 行转发门面（仓内 8 处测试直接调用），注释标注「不要在此重新实现逻辑」。

### 拆分中触发守门测试并修正（值得记录）

首次抽 `ToolCallInvoker` 时把「结算可用性判定」也搬了进去，`ToolViewSingleSourceTests` 立即报错：

> `GetRegisteredTools()` 只服务管理视图；用它判定本轮可用工具会绕过结算端口

它同时锁定三件事：注册表枚举必须收敛到 `AgentExecutor` 的单一 helper、`ResolveCanonicalId` 必须出现在判定处、helper 签名要精确匹配。按测试意图把**结算门闸移回 `AgentExecutor`**，`ToolCallInvoker` 只留「权限 + 执行」，`ToolVisibilityGate` 退化为纯静态（接收已解析的 `isEnabled`/`isRegistered` 布尔值）。

## 五、Host Shape 收敛

| 项 | 原 | 现 |
|---|---|---|
| `HostShapeDescriptor` | 5 个包各定义一份同名同形状类型 | `Hosting/HostShapeDescriptor.cs` 一份；各 Shape 只留 id 常量 + `Descriptor` 实例 |
| 空权限通道 | `EventStreamPermissionChannel`(15行) ≡ `TuiPermissionChannel`(20行)，注释自认「语义完全一致」 | `Hosting/EventStreamPermissionChannel.cs` 一份 |
| 「最小可执行宿主」定义 | Tui/Headless/Embed 注册 `AddSessionHosting + AddExecutionEngine`；**Web 与 Gateway 缺失**，导致 `WebUiHost` 与 `Gateway.Server` 各自手抄两行 | `Hosting/HostShapeRegistration.AddHostShapeBaseline`：形态描述 + 结算选项 + 权限通道 + 会话运行时 + 执行引擎，一处定义 |
| `ProcessSettlementOptions` | 5 份 | 由基线统一构造；Embed 的 `defaultBoot: "minimal"` 经可选参数保留 |
| `AreRequirementsEnabled` | 3 份，且 `catalog is null` 兜底方向**相反** | `UiContributionVisibility` 一份，**fail-closed**（原先前两份返回 true 会让贡献「导航隐藏但 URL 可直达」） |

> **审查中撤回的一项**：我曾新增 `TokenBudgetModule : ISeeingModule` 把预算 Hook 收编进 Activate/Deactivate。评估后删除——5 个内置能力集全含 `tokenbudget`（永远 Activate，与宿主 `UseTokenBudgetHooks()` 永远重复），唯一新增行为是 `DeactivateAsync` 会 `Remove` 同一实例从而摘掉宿主注册的接线，且零测试覆盖。属净负值改动。

## 六、死代码清理

| 删除项 | 规模 | 理由 |
|---|---|---|
| `Core/AgentDiscovery.cs` + 测试 | 417 + 328 行 | 全仓仅 DI 注册、无业务调用；且它是 AGENT.md 的**第二套解析器**（`UnderscoredNamingConvention` + `mode` 默认 `All`，与 `AgentManager` 的 camelCase + `Primary` 冲突） |
| `IExecutionPipeline` + `ExecutionPipeline` + `PipelineBuilder` + `Middlewares/`（含 `RetryMiddleware` 第四份重试实现）+ 测试 | 约 480 行 | `AddExecutionPipeline()` 全仓零调用，整条中间件管道是死的 |
| `WebUI/Services/SessionMessageConverter.cs` | 191 行 | 全仓零引用 |
| Memory 4 个死接口（`IMemoryBenchmark` / `IMemoryEvaluator` / `IMemoryFilter` / `IMemoryIndexer`） | — | 各仅自身定义 |
| `WebUI/Components/TodoPanel.razor` + `todo.css` 侧栏样式 | 组件 + 约 175 行 CSS | 产品决策移除（见 3.2） |

**保留**（审查后确认不是死代码）：`McpClientManager` 的「旧版包装器」region（`IMcpClientWrapper`/`StdioMcpClientWrapper`/`HttpMcpClientWrapper` 是 MCP 传输层核心类型，被三个 Factory 与 Coordinator 使用——region 命名有误导性）、`BudgetEvents`（`BudgetStatusEvent`/`BudgetWarningEvent` 有 WebUI 与 TUI 完整处理器）、`PredefinedModels`（`PredefinedProviders` 的默认模型表来源）。

## 七、新增基础包

**`src/primitives/Seeing.Files`** —— 只依赖 BCL + `Logging.Abstractions`，零业务领域。

承载 `AtomicFileWrite`（进程级按路径锁 + GUID 临时名 + 移动重试 + 复制兜底），从 `Seeing.Session` 迁出。**为什么必须独立成包**：`Seeing.Session` 是依赖地板，它自己也要原子写；而 6 个消费者（Session / Comms / Memory / Mcp / Scheduler / ModelCatalog）分属不同层，没有更合适的共同落点。

**替换的 5 处退化实现**（固定 `.tmp` + 无跨进程锁，历史「配置莫名回退」的成因）：

| 位置 | 影响文件 |
|---|---|
| `Comms/Directories/JsonChannelDirectoryStore.cs` | `channel-directory.json` |
| `Memory/Core/Storage/LocalFileStore.cs` | 记忆笔记 |
| `Mcp/OAuth/McpOAuthStorage.cs` | OAuth 签名密钥 |
| `Mcp/OAuth/McpOAuthClientRegistrationStore.cs` | 动态客户端注册缓存 |
| `Llm.ModelCatalog.Builtin/BuiltinCapabilitySource.cs` | 模型目录本地覆盖 |

`Scheduler/Persistence/ScheduleAtomicFile.cs` 从 135 行逐字副本降为纯转发薄壳（保留是因为它的调用点用不同参数顺序）。

## 八、破坏性 / 行为变更清单

| 项 | 变更 | 影响 |
|---|---|---|
| `AreRequirementsEnabled(null catalog)` | `true` → **`false`** | 无 `IModuleCatalog` 的组合会隐藏带 `Requires` 的贡献与路由。宿主启动期解析 catalog 属必需依赖，正常路径不受影响 |
| `ISchedulerOptionsProvider` | 新增 `OnChange(Action<SchedulerOptions>)` | 外部实现需补该方法 |
| `SessionGroupManager.ArchiveGroupAsync` | 部分失败**抛异常**而非返回 `true` | 已有 callers：`SessionDirectory`（上抛给用户）、`SessionArchivalScanService`（有 try/catch） |
| `Hosting.Web` / `Hosting.Gateway` 的 `AddSeeingHosting*` | **新增** `AddSessionHosting` + `AddExecutionEngine` + `ProcessSettlementOptions` + `IPermissionChannel` | 与 Tui/Headless/Embed 对齐；sample 中手抄的两行已删除（重复注册 `ExecutionOptions` 是具体实例，后者覆盖前者） |
| `Hosting.Tui` 权限通道类型 | `TuiPermissionChannel` → `EventStreamPermissionChannel` | 行为相同（两者原本都是三方法 no-op） |
| `ExecutionRunner.BuildHistoryFromSession` | 转发到 `SessionHistoryBuilder` | 签名与行为不变 |
| `HookManager.AddHandler` | 同点重复登记返回不新增 | 依赖「重复注册以叠加执行」的用法会失效（仓内无此用法） |
| WebUI 待办侧栏 | **移除** | 见 3.2；内联卡片保留 |

## 九、`ExecutionOptions` 配置陷阱（审查中发现并修复）

拆分时我把 `WebUiHost` 的手抄 `AddExecutionEngine(options => ...)` 一度改成 `services.Configure<ExecutionOptions>(...)`。这是**错的**：

```csharp
// AddExecutionEngine 内部
var options = new ExecutionOptions();
configure?.Invoke(options);
services.AddSingleton(options);   // 具体实例，不是 IOptions<T>
```

`SessionRuntime` 直接注入 `ExecutionOptions`（构造参数），仓内**没有任何** `IOptions<ExecutionOptions>` 消费者。因此 `Configure<T>()` 注册的 `IOptions` 成了孤儿，WebUI 的 5 项执行参数（并发上限 / 缓冲 / 历史 / 空闲超时 / 清理间隔）会**静默回落到默认值**。

已改回经 `AddSeeingHostingWeb(options => ...)` 的 `configureExecution` 传入，并在原处留注释说明「为何不能事后 Configure」。这条已写入代码注释，防止后续重构重犯。

## 十、测试

- **总计 5408 通过 / 0 失败**
- 本轮新增 12 个：`PathBoundaryTests`（10）、`SessionGroupArchiveFailureTests`（2）
- 删除 5 个测试文件（对应删除的死代码）
- 2 处既有测试因语义变更更新：`TuiChatEngineWiringTests`（权限通道类型）、`EventStreamHandler` 相关的待办断言

> `Seeing.Agent.Tui.Tests` 偶有 1 例失败（`RawInputReaderMouseTests.Drain_X10MouseSplitAfterLoneEsc`），三次重跑均全绿，属既有的终端输入时序抖动，与本轮改动无关。

## 十一、未处理（供后续立项）

| 项 | 说明 |
|---|---|
| `ModelConfigManager` 的刷新层/持久化层拆分 | 需先引入 `IModelCatalogStore` 端口把缓存抽象出去：`_cacheLock` 同时保护缓存与版本号，三处版本字段的单调性判定记录了真实事故（见 `ModelCatalogAggregationTests`）。跨对象协调会把正确性核心变成协议 |
| `GatewayClientSupervisor` 未同步加模块门控 | 服务端已门控、客户端没有（`Boot=secure` 时服务端不监听而 UI 仍查询状态）。但它是**被动查询**非主动重连，无连接风暴风险 |
| `ChatEventTracker` 之外的元数据散点写入 | `SessionData.Context/Metadata/State` 仍是裸字典 + `Clone` 浅拷贝。本轮只在 Todo 适配器边界加拷贝止损，未做全面并发容器改造（属破坏性变更，需单独设计） |
| `TuiChatEngine.cs`（2269）/ `SessionWindow.razor`（2003） | sample 层大文件，未拆 |
| `ContextAssembler.cs`（909） | **建议不拆**：长度来自上下文源管道的完整状态机，拆开会把「一次装配」的因果链散落到多文件 |
