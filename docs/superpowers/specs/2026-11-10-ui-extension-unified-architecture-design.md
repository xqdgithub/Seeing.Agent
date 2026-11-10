# UI 扩展统一架构设计（UI Extension Envelope）

**日期:** 2026-11-10（v2，经六维独立审查后重写）
**状态:** 待评审
**取代:** `2026-11-10-ui-extension-unified-architecture-design.md`（v1，作废）、`2026-11-09-workbench-plugin-platform-design.md`（工作台部分另立规格）
**范围:** `Seeing.Agent.Abstractions`（`Ui/` 契约重写）、`Seeing.Agent.Core`（注册表 + 可见性 + 路由模板）、8 个贡献模块、`samples/Seeing.Agent.WebUI`（视图表 + 消费方）

---

## 0. v1 的五个错误（本版更正）

v1 经 6 个独立维度审查（分层依赖 / 可实现性 / 完整性 / 灵活性 / 工作台归位 / 行业对照），发现以下错误。**这些不是补充，是更正**——v1 的对应条款作废。

| # | v1 的错误 | 更正 | 证据 |
|---|-----------|------|------|
| E1 | §1.2 D3 称"三套可见性实现" | **大部分已收敛**。`SessionShellVisibility.cs:112-113` 与 `ModuleRouteResolver.cs:205-206` 均已转调 `UiContributionVisibility.AreRequirementsEnabled`。真正的分叉只剩 `IsVisible`（场景白名单）一处，且只有 `SlotHost.razor:66` 一个调用点。 | 见代码 |
| E2 | §1.2 D1 称"`Type` 让运行时插件在契约层就被否决" | **不成立**。`System.Type` 不产生程序集引用；运行时插件自带组件，`typeof(MyPage)` 合法。`ViewKey` 的真实价值只有三条（见 §2.3）。 | 见 §2.3 |
| E3 | §3.2 讨论 `ModuleId` 如何"贴"（三方案） | **伪问题**。注册表 `Entry` 已持有归属，record 上不该有这个字段。 | 见 §4.4 |
| E4 | §7「工作台归位」按既有基线设计迁移 | **基线不存在**。`rg IWorkbenchRegistry\|WorkbenchContribution\|WorkbenchDock` 在 `src`/`samples` 零命中；旧计划 0/89 勾选。工作台**另立规格**。 | 见 §9 |
| E5 | §8 分期「P0 保留旧类型标 `[Obsolete]`」 | **不可编译**。`SettingsCardContribution` / `SlotContribution` 与旧类型**同名同命名空间** → CS0101，`[Obsolete]` 不救；且全仓 `TreatWarningsAsErrors=false`、WebUI `NoWarn=CS0618`，「零警告」判据空转。 | 见 §8.1 |

**v1 另有 6 处内部矛盾**（`Get<T>` 无会话上下文 / `AddRange` 不在接口上 / `DescribeUi()` 在 try 内 / `UiVisibility` 依赖 Core 类型 / `All` 承诺与收集时机冲突 / 壳层 `AddRange` 构成第二通道），全部在 §4–§6 修正。

---

## 1. 现状事实（核实过的基线）

### 1.1 六套并行机制

| # | 机制 | 契约位置 | 消费者 | 状态 |
|---|------|---------|--------|------|
| 1 | Nav / Route | `UiContributionTypes.cs:15,80` → `IUiContributionRegistry.Routes` | `AppSidebar`、`ModuleRouter` | 活；两类型共用一表 + 转换代码（`UiContributionRegistry.cs:105-114`，填 `Icon: ""`） |
| 2 | SettingsCard | `UiContributionTypes.cs:45` | `Settings.razor:142` | 活 |
| 3 | Slot | `UiContributionTypes.cs:58` + `SlotHost.razor` | 4 处挂载点 | **仅 1 个使用者**（`session_file_panel`） |
| 4 | MessageRenderer | `UiContributionTypes.cs:69` | **无** | 死代码 |
| 5 | 会话内容渲染 | `IContentBlockRenderer` + `IMessageComponent` + `IToolCallSlotProvider` + `TimelineItemKind` | 渲染管线 | 活；**实为 4 套**，冲突语义各异 |
| 6 | 渠道插件配置表单 | `IGatewayChannelPlugin.cs:26` `ConfigFormComponentType` | **无** | **死字段**（只被搬运，从不传给 `DynamicComponent`） |

### 1.2 规模基线

| 项 | 数量 | 出处 |
|---|---|---|
| `ISeeingModule` 实现 | **35** | `rg -l ": ISeeingModule" src plugs` |
| 其中需要 UI 贡献 | **8**（23%） | Basic / Skills / Mcp / Scheduler / Memory / Acp / Reflection / Gateway |
| 显式 `new XxxModule(uiRegistry)` 的 extension 调用点 | **7** | Scheduler:103,214 / Memory:75 / Acp:63 / Gateway:50,90 / HostComposition:133 |
| 旧贡献构造点（src+samples） | **30** | `rg -c "new (Nav\|Route\|SettingsCard\|Slot\|MessageRenderer)Contribution"` |
| 旧贡献构造点（tests） | **8** | 同上 |
| `KnownModuleRoutes` 表项 | **15**，其中 **5 条仅由 enrich 产生** | `ModulePageRouteBinder.cs:26,29,33,47,49` |
| 插槽挂载点 | **4** | `SessionWindow:207,229`、`SessionToolbar:146`、`PromptInput:188` |
| 仍带 `@page` 的模块页 | **3** | `MemoryPage.razor:1`、`WorkspacesPage.razor:1`、`ReflectionRunsPage.razor:1` |
| `Abstractions` 纯函数豁免（06:191-201 记录） | 6 处 | 不含读进程状态的判定 |
| `Abstractions` 默认接口方法 | 3 处（2 处未记录） | `IExecutionOutcomeObserver.cs:33`、`IExecutionInFlightBoundary.cs:25` |

### 1.3 根因（一条因果链）

```
根因：贡献 = 激活期构造的、携带 Type 的 C# 对象图
 │
 ├─(a) 能力包不能引用 samples/WebUI 的组件
 │      ⇒ 模块只能声明 route/title，ComponentType 由宿主补
 │      ⇒ ModulePageRouteBinder 15 条表 + 覆盖式 enrich
 │
 ├─(b) 注册表住在 samples（WebUI DI），Core 生命周期看不见它
 │      ⇒ 能 Register 的只能是模块自己
 │      ⇒ 双身份 + 手写对称 + 反射构造探测
 │      ⇒ "忘记 Unregister = 永久失效"（ReflectionModule.cs:136-138 记录的故障）
 │
 └─(c) 注册表只给未过滤扁平集合，可见性有两个正交作用域
        ⇒ 消费者各自再过滤（已收敛到 1 份 + 1 处场景分叉，见 E1）
```

**v1 新引入的第 4 条果**：让 registry 同时持有"事实"与"可见性"，导致接口无法承载会话上下文。本版用原则 2 消除。

### 1.4 真实故障（设计动机的实证）

`ReflectionModule.cs:136-138` 的注释原文：

> UI 贡献必须与 Activate 的 Register 对称注销，否则 Deactivate → Activate 之后侧栏「复盘记录」入口永久消失（其余模块 Memory/Scheduler/Acp/Skills/Mcp/Basic 均已对称）。

这是"设计缺陷而非纪律问题"的直接证据，是原则 3 的动机。

---

## 2. 架构原则

