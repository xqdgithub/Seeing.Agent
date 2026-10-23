# 04 开发规范与反模式

## 1. 命名与分层后缀

沿用 `AGENTS.md`：`Store` / `Registry` / `Manager` / `Service` / `Provider` / `Executor` / `Channel` / `Loader` / `Sink` / `Aggregator` / `Factory` / `Authorizer` / `Mapper`。  
- `Aggregator`：UI 投影聚合（Scoped/circuit 维度，不持权威执行态，允许落盘 UI 缓存），如 `TaskCardAggregator`。  
- `Factory`：构造器端口（Provider 语义），如 `IPermissionAuthorizerFactory`/`LlmClientFactoryResolver`。  
- `Authorizer`：授权判定窄端口（请求-响应），如 `IPermissionAuthorizer`/`ExecutionContextPermissionAuthorizer`。  
- `Mapper`：纯函数映射，如 `PermissionKindMapper`。  
Hook 点用 `HookPoints.*`，禁止魔法字符串。  
私有字段 `_camelCase`；静态 `s_camelCase`。  
Core 程序集内命名空间须为 `Seeing.Agent.Core.*`（契约仍可在 Abstractions 的 `Seeing.Agent.Configuration` / `Seeing.Agent.Llm` 等）。

## 2. 磁盘放置

| 新项目类型 | 目录 |
|------------|------|
| 原语 | `src/primitives/` |
| 契约 | `src/abstractions/`（通常只扩展 Abstractions） |
| 脊柱 | `src/spine/` |
| Hosting / Shape | `src/hosting/` |
| 能力模块 | `src/capabilities/` |
| Gateway 协议/集成 | `src/gateway/` |
| 对应测试 | `tests/` 同名分类 |

**禁止**在 `src/` 根下新建平铺项目目录。程序集名保持 `Seeing.Agent.*`（或既有命名），只改路径。

## 3. DI 生命周期（脊柱）

| 类型 | 生命周期 |
|------|----------|
| ToolManager、HookManager、ModuleCatalog、SettlementEngine、ScenarioCatalog | Singleton |
| 模块工具具体类型 | Singleton 工厂；**经 Activate 才 Register 到 ToolManager** |
| `IModuleHostedService` | 与 `IHostedService` 同实例；可停可复活 |
| Middleware | Transient |

**工具装饰器链（实际语义，2026-09-25 校正）：** 注册顺序为「重试→超时→缓存→输出限长」，`ToolDecoratorRegistry.Apply` 按注册顺序依次包裹（**后注册者居外层**），最终包装为
`ToolOutputLimiter(Cached(Timeout(Retry(tool))))`：
- `RetryToolDecorator` 在最内层（3 次、1s 指数退避；可重试集合 `TimeoutException`/`HttpRequestException`/`TaskCanceledException`/`IOException`）；
- `ToolTimeoutDecorator` 包裹重试，**全局超时覆盖重试总时长**（能力声明 `timeout.skip`/`timeout.budget`，兜底 `SeeingAgentOptions.ToolExecutionTimeout`）；
- `ToolOutputLimiterDecorator` 在最外层处理最终返回结果；缓存默认关闭（仅 `cache.enabled=true` 的外部工具生效）。
注册点：`ServiceCollectionExtensions`（`AddSeeingCore` 内 `IToolDecoratorRegistry` 工厂）；`ToolManager.RegisterToolAsync` 时 `Apply()`。

## 4. 强制检查清单（每个 PR）

### 依赖

- [ ] 新能力包 **无** `ProjectReference` → `Seeing.Agent.Core`  
- [ ] Core **无** 新增能力包引用  
- [ ] Host Shape **无** Tools/Memory/Scheduler/Gateway 集成包引用  
- [ ] Hosting **无** ACP / Skills 引用（允许 Tools.Support）  
- [ ] Gateway 集成包 **无** Core / Hosting 引用  

### 模块与热重载对称

