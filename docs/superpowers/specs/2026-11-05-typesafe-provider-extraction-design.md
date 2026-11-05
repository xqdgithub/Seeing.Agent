# TypeSafe Provider 下沉为插件 —— 设计

**日期:** 2026-11-05
**状态:** 待批准

## 1. 背景与问题

模型设置页的 Provider 列表里出现了一个叫 `SystemOne` 的条目，点开显示「来源：配置 / 加载自用户级配置文件」，
并给出 Name / Base Url / Api Key / Capabilities / List Models / Timeout / Use Proxy 的可编辑表单。

这个展示是错的，而且底层结构也是错的：

1. **能力包在造 Provider。** `Seeing.Agent.Llm.SystemOne`（模块 `llm.systemone`）做了两件事：
   - 登记 `typesafe` 协议的客户端工厂 —— 这与 `Seeing.Agent.Llm.OpenAI` 登记 `openai`/`openai-responses`
     完全对等，是「底层协议提供者」该干的活；
   - 在 `ActivateAsync` 里往 `IProviderRegistry` 注册一个 `ProviderOrigin.Builtin` 的
     `SystemOneLlmProvider`，自带 `~/.seeing/providers/systemone.json` 与 `jev-latest` 种子模型 ——
     这是「某个具体端点」的活，本该由插件承担。
2. **UI 被来源枚举骗了。** `ModelsPage.razor` 只对 `ProviderOrigin.Extension` 显示「扩展」标签，
   其余一律走「配置」分支。`Builtin` 没有分支，于是被当成用户手写的配置型 Provider 渲染，
   给了它一个能「保存配置」的表单（保存写的是它自己的私有 JSON，不是 `Providers` 节——用户完全看不出来）。
3. **概念混叠。** 包的命名空间、类名（`SystemOneLlmProvider` / `SystemOneConfigStore`）、
   配置文件名（`systemone.json`）都指向一个已经消失的「SystemOne 平行栈」；
   而实际的 wire 协议叫 TypeSafe，客户端类名却叫 `TypeSafeSystemOneClient`。

## 2. 目标 / 非目标

### 目标

- 能力包 `llm.typesafe` 只负责**协议**：登记 `typesafe` 协议工厂，不再注册任何 Provider。
- Provider 下沉到插件 `Seeing.Provider.TypeSafeApi`，来源为 `ProviderOrigin.Extension`，
  在设置页标「扩展」、走 schema 驱动表单、配置写 `~/.seeing/providers/typesafe-api.json`。
- 包与模块改名，让名称与「TypeSafe 协议」这一事实一致。
- 「配置型 Provider 选 `typesafe` 协议」这条路继续可用（本来就由工厂提供，不依赖内置 Provider）。

### 非目标

- 不改工具 id（`systemone_ask` / `systemone_noul` / `systemone_choice` / `systemone_score` 与内嵌 Skill 名）。
- 不改 `SystemOne` 工具包的包名与模块 id（`Seeing.Agent.Tools.SystemOne` / `llm.systemone.tools`）。
- 不改 Provider 来源优先级规则（扩展优先于用户配置，见 §6）。
- 不改 `IDecisionClient` / `IModelCatalogClient` / `ILlmRouter` 等契约。
- 不为「内置 Provider」新增 UI 分支（下沉后列表里不再有 `Builtin` 来源的 Provider）。

### 反目标

- 让 `Builtin` 来源在 UI 里变成第三种可编辑形态（那等于把两个错误叠起来：既有不该存在的 Provider，
  又多一条要维护的渲染分支）。
- 为了「向后兼容」在能力包里留一个空的 Provider 壳。

## 3. 约束

- **分层**：能力包只依赖 Abstractions + 原语（+ support 层）；插件同样只能依赖 Abstractions + 原语 + support。
  插件不得引用 `Seeing.Agent.Llm.TypeSafe`（协议包），否则插件会把协议实现绑死在宿主上，
  与另外四个 `plugs/providers` 插件的行为不一致。