### 2.1 契约只放数据与端口；判定落脊柱；视图落宿主

| 层 | 内容 |
|---|---|
| **Abstractions** | `UiContribution` 家族（纯数据）、`UiPoint` / `UiPayloadKind`、`IUiPointCatalog`、`IUiContributor`、`IUiContributionRegistry`（含写端口）、`IUiViewKeyCatalog`、`UiContributionDiagnostic`、`UiRouteMatch` |
| **Core** | `UiContributionRegistry` 实现、`UiVisibility`（唯一判定）、`UiScenarioGate`（场景名→模块集）、`UiRouteTemplate`（模板匹配）、`UiPayloadKindMap` |
| **Host** | `WebUiPoints`（声明扩展点）、`WebUiViewResolver`（`ViewKey → Type` 强类型）、挂载组件 |

**为什么判定与解析落 Core 而非 Abstractions**：

- `UiVisibility` 不是纯函数：读 `IModuleCatalog`（进程状态做准入决策），不属于 06:191 的"无状态纯函数"豁免类别。
- 它需要 `IScenarioCatalog`（`ScenarioCatalog.cs:9`）与 `BuiltInScenarios`（`BuiltInScenarios.cs:7`），**两者都在 Core，Abstractions 没有**。放 Abstractions 会把逻辑劈成两半，让 Tui 再写一份 `ResolveScenarioModuleSet`——三处分叉变成**跨程序集**分叉。
- `UiRouteTemplate` 是纯字符串逻辑，但**全部宿主都引用 Core**（WebUI/Tui/Cli/Gateway.Server 均 `ProjectReference Core`），放 Core 无需扩契约层纯函数豁免清单（06 已记录"上提 PathSafety 到 Abstractions 已决策放弃，避免契约层纯函数膨胀"）。
- Core 已有三个同构先例：`ModuleCatalog`（`ModuleCatalog.cs:8`）、`DynamicToolContributorRegistry`（`DynamicToolContributorRegistry.cs:9`）、`SurfaceRegistry`（`SurfaceRegistry.cs:13`）。

**Core 版实现的三条硬约束**（写入 04 检查清单 + 不变量测试）：

1. `src/spine/Seeing.Agent.Core/Ui/` 下不得出现 `Microsoft.AspNetCore.*` / `IComponent` / `RenderFragment`。
2. 视图解析（`IUiViewKeyCatalog` 的实现）**不得**进 Core。
3. `IUiPointCatalog` 未注册时（Headless / Embed / Gateway.Server），registry **no-op 不收集**，而非"收集后逐条丢弃 + 记诊断"——避免无界面进程为 30 条贡献刷启动日志。

### 2.2 注册表只做「事实」，可见性由调用方显式判定

```csharp
// registry：只按 point + module 过滤，不做可见性
IReadOnlyList<T> Get<T>(string pointId);
IReadOnlyList<UiContribution> All { get; }

// visibility：唯一实现，多调用点
static bool UiVisibility.IsVisible(UiContribution c, IModuleCatalog? catalog,
                                   string? effectiveScenario, IReadOnlySet<string>? scenarioGate);
```

**"单一实现" ≠ "唯一调用点"。** 这是项目既有范式（`DynamicToolContributorRegistry` + `ModuleCatalog.IsEnabled`）。v1 让 `Get<T>` 承担可见性，直接导致会话级可见性从端口上不可达。

### 2.3 声明早于激活

行业里"声明"的定义就是**不需要激活即可读**（VS Code `contributes` / Eclipse `plugin.xml` / JetBrains `<extensions>`）。收集点因此从「Activate 之后」移到「结算完成后扫描全部 `Available` 模块」：

```csharp
// ModuleLifecycleManager 构造时已持有全部模块（含未启用）：ModuleLifecycleManager.cs:32
// 结算完成 → 对所有 Available 模块读 DescribeUi()（纯函数，无副作用）
```

这一步同时修好三件事：

1. `All` / `FindPageByRoute` 能覆盖未启用模块（否则 P3 删 `ModulePageRouteBinder` 后，直链 `/memory` 而 memory 未启用 → `NotEnabledFragment` 拿不到 `FeatureTitle`/`ModuleId` → `SuggestScenarios()` 返回空，丢失"可切换到 code/work"这条最有用的提示）。
2. 消除 v1 的 `All` 文档与收集时机的矛盾。
3. 为将来的懒激活留好前置（懒激活本期不做）。

### 2.4 可选能力用独立接口，不塞主干

`IUiContributor` 独立于 `ISeeingModule`。35 个模块只有 8 个（23%）需要 UI。项目已有三个同范式先例：`IDynamicToolContributor`、`IExecutionOutcomeObserver`、`IModuleHostedService`。

另有两个具体风险：

- **Moq 对默认接口方法**：Castle proxy 会**实现**该成员返回 `default`，**不走** DIM 默认实现 → `AddRange(id, null)` → NRE。
- **接口膨胀有先例**：`docs/superpowers/plans/interface-audit-table.md:20` 记录 `IAgentManager` 因"26 方法 ISP 违反"被删除。

---

## 3. 契约层（Abstractions）

### 3.1 扩展点

```csharp
namespace Seeing.Agent.Abstractions.Ui;

/// <summary>载荷种类（封闭集：新增载荷须改契约发版）。</summary>
public enum UiPayloadKind
{
    /// <summary>页面（路由 + 可选侧栏位）。</summary>
    Page,

    /// <summary>设置页卡片。</summary>
    SettingsCard,

    /// <summary>命名插槽条目。</summary>
    Slot
}

/// <summary>
/// UI 扩展点：宿主声明「这里能挂什么」。
/// <para>未声明的点上的贡献被拒收并记诊断；<see cref="IUiPointCatalog"/> 未注册时 registry no-op（见 §2.1 约束 3）。</para>
/// </summary>
public sealed record UiPoint
{
    /// <summary>稳定标识（如 <c>shell.page</c> / <c>session.toolbar.actions</c>）。只增不改。</summary>
    public required string Id { get; init; }

    /// <summary>诊断用名称。</summary>
    public required string DisplayName { get; init; }

    /// <summary>可接受的载荷种类。</summary>
    public required UiPayloadKind PayloadKind { get; init; }

    /// <summary>最多接受的贡献数（<c>1</c> = 独占）。默认不限。</summary>
    public int MaxItems { get; init; } = int.MaxValue;

    /// <summary>可见性作用域：进程级（不随会话场景收窄）或会话级。</summary>
    public UiVisibilityScope Scope { get; init; } = UiVisibilityScope.Process;

    /// <summary>字段级自描述版本；变更载荷语义时递增。</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>扩展点的可见性作用域。</summary>
public enum UiVisibilityScope
{
    /// <summary>进程级：只看模块启用集，忽略会话场景（侧栏、设置卡）。</summary>
    Process,

    /// <summary>会话级：额外随会话 Scenario 收窄（插槽）。</summary>
    Session
}

/// <summary>宿主支持的扩展点集合。</summary>
public interface IUiPointCatalog
{
    IReadOnlyList<UiPoint> Points { get; }
    UiPoint? Find(string pointId);
    bool Supports(string pointId);
}
```

**`PayloadKind` 用枚举而非 `Type`**：`System.Type` 经 `System.Text.Json` 序列化为 `{}`，无法投影到诊断页；4 类载荷是封闭集，枚举足够。v1 的 `Type PayloadKind` 与 `Get<T>` 构成双真相且从不校验。

