# UI 扩展统一架构设计（UI Extension Envelope）

**日期:** 2026-11-10（v4：经两轮共 8 维审查，契约已落地验证）
**状态:** 契约与核心实现已验证（118 项测试全绿）；待评审后进入实施
**验证代码:** `tests/probe/Seeing.Agent.UiEnvelopeProbe/`（+ `.Tests`，118 测试全绿）
**取代:** v1、v2、v3（错误清单见 §0）
**分支:** `refactor/ui-extension-envelope`

---

## 0. 历史版本错误清单

### v1 的 5 个错误

| # | 错误 | 更正 |
|---|------|------|
| E1 | 称「三套可见性实现」 | **大部分已收敛**：`SessionShellVisibility.cs:112-113` 与 `ModuleRouteResolver.cs:205-206` 均已转调 `UiContributionVisibility.AreRequirementsEnabled`。真正分叉只剩 `IsVisible` 一处 |
| E2 | 称「`Type` 让运行时插件在契约层被否决」 | **不成立**。`System.Type` 不产生程序集引用；运行时插件自带组件。`ViewKey` 的真实价值只有三条（§2.3） |
| E3 | 讨论 `ModuleId` 如何「贴」进 record | **伪问题**。归属由注册表条目持有 |
| E4 | §7「工作台归位」按既有基线迁移 | **基线不存在**（`rg workbench` 零命中，旧计划 0/89）。工作台另立规格 |
| E5 | 「P0 保留旧类型标 `[Obsolete]`」 | **不可编译**：`SettingsCardContribution`/`SlotContribution` 同名同命名空间 → CS0101 |

### v2 的 4 个错误

| # | 错误 | 更正 |
|---|------|------|
| E6 | `webui.shell` 做成真 `ISeeingModule` | **会锁死用户**。`FullCapabilityCompositionTests.cs:36,58` 钉死 `Enabled == Full`；`WebUI.csproj:25` 已引用 HostComposition（反向引用成环）。用户改 Boot 能力集后 `/settings` 消失，无法自救。改为 `IUiHostContributor` |
| E7 | `Conditions` 单值 AND 取代 `Scenarios` | **语义倒退**（白名单是 OR）。**且 `Scenarios` 全仓零使用**，应直接删除 |
| E8 | `Requires` 空 = 隐含归属模块 | **前提错误**。实测 10 处模块贡献的 `Requires` 全部等于自身模块 id（§1.5） |
| E9 | 声明期收集的动机是「保住建议工作模式的提示」 | **该提示今天就不存在**（`BuiltInScenarios.All` 只有 `full`，`Modules = ["*"]`，`NotEnabledFragment.razor:39-47` 恒不匹配）。真实收益是「未启用时仍能拿到 `FeatureTitle`/`ModuleId`」 |

### v3 的 8 个错误（两轮审查发现）

| # | 错误 | 更正 |
|---|------|------|
| E10 | **规格 §4.3 声称 `UiScenarioGate` 已实现并测试，实际零代码** | 已补实现 + 7 项测试 |
| E11 | 多元素 `Requires` 的会话门从 **AND 变 OR** | 旧 `SessionShellVisibility.cs:84-95` 是 `foreach { if (!contains) return false; }`。已改回 AND |
| E12 | 空门集从「全部隐藏」变「不收敛」（fail-open） | 触发路径：用户清空工作模式模块列表。已区分：`null` = 不收敛，空集 = 全隐藏 |
| E13 | catalog 缺失时壳层也隐藏（旧实现空 `Requires` 先返回 true） | 旧 `UiContributionVisibility.cs:85-87`。壳层若隐藏，用户失去 `/settings` 无法自救。已改为空 `Requires` 优先放行 |
| E14 | 三处 `DynamicComponent` 无 `ErrorBoundary` | `ModuleRouter.razor:30`、`Settings.razor:142`、`SlotHost.razor:21`。Blazor Server 渲染期未捕获异常会终止 circuit |
| E15 | 模块贡献空 `Requires` 未拦截 | 会变成进程级 → 模块停用后页面仍在但工具已卸载（「半残但看起来正常」）。已拒收 |
| E16 | 跨 owner `(point, id)` 重复未检测 | `Settings.razor:141` 的 `@key` 重复会崩渲染。已加 `CrossOwnerDuplicate` |
| E17 | 停用模块的页会抢赢启用模块的页 | 旧实现停用模块路由不在表内。已给 `Resolve` 加可见性谓词 |

**v2 另有 9 处硬编译错误**（`IsVisible` 双签名、`RecordInvalidDeclaration` 不在接口、`_log`/`_ui` 非字段、`candidates`/`HasShorterPrefix` 未定义、`UiPayloadKindMap` 无签名、`Entry.IsActive` 不存在、`UiRouteMatch` 缺归属、`FindPageByRoute` 语义未定、`NavView.GroupOrder` 缺失）——全部由「先写代码再写文档」消除。

