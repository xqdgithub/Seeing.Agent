# UI 扩展统一架构 Release Notes

**日期:** 2026-11-10
**范围:** `src/abstractions/Seeing.Agent.Abstractions/Ui/`、`src/spine/Seeing.Agent.Core/Ui/`、`samples/Seeing.Agent.WebUI/Shell/`、8 个能力模块、`Seeing.Gateway`
**分支:** `refactor/ui-extension-envelope`（批次 A/B/C/D 全部完成）
**设计规格:** [`docs/superpowers/specs/2026-11-10-ui-extension-unified-architecture-design.md`](../superpowers/specs/2026-11-10-ui-extension-unified-architecture-design.md)（v4）
**实施计划:** [`docs/superpowers/plans/2026-11-10-ui-extension-envelope.md`](../superpowers/plans/2026-11-10-ui-extension-envelope.md)

## 一句话

UI 扩展从「6 套并行机制 + 贡献自带 `Type` 且住 samples」收敛为**统一信封 + `ViewKey` + 声明早于激活**：契约只放纯数据，判定落 Core，视图解析落宿主。迁移期最痛的三处（路由双份注册、模块停用后入口永久消失、注册表只给未过滤扁平集）随之消失。

## 一、概要

### 1.1 迁移前的 6 套并行机制

| # | 机制 | 位置（迁移前） | 结局 |
|---|------|---------------|------|
| 1 | `NavContribution` / `RouteContribution` | `Abstractions/Ui/UiContributionTypes.cs` | 合并为 `PageContribution`（`Nav == null` 即纯路由） |
| 2 | `SettingsCardContribution`（旧形状） | 同上 | 改建为信封形状的新类型 |
| 3 | `SlotContribution`（旧形状） + `SlotHost` | 同上 + `Shared/SlotHost.razor` | 改建为信封形状；`SlotHost` 改读扩展点 |
| 4 | `MessageRendererContribution` | 同上 | **删除**（零消费者死代码） |
| 5 | 会话内容渲染 4 套 | `IContentBlockRenderer` / `IMessageComponent` / `IToolCallSlotProvider` / `TimelineItemKind` | **不在本期**（见 §5.2） |
| 6 | `IGatewayChannelPlugin.ConfigFormComponentType` | `src/gateway/Seeing.Gateway/Plugins/` | **删除**（死字段，只被搬运从未消费） |

### 1.2 三层分工

| 层 | 位置 | 内容 |
|----|------|------|
| **契约** | `src/abstractions/Seeing.Agent.Abstractions/Ui/` | `UiPoint` / `UiPayloadKind` / `UiVisibilityScope` / `UiContribution` 家族 / `IUiContributionRegistry` / `IUiContributor` / `IUiHostContributor` / `IUiViewKeyCatalog` / `IUiContributionValidator` / `UiRouteMatch`。**零实现**：无方法体、无 `Microsoft.AspNetCore.*` |
| **脊柱** | `src/spine/Seeing.Agent.Core/Ui/` | `UiContributionRegistry`（事实 + 校验 + 诊断）、`UiContributionCollector`（两段收集）、`UiVisibility`（唯一可见性实现）、`UiScenarioGate`、`UiRouteTemplate`、`UiPointCatalog`、`UiPayloadKindMap`、DI 扩展 |
| **宿主** | `samples/Seeing.Agent.WebUI/Shell/` | `WebUiPoints`（声明 6 个扩展点）、`WebUiViews`（31 条 `ViewKey → Type`）、`WebUiViewResolver`、`WebUiHostContributor`（`host.webui`，13 页面 + 4 设置卡）、`WebUiUiContributionValidator`、`WebUiViewKeys` |

### 1.3 关键收益

| 收益 | 依据 |
|------|------|
| **能力包不再需要宿主组件类型** | 模块只声明 `ViewKey` 字符串；`WebUiViews.cs` 是唯一知道组件类型的地方。能力包零 `ProjectReference` 到 samples |
| **模块停用不再永久丢入口** | 声明在结算后收集且不随 Deactivate 撤销（`ModuleLifecycleManager.CollectUiContributions`）。历史故障「Deactivate → Activate 后侧栏永久消失」的成因（手写 `Register`/`Unregister` 对称）被消除 |
| **未启用模块的直链能给出正确文案** | 注册表保留未启用模块的声明，`ModuleRouter` 先用「可见集」匹配、未命中再用「声明全集」取 `Title` / `Requires` |
| **路由从 2 处变 1 处** | 3 个页面文件删掉 `@page`；15 条模块路由由模块自己声明（含 5 条原先只由 enrich 补全的详情路由） |
| **可见性只有一份实现** | `UiVisibility`。迁移前 `UiContributionVisibility` / `SessionShellVisibility` / `ModuleRouteResolver` 各算一次 |
| **贡献坏掉在启动期报出来** | 8 类 `UiDiagnosticKind` 进 `Diagnostics`，`WebUiHost` 启动时一次性打日志；不再等用户点进去看到白屏 |