**`Scope` 解决 v1 未定的一个问题**：`Scenarios` 对页面/设置卡是否生效。现状是**刻意只对插槽生效**（`UiContributionVisibility.cs:10-11` 明确"进程 Scenario 名**不**硬藏已挂载模块"；`SessionShellVisibility.cs:38-47` 同）。合并成单一 `IsVisible` 后若统一处理，侧栏会被会话场景永久隐藏（埋雷）。因此按点声明作用域，而不是按贡献猜。

### 3.2 贡献信封

```csharp
/// <summary>
/// UI 贡献信封：所有载荷的公共部分。
/// <para><b>不含 <c>ModuleId</c></b>：归属由注册表 Entry 持有（见 §4.4）。</para>
/// </summary>
public abstract record UiContribution
{
    /// <summary>扩展点 id。</summary>
    public required string Point { get; init; }

    /// <summary>模块内唯一标识。</summary>
    public required string Id { get; init; }

    /// <summary>依赖的模块 id；空表示隐含 <c>[归属模块]</c>（见下方说明）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>额外可见性条件（AND）；空表示无条件。</summary>
    public IReadOnlyList<UiCondition> Conditions { get; init; } = [];

    /// <summary>排序（升序）。同值时按 <c>(Id, 归属模块)</c> 稳定 tiebreak。</summary>
    public int Order { get; init; }
}

/// <summary>可见性条件种类。</summary>
public enum UiConditionKind
{
    /// <summary>模块已启用。</summary>
    Module,

    /// <summary>会话工作模式名匹配。</summary>
    Scenario,

    /// <summary>宿主能力可用（如 workspace-resolved / gateway-reachable）。</summary>
    Capability,

    /// <summary>会话类型匹配（root / fork / subagent）。</summary>
    SessionKind,

    /// <summary>特性开关。</summary>
    Feature
}

/// <summary>一个可见性条件。</summary>
public sealed record UiCondition(UiConditionKind Kind, string Value);
```

**`Conditions` 而非只有 `Requires` + `Scenarios`**：v1 的两个硬字段是固定合取，第三个条件（能力门/会话类型）到来时要改基类（4 载荷重编译）+ 改签名（所有调用点）+ `Get` 再改签名。用开放集合后新增条件种类 = 加一个 `case`，4 载荷零改动。

**`Requires` 空值语义**：空表示隐含 `[归属模块]`。这样 8 个模块里 `Requires = ["basic"]` / `["skills"]` / `["mcp"]` 这类与自身 id 相同的模板代码可以省略（`BasicModule.cs:52`、`SkillsModule.cs:123`）。

### 3.3 三类载荷

```csharp
/// <summary>侧栏导航位；null 表示该页面不进侧栏（纯路由）。</summary>
public sealed record NavPlacement
{
    /// <summary>图标 token（如 <c>database</c>）；宿主映射到具体图标库。</summary>
    public required string Icon { get; init; }

    /// <summary>分组名；null/空 = 顶层扁平。</summary>
    public string? Group { get; init; }

    /// <summary>分组图标 token。</summary>
    public string? GroupIcon { get; init; }

    /// <summary>分组排序（同组内按贡献的 <c>Order</c>）。</summary>
    public int GroupOrder { get; init; }
}

/// <summary>
/// 页面贡献：路由模板 + 视图键 + 可选侧栏位。
/// <para>合并旧 <c>NavContribution</c> 与 <c>RouteContribution</c>：「纯路由」= <see cref="Nav"/> 为 null。</para>
/// </summary>
public sealed record PageContribution : UiContribution
{
    /// <summary>路由模板（可含 <c>{Param}</c> / <c>{*CatchAll}</c>）。</summary>
    public required string Route { get; init; }

    /// <summary>页面标题（侧栏与「未启用」提示共用）。</summary>
    public required string Title { get; init; }

    /// <summary>视图键。</summary>
    public required string ViewKey { get; init; }

    /// <summary>侧栏位；null = 不进侧栏。</summary>
    public NavPlacement? Nav { get; init; }
}

/// <summary>设置页卡片。</summary>
public sealed record SettingsCardContribution : UiContribution
{
    /// <summary>卡片标识（宿主用作 Tab key；须全进程唯一）。</summary>
    public required string Key { get; init; }

    /// <summary>卡片标题。</summary>
    public required string Title { get; init; }

    /// <summary>视图键。</summary>
    public required string ViewKey { get; init; }
}

/// <summary>命名插槽条目。</summary>
public sealed record SlotContribution : UiContribution
{
    /// <summary>视图键。</summary>
    public required string ViewKey { get; init; }

    /// <summary>诊断用显示名。</summary>
    public string? DisplayName { get; init; }
}
```

`Icon` 从「AntDesign Outline 图标名」改为**中性 token**：宿主映射到具体图标库（Web → AntDesign，Tui → 文本符号），避免契约层固化了某个前端库的词汇。

### 3.4 模块侧贡献端口

```csharp
namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// 可选能力：模块声明自己的 UI 贡献（纯数据，不触碰任何注册表）。
/// <para>由 Core 在结算后统一收集，模块作者不写 Register/Unregister，也无法忘记成对。</para>
/// </summary>
public interface IUiContributor
{
    /// <summary>返回本模块的 UI 贡献；返回 null 或含 null 元素时被跳过并记诊断，不抛。</summary>
    IReadOnlyList<UiContribution> DescribeUi() => [];
}
```

**独立于 `ISeeingModule`**（理由见 §2.4）。`DescribeUi()` 是 DIM——`Abstractions` 已有 3 处先例（`IExecutionOutcomeObserver.cs:33`、`IExecutionInFlightBoundary.cs:25`），本设计为第 3 类豁免（接口默认实现），须在 06 文档补记。

### 3.5 注册表端口（含写端口）

```csharp
namespace Seeing.Agent.Abstractions.Ui;

/// <summary>UI 贡献注册表：由结算驱动填充，消费方只读。</summary>
public interface IUiContributionRegistry
{
    // ── 写端口（Core 内部 + 宿主壳层）──
    /// <summary>登记某模块的全部贡献（幂等：先移除该模块旧条目再添加）。</summary>
    void AddRange(string moduleId, IReadOnlyList<UiContribution> contributions);

    /// <summary>移除某模块的全部贡献。</summary>
    void RemoveByModule(string moduleId);

    // ── 读端口（不做可见性；调用方自行判定，见 §2.2）──
    /// <summary>全部已收集贡献（含未启用模块的声明与因故被拒者的诊断，见 <see cref="Diagnostics"/>）。</summary>
    IReadOnlyList<UiContribution> All { get; }

    /// <summary>某扩展点上的指定载荷（按 <c>Order</c>、<c>Id</c>、归属模块稳定排序）。</summary>
    IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution;

    /// <summary>全部页面贡献（按 <c>Order</c> 稳定排序）。</summary>
    IReadOnlyList<PageContribution> Pages { get; }

    /// <summary>按路由模板精确查找（含未启用者，供「未启用」提示）。</summary>
    PageContribution? FindPageByRoute(string route);

    /// <summary>收集过程中被拒的贡献与原因（供诊断；不随 <see cref="Changed"/> 清空）。</summary>
    IReadOnlyList<UiContributionDiagnostic> Diagnostics { get; }

    /// <summary>贡献集合变更（登记 / 移除 / 模块启停结算完成）。</summary>
    event EventHandler? Changed;
}

/// <summary>诊断项。</summary>
public sealed record UiContributionDiagnostic(
    UiDiagnosticKind Kind,
    string ModuleId,
    string PointId,
    string ContributionId,
    string Message);

/// <summary>被拒原因。</summary>
public enum UiDiagnosticKind
{
    /// <summary>宿主未声明该扩展点。</summary>
    UnsupportedPoint,

    /// <summary>载荷种类与点声明不符。</summary>
    PayloadMismatch,

    /// <summary>同一 (模块, 点, id) 重复。</summary>
    DuplicateId,

    /// <summary>同一 (点, 路由) 重复。</summary>
    DuplicateRoute,

    /// <summary>超过点的 <c>MaxItems</c>。</summary>
    ExceededMaxItems,

    /// <summary>视图键无法解析（由宿主校验器判定）。</summary>
    ViewKeyUnresolved,

    /// <summary>贡献声明本身异常（DescribeUi 抛错或返回 null）。</summary>
    InvalidDeclaration
}
```