---

## 1. 现状事实（核实过的基线）

### 1.1 六套并行机制

| # | 机制 | 位置 | 状态 |
|---|------|------|------|
| 1 | Nav / Route | `UiContributionTypes.cs:15,80` | 活；两类型共用一表 + 转换代码（`UiContributionRegistry.cs:105-114` 填 `Icon: ""`） |
| 2 | SettingsCard | `:45` | 活 |
| 3 | Slot | `:58` + `SlotHost.razor` | **仅 1 个使用者**（`session_file_panel`） |
| 4 | MessageRenderer | `:69` | **死代码**（零消费者） |
| 5 | 会话内容渲染 | `IContentBlockRenderer` + `IMessageComponent` + `IToolCallSlotProvider` + `TimelineItemKind` | 活；**实为 4 套**，冲突语义各异 |
| 6 | 渠道插件配置表单 | `IGatewayChannelPlugin.cs:26` | **死字段**（只被搬运，从不传给 `DynamicComponent`） |

### 1.2 规模基线

| 项 | 数量 |
|---|---|
| `ISeeingModule` 实现 | 35（仅 8 个需要 UI，23%） |
| 显式 `new XxxModule(uiRegistry)` 的 extension 调用点 | 7 |
| 旧贡献构造点（src+samples / tests） | 30 / 8 |
| `KnownModuleRoutes` 表项 | 15（其中 **5 条仅由 enrich 产生**） |
| 插槽挂载点 | 4（含易漏的 `session_file_panel`） |
| 仍带 `@page` 的模块页 | 3（与 `05-extension-guide.md:122` 冲突，路由双份注册） |

### 1.3 根因（一条因果链）

```
根因：贡献 = 激活期构造的、携带 Type 的 C# 对象图
 ├─(a) 能力包不能引 samples/WebUI 的组件 ⇒ ModulePageRouteBinder 15 条表 + 覆盖式 enrich
 ├─(b) 注册表住 samples，Core 生命周期看不见 ⇒ 双身份 + 手写对称 + 反射构造探测
 └─(c) 注册表只给未过滤扁平集合 ⇒ 消费者各自过滤
```

### 1.4 真实故障（设计动机的实证）

`ReflectionModule.cs:136-138` 注释原文：

> UI 贡献必须与 Activate 的 Register 对称注销，否则 Deactivate → Activate 之后侧栏「复盘记录」入口永久消失

### 1.5 关键实测：`Requires` 是归属声明，不是依赖声明

| 模块 | 贡献的 `Requires` |
|---|---|
| Skills | `["skills"]` |
| Mcp | `["mcp"]` |
| Reflection | `[ModuleId]`（自身常量） |
| Scheduler | `["scheduler"]` ×2 |
| Memory | `["memory"]` |
| Acp | `["acp"]` |
| Basic | `["basic"]` |
| Gateway | `["gateway"]` ×2 |

壳层例外：`/settings/boot`、`/settings/scenarios` 为 `[]`；`/settings/scheduler` 为 `["scheduler"]`、`/settings/memory` 为 `["memory"]`（壳层代模块声明）。

**结论**：`Requires` = 归属模块列表，空 = 进程级（不受模块启停影响）。无需第二套 `ModuleId` 概念。

### 1.6 关键实测：`Scenarios` 全仓零使用

```
rg "Scenarios:" → 仅 UiContributionTypes.cs:20,62（类型声明）
```

**结论**：删除。v1/v2 为它设计的 `Conditions` 集合、`UiVisibilityContext` 六字段、OR/AND 语义迁移全属过度设计。

---

## 2. 架构原则

### 2.1 契约只放数据与端口；判定落脊柱；视图落宿主

| 层 | 内容 |
|---|---|
| **Abstractions** | `UiPoint` / `UiPayloadKind` / `UiContribution` 家族 / `IUiPointCatalog` / `IUiContributor` / `IUiHostContributor` / `IUiContributionRegistry` / `IUiViewKeyCatalog` / `IUiContributionValidator` / `UiRouteMatch` |
| **Core** | `UiContributionRegistry`、`UiVisibility`、`UiScenarioGate`、`UiRouteTemplate`、`UiPayloadKindMap`、`UiPointCatalog` |
| **Host** | `WebUiPoints`（声明扩展点）、`WebUiViewResolver`（`ViewKey → Type`）、挂载组件 |

**为什么判定与解析落 Core**：