## 二、破坏性变更清单

> 全部为**编译期**破坏。仓库内所有调用点已同步，下列迁移指引面向仓外实现者与后续 PR。

### 2.1 契约类型删除

| 删除项 | 替代 | 迁移指引 |
|--------|------|---------|
| `NavContribution` | `PageContribution` + `NavPlacement?` | `Route`/`Title`/`Requires`/`Order` 原样迁；`ComponentType` → `ViewKey`；`Icon`/`Group`/`GroupIcon` 移入 `NavPlacement`（注意 `Order` 留在信封本身，`GroupOrder` 才在 `NavPlacement`） |
| `RouteContribution` | `PageContribution`（`Nav = null`） | 纯路由不再有独立类型：把 `Nav` 留空即可。旧 `RouteContribution.Requires` 是 `IReadOnlyList<string>?`（可空），新契约是 `[]` 默认的空列表——壳层写 `[]` 而非 `null` |
| `LegacySettingsCardContribution` | `SettingsCardContribution` | `Route` 字段**消失**（新契约不靠路由定位卡片）。Tab key 改用 `{OwnerId}.{Id}`，见 §3.3 |
| `LegacySlotContribution` | `SlotContribution` | `Name`（旧槽名）→ 扩展点 id（映射见 §3.10）；`ComponentType` → `ViewKey`；`Scenarios` 删除 |
| `MessageRendererContribution` | 无（零消费者） | 直接删除。会话内容渲染仍走 `IContentBlockRenderer` 等 4 套既有机制（不在本期范围） |
| `IUiContribution` | `IUiContributor` / `IUiHostContributor` | 旧接口是两个成员：`string ModuleId` + `IReadOnlyList<object> Contribute()`（返回值靠调用方 `as` 判型）。新接口是**单成员** `IReadOnlyList<UiContribution> DescribeUi()`：归属搬到 `Requires`，类型靠 `UiPayloadKindMap` 判，不再需要 `object` 装箱 |

### 2.2 `IUiContributionRegistry` 全新形状

旧接口（`Register` / `Unregister` / `NavItems` / `SettingsCards` / `Slots` / `Routes`）**整体删除**，同名新接口只有下列成员：

```csharp
void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions); // 幂等：整批替换该 owner
void RemoveByOwner(string ownerId);
void ReportInvalidDeclaration(string ownerId, string message);

IReadOnlyList<UiContributionEntry> All { get; }
IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution;
IReadOnlyList<PageContribution> Pages { get; }
IReadOnlyList<UiContributionDiagnostic> Diagnostics { get; }

event EventHandler? Changed;
```

**迁移指引**：`Register(this)` → 删除（模块不再自注册，改由 `UiContributionCollector` 在结算后扫描 `IUiContributor`）；`Unregister(id)` → 删除（模块停用**不**撤销声明，可见性由 `UiVisibility` 判定）；`NavItems`/`Slots` → `Get<PageContribution>("shell.page")` / `Get<SlotContribution>(pointId)`。

> **实现类与接口差一个 `I`**：`UiContributionRegistry`（Core）实现 `IUiContributionRegistry`（Abstractions）。DI 同时注册两者（具体类型为单例，接口转发），以便 `Collector.IsEnabled` 读取 `IsEnabled`。

### 2.3 宿主侧删除

| 删除项 | 原职责 | 替代 |
|--------|--------|------|
| `samples/.../Services/ModulePageRouteBinder.cs` | 15 条路由硬编码表 + 覆盖式 enrich | 模块自己声明 `PageContribution` |
| `samples/.../Services/ModuleRouteResolver.cs` | 路径 → 组件解析 + 可见性 | `UiRouteTemplate.Resolve` + `WebUiViewResolver.Resolve` |
| `samples/.../Services/UiContributionVisibility.cs` | 可见性（与另两份分叉） | `UiVisibility` |
| `samples/.../Services/UiContributionRegistry.cs`（及过渡期 `LegacyUiContributionRegistry.cs`） | 进程内贡献表 | `Core/Ui/UiContributionRegistry` |
| `samples/.../Services/CoreShellUiContribution.cs` | 壳层贡献的旧形状 | `Shell/WebUiHostContributor.cs` |
| `samples/.../State/SlotRegistry.cs` | 静态插槽注册表（legacy 回退） | 扩展点 + `IUiContributionRegistry.Get<SlotContribution>` |

`Services/SessionShellVisibility.cs` **保留但已瘦身**：不再做任何过滤，只剩两个转发（`ResolveEffectiveScenario` → `UiScenarioGate.ResolveEffective`、`ResolveDefaultAgent`），供 `SessionWindow` / `SessionToolbar` 推导默认 Agent。

### 2.4 `Scenarios` 字段删除（全仓零使用）

`NavContribution` 与 `LegacySlotContribution` 各自的 `Scenarios` 参数删除。迁移前扫描确认：`Scenarios:` 只出现在两个类型声明处，无任何构造点赋值。