**写端口进接口**：v1 把 `AddRange` 只写在实现注释里，`_ui?.AddRange(...)` 编译不过。既有 `Register`/`Unregister` 本就在接口上（`IUiContributionRegistry.cs:9,12`），保持一致，不为 2 个方法新建接口。

**`Get<T>` 必须校验载荷种类**：`payloadKindOf(T) != point.PayloadKind` 时抛 `InvalidOperationException`（不静默返 `[]`——那会让"用错类型"看起来像"没有贡献"）。

### 3.6 视图键端口

```csharp
/// <summary>
/// 视图键目录：只回答「有没有 / 有哪些」，不涉及渲染载体类型。
/// <para>各宿主自己定义强类型解析入口（Web → <c>Type?</c>，Tui → <c>Func&lt;TuiRenderContext, IRenderable&gt;?</c>）。</para>
/// </summary>
public interface IUiViewKeyCatalog
{
    bool CanResolve(string viewKey);
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>宿主侧启动审计：由宿主注册，Core 在收集时调用。</summary>
public interface IUiContributionValidator
{
    /// <summary>返回 null 表示通过；否则为诊断文本，该贡献被拒收。</summary>
    string? Validate(UiContribution contribution, UiPoint point);
}
```

**不提供 `object? Resolve(string viewKey)`**：v1 的 `IUiViewResolver.Resolve → object?` 在 Abstractions 里**零消费者**，却把类型判别推给每个消费点（每个宿主、每个调用点都要写 `switch (view as Type / Func / ...)`）。改为"契约只答有没有，宿主各自强类型解析"。

---

## 4. Core 实现

### 4.1 目录

```
src/spine/Seeing.Agent.Core/Ui/
  UiContributionRegistry.cs       ← 唯一实现（§4.2）
  UiPointCatalog.cs               ← IUiPointCatalog 默认实现（IReadOnlyDictionary 索引）
  UiContributionRegistrationExtensions.cs  ← AddSeeingUiPoints(params UiPoint[])
  UiVisibility.cs                 ← 唯一可见性判定（§4.3）
  UiVisibilityContext.cs          ← 判定输入对象（§4.3）
  UiScenarioGate.cs               ← 场景名 → 模块集解析（§4.3）
  UiRouteTemplate.cs              ← 模板匹配 + 最优路由解析（§4.4）
  UiPayloadKindMap.cs             ← UiPayloadKind ↔ 载荷类型 映射
  UiDiagnosticsCollector.cs       ← 诊断累积（§4.5）
```

### 4.2 注册表

**收集时机**：结算完成后扫描**全部 `Available` 模块**（不是只扫 `Enabled`），`Entry` 打 `IsActive` 标记。

```csharp
// ModuleLifecycleManager 内（构造时已持有全部模块：ModuleLifecycleManager.cs:32）
// 结算 → 收集（一次）→ 再 Activate 启用集
private void CollectUiContributions()
{
    if (_ui is null) return;                    // IUiPointCatalog 未注册 → registry no-op
    foreach (var (id, module) in _modulesById)
    {
        if (module is not IUiContributor contributor) continue;
        IReadOnlyList<UiContribution> items;
        try { items = contributor.DescribeUi() ?? []; }
        catch (Exception ex)
        {
            _log.LogError(ex, "模块 {ModuleId} 的 UI 贡献声明失败", id);
            _ui.RecordInvalidDeclaration(id, ex.Message);
            continue;                            // 绝不让它拖垮启动
        }
        _ui.AddRange(id, items);
    }
}
```

**必须同时修 fallback 分支**：`ServiceCollectionExtensions.cs:944-950` 在 `SettlementEngine`/`ModuleLifecycleManager` 缺失时直接 `module.ActivateAsync`，**绕过 lifecycle** → 极简宿主的 UI 会全空且无错。修法：把收集抽成静态 helper，两处共用。

**`AddRange` 行为矩阵**：

| 情况 | 行为 |
|---|---|
| `IUiPointCatalog` 未注册 | registry no-op（不收集、不打日志） |
| `Point` 未声明 | 拒收 + `UnsupportedPoint` 诊断 |
| `PayloadKind` 与点不符 | 拒收 + `PayloadMismatch` 诊断 |
| `(moduleId, point, id)` 重复 | **幂等覆盖**（先删同键再插，不是"拒后者"） |
| `(point, route)` 重复 | 保留 `(Order, Id, ModuleId)` 最小者 + `DuplicateRoute` 诊断 |
| 超过 `MaxItems` | 按 `Order` 取前 N + `ExceededMaxItems` 诊断 |
| 宿主校验器返回非 null | 拒收 + `ViewKeyUnresolved` 诊断 |
| 元素为 null | 跳过 + `InvalidDeclaration` 诊断 |
| `DescribeUi()` 抛异常 | 整模块跳过 + `InvalidDeclaration`，**绝不外抛** |

**幂等覆盖而非"拒后者"**：Deactivate → Activate 循环（`ReflectionModule.cs:136-138` 记录的正是这个坑）下 `AddRange` 会被调多次，同模块同一贡献重复登记会误报冲突。

### 4.3 可见性

```csharp
/// <summary>可见性判定的唯一实现。</summary>
public static class UiVisibility
{
    /// <summary>
    /// 判定贡献是否可见。规则（按序，任一不满足即不可见）：
    /// ① <c>Requires</c>（空则隐含归属模块）全部启用；catalog 为 null 时 fail-closed；
    /// ② 若点的 <c>Scope == Session</c>，按 <paramref name="ctx"/> 评估 <c>Conditions</c> 与场景闸；
    /// ③ 若点的 <c>Scope == Process</c>，忽略会话相关条件（场景/会话类型）。
    /// </summary>
    public static bool IsVisible(UiContribution c, UiPoint point, UiVisibilityContext ctx);

    /// <summary>返回带原因码的判定结果（诊断用；<see cref="IsVisible"/> 的便捷重载）。</summary>
    public static UiVisibilityDecision Evaluate(UiContribution c, UiPoint point, UiVisibilityContext ctx);
}

/// <summary>判定输入的聚合（唯一会增长的参数）。</summary>
public sealed record UiVisibilityContext(
    IModuleCatalog? Catalog,
    string? EffectiveScenario = null,
    IReadOnlySet<string>? ScenarioModuleGate = null,
    IReadOnlySet<string>? Capabilities = null,
    string? SessionKind = null,
    IReadOnlySet<string>? Features = null)
{
    /// <summary>进程级上下文（不传会话相关字段）。</summary>
    public static UiVisibilityContext Process(IModuleCatalog? catalog) => new(catalog);
}

/// <summary>带原因的判定结果。</summary>
public sealed record UiVisibilityDecision(bool Visible, string? Code = null, string? Detail = null);
```