- `UiVisibility` 读 `IModuleCatalog`（进程状态做准入决策），不属于 06:191 的「无状态纯函数」豁免。
- `UiScenarioGate` 依赖 `IScenarioCatalog`（`ScenarioCatalog.cs:9`）与 `BuiltInScenarios`（`BuiltInScenarios.cs:7`），**两者都在 Core**。放 Abstractions 会把逻辑劈两半，让 Tui 再写一份。
- `UiRouteTemplate` 是纯字符串逻辑，但全部宿主都引用 Core，放 Core 不扩契约层豁免清单（06 已记录「上提 PathSafety 到 Abstractions 已决策放弃」）。
- Core 已有三个同构先例：`ModuleCatalog`、`DynamicToolContributorRegistry`、`SurfaceRegistry`。

**Core 实现的三条硬约束**（不变量测试守门）：

1. `src/spine/Seeing.Agent.Core/Ui/` 下不得出现 `Microsoft.AspNetCore.*` / `IComponent` / `RenderFragment`。
2. 视图解析实现不得进 Core。
3. `IUiPointCatalog` 未注册时注册表 **no-op**（不收集、不诊断、不打日志）。

### 2.2 注册表只做「事实」

`All` / `Get<T>` / `Pages` 返回事实集合（含未启用模块的声明），**不做可见性**。调用方用 `UiVisibility.IsVisible` 显式判定。

理由：会话级可见性需要会话上下文，注册表拿不到（v1/v2 的核心缺陷）。「单一实现」≠「唯一调用点」——这是项目既有范式（`DynamicToolContributorRegistry` + `ModuleCatalog.IsEnabled`）。

### 2.3 声明早于激活

收集点从「Activate 之后」移到「结算完成后扫描全部 `Available` 模块」。真实收益：模块未启用时仍能拿到 `FeatureTitle` / `ModuleId`，使直链能给出正确的「未启用」提示（`ModuleRouteResolver.cs:66-77` 的既有行为）。

### 2.4 可选能力用独立接口

`IUiContributor` 独立于 `ISeeingModule`（35 个模块只有 8 个需要）。项目已有三个同范式先例：`IDynamicToolContributor`、`IExecutionOutcomeObserver`、`IModuleHostedService`。

`DescribeUi()` **是抽象成员而非默认接口方法**：单成员接口的 DIM 等于 marker，且 Moq 的 Castle proxy 会实现它返回 `default`（不走默认实现）→ NRE。Abstractions 现有 3 处 DIM 先例不适用于此。

### 2.5 宿主壳层不是模块

壳层页面（`/sessions`、`/settings`）与设置卡必须**始终可达**：绑到模块上会导致用户切换 Boot 能力集后失去进入设置页的入口。用 `IUiHostContributor`（`OwnerId` 以 `host.` 开头），在 `Build()` 后、`InitializeSeeingAsync()` 前收集，与工作区是否选定无关。

壳层贡献的 `Requires` 为空 = 进程级 = 不受任何模块门控。

---

## 3. 契约（已验证可编译）

> **验证代码**：`tests/probe/Seeing.Agent.UiEnvelopeProbe/EnvelopeContracts.cs`、`EnvelopeImplementations.cs`
> **测试**：`tests/probe/Seeing.Agent.UiEnvelopeProbe.Tests/`（48 项，全绿）
> 验证项目用 `*Probe` 后缀规避与旧类型同名（`SettingsCardContribution` / `SlotContribution`）；实施时去后缀。

### 3.1 扩展点

```csharp
public enum UiPayloadKind { Page, SettingsCard, Slot }

public enum UiVisibilityScope
{
    /// <summary>进程级：只看模块启用集（侧栏 / 设置卡 / 页面）。</summary>
    Process,
    /// <summary>会话级：额外随会话工作模式收窄（会话插槽）。</summary>
    Session
}

public sealed record UiPoint
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required UiPayloadKind PayloadKind { get; init; }
    public UiVisibilityScope Scope { get; init; } = UiVisibilityScope.Process;
    public int MaxItems { get; init; } = int.MaxValue;
}

public interface IUiPointCatalog
{
    IReadOnlyList<UiPoint> Points { get; }
    UiPoint? Find(string pointId);
    bool Supports(string pointId);
}
```

### 3.2 信封

```csharp
public abstract record UiContribution
{
    public required string Point { get; init; }
    public required string Id { get; init; }

    /// <summary>归属模块 id 列表；空 = 进程级（不受模块启停影响）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    public int Order { get; init; }
}
```

### 3.3 三类载荷

```csharp
public sealed record PageContribution : UiContribution
{
    /// <summary>路由模板；占位名 <c>{name}</c> 逐字成为参数键（须等于页面 [Parameter] 名）。</summary>
    public required string Route { get; init; }
    public required string Title { get; init; }
    public required string ViewKey { get; init; }
    /// <summary>侧栏位；null = 纯路由不进侧栏。</summary>
    public NavPlacement? Nav { get; init; }
}

public sealed record NavPlacement
{
    public required string Icon { get; init; }      // 中性 token，宿主映射
    public string? Group { get; init; }
    public string? GroupIcon { get; init; }
    public int GroupOrder { get; init; }
}

public sealed record SettingsCardContribution : UiContribution
{
    public required string Title { get; init; }
    public required string ViewKey { get; init; }
}

public sealed record SlotContribution : UiContribution
{
    public required string ViewKey { get; init; }
    public string? DisplayName { get; init; }
}
```