**连带影响**：`SessionShellVisibility.IsVisible` 的白名单分支曾是唯一读点（唯一调用点恒传 `null`），该分支随批次 C 整体替换消失。

### 2.5 `IGatewayChannelPlugin.ConfigFormComponentType` 删除

死字段：从 `IGatewayChannelPlugin` → `GatewayChannelTypeInfo` → `GatewayChannelRegistry` → `GatewayClientConfigService`（`GatewayClientViewModel`）→ `GatewayClientsPage` 逐层搬运，**从未传给 `DynamicComponent`**。删除后 `GatewayChannelTypeInfo` 从 10 个位置参数减为 9 个（`ConfigFormComponentType` 是末位），`GatewayClientViewModel` 也去掉同名属性。

### 2.6 8 个模块的 `_ui` 字段与构造参数删除

`basic` / `skills` / `mcp` / `scheduler` / `memory` / `acp` / `reflection` / `gateway` 全部：

```csharp
// 删除
private readonly IUiContributionRegistry? _ui;
public XxxModule(IUiContributionRegistry? uiRegistry) { _ui = uiRegistry; }
_ui?.Register(this);        // ActivateAsync
_ui?.Unregister(Id);        // DeactivateAsync

// 新增
public sealed class XxxModule : ISeeingModule, IUiContributor
{
    public IReadOnlyList<UiContribution> DescribeUi() => [ /* 纯数据 */ ];
}
```

同步删除 7 处 extension 调用点的 `uiRegistry` 实参（`SchedulerServiceCollectionExtensions` ×2、`MemoryServiceExtensions`、`AcpServiceCollectionExtensions`、`GatewayServiceCollectionExtensions` ×2、`FullCapabilityComposition`）与 2 处反射构造探测（`ModuleRegistrationExtensions`、`ServiceCollectionExtensions`）。

### 2.7 3 处 `@page` 删除

`Pages/MemoryPage.razor`、`Pages/WorkspacesPage.razor`、`Pages/ReflectionRunsPage.razor` 的 `@page` 指令删除。它们此前与 `ModulePageRouteBinder` 的路由表**各注册一次**，是「路由双份注册」的直接来源。

**现在**：`samples/Seeing.Agent.WebUI/Pages/_Host.cshtml` 是唯一保留 `@page` 的文件。

**副作用核实**：`WorkspacesPage` 由 `ModuleRouter.razor` 直接渲染（不经路由表），`WorkspaceGateTests` 断言的是 `ModuleRouter` 源码含 `<WorkspacesPage`，与 `@page` 无关。

### 2.8 其它

| 项 | 变更 |
|----|------|
| `UiVisibility.AreRequirementsEnabled` | 过渡期的静态门闸方法删除。Core 只有 `UiVisibility.IsVisible` / `Evaluate` |
| `UiPoint` 集合 | 新增 `IUiPointCatalog` + `UiPointCatalog`；未注册时注册表与收集器**整体 no-op**（无界面宿主零改动） |
| `WebUiHost` / `WorkspaceReadyInitializer` | 两处重复的旧注册删除；改为 `AddSeeingUiPoints(...)` + `Build()` 后 `CollectHostContributions(...)` |
| `UiContributionEntry` | 新增 `readonly record struct (string OwnerId, UiContribution Contribution)`，归属贴在条目上而非贡献内 |

## 三、新契约要点

### 3.1 `UiPoint`：宿主声明「这里能挂什么」

```csharp
public sealed record UiPoint
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required UiPayloadKind PayloadKind { get; init; }
    public UiVisibilityScope Scope { get; init; } = UiVisibilityScope.Process;
    public int MaxItems { get; init; } = int.MaxValue;
}
```

`UiPayloadKind` 是**封闭集**（`Page` / `SettingsCard` / `Slot`）。`UiPayloadKindMap` 用类型查表（类型参数不能做 `switch case`），并沿基类链回溯到 `UiContribution` 之前 —— 因此新增载荷类型必须同时在这一张表里登记，否则会被判 `PayloadMismatch`。

WebUI 声明的 6 个点（`Shell/WebUiPoints.cs`）：

| Id | `PayloadKind` | `Scope` |
|----|--------------|---------|
| `shell.page` | `Page` | `Process` |
| `shell.settings.card` | `SettingsCard` | `Process` |
| `session.prompt` | `Slot` | `Session` |
| `session.toolbar.actions` | `Slot` | `Session` |
| `session.input.toolbar` | `Slot` | `Session` |
| `session.file.panel` | `Slot` | `Session` |

### 3.2 `UiContribution`：统一信封

```csharp
public abstract record UiContribution
{
    public required string Point { get; init; }
    public required string Id { get; init; }
    public IReadOnlyList<string> Requires { get; init; } = [];   // 归属模块；空 = 进程级
    public int Order { get; init; }
}
```