`IsVisible` 保持 `bool` 返回（消费者不必处理原因码），另给 `Evaluate` 供诊断页。

**`UiScenarioGate`**（与 `UiVisibility` 同落 Core）：

```csharp
/// <summary>场景名 → 模块集。唯一实现（原 SessionShellVisibility.ResolveScenarioModuleSet 上移）。</summary>
public static class UiScenarioGate
{
    public static IReadOnlySet<string>? Resolve(string? effectiveScenario, IScenarioCatalog? catalog);
}
```

**空串防护**：`ModuleCatalog.IsEnabled` 对空白 id 会抛 `ArgumentException`（`ModuleCatalog.cs:60-66`），而现状三份实现都手动跳过空白（`UiContributionVisibility.cs:93-94`）。`UiVisibility` 必须显式规定"空白 `Requires`/`Conditions.Value` 视为无效并记诊断"，否则一个 `Requires: [""]` 的笔误会把整个侧栏渲染炸掉。

### 4.4 路由模板

```csharp
/// <summary>一次路由匹配结果。</summary>
public sealed record UiRouteMatch(
    PageContribution Page,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// 路由模板匹配（从 ModuleRouteResolver 迁出，<c>{*CatchAll}</c> 语义原样保留）。
/// </summary>
public static class UiRouteTemplate
{
    public static string NormalizePath(string? path);

    /// <summary>模板匹配；score 为「字面段数 × 1000 + 模板长度」。</summary>
    public static bool TryMatch(string template, string path,
        out IReadOnlyDictionary<string, object?> parameters, out int score);

    /// <summary>在候选页面中取最优匹配。</summary>
    public static UiRouteMatch? Resolve(string path, IEnumerable<PageContribution> pages);
}
```

`ModuleRouteResolver` 的 `TryMatchTemplate` / `NormalizePath` / `TryMatchBest` 三件套整体上移；`ModuleRouteKind` / `ModuleRouteResolution` 保留在 WebUI（宿主渲染概念）。

### 4.5 诊断

`Diagnostics` 由 `UiDiagnosticsCollector` 累积（注册表内部持有），`Changed` 不清空，`RemoveByModule` 清理该模块的旧诊断。宿主诊断页可直接投影：`Points × PayloadKind × 贡献数 × 被拒原因`。

---

## 5. 宿主实现（WebUI）

### 5.1 扩展点声明

```csharp
// samples/Seeing.Agent.WebUI/Shell/WebUiPoints.cs
public static class WebUiPoints
{
    public static readonly UiPoint ShellPage = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page,
      Scope = UiVisibilityScope.Process };

    public static readonly UiPoint ShellSettingsCard = new()
    { Id = "shell.settings.card", DisplayName = "设置卡片", PayloadKind = UiPayloadKind.SettingsCard,
      Scope = UiVisibilityScope.Process };

    public static readonly UiPoint SessionPrompt = new()
    { Id = "session.prompt", DisplayName = "输入区上方", PayloadKind = UiPayloadKind.Slot,
      Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionToolbarActions = new()
    { Id = "session.toolbar.actions", DisplayName = "会话工具栏右侧", PayloadKind = UiPayloadKind.Slot,
      Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionInputToolbar = new()
    { Id = "session.input.toolbar", DisplayName = "输入框工具栏", PayloadKind = UiPayloadKind.Slot,
      Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionFilePanel = new()
    { Id = "session.file.panel", DisplayName = "会话右侧面板", PayloadKind = UiPayloadKind.Slot,
      Scope = UiVisibilityScope.Session };

    public static IReadOnlyList<UiPoint> All { get; } =
        [ShellPage, ShellSettingsCard, SessionPrompt, SessionToolbarActions, SessionInputToolbar, SessionFilePanel];
}
```

**注册时序硬约束**：必须在 `Build()` **之前**（与 `AddSeeingHostingWeb` 同段，`WebUiHost.cs:97` 附近）调 `services.AddSeeingUiPoints(WebUiPoints.All)`。若在 `Build()` 后注册（如 `WorkspaceReadyInitializer` 的延迟路径），Activate 时 catalog 为空 → 全部贡献被拒。

### 5.2 视图表

```csharp
// samples/Seeing.Agent.WebUI/Shell/WebUiViewResolver.cs
public sealed class WebUiViewResolver : IUiViewKeyCatalog
{
    /// <summary>登记视图；类型必须实现 IComponent。重复键抛错（快速失败）。</summary>
    public void Register(string viewKey, Type componentType);

    /// <summary>强类型解析（WebUI 专用入口）。</summary>
    public Type? Resolve(string viewKey);

    public bool CanResolve(string viewKey);
    public IReadOnlyCollection<string> Keys { get; }
}
```

启动时单点登记（`samples/Seeing.Agent.WebUI/Shell/WebUiViews.cs`），并注册校验器：

```csharp
public sealed class WebUiViewValidator(IUiViewKeyCatalog views) : IUiContributionValidator
{
    public string? Validate(UiContribution c, UiPoint point)
    {
        var key = c switch
        {
            PageContribution p => p.ViewKey,
            SettingsCardContribution s => s.ViewKey,
            SlotContribution sl => sl.ViewKey,
            _ => null
        };
        if (key is null) return $"未知载荷类型：{c.GetType().Name}";
        return views.CanResolve(key) ? null : $"视图未装配：{key}";
    }
}
```

### 5.3 视图缺失的降级（v1 完全没写）

| 消费点 | 解析失败 | 级别 |
|---|---|---|
| `ModuleRouter`（页面） | 渲染"功能未装配"占位（复用 `NotEnabledFragment`，文案区分"模块未启用"/"视图缺失"） | Error |
| `AppSidebar`（导航） | **不渲染该项**（有入口点进去白屏比没入口更糟） | Warning |
| `Settings.razor`（卡片） | 跳过该 TabPane | Warning |
| `SlotHost` | 不渲染 | Debug（插槽本就允许宿主不支持） |

实际实现上由 `IUiContributionValidator` 在**收集期**拒收（进 `Diagnostics`），所以运行期正常不会走到 null；此表是"校验器缺失/绕过"时的兜底。

### 5.4 侧栏投影（保住既有视觉契约）

`AppSidebar` 不直接把 `PageContribution` 塞进模板，先投影：

```csharp
private sealed record NavView(string Key, string Route, string Title, string Icon,
                              string? Group, string? GroupIcon, int Order);

_visibleNav = Registry.Get<PageContribution>(WebUiPoints.ShellPage.Id)
    .Where(p => p.Nav is not null)
    .Where(p => UiVisibility.IsVisible(p, WebUiPoints.ShellPage,
        UiVisibilityContext.Process(Catalog)))
    .Where(p => p.Route.IndexOf('{') < 0)              // 规则①：参数化路由不进侧栏
    .Where(p => !HasShorterPrefix(p.Route, candidates)) // 规则②：更短前缀隐藏子路径
    .Select(p => new NavView(...))
    .ToList();
```

**两条规则必须保留**（v1 完全没提）：