### 3.4 注册表

```csharp
public readonly record struct UiContributionEntry(string OwnerId, UiContribution Contribution);

public interface IUiContributionRegistry
{
    void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions);  // 幂等：整批替换
    void RemoveByOwner(string ownerId);
    void ReportInvalidDeclaration(string ownerId, string message);

    IReadOnlyList<UiContributionEntry> All { get; }
    IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution;
    IReadOnlyList<PageContribution> Pages { get; }
    IReadOnlyList<UiContributionDiagnostic> Diagnostics { get; }

    event EventHandler? Changed;
}
```

**已实现并验证的行为**：

| 情况 | 行为 | 测试 |
|---|---|---|
| `IUiPointCatalog` 未注册 | no-op（不收集、不诊断） | ✅ |
| 未声明的扩展点 | 拒收 + `UnsupportedPoint` | ✅ |
| 载荷种类不符 | 拒收 + `PayloadMismatch` | ✅ |
| 同批 id 重复 | 拒收后者 + `DuplicateId` | ✅ |
| 同批路由重复 | 拒收后者 + `DuplicateRoute` | ✅ |
| 超 `MaxItems` | 拒收 + `ExceededMaxItems` | ✅ |
| 校验器拒绝 | 拒收 + `ValidatorRejected` | ✅ |
| 校验器抛异常 | 拒收（不外抛） | ✅ |
| 元素为 null | 跳过 + `InvalidDeclaration` | ✅ |
| 同 owner 重复 `AddRange` | 整批替换（幂等） | ✅ |
| 诊断重复 | 按键替换，不累积 | ✅ |
| `Changed` 订阅者抛异常 | 隔离，不外抛 | ✅ |

### 3.5 贡献源

```csharp
/// <summary>模块贡献：结算后收集。DescribeUi 必须纯数据、无副作用。</summary>
public interface IUiContributor
{
    IReadOnlyList<UiContribution> DescribeUi();
}

/// <summary>宿主贡献：Build 后收集，不受模块启停影响。</summary>
public interface IUiHostContributor
{
    /// <summary>必须以 host. 开头，且不得与任何模块 id 相同。</summary>
    string OwnerId { get; }
    IReadOnlyList<UiContribution> DescribeUi();
}
```

### 3.6 视图与路由

```csharp
/// <summary>只答有没有；各宿主自定义强类型解析。</summary>
public interface IUiViewKeyCatalog
{
    bool CanResolve(string viewKey);
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>宿主侧收集期校验。</summary>
public interface IUiContributionValidator
{
    string? Validate(UiContribution contribution, UiPoint point);
}

public sealed record UiRouteMatch(PageContribution Page, IReadOnlyDictionary<string, object?> Parameters);
```

---

## 4. 判定与匹配（Core，已验证）

### 4.1 可见性

```csharp
public sealed record UiVisibilityContext(
    IModuleCatalog? Catalog,
    IReadOnlySet<string>? ScenarioModuleGate = null)
{
    public static UiVisibilityContext Process(IModuleCatalog? catalog) => new(catalog);
}

public static class UiVisibility
{
    public static bool IsVisible(UiContribution c, UiPoint point, UiVisibilityContext ctx);
    public static UiVisibilityDecision Evaluate(UiContribution c, UiPoint point, UiVisibilityContext ctx);
}
```

**规则（按序）**：

1. `Catalog is null` → 不可见（`CatalogUnavailable`，fail-closed）。
2. `Requires` 含空白项 → 不可见（`InvalidRequire`）——`ModuleCatalog.IsEnabled` 对空白会抛（`ModuleCatalog.cs:60-66`），必须在此拦住。
3. `Requires` 中任一模块未启用 → 不可见（`ModuleDisabled`，`Detail` 为模块 id）。
4. 仅当 `point.Scope == Session` 且门集非空时，要求归属模块落在门集内（`ScenarioMismatch`）。**空 `Requires`（进程级贡献）不受门集收窄。**

**验证覆盖**：11 项测试，含「Process 作用域忽略门集」「Session 作用域空 Require 不被收窄」「空串 Require 不抛」等。

### 4.2 路由模板

```csharp
public static class UiRouteTemplate
{
    public static string NormalizePath(string? path);
    public static bool TryMatch(string template, string path,
        out IReadOnlyDictionary<string, object?> parameters, out int score);
    public static UiRouteMatch? Resolve(string path, IEnumerable<UiContributionEntry> entries);
}
```