**没有 `ComponentType`，也没有 `ModuleId`**——前者是契约层不得携带 Blazor 类型的分层硬约束，后者由注册表条目持有（实测 10 处模块贡献的 `Requires` 全部等于自身模块 id，见 §4.1）。

`with` 表达式会被 `AddRange` 用于重建条目，因此载荷是 `sealed record` 且派生类型被保留（`EnvelopeContractTests.WithExpression_PreservesDerivedType` 守门）。

### 3.3 三类载荷

| 类型 | 特有字段 | 说明 |
|------|---------|------|
| `PageContribution` | `Route` / `Title` / `ViewKey` / `Nav: NavPlacement?` | `Nav == null` = 纯路由，不进侧栏。一条类型合并了旧 `Nav` + `Route` |
| `SettingsCardContribution` | `Title` / `ViewKey` | 无 `Route`；页签 key 由 `{OwnerId}.{Id}` 拼 |
| `SlotContribution` | `ViewKey` / `DisplayName?` | 挂在宿主声明的会话插槽点上 |

`NavPlacement`：`Icon`（中性 token，由宿主 `WebUiIconMap.Resolve` 翻译）/ `Group` / `GroupIcon` / `GroupOrder`。

### 3.4 `ViewKey`

契约只承诺「这是个字符串」。宿主用 `IUiViewKeyCatalog`（`CanResolve` / `Keys`）回答「有没有」，用 `WebUiViewResolver`（`Register` / `Resolve` / `CanResolve` / `Keys`）做**强类型**解析。`Register` 校验 `IComponent` 且重复键直接抛错（重复键会让渲染目标取决于注册顺序）。

**为什么不是 `Type`**：`System.Type` 本身不产生编译期程序集引用，所以「`Type` 阻碍运行时插件」的说法不成立（规格 §0 的 E2 已自我更正）。`ViewKey` 的真实价值只有三条——① 契约层与 Blazor 彻底解耦（`Abstractions/Ui/` 零 `Microsoft.AspNetCore.*`，由不变量测试守门）；② 同一视图可被多条路由复用（`/session` 与 `/session/{SessionId}` 共用 `page.session`）；③ 视图缺失可被宿主在**收集期**拒收成诊断，而不是渲染期白屏。

### 3.5 归属在 `Requires`

| 场景 | `Requires` | 语义 |
|------|-----------|------|
| 模块页面 / 插槽 | `[自身模块 id]` | 模块停用即不可见 |
| 宿主壳层页面 | `[]` | 进程级，永不受模块启停影响 |
| 宿主代模块声明（`settings.scheduler` / `settings.memory`） | `[scheduler]` / `[memory]` | 卡片随模块停用消失 |
| 文件面板插槽（声明在宿主侧，归属 `files.workspace`） | `[files.workspace]` | 视图是 WebUI 组件，能力包不能持有 |

**空 `Requires` 的模块贡献被拒收**（`InvalidDeclaration`）：否则页面会在模块停用后存活，而工具 / Hook / 命令已卸载——「半残但看起来正常」。

### 3.6 `host.*` 命名空间

| 规则 | 实现点 |
|------|--------|
| 宿主 `OwnerId` 必须以 `host.` 开头 | `UiContributionCollector.CollectHostContributions`（不以 `host.` 开头 → `LogError` + 跳过） |
| 模块不得占用 `host.{moduleId}` | `UiContributionRegistry.AddRange`（`CrossOwnerDuplicate` 诊断 + 拒收） |

**为什么强制**：注册表按 `ownerId` 整批替换，一个叫 `host.memory` 的模块会静默顶掉宿主贡献；反过来，宿主若不用 `host.` 前缀，其贡献会走「模块贡献必须有 `Requires`」校验而**整批拒收** → 侧栏与设置页全空。两条规则互为补集，缺一不可。

### 3.7 `UiVisibility` 的三条规则

按序求值（`Core/Ui/UiVisibility.cs`）：

| # | 规则 | 结果 |
|---|------|------|
| ① | `Requires.Count == 0` | 可见（进程级豁免；**catalog 为 null 时也可见**） |
| ② | `Catalog is null` / `Requires` 含空白项 / 任一模块未启用 | 不可见（`CatalogUnavailable` / `InvalidRequire` / `ModuleDisabled`，fail-closed） |
| ③ | `Point.Scope == Session` 且门集非 `null` 且 `Requires` 未全部落入门集 | 不可见（`ScenarioMismatch`，**AND 语义**） |

规则①先行是必需的：catalog 缺失时若壳层也隐藏，用户会失去 `/settings` 而无法自救。

规则③的 AND 语义来自旧实现 `SessionShellVisibility` 的 `foreach { if (!contains) return false; }`；迁移中曾一度变成 OR，被 `UiSemanticRegressionTests` 拦下。

会话门集由 `UiScenarioGate.Resolve` 给出，**`null` = 不收敛**（未指定 / 未知 / 通配 `*`），**空集 = 全部隐藏**（工作模式存在但模块列表为空）。两者语义相反，不可合并——空集走 `Contains` 全 false 是旧实现的 fail-closed 行为。