- 规则①：`UiContributionVisibility.cs:41` —— 参数化路由不进侧栏（否则 `/session/{SessionId}` 出现在菜单里）。
- 规则②：`:47` —— 有更短前缀时隐藏子路径（否则 `/memory/graph`、`/skills/create`、`/skills/{SkillName}` 挤进侧栏，且 `NavKey`（`AppSidebar.razor:368`）会把 `{SkillName}` 拼成非法 key）。

投影保留 `Icon`/`Group`/`Order`/`Route` 成员名 → 既有 `SidebarContractTests` 的源码文本断言不变。

### 5.5 分组顺序

`NavPlacement.GroupOrder` 取代 `AppSidebar.razor:305-322` 的硬编码 `preferred` 数组（`NavGroups.Control/Workspace/Settings` 三选三 + 其余按中文名字母序）。`NavGroups` 常量保留在 Abstractions（向后兼容），但新增分组不再依赖字母序碰运气。

---

## 6. 模块迁移映射

### 6.1 类型对照

| 旧 | 新 |
|----|----|
| `NavContribution(route,title,icon,requires,scenarios,ComponentType,group,groupIcon,order)` | `PageContribution { Point="shell.page", Route, Title, ViewKey, Nav=new(){Icon,Group,GroupIcon}, Requires, Order }` |
| `RouteContribution(route,title,ComponentType,requires)` | `PageContribution { ..., Nav = null }` |
| `SettingsCardContribution(route,title,ComponentType,requires)` | `SettingsCardContribution { Point="shell.settings.card", Key=route, Title, ViewKey }` |
| `SlotContribution(name,ComponentType,requires,scenarios)` | `SlotContribution { Point=映射后的点 id, Id, ViewKey }` |
| `MessageRendererContribution` | **删除**（死代码） |
| `IUiContribution` 接口 | **删除** → `IUiContributor` |
| `IUiContributionRegistry.{NavItems,SettingsCards,Slots,MessageRenderers,Routes}` | `{All, Get<T>, Pages, FindPageByRoute, Diagnostics}` |
| `ModulePageRouteBinder`（15 条表 + enrich） | **整体删除**；15 条逐行转 `PageContribution`（§6.2） |
| `UiContributionVisibility` / `SessionShellVisibility.FilterSlotsForSession` / `ModuleRouteResolver` 内联判定 | `UiVisibility` + `UiScenarioGate` |
| `SessionShellVisibility.FilterNavForProcess`（死代码，零调用） | **删除** |
| `State/SlotRegistry.cs`（静态 legacy 回退） | **删除** |
| `UiContributionRegistry`（`samples/WebUI`） | 移到 `src/spine/Seeing.Agent.Core/Ui/` |
| `IGatewayChannelPlugin.ConfigFormComponentType`（死字段） | **删除**（无消费者，不改变任何 UI 行为） |

### 6.2 15 条路由逐行映射表（**v1 缺失，缺此必丢 5 条详情路由**）

| 原模板 | 原组件 | 模块 | 新 `PageContribution` | `Nav` |
|---|---|---|---|---|
| `/memory` | `MemoryPage` | memory | `Id="page.memory"` | ✅ Group=工作区, Order=50 |
| `/memory/graph` | `MemoryGraphPage` | memory | `Id="page.memory.graph"` | ❌ null |
| `/memory/detail/{*FilePath}` | `MemoryDetailPage` | memory | `Id="page.memory.detail"` | ❌ null |
| `/cron-jobs` | `CronJobsPage` | scheduler | `Id="page.cron"` | ✅ Group=控制中心, Order=20 |
| `/cron-jobs/{JobId}` | `JobDetailPage` | scheduler | `Id="page.cron.detail"` | ❌ null |
| `/heartbeat` | `HeartbeatPage` | scheduler | `Id="page.heartbeat"` | ✅ Group=控制中心, Order=30 |
| `/mcp` | `McpPage` | mcp | `Id="page.mcp"` | ✅ Group=工作区, Order=30 |
| `/acp` | `AcpPage` | acp | `Id="page.acp"` | ✅ Group=工作区, Order=40 |
| `/gateway` | `GatewayPage` | gateway | `Id="page.gateway"` | ✅ Group=设置, Order=40 |
| `/gateway-clients` | `GatewayClientsPage` | gateway | `Id="page.gateway.clients"` | ✅ Group=设置, Order=50 |
| `/skills` | `SkillsPage` | skills | `Id="page.skills"` | ✅ Group=工作区, Order=10 |
| `/skills/create` | `SkillCreatePage` | skills | `Id="page.skills.create"` | ❌ null |
| `/skills/{SkillName}` | `SkillDetailPage` | skills | `Id="page.skills.detail"` | ❌ null |
| `/tools` | `ToolsPage` | basic | `Id="page.tools"` | ✅ Group=工作区, Order=20 |
| `/reflection` | `ReflectionRunsPage` | reflection | `Id="page.reflection"` | ✅ Group=工作区, Order=60 |

**规则**：带 `{` 或为已知子路径者一律 `Nav = null`。`Group`/`GroupIcon`/`Order` 逐行照抄原表。

壳层（`__webui.shell__` → 真模块 `webui.shell`）：

| 原类型 | 模板 | 新 `ViewKey` | `Nav` |
|---|---|---|---|
| `RouteContribution` | `/` | `page.index` | ❌ |
| `RouteContribution` | `/home` | `page.home` | ❌ |
| `RouteContribution` | `/workspaces` | `page.workspaces` | ❌ |
| `RouteContribution` | `/session` | `page.session` | ❌ |
| `RouteContribution` | `/session/{SessionId}` | `page.session` | ❌ |
| `RouteContribution` | `/conference` | `page.conference` | ❌ |
| `RouteContribution` | `/conference/{SessionId}` | `page.conference` | ❌ |
| `NavContribution` | `/sessions` | `page.sessions` | ✅ 控制中心, 10 |
| `NavContribution` | `/agents` | `page.agents` | ✅ 设置, 10 |
| `NavContribution` | `/agent-config` | `page.agent-config` | ✅ 设置, 20 |
| `NavContribution` | `/models` | `page.models` | ✅ 设置, 30 |
| `NavContribution` | `/security` | `page.security` | ✅ 设置, 80 |
| `NavContribution` | `/settings` | `page.settings` | ✅ 设置, 90 |

设置卡 4 条：`/settings/boot`、`/settings/scenarios`、`/settings/scheduler`、`/settings/memory` → `Key` 同名，`ViewKey` = `card.boot` / `card.scenarios` / `card.scheduler` / `card.memory`。

### 6.3 插槽映射表（**v1 缺 `session_file_panel`**）

| 现有 `SlotName` | 新点 id | 挂载点 |
|---|---|---|
| `session_prompt` | `session.prompt` | `SessionWindow.razor:207` |
| `session_toolbar_actions` | `session.toolbar.actions` | `SessionToolbar.razor:146` |
| `message_toolbar` | `session.input.toolbar` | `PromptInput.razor:188` |
| `session_file_panel` | `session.file.panel` | `SessionWindow.razor:229` |

`FilePanelUiContribution` 改为真模块 `files.workspace` 的 `IUiContributor` 实现（`ModuleId` 由注册表填，不再自报）。

### 6.4 8 个模块的 `_ui` 清理（**v1 只列 2 个文件，实际 7 个调用点**）