- [ ] 实现 `ISeeingModule`；稳定 `Id`；声明 `ProvidedTools` / `ProvidedSeams` / `DependsOn`  
- [ ] `ConfigureServices`：只登记 DI 与配置节；**不**无条件 Start 连接；**不** `AddSingleton<ITool>`  
- [ ] `ActivateAsync(sp, ct)`：`RegisterTool*` + UI Register + 开连接  
- [ ] `DeactivateAsync`：**对称** `UnregisterTool*` + UI Unregister + 关连接 / Stop Hosted  
- [ ] 长驻循环：`IModuleHostedService` + 启用门闩；可再 Activate 复活  
- [ ] 连接：Owner 空壳；Activate Open / Deactivate Close  
- [ ] 需要 UI：`IUiContribution`；路由用 `RouteContribution`（勿 `@page`）  

### 配置

- [ ] 新 Options 经 `IConfigSectionRegistry.Register`  
- [ ] 变更能进 `ConfigChanged` / ReloadHandler  
- [ ] 不引入第二套「偷偷读 json」旁路  
- [ ] 可清空字段支持 null 删除键  

### 执行与 schema

- [ ] 不在 Native executor 自算工具 schema 写入请求  
- [ ] 工具观察 `CancellationToken`；取消后不写父会话  
- [ ] schema/目录输出按工具 id 稳定排序（序数），保护 provider 前缀缓存  
- [ ] 分层暴露(exposure)只决定「进不进本轮 schema/目录」，**不得**当作权限；发现后的工具仍走完整审批链  
- [ ] 新增 prompt 分节靠贡献者自述 `Heading`/`Placement`，**禁止**改 `PromptBuilder` 的锚点表  
- [ ] 每轮 `tools[]` 与目录 token 计入 `SchemaSnapshotEvent`/`SourceBreakdown`，口径单源（resolver 一次估算）  

### 测试与扫描

- [ ] `dotnet build Seeing.Agent.slnx`  
- [ ] `dotnet vstest …/Seeing.Agent.Invariants.Tests.dll`  
- [ ] 粘贴 [README 防回归扫描](README.md) 无意外命中  

## 5. 反模式（看到即拒）

| 反模式 | 正确做法 |
|--------|----------|
| 在 Core 里 `new XxxModule().ConfigureServices` | 宿主 `AddSeeingModule` |
| 页面里 `if (GetService<MemoryService>()!=null)` | `IModuleCatalog.IsEnabled("memory")` |
| 为修 sample 在 Core 开特例开关 | 改 sample 组合或 Scenario |
| 复制结算/schema 逻辑 | 复用 SettlementEngine / JobService |
| 能力包引用 `UnifiedConfigManager` 具体类 | `IConfigSectionStore` / Abstractions 端口 |
| `ConfigureServices` 挂死 `ITool` | Activate/Deactivate 挂载 |
| Hosting.Gateway 直接 `AddSeeingGatewayServer` | sample 显式组合 |
| 混用 `\\` 与 `/` | 抽象文件系统 API |
| 静默吞掉模块注册失败 | 记录并失败可见 |
| 同 id 模块二次登记不 Replace | `AddSeeingModule(..., replace: true)` |
| 新项目平铺在 `src/Xxx` | 放入分类子目录 |
| Activate 开连接、Deactivate 不关 | 对称生命周期 |
| 用 appsettings 覆盖 SeeingAgent 真相 | 只写 `seeing.json` |
| 对 `SessionMessage`/`SessionToolCall` 集合属性原地 `Add/Remove/索引写` | **publish-on-write**：整体替换不可变集合引用（`x.ToolCalls = x.ToolCalls.Add(tc)`、`x.Metadata = x.Metadata.SetItem(k,v)`） |
| 宿主（WebUI/TUI/Gateway）直接写 `SessionData` 消息内容 | 只读投影；写入统一由 Hosting 权威路径（`ChatEventTracker`）完成 |
| 把 exposure/deferred（分层暴露、目录）当权限门 | exposure 只控制本轮 `tools[]`/目录可见性；调用许可恒走 `IPermissionAuthorizer` 决策链；目录正文不得含允许/拒绝裁决 |
| 为新增 prompt 分节改 `PromptBuilder` 硬编码锚点 | 贡献者自述 `Heading`+`Placement`；builder 不认识具体分节名 |
| 执行期用 `ISessionManager.LoadAsync` 读取当前会话 | 用 `Get`（内存活实例）或 `TryGetOrLoadAsync`/`GetOrLoadAsync`（内存优先）；裸 `LoadAsync` 会覆盖缓存 |