### 3.8 路由占位名契约

模板里的 `{name}` **逐字**等于 `UiRouteMatch.Parameters` 的键，也**逐字**等于页面组件的 `[Parameter]` 名。没有大小写转换、没有前缀、没有 snake/camel 映射。

```csharp
// SchedulerModule.DescribeUi()
Route = "/cron-jobs/{JobId}"        →  Parameters["JobId"]
// Pages/JobDetailPage.razor
[Parameter] public string JobId { get; set; } = "";
```

`ModuleRouter.ToParameterDictionary` 用 `OrdinalIgnoreCase` 装箱成 `Dictionary<string, object>`（丢弃 `null` 值以跳过使用默认值的参数）。

### 3.9 catch-all 三语义

`UiRouteTemplate.TryMatch` 对 `{*name}` 的行为（与 `ModuleRouteResolverCatchAllTests` 逐条对齐）：

| 语义 | 行为 |
|------|------|
| **不解码** | `%2F` 原样保留，不做 URL 解码 |
| **无尾段捕获空串** | `/memory/detail` 命中 `{*FilePath}` 时参数为 `""`（不是 `null`，也不是不匹配） |
| **剩余段用 `/` 连接** | 多段尾路径拼成一个字符串 |

**评分与 tiebreak**：`score = 字面量段数 × 1000 + 规范化模板长度`；同分时按**路由模板序数序** → **归属 owner 序数序**（与集合枚举顺序无关，保证确定性）。

### 3.10 四个插槽的映射

`SlotHost` 保留旧槽名作为组件调用点的稳定契约，内部映射到扩展点 id：

| 旧 `SlotName` | 扩展点 id | 挂载点 |
|--------------|-----------|--------|
| `session_prompt` | `session.prompt` | `SessionWindow.razor:207` |
| `session_toolbar_actions` | `session.toolbar.actions` | `SessionToolbar.razor:146` |
| `message_toolbar` | `session.input.toolbar` | `PromptInput.razor:188` |
| `session_file_panel` | `session.file.panel` | `SessionWindow.razor:229` |

未登记的槽名渲染空容器（不抛），与迁移前一致。

## 四、关键设计决策

### 4.1 `Requires` = 归属声明（不引入 `ModuleId`）

**依据**：迁移前实测 10 处模块贡献的 `Requires` 全部等于自身模块 id（Skills `["skills"]`、Mcp `["mcp"]`、Reflection `[ModuleId]`、Scheduler ×2、Memory、Acp、Basic、Gateway ×2）。壳层例外只有 `[]`（进程级）与「代模块声明」的 `["scheduler"]` / `["memory"]`。迁移后 15 条模块页面路由同样一律 `[自身 id]`（由 `PageChannelMigrationTests.ModuleContributions_ShouldAllDeclareOwnership` 守门）。

**收益**：一个字段同时表达「谁拥有」与「何时可见」，不需要第二套概念，也不会出现两者漂移。注册表用它在模块贡献缺归属时拒收（§3.5），`UiVisibility` 用它做模块启停判定，`ModuleRouter` 用它给「未启用」提示填 `ModuleId`。

### 4.2 `Scenarios` 字段删除（全仓零使用）

**依据**：`Scenarios:` 在全仓只出现在两个类型声明的参数表里，无任何构造点赋值。

**收益**：删掉了一个需要为它设计匹配语义（白名单是 OR 还是 AND？与 `Requires` 如何组合？）的空壳。规格 v1/v2 为它设计的 `Conditions` 集合、六字段 `UiVisibilityContext`、OR/AND 迁移全部成为不必要的工作。

### 4.3 宿主壳层不是模块（`IUiHostContributor`）

**依据**：把壳层做成真 `ISeeingModule` 会锁死用户——

- `FullCapabilityCompositionTests` 钉死 `result.Enabled` 必须 `BeEquivalentTo(BuiltInCapabilitySets.Full)`；
- `WebUI.csproj` 已引用 `HostComposition`，反向引用成环；
- 用户改 `Boot` 能力集后 `/settings` 消失，**无法自救**。

**做法**：`IUiHostContributor` 独立端口（`OwnerId = "host.webui"`），在 `Build()` 之后、`InitializeSeeingAsync()` 之前收集，与工作区是否选定无关。

### 4.4 注册表只做事实，可见性由调用方判定

**依据**：会话级可见性需要会话上下文（`UiScenarioGate` 的模块集来自会话 `Scenario`），注册表拿不到。若把判定塞进注册表，`All` / `Pages` 就必须接受一个上下文参数，而「未启用模块的声明仍要在表内」这一收益立刻消失。