**Catch-all 语义（与既有测试逐条对齐）**：不解码（`%2F` 原样）、无尾段捕获空串、剩余段用 `/` 连接。

**同分 tiebreak**：路由模板序数序 → 归属模块序数序（与枚举顺序无关）。

**验证覆盖**：19 项测试，含 15 条真实路由逐条命中、catch-all 三条语义、字面量优先、参数不吃多余段、确定性。

### 4.3 场景门

```csharp
public static class UiScenarioGate
{
    /// <summary>null 表示不收窄（未指定，或声明了通配）。</summary>
    public static IReadOnlySet<string>? Resolve(string? effectiveScenario, IScenarioCatalog? catalog);
}
```

---

## 5. 宿主实现（WebUI）

### 5.1 扩展点声明

```csharp
public static class WebUiPoints
{
    public static readonly UiPoint ShellPage = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    public static readonly UiPoint ShellSettingsCard = new()
    { Id = "shell.settings.card", DisplayName = "设置卡片", PayloadKind = UiPayloadKind.SettingsCard };

    public static readonly UiPoint SessionPrompt = new()
    { Id = "session.prompt", DisplayName = "输入区上方",
      PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionToolbarActions = new()
    { Id = "session.toolbar.actions", DisplayName = "会话工具栏右侧",
      PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionInputToolbar = new()
    { Id = "session.input.toolbar", DisplayName = "输入框工具栏",
      PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session };

    public static readonly UiPoint SessionFilePanel = new()
    { Id = "session.file.panel", DisplayName = "会话右侧面板",
      PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session };

    public static IReadOnlyList<UiPoint> All { get; } =
        [ShellPage, ShellSettingsCard, SessionPrompt, SessionToolbarActions, SessionInputToolbar, SessionFilePanel];
}
```

**注册时序**：`AddSeeingUiPoints(WebUiPoints.All)`、`WebUiViewResolver`（全部视图已 `Register`）、`IUiContributionValidator` 三者都必须在 **`Build()` 之前**完成。否则 validator 会对每条贡献返回「视图未装配」→ 空 UI。

### 5.2 视图表

```csharp
public sealed class WebUiViewResolver : IUiViewKeyCatalog
{
    public void Register(string viewKey, Type componentType);  // 校验 IComponent；重复键抛错
    public Type? Resolve(string viewKey);                      // WebUI 专用强类型入口
    public bool CanResolve(string viewKey);
    public IReadOnlyCollection<string> Keys { get; }
}
```

### 5.3 视图缺失的降级

| 消费点 | 行为 | 级别 |
|---|---|---|
| `ModuleRouter` | 渲染「功能未装配」占位，文案区分「模块未启用」/「视图缺失」 | Error |
| `AppSidebar` | 不渲染该项（有入口点进去白屏比没入口更糟） | Warning |
| `Settings.razor` | 跳过该 TabPane | Warning |
| `SlotHost` | 不渲染 | Debug |

实际由 `IUiContributionValidator` 在收集期拒收（进 `Diagnostics`），运行期正常走不到 null。此表是校验器缺失时的兜底。

### 5.4 侧栏投影（保住既有视觉契约）

```csharp
private sealed record NavView(string Key, string Route, string Title, string Icon,
                              string? Group, string? GroupIcon, int Order, int GroupOrder);

private void RefreshNav()
{
    var ctx = UiVisibilityContext.Process(Catalog);
    var point = WebUiPoints.ShellPage;

    // 规则①：进程级可见（未启用模块的声明在这里滤掉）
    var candidates = Registry.Get<PageContribution>(point.Id)
        .Where(p => p.Nav is not null)
        .Where(p => UiVisibility.IsVisible(p, point, ctx))
        .Where(p => p.Route.IndexOf('{') < 0)      // 规则②：参数化路由不进侧栏
        .ToList();

    var routes = candidates.Select(p => p.Route).ToList();

    _visibleNav = candidates
        .Where(p => !HasShorterPrefix(p.Route, routes))   // 规则③：更短前缀隐藏子路径
        .Select(p => new NavView(
            Key: NavKey(p.Route), Route: p.Route, Title: p.Title,
            Icon: WebUiIconMap.Resolve(p.Nav!.Icon),
            Group: p.Nav.Group, GroupIcon: WebUiIconMap.ResolveOptional(p.Nav.GroupIcon),
            Order: p.Order, GroupOrder: p.Nav.GroupOrder))
        .OrderBy(v => v.Order).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();
    // …分组：按 GroupOrder 排序，取代硬编码 preferred 数组
}
```