- **协议→工厂唯一映射**：`typesafe` 只能绑一个工厂，重复登记在 `LlmClientFactoryResolver` 构造期即抛。
- **schema 单点**：Provider 表单的字段来自 `IConfigurableLlmProvider.GetConfigSchema()`，
  由 `OptionsSchemaBuilder.FromType` 反射生成；插件只需给 `Options` 类加注解。
- **组合根单点**：模块注册只在 `samples/Seeing.Agent.HostComposition/FullCapabilityComposition.cs`，
  受 `FullCapabilityCompositionTests` 守门（必须恰好等于 `BuiltInCapabilitySets.Full`）。

## 4. 方案

### 考虑过的三条路

**A. 只删 Provider，能力包保持原样（无名改名）**
最小改动：删 `SystemOneLlmProvider` / `SystemOneConfigStore`，用户要 decision 能力就得手写
`Providers.typesafe`。缺点：`systemone_*` 工具在任何默认宿主下都不可用（无模型可路由），
与本仓库「Full 档开箱可用」的定位冲突；且包的名称仍然名不副实。

**B. Provider 下沉为插件 + 能力包改名（选定）**
能力包退化成「协议工厂包」，与 `llm.openai` / `llm.anthropic` 三个包完全同构；
Provider 变成 `plugs/providers` 下的第五个插件，走既有的插件契约（`ConfigurableProviderBase`）。
默认仍可用（进 Full），UI 上正确标「扩展」，同时用户仍可自建配置型 `typesafe` Provider。
代价：模型引用前缀随之变化（`systemone/` → `typesafe-api/`），属破坏性变更，需写迁移说明。

**C. 把 Provider 挪到 `Seeing.Agent.Llm.ModelCatalog.Builtin` 之类的另一个能力包**
没有解决问题：来源仍是 `Builtin`，UI 依旧误判为「配置」，且把「端点」问题塞进「目录」包，职责更乱。

选 B。

### 选定的结构

```
src/capabilities/Seeing.Agent.Llm.TypeSafe/          ← 原 Seeing.Agent.Llm.SystemOne（改名）
  TypeSafeModule.cs                                   ← 原 SystemOneModule（只剩工厂登记）
  Clients/TypeSafeClient.cs                           ← 原 TypeSafeSystemOneClient
  Clients/TypeSafeClientFactory.cs                    ← 原 TypeSafeSystemOneClientFactory
  Clients/TypeSafeHttpHelper.cs                       ← 原 SystemOneHttpHelper
  Clients/TypeSafeJsonModels.cs                       ← 原名不变（已含 TypeSafeProtocol / TypeSafeWireMap）
  Extensions/TypeSafeServiceExtensions.cs             ← AddTypeSafe(registry)

plugs/providers/Seeing.Provider.TypeSafeApi/          ← 新插件
  TypeSafeApiLlmModule.cs                             ← 模块 id=provider.typesafe-api，DependsOn llm.typesafe
  TypeSafeApiProvider.cs                              ← ConfigurableProviderBase<TypeSafeApiOptions>
  TypeSafeApiOptions.cs                               ← name/baseURL/apiKey/capabilities/listModels/timeout/useProxy/models
  TypeSafeApiConfigStore.cs                           ← ~/.seeing/providers/typesafe-api.json（含旧文件名回退读）
```

### 命名与 id 映射