**做法**：「单一实现」不等于「唯一调用点」——`UiVisibility` 是唯一实现，`ModuleRouter` / `AppSidebar` / `Settings.razor` / `SlotHost` 各自在**自己的上下文里**调用它。`UiRouteTemplate.Resolve` 因此把可见性做成显式谓词 `Func<UiContributionEntry, bool>?`；生产调用方**必须**传（否则停用模块的高分页会抢赢启用模块的页——旧实现里停用模块的路由根本不在表内，不可能参与竞争）。

### 4.5 声明早于激活

**依据**：收集点从「Activate 之后」移到「结算完成后扫描全部模块」。真实收益不是「保住建议工作模式的提示」（该提示今天并不存在——`BuiltInScenarios.All` 只有 `full`，`Modules = ["*"]`，`NotEnabledFragment` 的建议分支恒不匹配），而是：

- 模块未启用时仍能拿到 `FeatureTitle` / `ModuleId` → 直链给出「未启用『知识图谱』」而不是「未启用『页面』」；
- `Deactivate → Activate` 之后入口不再消失（不需要任何对称注销代码）。

**收集点收敛在 `ModuleLifecycleManager.ActivateAsync` 入口**（而不是各调用方）：启动（`InitializeSeeingAsync`）、配置热重载（`ModuleSettlementReloadHandler`）、工作区切换后的重新结算都会走到这里。无 `SettlementEngine` 的 fallback 路径（`ServiceCollectionExtensions` 逐模块直接激活）不看 `ModuleLifecycleManager`，因此单独调用一次 `CollectModuleUiContributions`。任一遗漏都表现为「某个模块的页面永久消失」。

### 4.6 `ViewKey` 而非 `Type`

**依据**：能力包只依赖 Abstractions + 原语，不可能持有 `samples/Seeing.Agent.WebUI` 的组件类型。若契约携带 `Type`，模块声明就必须写在宿主侧——这正是迁移前 `ModulePageRouteBinder` 存在的原因（15 条硬编码路由表 + 覆盖式 enrich 补齐详情路由）。

**做法**：契约携带 `ViewKey` 字符串，宿主维护唯一视图表（31 条：11 壳层页面 + 15 模块页面 + 4 设置卡 + 1 插槽）。**命名约定**：`ViewKey` 与贡献 `Id` 同名（便于守门与人工核对）；仅当多条贡献共用同一组件时才重复出现（`page.session.detail` → `page.session`）。插槽只有一个内置贡献，因此抽了 `WebUiViewKeys` 常量，避免声明处与登记处两处字符串漂移。

## 五、已知边界

| 项 | 说明 |
|----|------|
| **catch-all 仍需宿主手工映射** | `WebUiHost.cs` 的 `app.MapFallbackToPage("/memory/detail/{*FilePath}", "/_Host")` 必须保留：该路径含扩展名，ASP.NET Core 的通用 fallback 会把「看起来像文件」的请求当静态文件跳过，深链接直接 404。**「单一通道」在 ASP.NET 路由层不成立**——信封统一的是「谁声明路由」，不是「谁来 serve 请求」 |
| **页面标题仍是编译期中文字面量** | `PageContribution.Title` 由模块直接写中文。能力包无法引用 `WebUI/Services/UiText.cs`（依赖方向不允许），i18n 资源键不在本期 |
| **`UiPoint.Id` 只增不改** | 无版本机制。扩展点 id 是宿主与模块之间的字符串协议；进程内编译期组装，错配在编译期就爆，因此不引入运行时版本协商 |
| **`Diagnostics` 不持久化** | 进程内内存，重启清空。`WebUiHost` 启动时打一次 `LogError`（含 kind / owner / contribution id），修的是声明而不是渲染分支 |
| **`NavGroups` 三个中文常量保留** | 向后兼容（能力包与壳层的共同词汇）；新分组走 `NavPlacement.Group` + `GroupOrder`，`GroupOrder == 0` 视为「排在所有已声明之后」（否则第三方分组会因默认 0 排到「控制中心」之前） |
| **`RemoveByOwner` 在仓库内无生产调用点** | 模块停用不撤销声明（见 §4.5）；保留该 API 是契约完整性（宿主卸载插件、测试隔离） |
| **视图缺失的降级分散在四个消费点** | 收集期由 `WebUiUiContributionValidator` 拦成诊断，运行期正常走不到 null；兜底行为：`ModuleRouter` 渲染「未启用 / 视图未装配」占位（Error 日志）、`AppSidebar` 不渲染该项、`Settings.razor` 跳过页签、`SlotHost` 不渲染 |

### 5.1 壳层保留区清单（装不进三类载荷的入口）

下列入口**刻意**保持宿主硬编码。写下来是为了让每个新模块作者不必重复询问「这个能不能做成贡献」。