**三条规则全部保留**：`UiContributionVisibility.cs:41`（参数化路由）、`:47`（更短前缀）、`UiVisibility`（模块启用）。投影保留 `Icon`/`Group`/`Order`/`Route` 成员名 → `SidebarContractTests` 的源码文本断言不变。

### 5.5 图标 token 映射

`NavPlacement.Icon` 是中性 token（`database` / `team` / `tool`），由 `WebUiIconMap.Resolve(token)` 映射到 AntDesign 名字。现状 `AppSidebar.razor:86` 直接把字符串喂给 `Icon`，靠「恰好同名」工作；显式映射表消除这个隐式耦合。

---

## 6. 迁移映射

### 6.1 15 条路由逐行映射（缺此必丢 5 条详情路由）

| 原模板 | 新 `Id` | `Nav` |
|---|---|---|
| `/memory` | `page.memory` | ✅ 工作区, 50 |
| `/memory/graph` | `page.memory.graph` | ❌ null |
| `/memory/detail/{*FilePath}` | `page.memory.detail` | ❌ null |
| `/cron-jobs` | `page.cron` | ✅ 控制中心, 20 |
| `/cron-jobs/{JobId}` | `page.cron.detail` | ❌ null |
| `/heartbeat` | `page.heartbeat` | ✅ 控制中心, 30 |
| `/mcp` | `page.mcp` | ✅ 工作区, 30 |
| `/acp` | `page.acp` | ✅ 工作区, 40 |
| `/gateway` | `page.gateway` | ✅ 设置, 40 |
| `/gateway-clients` | `page.gateway.clients` | ✅ 设置, 50 |
| `/skills` | `page.skills` | ✅ 工作区, 10 |
| `/skills/create` | `page.skills.create` | ❌ null |
| `/skills/{SkillName}` | `page.skills.detail` | ❌ null |
| `/tools` | `page.tools` | ✅ 工作区, 20 |
| `/reflection` | `page.reflection` | ✅ 工作区, 60 |

**规则**：带 `{` 或为已知子路径者一律 `Nav = null`。

壳层 13 条（`host.webui`，`Requires = []`）+ 设置卡 4 条，见验证代码注释。

### 6.2 插槽映射

| 现有 `SlotName` | 新点 id | 挂载点 |
|---|---|---|
| `session_prompt` | `session.prompt` | `SessionWindow.razor:207` |
| `session_toolbar_actions` | `session.toolbar.actions` | `SessionToolbar.razor:146` |
| `message_toolbar` | `session.input.toolbar` | `PromptInput.razor:188` |
| `session_file_panel` | `session.file.panel` | `SessionWindow.razor:229` |

### 6.3 模块 `_ui` 清理点（7 处，非 2 处）

`SchedulerServiceCollectionExtensions.cs:103,214`、`MemoryServiceExtensions.cs:75`、`AcpServiceCollectionExtensions.cs:63`、`GatewayServiceCollectionExtensions.cs:50,90`、`FullCapabilityComposition.cs:133`。

另删 `ModuleRegistrationExtensions.cs:76-83` 与 `ServiceCollectionExtensions.cs:206-211` 的反射构造分支。

### 6.4 删除清单

| 项 | 位置 |
|---|---|
| `MessageRendererContribution` + `MessageRenderers` 属性 + 字段 + `RebuildUnlocked` 4 处 | 7 处（含 `IUiContribution.cs:13-15` 的 `<see cref>`） |
| `SessionShellVisibility.FilterNavForProcess` | 死代码，零调用 |
| `State/SlotRegistry.cs` + `SlotHost` 的 legacy 回退 | 死路径 |
| 3 处 `@page` | `MemoryPage.razor:1`、`WorkspacesPage.razor:1`、`ReflectionRunsPage.razor:1` |
| `IGatewayChannelPlugin.ConfigFormComponentType` + 6 处搬运 | 死字段（无消费者） |
| `Scenarios` 字段（`NavContribution` / `SlotContribution`） | 零使用 |

---

## 7. 分期

### Step 0：前置清理（编译通过、行为零变化）

- 只重命名**真冲突**的两个类型：`SettingsCardContribution` → `LegacySettingsCardContribution`、`SlotContribution` → `LegacySlotContribution`（各 5 处引用）。`NavContribution` / `RouteContribution` 名字不冲突，**不动**。
- 删 `MessageRendererContribution`（7 处）+ `Scenarios` 字段 + 4 项死代码 + 3 处 `@page` + `ConfigFormComponentType`。
- 判据：`dotnet build` 全绿；侧栏/设置/插槽/路由逐项手工核对无变化。

### Step 1：契约 + Core（无消费方变更）

- 落地 §3/§4 全部契约与实现（**已验证的代码，去 `Probe` 后缀**），替换验证项目。
- 判据：48 项测试迁入正式测试项目并全绿。

### Step 2：页面通道切换