### 5.0 Code Mode（未来项）前置约束

本变更**不做** Code Mode（无 `Code` 策略值、无 `run_code`、无 Jint/ClearScript/Roslyn Scripting）。未来若要引入，隔离形态**必须先定**：受限解释器 vs 独立 runner 子进程。**进程内 Roslyn 脚本不可接受**——可 I/O / 反射 / 退出进程，等同任意代码执行。必须先有进程/沙箱边界再谈能力。


### 5.1 消息内容并发契约（publish-on-write）

`SessionMessage` / `SessionToolCall` 的集合成员（`ToolCalls`/`Parts`/`Metadata`/`TaskSteps`）为**不可变集合**（`ImmutableList`/`ImmutableDictionary`）。契约：

1. 结构变更**只能整体替换属性引用**（`= x.Add(...)` / `= x.SetItem(...)`），禁止原地修改。
2. 读取方（Dispatcher 渲染、LLM 出站构造、落盘、工具）直接持引用枚举，无需锁；后台写者发布新快照，读者持旧快照不受影响。
3. 元素标量字段（`SessionToolCall.Status/Result` 等）仍为原子引用/值写，允许；不构成集合破坏。

与 `SessionData.Messages` 的"锁内快照"契约互补：前者覆盖元素级集合，后者覆盖消息列表容器。详见 [`2026-10-08-session-message-immutable-concurrency-design.md`](../superpowers/specs/2026-10-08-session-message-immutable-concurrency-design.md)。

### 5.2 单一权威写入者契约

**执行期 `SessionData` 消息内容只有唯一写入者 `ChatEventTracker`（Hosting）。**

1. 执行路径：`ExecutionRunner`（由 `SessionRuntime` 消费循环驱动）**先投影（`ChatEventTracker.ApplyEvent`）再发布事件**，宿主收到事件时会话已写好。
2. 宿主（WebUI `EventStreamHandler`、TUI、Gateway）均为**只读投影**：订阅事件维护本地 UI 态、从 `SessionData` 派生视图；**禁止**直接写执行期消息内容（`SessionMessage`/`SessionToolCall` 的字段，含元素标量）。
3. 需要「投影写入」的语义（如 task 的 `TaskId/TaskAgent/TaskDescription/TaskBackground`、取消/加载的未完成标记）由 Hosting 权威路径完成：task 字段经 `TaskTool` 结果 `Metadata`（`TaskMetadataKeys`）→ `ChatEventTracker` 映射；未完成标记经 `IncompleteToolCallMarker` / `ISessionRuntime.ReconcileIncompleteTasksAsync`。
4. 宿主需写执行期消息内容时，一律上提为 `ISessionRuntime` 服务端方法。
5. **例外**：宿主在**非执行期**注入的会话级系统消息（如 Gateway 取消/错误横幅 `AddMessage`、会话重置）与 `SelectedModel/SelectedAcpMode` 用户显式选择不属执行期投影，仍由宿主负责。

详见 [`2026-10-09-single-writer-eventstream-design.md`](../superpowers/specs/2026-10-09-single-writer-eventstream-design.md)。

### 5.3 会话读取契约（内存优先）