| # | 入口 | 位置 | 为什么不做 |
|---|------|------|-----------|
| 1 | 侧栏「聊天」 | `AppSidebar.razor`（`Key="chat"`） | 无静态 `Route`：路径按 `AppState.CurrentSessionId` 动态算；选中键是固定字符串 `chat` 而非路由。`PageContribution.Route` 语义不符。`SidebarProjectionService.SelectedKeys` 已把 `""` / `home` / `session` → `chat` 的特例纳入并有测试 |
| 2 | 侧栏「审批中心」 | 同上（`Key="permission-center"`） | 需要「实时计数徽标 + 打开抽屉」：`Badge Count=@PermissionView.TotalCount` 必须写在 `IconTemplate` 里（写在 `ChildContent` 会被折叠态 tooltip 复制并染黑文字），点击走 `UiOverlayService`。三类载荷都表达不了 |
| 3 | 侧栏底部工具条 | `AppSidebar.razor` 的 `sider-footer-stack` | 工作区切换（`WorkspaceSwitcherButton`）/ 关闭 / 折叠，纯宿主 chrome |
| 4 | 首页 dashboard（能力格 / 调度器 / MCP 面板） | `Pages/Index.razor` | 卡片需要「数据 + 排版占位 + 点击行为」，`SlotContribution` 只有 `ViewKey`。可见性仍各自用 `IModuleCatalog.IsEnabled` 判定 |
| 5 | 斜杠命令面板 | `ICommandRegistry` | 平行的第二套「可发现入口」，可见性走「激活期增删」而非 `Requires`；`CommandMetadata` 已自带 `Category` / `SortOrder` / `IsHidden`。语义与「声明期收集」相反 |
| 6 | 通知 / 重试横幅 | `SessionWindow.razor` | 无真实模块需求；现在设计会是「为死字段做设计」。有需求时再加 `session.banner` 点 |
| 7 | Token 预算状态栏 | `PromptInput.razor` 的 `prompt-footer` | 跨宿主「状态段」抽象，本期不接 Tui |
| 8 | 会议页侧栏分区 | `Conference.razor`（`SessionSidebarView.Build`） | 按 `SessionRelation` 的**固定 3 类**分区（主线历史 / 派生 / 子会话），不是「多贡献聚合位」 |

### 5.2 会话内容渲染 4 套机制不在本期

`IContentBlockRenderer` / `IMessageComponent` / `IToolCallSlotProvider` / `TimelineItemKind` 是**投影管线**而不是布局贡献，且三套注册表的冲突语义各异（同名替换 / 同 `BlockType` 替换 / 不去重按序全渲染）。本期只删掉其中唯一死掉的 `MessageRendererContribution`。

## 六、验证记录

### 6.1 全量

| 项 | 结果 |
|----|------|
| `dotnet build Seeing.Agent.slnx` | **0 错误**（4 条 `NU1510` 包裁剪警告，既有） |
| 全部测试程序集（52 个，不含 `tests/probe`） | **5873 通过 / 0 失败** |
| 验证脚手架 `tests/probe/Seeing.Agent.UiEnvelopeProbe.Tests` | 118 通过（未进 `.slnx`；批次 F 清理） |

### 6.2 本轮新增 / 改写的守门测试

| 测试 | 数量 | 钉住什么 |
|------|------|---------|
| `Seeing.Agent.Tests/Ui/*`（`UiVisibility` 12 / `UiRouteTemplate` 30 / `UiContributionRegistry` 19 / `UiScenarioGate` 8 / `UiContributionCollector` 11 / `UiSemanticRegression` 9 / `EnvelopeContract` 8 / `UiContributionContracts` 7） | **104** | 契约形状（无 `Type` / 无 `ModuleId` / 无 `Scenarios`、`DescribeUi` 必须抽象）；可见性三条规则与 AND 语义、catalog 缺失 fail-closed 而壳层豁免；catch-all 三语义 + 15 条真实路由逐条命中；注册表 8 类诊断、幂等替换、跨 owner 重复；场景门 5 分支与 `null` vs 空集；收集链异常隔离（失败时只记诊断、不 `AddRange` 抹掉诊断） |
| `Invariants.Tests/Ui/CoreUiLayerInvariantTests` | 1 | `Core/Ui/` 下所有类型成员的签名不得出现 `Microsoft.AspNetCore.*` |
| `WebUI.Tests/Shell/PageChannelMigrationTests` | 11 | 28 条路由一条不少（15 模块 + 13 壳层）；5 条 enrich-only 详情路由由模块声明且 `Nav == null`、`ViewKey == Id`；壳层 17 条贡献中页面全为进程级；侧栏成员与迁移前逐项一致；三个分组顺序不变；标题 / 图标逐条一致；10 条参数路由可解析；每条 `ViewKey` 可解析；停用模块仍能取到标题与归属 |
| `WebUI.Tests/Shell/SettingsAndSlotMigrationTests` | 10 | 设置页仍 4 张卡且 `{OwnerId}.{Id}` key 唯一；4 个插槽点均存在且 `Scope = Session`；文件面板归属 `files.workspace`、随模块停用与会话收窄而消失；设置卡（`Process`）不受会话门影响；两处 `DynamicComponent` 都有 `ErrorBoundary`；`src/` 不再出现 4 个旧类型名 |
| `WebUI.Tests/Services/SidebarProjectionServiceTests` | 26 | 投影五条规则（参数化路由剔除 / 更短前缀隐藏子路径 / `GroupOrder` 分组 / 组内 `(Order, Title)` / 选中键特例与前缀匹配）与 `GroupOrder = 0` 排最后 |
| `Seeing.Agent.Mcp.Tests/McpModuleLifecycleTests` | 5 | `DescribeUi()` 单条 `PageContribution`、`Requires == ["mcp"]`、`Nav` 非空；Activate/Deactivate 不再有 `IUiContributionRegistry` 交互 |