| 项 | 旧 | 新 |
|---|---|---|
| 能力包程序集 | `Seeing.Agent.Llm.SystemOne` | `Seeing.Agent.Llm.TypeSafe` |
| 能力包目录 | `src/capabilities/Seeing.Agent.Llm.SystemOne` | `src/capabilities/Seeing.Agent.Llm.TypeSafe` |
| 能力包模块 id | `llm.systemone` | `llm.typesafe` |
| 模块类 | `SystemOneModule` | `TypeSafeModule` |
| 协议工厂类 | `TypeSafeSystemOneClientFactory` | `TypeSafeClientFactory` |
| 客户端类 | `TypeSafeSystemOneClient` | `TypeSafeClient` |
| HTTP 辅助 | `SystemOneHttpHelper` | `TypeSafeHttpHelper` |
| 插件程序集 | —（新增） | `Seeing.Provider.TypeSafeApi` |
| 插件扩展 id | — | `seeing.provider.typesafe-api` |
| 插件模块 id | — | `provider.typesafe-api` |
| **Provider id** | `systemone` | `typesafe-api` |
| 模型引用前缀 | `systemone/jev-latest` | `typesafe-api/jev-latest` |
| 插件配置文件 | `~/.seeing/providers/systemone.json` | `~/.seeing/providers/typesafe-api.json` |
| 工具包（不动） | `Seeing.Agent.Tools.SystemOne` / `llm.systemone.tools` | 同名 |
| 工具 id（不动） | `systemone_*` | 同名 |

**为什么 Provider id 用 `typesafe-api` 而不是 `typesafe`**：id 同时是模型引用前缀，
而「用户自己建一个 id=`typesafe` 的配置型 Provider」是最自然的写法。一旦扩展占了 `typesafe`，
按现有「扩展优先」规则用户的配置会被 `ProviderManager` 静默跳过（只留一条 Warning 日志），
排查成本很高。`typesafe-api` 既与插件同名，也让 `typesafe` 这个自然 id 留给了用户。

## 5. 详细设计

### 5.1 能力包瘦身（`Seeing.Agent.Llm.TypeSafe`）

- 删除 `SystemOneLlmProvider`、`SystemOneConfigStore`、`SystemOneOptions`、`SystemOneProviderActivationTests`。
- `TypeSafeModule`：`ConfigureServices` 只做三件事（与 `OpenAiLlmModule` 一致）——
  `TryAddSingleton<TypeSafeClientFactory>`、`ILlmClientFactory` 指向同一实例、
  登记 `LlmClientFactoryRegistration(ProviderTypes.TypeSafe, factory)`。
  `ActivateAsync` / `DeactivateAsync` 变为空操作，`DependsOn` 为空。
- **不再反射 `IWorkspaceProvider`**，也不再写任何用户目录文件；包内的
  `IWorkspaceProvider` / `IModelCapabilityManager` 依赖随之移除。
- 种子模型 `jev-latest` 迁到插件的 `TypeSafeApiOptions.Models` 默认值（理由不变：
  远端目录需要凭据，无种子则无凭据时目录为空、Router 第一层必失败）。
- `TypeSafeProtocol.DefaultBaseUrl`（`https://api.typesafe.ai`）与 `DefaultTimeoutMs`（10s）
  保留在包内（协议自身的事实），插件通过 `BaseUrl` 缺省与 `Timeout` 缺省使用它们。

### 5.2 新插件 `Seeing.Provider.TypeSafeApi`

延续既有四个插件的写法（见 `Seeing.Provider.DeepSeek`）：

- `TypeSafeApiProvider : ConfigurableProviderBase<TypeSafeApiOptions>`
  - `Id => "typesafe-api"`（固定，不随配置变——注册表按 id 索引）
  - `Name => Options.Name ?? "TypeSafe"`（`TypeSafeApiOptions.Name` 默认 `"TypeSafe"`）
  - `Protocol => ProviderTypes.TypeSafe`
  - `DefaultCapabilities => LlmCapabilityKind.Decision`
  - `BuildProviderConfig()`：`BaseUrl` 为空时留空（由协议缺省兜底），`Timeout` 透传
  - `CreateClient` → `CreateClientViaFactory(config, capability)`（装饰器由工厂施加）
  - `OnOptionsSavedAsync` → 重新 `Register` 一次，让 `ProviderInfo.Capabilities` 跟上新配置
  - `GetModelsAsync`：静态 `Models` ∪（`ListModels` 时的远端目录），静态优先；
    远端失败只记 Warning 并保留静态表（照抄现 `SystemOneLlmProvider` 的实现）
  - **不订阅** `IProviderRegistry.ProvidersChanged`：下沉后「用户同 id 配置覆盖扩展」不再成立
    （扩展优先），原先那套「用户配置存在则让位 / 撤销后自动回位」的逻辑随之内联失效，一并删除。