- 8 模块 + `WebUiHostContributor` 实现贡献端口，声明 15 + 13 条 `PageContribution`。
- 删 7 处 extension 调用点 + 2 处反射分支。
- 删 `ModulePageRouteBinder`。
- `ModuleRouter` 改用 `UiRouteTemplate.Resolve` + `WebUiViewResolver.Resolve`（保留 `WorkspaceGate` 三条分支）。
- `AppSidebar` 改用 §5.4 投影。
- 判据：28 个页面可达；未启用提示的 `FeatureTitle`/`ModuleId` 正常；侧栏项与迁移前逐项一致（5 条详情路由不出现在侧栏）。

### Step 3：设置卡 + 插槽切换

- `Settings.razor` / `SlotHost` 改用新端口 + `UiVisibility` + `UiScenarioGate`。
- `FilePanelUiContribution` → `files.workspace` 的 `IUiContributor`。
- 删余下旧类型与 `IUiContribution` 接口。
- 判据：4 卡片正常；4 插槽正常；会话场景收窄对插槽生效、对侧栏不生效。

### Step 4：文档

`docs/architecture/40-ui-extension-unified-architecture-release-notes.md`；更新 `README` / `02` / `04` / `05` / `06` / `AGENTS.md` / `CLAUDE.md`。

---

## 8. 明确不在本期范围

### 8.1 宿主保留区清单（业务审查 A 的结论）

下列 UI 入口**装不进** Page/SettingsCard/Slot 三类载荷，硬塞会把信封撑成万能基类。逐项写明「为什么不做」，避免每个新模块作者重复询问。

| # | 入口 | 位置 | 为什么不做 | 后续 |
|---|---|---|---|---|
| 1 | 侧栏「聊天」 | `AppSidebar.razor:61` | 无静态 Route（路径按 `AppState.CurrentSessionId` 动态算，`:414-418`）；选中键是固定的 `chat` 而非路由。`PageContribution.Route` 语义不符 | 保持宿主硬编码；`SidebarProjector.SelectedKeys` 已把 `""/home/session → chat` 特例纳入并测试 |
| 2 | 侧栏「审批中心」 | `AppSidebar.razor:73-82` | 需要「实时计数徽标 + 打开抽屉」，三类载荷都表达不了（`Badge Count=@PermissionView.TotalCount` 在 `IconTemplate` 里，点击走 `UiOverlayService`） | 保持宿主硬编码 |
| 3 | 侧栏底部工具条 | `AppSidebar.razor:117-146` | 工作区切换 / 关闭 / 折叠，纯宿主 chrome | 保持 |
| 4 | 首页 dashboard 三栏 | `Index.razor:427-458`（6 能力格）、`:193-350`（调度器）、`:383-401`（MCP） | 卡片需要「数据 + 排版占位 + 点击行为」，`SlotContribution` 只有 ViewKey | 保持硬编码；**但可见性应统一**（见 §8.2） |
| 5 | 斜杠命令面板 | `ICommandRegistry.cs:7-38` | 平行的第二套「可发现入口」，其可见性走「激活期增删」而非 `Requires`；`CommandMetadata` 已自带 `Category`/`SortOrder`/`IsHidden`。语义与贡献的「声明期收集」相反 | 独立域，不合并 |
| 6 | 通知 / 重试横幅 | `SessionWindow.razor:120-140`（三套手写样式） | 无真实模块需求；现在设计会是「为死字段做设计」 | 有真实需求时再加 `session.banner` 点 |
| 7 | Token 预算状态栏 | `PromptInput.razor:192-199`；TUI 同概念 `TuiChatEngine.cs:1853` | 跨宿主「状态段」抽象，本期不做 Tui 接入 | 宿主保留区 |
| 8 | `SessionWindow` 的 `ToolbarLeftActions` / `ToolbarRightActions` / `Header` | `SessionWindow.razor:344-346` | 是「页面 → 窗口」的注入（无 `Requires`、无门控），与「模块 → 窗口」的 slot 不同。`Header` 全仓零使用（死参数） | `*Actions` 保留（页面是宿主）；`Header` 随 Step 0 删除 |
| 9 | 会议页侧栏分区 | `Conference.razor:57-89` | 按 `SessionRelation` 的固定数据分区（`SessionSidebarView.Build` 固定 3 类），不是「多贡献聚合位」 | 保持 |

### 8.2 顺带修正的既有缺陷

首页可见性策略已分裂，迁移时一并统一（不属于本架构范围，但同批处理成本最低）：

| 项 | 现状 | 应改为 |
|---|---|---|
| `_skillCount` | `ModuleCatalog.IsEnabled("skills")` 判定（`Index.razor:585-587`） | 保持 |
| MCP 面板 | `IsEnabled("mcp")` 判定（`:590`） | 保持 |
| `_schedulerStatus` | **无条件执行**（`:602` `await SchedulerStatus.GetStatusAsync()`） | 加 `IsEnabled("scheduler")` 判定 |

