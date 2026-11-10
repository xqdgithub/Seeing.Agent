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
- [ ] 需要 UI：实现 `IUiContributor`（`DescribeUi()` 纯数据）；路由用 `PageContribution`（勿 `@page`）  

### 配置

- [ ] 新 Options 经 `IConfigSectionRegistry.Register`  
- [ ] 变更能进 `ConfigChanged` / ReloadHandler  
- [ ] 不引入第二套「偷偷读 json」旁路  
- [ ] 可清空字段支持 null 删除键  

### JSON 序列化

- [ ] **禁止裸写 `new JsonSerializerOptions`**：一律走 `Seeing.Serialization.SeeingJson`
      （`Readable` / `ReadableIndented` / `Create(...)`）；
      需要自定义编码器（如 HMAC/内容哈希）才用 `Signature`，并在注释写明理由  
- [ ] 落盘与展示用 `ReadableIndented`，协议/模型可见载荷用 `Readable`  
- [ ] 参与签名或内容哈希的序列化**必须**用 `Signature`（默认编码器，字节稳定）；
      改动它会让既有签名与哈希全部失效  
- [ ] 由 `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/JsonOptionsInvariantTests.cs` 守门

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
| 在多个位置各自渲染同一上下文清单（提示词基线、`/skill` 列表、工具错误提示各拼一份） | 只走源的 `RenderInitial`/`Render` 与 `SkillCatalogFormatter` 等**唯一格式化入口**；同一值重复渲染必须逐字节相等（前缀缓存前提） |
| 用路径前缀 / 字符串嗅探判定来源（如 `embedded://`、`StartsWith("skills")`） | 用强类型事实：`SkillCatalogEntry.Source` / `ProviderId`、`ContextSourceKey`；嗅探会在路径布局变化时静默错判 |
| 绕过 `ISkillCatalog` 直接修改技能状态（写 `skill-state.json`、自己维护字典/列表缓存、`Directory.CreateDirectory` 建技能目录） | 写走 `ISkillWriter`（唯一写入口，写完 `RefreshAsync`），读走 `ISkillCatalog.Current`/`Subscribe`；`ISkillManager` 写面为 `[Obsolete(error: true)]`，类型系统必须拦住第二条写路径 |
| 把 `tests/**` 测试项目禁用 CPM 上报为违规 | 这是既有约定（`Seeing.Agent.WebUI.Files.Tests` 等）：测试项目写 `ManagePackageVersionsCentrally=false` + 显式版本；`src/`、`samples/` 宿主与库项目仍必须走 CPM |

### 5.0 Code Mode（未来项）前置约束

本变更**不做** Code Mode（无 `Code` 策略值、无 `run_code`、无 Jint/ClearScript/Roslyn Scripting）。未来若要引入，隔离形态**必须先定**：受限解释器 vs 独立 runner 子进程。**进程内 Roslyn 脚本不可接受**——可 I/O / 反射 / 退出进程，等同任意代码执行。必须先有进程/沙箱边界再谈能力。


### 5.1 消息内容并发契约（publish-on-write）

`SessionMessage` / `SessionToolCall` 的集合成员（`ToolCalls`/`Parts`/`Metadata`/`TaskSteps`）为**不可变集合**（`ImmutableList`/`ImmutableDictionary`）。契约：

1. 结构变更**只能整体替换属性引用**（`= x.Add(...)` / `= x.SetItem(...)`），禁止原地修改。
2. 读取方（Dispatcher 渲染、LLM 出站构造、落盘、工具）直接持引用枚举，无需锁；后台写者发布新快照，读者持旧快照不受影响。
3. 元素标量字段（`SessionToolCall.Status/Result` 等）仍为原子引用/值写，允许；不构成集合破坏。

与 `SessionData.Messages` 的「写时失效快照」契约互补：前者覆盖元素级集合，后者覆盖消息列表容器。

**消息列表容器契约（2026-11-04 更新）：**