- `TypeSafeApiLlmModule : ISeeingModule`
  - `Id => "provider.typesafe-api"`，`DependsOn => ["llm.typesafe"]`，工具空、seam 空
  - `ConfigureServices`：`TryAddSingleton` 三个（ConfigStore / Provider）
  - `ActivateAsync`：`WarmupAsync` 后注册，来源 `Extension`、`OwnerId = ExtensionId`，
    **`DisposeByRegistry: false`**
  - `DeactivateAsync`：`Unregister(provider.Id)`（照抄 DeepSeek 的对称写法）

  **`DisposeByRegistry: false` 是必须的，不能照抄 DeepSeek 的默认值。**
  `TypeSafeApiProvider` 来自 DI 单例，注册表若在注销时释放它，任何一次
  `Deactivate`（结算热重载 / Boot 切换 / 禁用再启用）之后再 `Activate`，
  `WarmupAsync` 之后取客户端就会撞 `ObjectDisposedException`，且容器会二次 Dispose。
  旧 `SystemOneModule` 正是为此显式写了 `false`。
  现有四个 provider 插件都用了默认 `true`，属同类隐患；**本次不改它们**（超出本变更范围），
  在 §9 记为已知债。
- `TypeSafeApiOptions`（JSON 键沿用旧文件的 camelCase，便于回退读）
  - `name`（默认 `"TypeSafe"`）、`baseURL`、`apiKey`、`capabilities`、`listModels`（默认 true）、
    `timeout`（默认 0 = 用协议缺省 10s）、`useProxy`（默认 false）、`models`（默认含 `jev-latest` 种子）
  - **`apiKey` 不加 `[Required]`。** 理由：本插件的设计前提是「无凭据也能用种子模型」，
    加必填会让 schema 表单直接拦住保存，用户连「先不填 key 看看」都做不到，
    也无法把已保存的 key 清空回到无凭据状态。这与 DeepSeek 等插件（端点必须有 key 才有意义）不同。
- `TypeSafeApiConfigStore`
  - 路径 `~/.seeing/providers/typesafe-api.json`
  - **一次性回退读**：新文件不存在而 `providers/systemone.json` 存在时，读旧文件（只读不写），
    下次保存自然落到新文件名。理由：用户可能已经在那份内建配置文件里填了 apiKey，直接丢弃等于静默清空凭据。
  - 回退读命中时记一条 Information 日志，指明迁移已发生。

### 5.3 组合与默认启用

**关键约束**：`s_providerModules` 被 `Minimal` / `Code` / `Work` / `Research` / `Full` 五档共同展开，
而 `SettlementEngine.ValidateHardDependencies` 对「启用集里 `DependsOn` 未启用」是
**抛 `SettlementException` 拒绝启动**，不是告警。`Hosting.Embed` 的默认 boot 恰好是 `minimal`。
因此若只把 `provider.typesafe-api`（`DependsOn: llm.typesafe`）塞进 `s_providerModules`，
四个低档会在启动时直接崩。

选定处理：**把 `llm.typesafe` 与 `llm.openai` 并排加进 `Minimal` / `Code` / `Work` / `Research`**
（四档现在都只有 `llm.openai`）。理由：改名后的 `llm.typesafe` 是一个**零成本协议工厂包**
（无网络请求、无后台任务、无配置读写），与 `llm.openai` 完全同构，加进去不改变这些档位的启动成本；
换来的是四档都能用 decision 协议，且 `DependsOn` 天然满足，`s_providerModules` 的语义得以保留。