- `Get(id)`：只读内存缓存，返回执行中的**活实例**；执行期读取当前会话的唯一安全入口。
- `TryGetOrLoadAsync(id, ct)`：内存优先；缺失时才从存储加载；都不存在返回 `null`（不创建、不抛）。
- `GetOrLoadAsync(id, ct)`：同上，但缺失时抛异常。
- **禁止用 `LoadAsync` 读取执行中的会话**：它会把磁盘副本写回 `_sessionDataCache`，替换执行循环持有的活实例；而 `SaveAsync` 从缓存取对象落盘，导致后续消息与工具终态丢失（典型表现：UI 工具卡片永久停在「等待」）。仅在确认目标不在内存、或确实需要显式重载时使用。

## 6. 常见错误 PR 对照

| 意图 | 错误改法 | 规范改法 |
|------|----------|----------|
| WebUI 要加 Memory 页 | 页面 `@page` + 直接注入服务 | 模块 `RouteContribution` + Activate Register |
| Gateway.Server 要起通道 | Hosting.Gateway 引用 Agent.Gateway | sample 组合两个 Add* |
| 工具默认始终可用 | `AddSingleton<ITool, T>` | Activate `RegisterTool` |
| 关模块立刻断在途 | 强杀且无选项 | 默认推迟；显式 `ForceCancelInFlight` |
| 能力包要用 Core 某个 Helper | ProjectReference Core | 上提接口到 Abstractions 或复制纯函数到 Support |
| 配置加字段 | 只加 Options 属性 | Register 节 + Reload 路径 + 测试 Save/Load |

## 7. 代码评审最低标准

1. 依赖方向（csproj ProjectReference）与磁盘分类  
2. 是否破坏 `InitializeSeeingAsync` 前模块登记顺序  
3. 是否破坏进程/会话场景语义与热重载对称性  
4. 是否引入配置双源或双算 schema  
5. Activate/Deactivate 是否成对（工具 / UI / 连接 / Hosted）

## 8. 测试命令

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```

注意：测试项目若启用 MTP，本环境常出现 “Zero tests ran”；优先对已构建 dll 使用 `dotnet vstest`。

## 9. 工具描述与参数 Schema 规范（R1–R9）

适用于所有实现 `ITool.Description` 的内置工具（MCP 工具由 server 定义，不适用）。设计来源：`docs/superpowers/specs/2026-10-23-builtin-tool-descriptions-and-prompt-routing-design.md`。

- **R1 语言**：简体中文；工具 id、参数名、命令、专有名词保留原文。
- **R2 首句**：`Description` 第一行 ≤ 60 字符、自足、不依赖后文；前 20 字符内出现「动作 + 对象」；包含该工具的主用途中文词（`tool_search` 的检索语料也取自描述）。
- **R3 正文**：可选，总长 ≤ 700 字符；用短行 + `-` 列表；**不使用 `##` 标题**。
- **R4 兄弟工具路由**：易混工具必须双向点名，用反引号包裹**精确 id**（`read` 而非 `Read`）。
- **R5 写入语义**：`write`/`edit`/`delete` 明说创建、覆盖、递归、不可恢复。
- **R6 路径语义**：统一写「绝对路径，或相对当前工作目录的路径」。
- **R7 前置条件**：需要审批、宿主面板、模块挂载的必须写明。
- **R8 动态描述**：运行时生成的描述同样遵守 R1–R7，且与 fallback 共享首句（`task`）。
- **R9 术语**：当前会话 / 本次执行 / 下一次执行 / 工作目录 / 子代理；不用「本轮」「下一轮」。

参数 Schema 同规则：每个属性（含 `items` 与嵌套对象）必须有非空 `description`；有默认值写进描述；离散取值用 `enum` 且描述列出取值；数值给 `minimum`/`maximum` 与单位；`required` 必须与实现一致。

约束由 `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests` 机械校验（52 个内置工具全覆盖）；提示词路由段由 `tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests/PromptRoutingTests.cs` 校验。