1. `SessionData.Messages` 返回锁内**缓存快照**：同一版本内多次读取返回**同一实例**（避免热路径反复 `ToList`）；任何容器写入（`AddMessage`/`InsertMessage`/`RemoveMessage`/`RemoveMessages`/`ReplaceMessages`/`ClearMessages` 等）都会使其失效。
2. 该返回值是**只读视图**：调用方不得 Cast 为 `List<SessionMessage>` 或就地修改 —— 修改不会经过失效逻辑，会污染会话真相。
3. `SessionData.GetActiveMessages()` 仍返回**独立副本**（对外承诺可变 `List`），调用方可安全持有与修改。
4. `SessionData.TryGetMessage(id, out msg)` 按 Id 查（锁内惰性建索引，容器写入后失效）。
   **契约**：消息 Id 由写者在入列前确定（`ChatEventTracker` 赋 `{loopId}_step{n}`；`SessionMessage` 工厂方法赋 `NewId()`），索引建立后**不得再改写**元素 Id；缺 Id 的消息不入索引，也进不了 UI 时间线。宿主层**禁止**为补齐 Id 而回写 `SessionMessage`（会同时破坏索引与 `@key` 稳定性）。

详见 [`2026-10-08-session-message-immutable-concurrency-design.md`](../superpowers/specs/2026-10-08-session-message-immutable-concurrency-design.md) 与 [`27-webui-render-performance-release-notes.md`](27-webui-render-performance-release-notes.md)。

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
| WebUI 要加 Memory 页 | 页面 `@page` + 直接注入服务 | 模块实现 `IUiContributor` 声明 `PageContribution`（`ViewKey` 在宿主视图表登记） |
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

## 10. WebUI 界面文案规范（U1–U9）

适用于 `samples/Seeing.Agent.WebUI` 及其组件库 `Seeing.Agent.WebUI.Files` 中一切面向用户的文字（标签、按钮、占位符、帮助文字、提示消息、空状态、弹窗标题、`PageTitle`、`Tooltip`、`aria-label`）。

- **U1 语言**：一律简体中文。仅允许保留五类英文：① 产品/协议专名（MCP、Gateway、ACP、WebSocket、JSON、SQLite、Quartz）；② 通用技术词（Token、Embedding、Prompt、Provider、API Key、Base URL）；③ 运行时与状态分类名（Native、ACP 等用于区分实现/运行方式的值，它们是稳定的技术标识而非可意译的概念）；④ 用户自己输入的数据（模型 id、路径、渠道标识、`provider/model` 形式的引用）；⑤ 状态枚举的**内部取值**——但必须经映射后展示，不得直出。
- **U2 术语唯一**：Agent→**智能体**、Session→**会话**、Scenario→**工作模式**（禁止再写「场景」）、conversation→**对话**、Tool→**工具**、Skill→**技能**。同一概念全站只有一个说法，页面不得自行发明译名。
- **U3 单一出口**：术语、状态标签、单位格式统一走 `samples/Seeing.Agent.WebUI/Services/UiText.cs`。页面与组件禁止就地拼状态文案；新增状态先加到 `UiText`。
- **U4 不直出枚举**：`JobState`、`McpConnectionState`、`ToolCategory`、`ModelType`、`MemoryType`、`PermissionKind`、`PermissionEffect`、`ReflectionTriggerKind`、`ScheduleSources`/`ScheduleTaskTypes` 等一律经 `UiText.*` 映射后再展示。`Enum.ToString()` 只允许出现在调试输出。
- **U5 无内部语言**：界面不得出现配置键名（`DefaultModels`、`Boot`、`Modules.Enabled`、`Tools.Disabled`、`Seams`）、进程内部动词（Activate、结算、bootEnabled、Host Shape、Passthrough）、设计决策编号（「决议 N」）、或指导用户去手改 JSON 的说明。这类内容只留在文档与代码注释里；界面用用户能懂的行为描述替代。
- **U6 无口语与文学化**：不使用「没事怎么回复」「一开跑就有成本」「留痕」等口语，也不用诗句、比喻、感叹号。用祈使句 + 事实陈述。
- **U7 标点统一**：中文语境用全角标点（`，。：；（）「」`）；省略号统一 `……`（禁止 `...` 与 `…` 混用）；页面标题分隔用 `·`。
- **U8 单位与数字**：耗时用「2 小时 5 分」「12 分 3 秒」「750 毫秒」；文件大小用「512 字节」「2 KB」；不要出现 `12s`、`3m7s`、`250ms`。
- **U9 空状态可行动**：空状态必须同时说明「为什么空」与「下一步做什么」，并提供对应操作按钮；不得只写「暂无数据」。