- `BuiltInCapabilitySets`：
  - `Minimal` / `Code` / `Work` / `Research`：在 `"llm.openai"` 后加 `"llm.typesafe"`。
  - `s_providerModules`：最前加 `"provider.typesafe-api"`。
  - `Full`：`"llm.systemone"` → `"llm.typesafe"`（`"llm.systemone.tools"` 不动）。
- `FullCapabilityComposition`：`services.AddSystemOne(registry)` → `services.AddTypeSafe(registry)`；
  新增 `services.AddSeeingModule<TypeSafeApiLlmModule>(registry)`；
  `services.AddSystemOneTools(registry)` 不动。
- 三个宿主 csproj（WebUI / Tui / HostComposition）把 `Seeing.Agent.Llm.SystemOne` 引用改成
  `Seeing.Agent.Llm.TypeSafe`，并各加一条 `Seeing.Provider.TypeSafeApi` 引用（与其它 provider 插件并列）。
  `Seeing.Gateway.Server` 经 HostComposition 传递获得，只改它那两行路径。
- `Seeing.Agent.slnx`：两处路径改名 + 新增插件与插件测试项目。

### 5.4 工具包的最小连带改动

`Seeing.Agent.Tools.SystemOne` 只有两处：

- `SystemOneToolsModule.DependsOn`：`llm.systemone` → `llm.typesafe`
- `SystemOneTool` 的失败文案：`请启用 llm.systemone 模块` → `请启用 llm.typesafe 模块`
  （对应测试 `SystemOneToolExecuteTests` 的断言同步）

工具 id、Skill 名、包名、`llm.systemone.tools` 模块 id 一律不动。

### 5.5 注册表与 UI

- `ProviderRegistration` 的 `<summary>` 里「如 `llm.systemone`」改为「如 `llm.typesafe`」。
- **`ProviderOrigin.Builtin` 保留枚举成员，不删。** 理由：
  它是 Abstractions 层的公开契约（外部插件可能已读取该值），删除是跨包破坏性变更，
  与本变更「把 Provider 从能力包挪到插件」无关；而保留的成本只是一条永不触发的枚举值。
  同时 `ModelsPage.razor` 的「非 Extension 一律按配置渲染」兜底分支**也不改**——
  下沉后确实不再产生 `Builtin` 来源，但那条分支同时也是对未知来源的安全兜底，
  改成显式 `== Configured` 会让将来任何新来源静默落入 `Empty` 分支。
  两处都加注释说明「当前无生产者」，避免读者以为还有内置 Provider。
- `ModelsPage.razor` 的渲染逻辑本身**不改**。下沉后 `systemone` 条目消失，`typesafe-api` 走
  `Origin == Extension` 分支：列表标「扩展」，详情页显示「由扩展 seeing.provider.typesafe-api 注册，
  配置由扩展自身管理」，表单走 schema 驱动（Name / Base Url / Api Key / Capabilities / List Models /
  Timeout / Use Proxy 七项由 `OptionsSchemaBuilder` 从 `TypeSafeApiOptions` 反射生成）。
- `ServiceCollectionExtensions.RegisterLlmServices` 的注释里 `SystemOneModule` → `TypeSafeModule`。

### 5.6 文档同步（本次范围内）

改名会让现有架构文档变成事实错误，本次一并修正，不留给后续：

| 文件 | 改动 |
|---|---|
| `docs/architecture/02-modules.md` | 模块地图两行：`Seeing.Agent.Llm.SystemOne / llm.systemone` → `Seeing.Agent.Llm.TypeSafe / llm.typesafe`，描述改为「只登记 typesafe 协议工厂」；新增 `Seeing.Provider.TypeSafeApi / provider.typesafe-api` 一行；工具行 `DependsOn` 改 `llm.typesafe` |
| `docs/architecture/README.md` | 索引新增 `29-typesafe-provider-extraction-release-notes.md` |
| `docs/architecture/06-compliance-audit.md` | 「模块生命周期对称」行里的 `SystemOne` 表述更新为新模块名（该行本就是描述现状，不是历史记录） |
| `docs/architecture/29-typesafe-provider-extraction-release-notes.md`（新建） | 本次的破坏性清单、迁移步骤（`DefaultModels.decision` 需重选）、`systemone.json` 回退读行为 |
| `docs/AGENTS.md`（仓库根 `AGENTS.md`） | WHERE TO LOOK 表里 `systemone` 相关表述与依赖方向表若提及包名，一并更新 |
| **不改** `docs/architecture/11-…` / `28-…` 等历史 Release Notes | 它们是带日期的历史记录，改历史等于伪造记录；新 Release Notes 里指明「28 号文中关于 SystemOne 合流的描述已被 29 号取代」 |