**具体断言示例**（便于后续 PR 对照意图）：

```csharp
// PageChannelMigrationTests.DisabledModuleRoute_ShouldStillResolveToKnownPageTitle
visibleMatch.Should().BeNull("memory 被禁用，可见集里不该有这个页面");
var knownMatch = UiRouteTemplate.Resolve("/memory/graph", registry.All);
knownMatch!.Page.Title.Should().Be("知识图谱");
knownMatch.Page.Requires.Should().Equal("memory");

// SettingsAndSlotMigrationTests.SettingsCard_ShouldIgnoreSessionGate
UiVisibility.IsVisible(card, point,
    new UiVisibilityContext(new Catalog("memory"), new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
    .Should().BeTrue("设置卡不随会话工作模式收窄");

// SettingsAndSlotMigrationTests.SourceMustNotReferenceLegacyContributionTypes
// 扫描 src/**/*.cs（排除 obj/bin），4 个旧类型名命中即失败
```

> 迁移期发现并修正的一处测试缺陷：`page.Requires.Should().Equal("mcp", "原因")` 会被 `params` 重载解析成「期望两个元素」，断言永远失败。改为 `Equal(["mcp"], "原因")`。全仓扫描确认仅此一处同类误用。

### 6.3 手工验收（批次 C / D 判据）

- 28 个页面全部可达（逐个直链，含 5 条详情路由）；
- 侧栏 6 分组、顺序、图标与迁移前逐项一致，5 条详情路由**不出现**在侧栏；
- 未启用模块直链给「未启用『知识图谱』」并带正确的可切换工作模式建议；
- `/workspaces` 仍走 `EmptyLayout` 整页渲染；
- 设置页 4 张卡顺序为 启动能力 → 工作模式 → 调度器 → 记忆（`Order` 10/20/30/40）；
- 4 个插槽正常渲染（含文件面板）；切到窄工作模式后插槽收窄而侧栏不变。

### 6.4 与设计规格的差异（以代码为准）

| 项 | 规格写法 | 实际实现 |
|----|---------|---------|
| 接口命名 | 直接落地新形状的 `IUiContributionRegistry` | 批次 B1 因旧同名接口仍存活，先落为 `IUiContributionCatalog`，批次 D 改回 `IUiContributionRegistry`（12 处引用同步） |
| `RemoveByOwner` 用途 | 「模块停用后撤销」 | 模块停用**不**撤销声明；该方法只保留契约完整性 |
| 场景门测试数 | 7 | 8（多出「`ResolveEffective` 会话级优先」一条） |
| 可见性规则顺序 | 规格 §4.1 把「`Catalog is null` → 不可见」列为第 1 条 | 代码先判 `Requires.Count == 0` 直接可见，再判 catalog。顺序差异对结果有影响：catalog 缺失时壳层贡献仍可见（否则用户失去 `/settings`） |
| 路由模板测试数 | 19 | 30 |
| 侧栏投影测试数 | 22 | 26 |
| 测试总数 | 118（验证脚手架） | 迁入正式项目后：`Seeing.Agent.Tests/Ui` 104 + 不变量 1 + `PageChannelMigrationTests` 11 + `SettingsAndSlotMigrationTests` 10 + `SidebarProjectionServiceTests` 26，另有改写 5 + 6 + 3 + 10（`McpModuleLifecycleTests` / `ModuleRouteResolverCatchAllTests` / `JobDetailEditNavigationTests` / `SidebarContractTests`）。脚手架 118 项仍在 `tests/probe/`，未进 `.slnx` |

## 七、后续（不在本期）

| 项 | 说明 |
|----|------|
| 清理验证脚手架 `tests/probe/` | 计划批次 F；当前未进 `.slnx`，不参与构建与测试 |
| 运行时程序集加载 | 需 ALC 统一、依赖解析、静态资源端点、CSS 隔离、`ViewKey` 命名空间隔离五件事；`IUiViewKeyCatalog` 一件都没解决 |
| 懒激活 | 规模不匹配；声明期收集已为其铺路（未激活模块的声明已在表内） |
| Tui 接入 | 单屏聊天无「页面」概念；需先改 `TuiRenderer` 的段拼装 |
| 权限级可见性 | 全仓无认证接线 |
| i18n 资源键 | 多语言需求未出现；`init` 属性加字段不破坏既有声明 |
| 会话内容渲染 4 套统一 | 见 §5.2 |