守门测试：`tests/apps/Seeing.Agent.WebUI.Tests/Services/UiTextTests.cs` 断言术语唯一、状态映射完整、单位格式不含拉丁后缀。

审查命令（新增/修改页面时人工过一遍）：

```bash
# 1) 仍在界面上直出的英文（排除注释与代码行）
rg -n --no-heading -g '*.razor' \
   '(Label|Title|Tab|Placeholder|Message|EmptyTitle|EmptyDescription|OkText|CancelText|Description)="[^"@]*[A-Za-z]{3,}' \
   samples/Seeing.Agent.WebUI

# 2) 不应出现的内部语言
rg -n -g '*.razor' '决议|Activate|bootEnabled|Host Shape|Tools\.Disabled|Modules\.Enabled|Seams|DefaultModels' \
   samples/Seeing.Agent.WebUI

# 3) 非法省略号与半角冒号
rg -n -g '*.razor' '(\.\.\.|…)|>：[^/]' samples/Seeing.Agent.WebUI
```

## 11. UI 扩展规范（E1–E7）

适用于 `samples/Seeing.Agent.WebUI` 的宿主侧与所有想挂 UI 的能力模块。机制说明见 [`40`](40-ui-extension-unified-architecture-release-notes.md)。

### E1 只经信封声明，不自注册

模块实现 `IUiContributor`，`DescribeUi()` **纯数据、无副作用**（不读磁盘、不解析 DI、不构造组件）：

```csharp
public sealed class XxxModule : ISeeingModule, IUiContributor
{
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.xxx",
            Route = "/xxx",
            Title = "某某",
            ViewKey = "page.xxx",
            Requires = [Id],          // 必须写归属
            Order = 70,
            Nav = new NavPlacement { Icon = "star", Group = NavGroups.Workspace,
                                     GroupIcon = NavGroups.WorkspaceIcon, GroupOrder = 20 },
        },
    ];
}
```

**禁止**：在 `ActivateAsync` 里 `registry.Register(this)`；在 `DeactivateAsync` 里 `Unregister`。声明由 `UiContributionCollector` 在结算后统一扫描 `IUiContributor` 收集一次，模块启停只影响**可见性**，不影响声明是否在表内。

### E2 `ViewKey` 命名约定

| 载荷 | 约定 | 例 |
|------|------|-----|
| `PageContribution` | 与 `Id` 同名 | `Id = "page.xxx"` → `ViewKey = "page.xxx"` |
| `SettingsCardContribution` | 与 `Id` 同名 | `Id = "settings.xxx"` → `ViewKey = "settings.xxx"` |
| `SlotContribution` | 若同一点只有一条内置贡献，抽进 `WebUiViewKeys` 常量 | `WebUiViewKeys.SessionFilePanel` |

多条贡献复用同一视图（如 `/xxx` 与 `/xxx/{Id}`）时，`ViewKey` 指向前者，`Id` 各自唯一。

**新增页面必须同时**在 `samples/Seeing.Agent.WebUI/Shell/WebUiViews.cs` 登记 `ViewKey → 组件类型`；漏登记会被 `WebUiUiContributionValidator` 在收集期拒收（进 `Diagnostics`），表现为页面静默消失。