| 位置 | 形式 |
|---|---|
| `SchedulerServiceCollectionExtensions.cs:103` | `services.AddSingleton<ISeeingModule>(sp => new SchedulerModule(..., ui))` |
| `SchedulerServiceCollectionExtensions.cs:214` | 同上 |
| `MemoryServiceExtensions.cs:75` | 同上 |
| `AcpServiceCollectionExtensions.cs:63` | 同上 |
| `GatewayServiceCollectionExtensions.cs:50` | 同上 |
| `GatewayServiceCollectionExtensions.cs:90` | 同上 |
| `FullCapabilityComposition.cs:133` | `new GatewayModule(sp.GetService<IUiContributionRegistry>())` |

另需删 `ModuleRegistrationExtensions.cs:76-83` 与 `ServiceCollectionExtensions.cs:206-211` 的反射构造分支（`ReflectionModule` 走 `TryAddEnumerable` + DI 构造，不受影响）。

### 6.5 壳层做成真模块

```csharp
// samples/Seeing.Agent.WebUI/Shell/WebUiShellModule.cs
public sealed class WebUiShellModule : ISeeingModule, IUiContributor
{
    public const string ModuleId = "webui.shell";
    public string Id => ModuleId;
    public IReadOnlyList<string> ProvidedTools => [];
    public IReadOnlyList<string> ProvidedSeams => [];
    public IReadOnlyList<string> DependsOn => [];
    public void ConfigureServices(IServiceCollection services) { }
    public Task ActivateAsync(IServiceProvider sp, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(IServiceProvider sp, CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<UiContribution> DescribeUi() => [ /* 13 页面 + 4 设置卡 */ ];
}
```

**为什么做成真模块**：v1 用 `uiRegistry.AddRange("__webui.shell__", [...])` 直接注册，构成事实上的第二条通道（`__webui.shell__` 不在 `IModuleCatalog`，`IsEnabled` 永不成立，永不撤销）。做成模块后走标准 Activate/Deactivate，冲突校验、`Changed`、诊断、守门测试全部复用，且必须登记进 Boot 能力集（否则不 Activate）。

---

## 7. 分期（每步可独立构建）

采用「最少步数 + 每步一次到位」（用户决策）。关键前提：**Step 0 先把旧类型改名，使新旧契约可共存编译**——否则 `SettingsCardContribution` / `SlotContribution` 同名同命名空间会 CS0101。

### Step 0：前置清理（编译通过、行为零变化）

| 动作 | 文件 |
|------|------|
| 旧类型改名 `Legacy*` | `UiContributionTypes.cs` → `LegacyNavContribution` / `LegacyRouteContribution` / `LegacySettingsCardContribution` / `LegacySlotContribution` |
| 删死类型 | `MessageRendererContribution` + `IUiContributionRegistry.MessageRenderers` + `UiContributionRegistry._messageRenderers` |
| 删死代码 | `SessionShellVisibility.FilterNavForProcess`、`State/SlotRegistry.cs`（及 `SlotHost` 的 legacy 回退） |
| 删 3 处 `@page` | `MemoryPage.razor:1`、`WorkspacesPage.razor:1`、`ReflectionRunsPage.razor:1` |
| 删死字段 | `IGatewayChannelPlugin.ConfigFormComponentType` + 5 处搬运（`GatewayChannelRegistry:164`、`GatewayChannelTypeInfo:18`、`GatewayClientConfigService:87,444`、`GatewayClientsPage:286`） |
| 改 30 处构造点 | `new NavContribution` → `new LegacyNavContribution`（含 8 处测试） |

**判据**：`dotnet build` 全绿；侧栏/设置/插槽/路由行为逐项手工核对无变化。

**删 `@page` 的影响已核实**：`WorkspacesPage` 由 `ModuleRouter.razor:24` 直接渲染、`WorkspaceGateTests.cs:42` 断言的是 `ModuleRouter` 源码含 `<WorkspacesPage`（不涉及 `@page`）；`MemoryPage` / `ReflectionRunsPage` 由路由表渲染。三处均无副作用。

### Step 1：契约 + Core 骨架（无消费方变更）

新增 `Ui/` 全部契约（§3）、`Core/Ui/` 全部实现（§4）、`AddSeeingUiPoints` 扩展。旧类型仍为 `Legacy*`，旧注册表不动。

**判据**：构建绿；新增契约测试与 Core 测试全绿（可见性规则、路由模板、诊断累积、幂等覆盖、`with` 派生类型保留）。

### Step 2：页面通道切换（一次到位）

| 动作 |
|------|
| 8 模块 + `webui.shell` 实现 `IUiContributor`，声明 15 + 13 条 `PageContribution`（§6.2） |
| 删 8 模块的 `_ui` 字段/构造参数/`Register`/`Unregister` + 7 处 extension 调用点（§6.4） |
| 删反射构造分支（2 文件） |
| 删 `ModulePageRouteBinder` 整个文件 |
| `ModuleRouter` 改用 `UiRouteTemplate.Resolve` + `WebUiViewResolver.Resolve` |
| `AppSidebar` 改用 §5.4 投影 |
| 删除 `LegacyNavContribution` / `LegacyRouteContribution` |
| 改测试：`ModuleRouteResolverCatchAllTests`、`JobDetailEditNavigationTests`（引用 `UiRouteTemplate`） |

**判据**：所有 28 个页面可达；未启用提示的 `FeatureTitle`/`ModuleId` 与 `SuggestScenarios()` 正常工作（验证声明期收集）；侧栏项与迁移前逐项一致（含 5 条详情路由不出现在侧栏）；`ModuleRouter` 的 `WorkspaceGate` / `EmptyLayout` / `/workspaces` 三条分支原样保留。

### Step 3：设置卡 + 插槽通道切换

| 动作 |
|------|
| `Settings.razor` 改用 `Get<SettingsCardContribution>` + 可见性 |
| `SlotHost` 改用 `Get<SlotContribution>` + `UiVisibility` + `UiScenarioGate` |
| `FilePanelUiContribution` → `files.workspace` 模块的 `IUiContributor` |
| 删除 `LegacySettingsCardContribution` / `LegacySlotContribution` / `IUiContribution` 接口 / `IUiContributionRegistry` 旧成员 |
| 删 `UiContributionVisibility`（整体由 `UiVisibility` 取代） |

**判据**：设置页 4 卡片正常；4 个插槽正常（含 `session.file.panel`）；会话场景收窄对插槽生效、对侧栏不生效（验证 `UiPoint.Scope`）。

### Step 4：文档

`docs/architecture/40-ui-extension-unified-architecture-release-notes.md`；更新 `README` / `02` / `04` / `05` / `06`（补默认接口方法豁免第 3 类）/ `AGENTS.md` / `CLAUDE.md`。

---

## 8. 测试

### 8.1 新增