代码内注释里提到 `systemone` 的地方（`ProviderRegistration`、`SystemOneTool`、`SystemOneToolsModule`）
按 §5.4 / §5.5 的具体条目更新，其余历史性注释（如「原 SystemOne 平行栈已删除」）**保留**——
它们解释的是「为什么代码长这样」，仍有价值。

## 6. 优先级（保持现状）

不改规则，明确记录当前行为以免再次混淆：

| 情形 | 行为 |
|---|---|
| 扩展已注册 id=X，用户又在 `Providers` 写 id=X | 扩展胜；`ProviderManager` 跳过配置驱动并记 Warning |
| 用户想让 `typesafe` 协议走自己的端点 | 新建一个**不同 id** 的配置型 Provider，协议选 `typesafe`（工厂由 `llm.typesafe` 提供） |
| 想改 `typesafe-api` 本身的端点/凭据 | 在设置页编辑该扩展的 schema 表单（写 `typesafe-api.json`） |

## 7. 破坏性变更清单

1. Provider id `systemone` → `typesafe-api`；模型引用 `systemone/jev-latest` → `typesafe-api/jev-latest`。
2. 模块 id `llm.systemone` → `llm.typesafe`（`workMode` / `Boot` / 能力集里写死 `llm.systemone` 的配置会失配）。
3. 程序集 `Seeing.Agent.Llm.SystemOne` → `Seeing.Agent.Llm.TypeSafe`（外部若按程序集名引用需改）。
4. `~/.seeing/providers/systemone.json` 不再被写入；首次读有回退，之后写入 `typesafe-api.json`。
5. `DefaultModels.decision` 若原指向 `systemone/jev-latest` 会失效。

第 5 条**不做自动迁移**：`ValidateDefaultModels()` 已经在模型页顶部把失效的默认模型显式列出
（「以下默认模型已失效（配置已保留，请手动修正）」），用户一键重选即可；
写迁移逻辑意味着在 Core 里硬编码一个插件的 id，违反分层。此项写进 Release Notes 提示。

## 8. 测试计划

- **删除**：`SystemOneProviderActivationTests`（其守门对象已不存在）。
- **改名**：`tests/capabilities/Seeing.Agent.Llm.SystemOne.Tests` → `Seeing.Agent.Llm.TypeSafe.Tests`，
  命名空间/类名随之；`SystemOneModuleTests` → `TypeSafeModuleTests`，断言改为
  `Id == "llm.typesafe"`、`DependsOn` 为空、且**不再断言 Activated 时注册 Provider**
  （反向守门：`Activate` 后注册表仍为空，防止将来又把 Provider 塞回能力包）。
- **新增** `tests/plugs/Seeing.Provider.TypeSafeApi.Tests`：
  - `Activate_ShouldRegisterExtensionOrigin`（`Origin == Extension` 且 `OwnerId == seeing.provider.typesafe-api`）
  - `Deactivate_ShouldUnregister`
  - `WithoutConfig_ShouldExposeSeedDecisionModel`（`jev-latest` 且 `Types` 含 Decision）
  - `WithoutConfig_ShouldServeDecisionClient`（无 apiKey 也能建客户端；`Chat` 为 null）
  - `Schema_ShouldExposeNameBaseUrlApiKeyCapabilitiesListModelsTimeoutUseProxy`（7 字段齐全）
  - `ConfigStore_WhenOnlyLegacyFileExists_ShouldReadIt`（回退读）