### E3 `Requires` 语义

| 写法 | 语义 | 允许方 |
|------|------|--------|
| `[Id]` / `["模块id"]` | 归属该模块：模块未启用 → 不可见 | 模块 |
| `[]` | 进程级：不受任何模块启停影响 | **仅宿主**（`OwnerId` 以 `host.` 开头的贡献） |

- 模块贡献写 `[]` 会被 `UiContributionRegistry` 拒收（`InvalidDeclaration`）——否则模块停用后页面仍在而工具已卸载。
- 多项 `Requires` 是 **AND**：全部启用才可见；会话作用域下还要求全部落进工作模式门集。
- `Requires` 不是依赖声明。不要用它表达「需要 `llm.openai` 才能渲染」，那是 `DependsOn` 的事。

### E4 `host.*` 命名空间

- 宿主贡献者实现 `IUiHostContributor`，`OwnerId` 必须以 `host.` 开头（当前唯一实现：`host.webui`）。不以 `host.` 开头会被 `Collector` 拒绝并 `LogError`。
- 模块 id **不得**是 `host.xxx` 形状：注册表按 `ownerId` 整批替换，模块若占 `host.memory` 会静默顶掉宿主贡献（`CrossOwnerDuplicate` 拒收）。
- 宿主可以**代模块声明**（如设置卡、文件面板插槽），此时 `OwnerId` 仍用 `host.webui`，归属写在 `Requires` 里。

### E5 声明期校验

以下情况都会被拒收并记入 `Diagnostics`，不会进 UI：未声明的扩展点（`UnsupportedPoint`）、载荷种类不符（`PayloadMismatch`）、同批 id 重复（`DuplicateId`）、同批路由重复（`DuplicateRoute`）、超 `MaxItems`（`ExceededMaxItems`）、宿主校验器拒绝（`ValidatorRejected`，如视图未登记）、元素为 `null`（`InvalidDeclaration`）、跨 owner 同 `(point, id)`（`CrossOwnerDuplicate`）。

**`WebUiHost` 启动时会打一次 `LogError` 列出全部诊断**——先看这条日志，不要靠点页面猜。

### E6 消费点必须传可见性

`UiRouteTemplate.Resolve` 的第三个参数 `isVisible` 在生产调用方**必须传**：

```csharp
UiRouteTemplate.Resolve(path, Registry.All,
    e => e.Contribution is PageContribution p && UiVisibility.IsVisible(p, point, context));
```

不传会让停用模块的高分页抢赢启用模块的页（迁移前停用模块的路由根本不在表内，不可能参与竞争）。`isVisible = null` 只允许用于「取已知页面标题」这类诊断路径。

### E7 守门测试要求

新增或改动 UI 扩展时，PR 至少要覆盖：

| 测试 | 位置 |
|------|------|
| 声明形状与归属（`Requires == [自身 id]`、`Nav` 是否应为 null） | 各能力包的 `*ModuleLifecycleTests` |
| 路由集合完整（新增/删除路由时同步更新期望表） | `WebUI.Tests/Shell/PageChannelMigrationTests` |
| 视图表与声明无漂移（每条 `ViewKey` 可解析） | 同上 `EveryPageViewKey_ShouldBeResolvable` |
| 卡片 / 插槽的可见性与 key 唯一性 | `WebUI.Tests/Shell/SettingsAndSlotMigrationTests` |
| `Core/Ui/` 未引入 ASP.NET Core 类型 | `Invariants.Tests/Ui/CoreUiLayerInvariantTests` |
| 旧类型名不再出现 | `SettingsAndSlotMigrationTests.SourceMustNotReferenceLegacyContributionTypes` |

**禁止**用固定时长等待验证 UI 装配（见根 `AGENTS.md`「测试禁止等待型断言」）：注册表是同步收集的，测试直接构造 `UiContributionRegistry` + 显式 `AddRange` 即可断言，不需要起宿主。