| 套件 | 覆盖 |
|------|------|
| 契约测试 | `UiPoint` / `UiContribution` 必填项；`PageContribution.Nav = null` 即纯路由；`Requires` 空值隐含归属模块；**`with` 保留派生类型**（`(p with { Order = 1 }).Should().BeOfType<PageContribution>()`——若将来改成 `class` 会立刻爆） |
| Core / registry | 幂等覆盖（同模块重复 `AddRange` 不报冲突）；`PayloadKind` 不符抛 `InvalidOperationException`；`(point,route)` 冲突保留最小者 + 诊断；`MaxItems` 超限；catalog 未注册时 no-op；`DescribeUi()` 抛异常不外抛；fallback 分支也收集 |
| Core / visibility | 三条规则；`Scope=Process` 忽略会话条件；空串 `Requires` 跳过不抛；`Evaluate` 原因码 |
| Core / route | `{*CatchAll}` 多段捕获；字面量优先于 catch-all；`/cron-jobs/job_abc/edit` 不匹配 `/cron-jobs/{JobId}`（保留既有断言） |
| 守门 | 每个 `PageContribution.Route` 含 `{` 必须 `Nav == null`；每个 `SettingsCardContribution.Key` 全进程唯一；`src/` 下不得出现 `Legacy*` 类型（Step 3 后）；`Core/Ui/` 程序集内不得出现 `Microsoft.AspNetCore.*`；`WebUiPoints.All` 覆盖 `WebUiViews` 中出现的每个点；每个声明的点都有挂载点（双向守门，替代"Code Review 成对检查"） |
| 视图表 | 每个 `ViewKey` 可解析；组件实现 `IComponent`；重复键抛错 |

### 8.2 需改造的既有测试

| 文件 | 变更 |
|------|------|
| `UiContributionContractsTests.cs`（8 用例） | 6 个改写 / 2 个删除；`SettingsCardContribution` 用例改为断言**无** `ComponentType` 属性（把 D1 钉住）；`IUiContributionRegistry` 用例改断言新成员，**注意** `GetMethod("Get")` 对泛型返回 null，须用 `GetMethods().Single(m => m.Name == "Get" && m.IsGenericMethodDefinition)` |
| `ModuleRouteResolverCatchAllTests.cs` | 引用改 `UiRouteTemplate.TryMatch`；5 个 `{*CatchAll}` 断言语义不变 |
| `JobDetailEditNavigationTests.cs` | 改 `UiRouteTemplate`；"编辑子路由不存在"改为断言 `Registry.Pages` 无 `/cron-jobs/{JobId}/edit` |
| `McpModuleLifecycleTests.cs` | 删 `Mock<IUiContributionRegistry>`；改断言 `DescribeUi()` 返回单条 `PageContribution`（`Route=="/mcp"`, `ViewKey=="page.mcp"`）——**正好把 D4 的修复钉进测试** |
| `SidebarContractTests.cs`（11 用例） | 因 §5.4 投影保留成员名，**零改动** |

---

## 9. 明确不在本期范围

| 项 | 理由 |
|----|------|
| **工作台（Workbench）** | 零代码基线（`rg workbench` 在 `src`/`samples` 零命中，旧计划 0/89），**另立规格**。本规格不做任何工作台契约、扩展点或实现。 |
| 会话内容渲染 4 套机制统一（`IContentBlockRenderer` / `IMessageComponent` / `IToolCallSlotProvider` / `TimelineItemKind`） | 是"会话数据 → RenderFragment"的投影管线，不是布局贡献；且三套注册表冲突语义各异（同名替换 / 同 BlockType 替换 / 不去重按序全渲染）。硬塞会把管线退化成函数表。**只删死掉的 `MessageRendererContribution`。** |
| 运行时程序集加载 | 需 ALC 统一、依赖解析、静态资源端点、CSS 隔离、ViewKey 命名空间五件事，`IUiViewKeyCatalog` 一件都没解决。`ViewKey` 的价值是分层 + 可序列化诊断 + 启动期失败，**不靠运行时插件背书**。 |
| 懒激活 | 30 模块 / 28 贡献规模下收益为负；声明期收集（§2.3）已为其铺路。 |
| Tui 接入贡献系统 | Tui 是单屏聊天，无"页面"概念，只有 3 个真实扩展点；需先把 `TuiRenderer` 的段拼装从硬编码改为按点取贡献。`IUiViewKeyCatalog` 的存在不构成承诺。 |
| i18n 资源键（`TitleKey` 替代 `Title`） | 多语言需求未出现；`PageContribution` 用 init 属性，加可选字段不破坏。 |
| 权限级可见性 | 全仓 `samples/` 下无认证/授权接线；`UiConditionKind.Capability` 已为能力门预留。 |

---

## 10. 已知边界与债务

| 项 | 说明 |
|----|------|
| **catch-all 路由需宿主额外映射** | `WebUiHost.cs:301` 的 `MapFallbackToPage("/memory/detail/{*FilePath}", "/_Host")` **必须保留**：该路径含文件扩展名，`MapFallbackToPage("/_Host")` 会被 ASP.NET 当作静态文件跳过。因此"单一通道"在 ASP.NET 路由层不成立——`PageContribution` 单独不足以保证可达。 |
| 页面标题仍为编译期中文字面量 | 能力包不引用 `UiText`（依赖方向不允许），术语字典的"单一出口"在 UI 贡献层被绕开。 |
| `UiPoint.Id` 只增不改 | 无版本号机制；进程内编译期组装，版本错配在编译期爆。写入 04 规范。 |
| `NavGroups` 三个中文常量保留在 Abstractions | 向后兼容；新分组走 `NavPlacement.GroupOrder`。 |
| 分组顺序仍含兜底字母序 | `GroupOrder` 相同或未设置时按组名字母序（中文名不理想，待 i18n 一并解决）。 |
| `Diagnostics` 不持久化 | 进程内内存累积；重启清空。 |
| Step 0 的 `Legacy*` 别名期 | Step 2/3 之间两套类型并存；`Legacy*` 不得在新代码中出现（守门测试扫描）。 |

---

## 11. 附：六维审查裁决表

| 审查维度 | 采纳 | 否决 | 理由 |
|---|---|---|---|
| 分层依赖 | registry 落 Core、`UiVisibility` 落 Core、`IUiContributor` 独立、`IUiPointCatalog` 经 `GetService` 惰性取（不加 `ModuleLifecycleManager` 构造参数，保住 12 处测试 `new`） | 「`UiVisibility` 放 Abstractions」 | 它读进程状态 + 依赖 Core 的 `IScenarioCatalog` |
| 可实现性 | Step 0 重命名 + 无旧类型并存；补 `UiRouteMatch`/`UiRouteTemplate`；补 15 条路由映射表；补 `session_file_panel`；补 7 处 extension 调用点 | 「P0 保留旧类型标 `[Obsolete]`」 | CS0101 不可编译 |
| 完整性 | `session_file_panel` 入表；`@page` 三处清理；`DescribeUi()` 异常隔离；诊断载体；壳层做成真模块 | 「一并统一 4 套内容渲染机制」 | 投影管线 ≠ 布局贡献，成本不对等 |
| 灵活性 | `Conditions` + `UiVisibilityContext`；`UiPayloadKind` 枚举；`UiPoint.Scope`；诊断原因码 | 「`IsVisible` 返回类型改 `Decision`」 | 消费者不该处理原因码，另给 `Evaluate` |
| 工作台归位 | `IWorkbenchSessionStateStore`/`WorkbenchInstanceKey`/`WorkbenchViewReport` 等**全部移至工作台规格** | 「本期保留部分工作台类型」 | 零代码基线，保留即制造幽灵契约 |
| 行业对照 | 声明期收集（`All` 覆盖未启用模块）；双向点↔挂载守门；诊断可观测 | 「现在就做懒激活」 | 规模不匹配，收益为负 |

**根因判断采纳**：`Type` 不是唯一根因；真正的因果链是「贡献 = 激活期构造的对象图」→ 分层冲突（a）+ 生命周期视野外（b）+ 注册表不做作用域化（c）。因此最小必要修正是**声明期 + 生命周期驱动 + 事实/判定分离**三件，`ViewKey` 是必要不充分。