### 8.3 其余不在本期范围

| 项 | 理由 |
|---|---|
| 工作台 | 零代码基线，另立规格 |
| 会话内容渲染 4 套机制统一（`IContentBlockRenderer` / `IMessageComponent` / `IToolCallSlotProvider` / `TimelineItemKind`） | 是投影管线不是布局贡献；三套注册表冲突语义各异（同名替换 / 同 BlockType 替换 / 不去重按序全渲染）。只删死掉的 `MessageRendererContribution` |
| 运行时程序集加载 | 需 ALC 统一、依赖解析、静态资源端点、CSS 隔离、键命名空间五件事，`IUiViewKeyCatalog` 一件都没解决 |
| 懒激活 | 规模不匹配；声明期收集已为其铺路 |
| Tui 接入 | 单屏聊天无「页面」概念；需先改 `TuiRenderer` 的段拼装 |
| i18n 资源键 | 多语言需求未出现；`init` 属性加字段不破坏 |
| 权限级可见性 | 全仓无认证接线 |

---

## 9. 已知边界

| 项 | 说明 |
|---|---|
| catch-all 仍需宿主手工映射 | `WebUiHost.cs:301` 的 `MapFallbackToPage("/memory/detail/{*FilePath}", "/_Host")` 必须保留：该路径含扩展名，通用 fallback 会当静态文件跳过。**「单一通道」在 ASP.NET 路由层不成立。** |
| 页面标题仍是编译期中文字面量 | 能力包不引用 `UiText`（依赖方向不允许） |
| `UiPoint.Id` 只增不改 | 无版本机制；进程内编译期组装，错配在编译期爆 |
| `NavGroups` 三个中文常量保留 | 向后兼容；新分组走 `GroupOrder` |
| `Diagnostics` 不持久化 | 进程内内存，重启清空 |
| Step 0 的 `Legacy*` 别名期 | Step 2/3 之间并存；守门测试限定「只允许存在于 `UiContributionTypes.cs`」 |

---

## 10. 验证记录

| 项 | 结果 |
|---|---|
| 契约编译 | ✅ 0 错误 |
| 实现编译（registry / visibility / routeTemplate / pointCatalog / payloadKindMap / scenarioGate / sidebarProjector / collector） | ✅ 0 错误 |
| 测试 | ✅ **118 通过 / 0 失败** |
| 覆盖分布 | 可见性 13 / 路由 19 / 注册表 19 / 场景门 7 / 侧栏投影 22 / 收集链 13 / 语义回归 9 / 等价性 13 / 决策守门 3 |

**验证方式**：契约放在独立项目 `tests/probe/Seeing.Agent.UiEnvelopeProbe`，用 `*Probe` 后缀规避与旧类型同名，**与现有主线并行存在**。实施 Step 1 时把验证代码去后缀直接迁入正式位置。

### 10.1 这个方式实际消除了什么

| 问题类型 | v2 靠评审发现 | v3/v4 靠编译/测试发现 |
|---|---|---|
| 硬编译错误 | 9 处（靠 Agent 逐个读代码） | **0 处存活**（编译即报） |
| 语义回退（AND→OR、空集 fail-open、壳层黑屏） | 未发现 | **3 处**（测试断言直接暴露） |
| 声称已实现但不存在（`UiScenarioGate`） | 未发现 | **1 处**（`rg` 零命中） |
| 声明失败时诊断被自己抹掉 | 未发现 | **1 处**（`CollectorTests` 断言暴露） |

### 10.2 三个已定案的迁移决策

| 决策 | 选择 | 守门 |
|---|---|---|
| **D10** 宿主补全模块路由 | **模块自己声明全部 15 条**（含 5 条曾被 enrich 补全的详情路由） | `DecisionGuardTests.MigratedRouteSet_ShouldCoverAllLegacyRoutes` + `TheFiveEnrichOnlyRoutes_MustBeDeclaredByModules` |
| **D11** `host.*` 命名空间保护 | **强制校验**：模块不得占用 `host.{id}`；`Collector` 校验宿主 OwnerId 必须以 `host.` 开头 | `CollectorTests.HostWithInvalidPrefix_ShouldBeRejectedWithDiagnostic` + `DecisionGuardTests.ModuleCannotTakeHostNamespace` |
| **D12** 侧栏 `GroupOrder` 与选中键 | **`GroupOrder=0` 视为「排在所有已声明之后」**；`UpdateSelectedKey` 完整迁移 | `SidebarProjectorTests.GroupWithUnspecifiedOrder_ShouldSortLast` + `SelectedKeys_*` 7 项 |