- **更新**：
  - `LlmRoutingEndToEndTests`：模块列表换成 `TypeSafeModule` + `TypeSafeApiLlmModule`，
    断言 `typesafe-api` 在注册表、`typesafe-api/jev-latest` 可被 Router 解析。
  - `FullCapabilityCompositionTests`：`TypeSafeClientFactory` 类型断言；`BeEquivalentTo(Full)` 自动覆盖新模块。
  - `ModuleArchitectureInvariantTests`：扫描器自检列表里 `llm.systemone` → `llm.typesafe`。
  - `SystemOneToolExecuteTests`：错误文案断言。
- **验证顺序**：`dotnet build Seeing.Agent.slnx` → 相关测试项目用 `dotnet vstest` 逐项目跑
  （MTP 在本环境下 "Zero tests ran"，必须走 VSTest）。

## 9. 已知边界与已知债

- 插件测试项目不进 `SrcAssemblyScanner`（扫描范围只有 `src/`），因此
  `Full` 对新模块的覆盖靠 `FullCapabilityCompositionTests` 的等价断言守门，不靠不变量测试。
- 旧 `systemone.json` 回退读只做一次性的只读兜底，不删除、不搬移；用户仍有该文件时
  每次启动都会命中回退（新文件生成后自然不再命中）。
- `systemone_*` 工具名与 TypeSafe 协议名不一致，属既有命名债，本次不动。
- **已知债（不在本次修）**：`DeepSeek` / `MiMo` / `OpenCodeZen` / `SiliconFlow` 四个 provider 插件的
  `Activate` 都用 `ProviderRegistration` 默认的 `DisposeByRegistry: true` 注册 DI 单例，
  存在与 §5.2 描述的同一类热重载后 `ObjectDisposedException` 风险。
  本变更只为新插件写对，并在此登记，供后续统一整改。

## 10. 我做的判断（请在评审时复核）

| # | 判断 | 理由 |
|---|---|---|
| 1 | Provider id 取 `typesafe-api` | 与插件同名；把自然 id `typesafe` 留给用户的配置型 Provider |
| 2 | 插件进 Full 默认集 | 用户选择 B（保留开箱可用），与另外四个 provider 插件一致 |
| 3 | `llm.typesafe` 同时加进 Minimal/Code/Work/Research | 否则 `s_providerModules` 里的插件 `DependsOn` 失配，四档启动即抛 |
| 4 | 新插件 `DisposeByRegistry: false` | DI 单例不得由注册表释放（热重载后必崩） |
| 5 | 种子模型 `jev-latest` 随 Provider 迁入插件 Options 默认值 | 否则无凭据时目录为空、`systemone_*` 在默认宿主下不可用 |
| 6 | `apiKey` 不加 `[Required]` | 「无凭据也能用种子模型」是本插件前提，必填会锁死这条路径 |
| 7 | 旧 `systemone.json` 只读回退，不搬移 | 避免静默丢弃用户已填的 apiKey；搬移要处理半成功状态，收益低 |
| 8 | `DefaultModels` 不自动迁移 | 无法在 Core 里硬编码插件 id；已有 UI 失效提示兜底 |
| 9 | 工具包名 / 模块 id / 工具 id 全部不动 | 改动是对外破坏性变更，且与本次主题无关 |
| 10 | 删除插件的 `ProvidersChanged` 订阅与「让位/回位」逻辑 | 优先级保持「扩展优先」后，该逻辑永远不会触发，留着是死代码 |
| 11 | `ProviderOrigin.Builtin` 与 UI 兜底分支都保留 | Abstractions 公开契约，删除是跨包破坏；兜底分支仍是未知来源的安全网 |
| 12 | 文档同步纳入本次范围，历史 Release Notes 不改 | 改名直接让 02/README 变成事实错误；历史记录不得回改 |
