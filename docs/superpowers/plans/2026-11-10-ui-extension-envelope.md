# UI 扩展统一架构实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 UI 扩展机制从「6 套并行 + `Type` 硬绑编译期」重构为「统一信封 + `ViewKey` + 生命周期驱动」，并保证每个子批可独立构建、可独立验收。

**Architecture:** 契约层（`Seeing.Agent.Abstractions.Ui`）只放纯数据与端口；判定/匹配落 Core（`UiVisibility` / `UiRouteTemplate` / `UiScenarioGate` / `UiContributionRegistry`）；视图解析落宿主（`WebUiViewResolver`）。归属由注册表条目持有，可见性由调用方用唯一实现显式判定。**所有契约与核心实现已在 `tests/probe/Seeing.Agent.UiEnvelopeProbe` 中编译并通过 118 项测试**，本计划的任务是把已验证的代码迁入正式位置并切换消费方。

**Tech Stack:** .NET 10 / C# 13、Blazor Server、AntDesign 1.6.2、xUnit 3 + FluentAssertions 8

**设计规格:** `docs/superpowers/specs/2026-11-10-ui-extension-unified-architecture-design.md`（v4）

---

## Global Constraints

- 目标框架 `net10.0`；`ImplicitUsings=enable`；`Nullable=enable`。
- 能力包（`src/capabilities/*`）只可引用 `Seeing.Agent.Abstractions` + 原语层 + `src/support/*`；**禁止**引用 `Seeing.Agent.Core`。
- `Seeing.Agent.Abstractions` 零实现：`Ui/` 下不得出现方法体（`IUiContributor.DescribeUi` 是抽象成员，非默认实现）、不得出现 `Microsoft.AspNetCore.*`。
- `src/spine/Seeing.Agent.Core/Ui/` 下不得出现 `Microsoft.AspNetCore.*` / `IComponent` / `RenderFragment`。
- 框架命名一律用 `Workbench` 之外的通用词：本计划只做 UI 信封，**不含任何工作台内容**（工作台另立规格）。
- `Requires` 语义：**归属模块列表；空 = 进程级**（不受模块启停影响）。模块贡献空 `Requires` 被拒收；`host.*` 豁免。
- `host.*` 命名空间不可被模块占用；宿主 OwnerId 必须以 `host.` 开头。
- 路由占位名契约：模板中的 `{name}` 逐字等于 `UiRouteMatch.Parameters` 的键，也逐字等于页面组件的 `[Parameter]` 名。
- Catch-all 语义（与 `ModuleRouteResolverCatchAllTests` 逐条对齐）：**不解码**（`%2F` 原样）、无尾段捕获空串、剩余段用 `/` 连接。
- 测试用 VSTest 直跑已构建程序集（MTP 在本仓库无法发现测试）：
  `dotnet build tests/<path>/<proj>` → `dotnet vstest tests/<path>/<proj>/bin/Debug/net10.0/<proj>.dll`
- 测试禁止固定时长等待。
- WebUI 与 `Seeing.Agent.WebUI.Files` 两个 Razor 项目 `ManagePackageVersionsCentrally=false`，包版本显式钉（AntDesign 1.6.2 / BlazorMonaco 3.5.0 / Markdig 1.3.2）。

---

## 并行批次总览

```
批次 A（前置清理）           ← 必须最先，行为零变化
   │
   ├── 批次 B1（契约落地）  ─┐
   │                          │  可并行
   └── 批次 B2（Core 实现） ─┤  （B2 依赖 B1 的类型定义，
                              │   但 B1 只加文件不改旧契约，
                              │   故 B2 可与 B1 并行开始）
                              │
批次 C（页面通道切换）  ← 依赖 B1 + B2，串行，不可拆
   │
批次 D（设置卡 + 插槽） ← 依赖 C（复用 C 建立的视图表与投影）
   │
批次 E（文档）
```

| 批次 | 内容 | 可并行 | 判据 |
|---|---|---|---|
| **A** | 前置清理（改名、删死代码、删 `@page`） | 单批 | `dotnet build` 全绿 + 手工核对行为无变化 |
| **B1** | 新契约落 `Abstractions/Ui/` | 与 B2 并行 | 编译通过；契约测试绿 |
| **B2** | Core 实现（registry / visibility / route / gate） | 与 B1 并行 | 118 项测试迁入并全绿 |
| **C** | 页面通道切换（8 模块 + 壳层 + ModuleRouter + AppSidebar） | 单批（大） | 28 个页面可达；侧栏逐项一致 |
| **D** | 设置卡 + 插槽切换 | 单批 | 4 卡片 + 4 插槽正常；删余下旧类型 |
| **E** | 文档 | 单批 | 索引同步 |

---

### 批次 A：前置清理（行为零变化）

**目标：** 让新旧契约可以共存编译，删掉 4 项死代码。

**为什么必须先做：** `SettingsCardContribution` / `SlotContribution` 在本计划里会被**新类型占用同名**。若不先改名旧类型，编译必报 CS0101。`NavContribution` / `RouteContribution` 名字不冲突，**不动**（避免 40+ 处无意义改动）。

#### Task A1: 重命名冲突的两个旧类型

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/UiContributionRegistry.cs:14,18,22,28,31,35,40,46,88,92,100,108,110,123,130`
- Modify: `samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs:50,55,60,67`
- Modify: `samples/Seeing.Agent.WebUI/Services/FilePanelUiContribution.cs:15`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Settings.razor:179`
- Modify: `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/UiContributionVisibility.cs:61`
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionShellVisibility.cs:50-70`
- Modify: `tests/spine/Seeing.Agent.Tests/Ui/UiContributionContractsTests.cs:35-61`

**Interfaces:**
- Consumes: 无
- Produces: `LegacySettingsCardContribution`、`LegacySlotContribution`（都是位置参数 record，形状与旧类型完全一致）

- [ ] **Step 1: 改名两个旧类型**

`src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs`：

```csharp
/// <summary>
/// 设置页卡片贡献（旧形状，待迁移）。
/// </summary>
/// <param name="Route">卡片路由或锚点。</param>
/// <param name="Title">卡片标题。</param>
/// <param name="ComponentType">Blazor 组件类型（WebUI 侧再转为 RenderFragment）。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
[Obsolete("待迁移到 Ui.SettingsCardContribution（信封形状）。见 docs/architecture/40。")]
public record LegacySettingsCardContribution(
    string Route,
    string Title,
    Type ComponentType,
    IReadOnlyList<string> Requires);

/// <summary>
/// 命名插槽贡献（旧形状，待迁移）。
/// </summary>
/// <param name="Name">插槽名称（如 session_prompt、message_toolbar）。</param>
/// <param name="ComponentType">Blazor 组件类型（WebUI 侧再转为 RenderFragment）。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
/// <param name="Scenarios">可选场景白名单（全仓零使用，随迁移删除）。</param>
[Obsolete("待迁移到 Ui.SlotContribution（信封形状）。见 docs/architecture/40。")]
public record LegacySlotContribution(
    string Name,
    Type ComponentType,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string>? Scenarios = null);
```

- [ ] **Step 2: 批量改为 `Legacy*`**

对下列每一处，把 `SettingsCardContribution` → `LegacySettingsCardContribution`、`SlotContribution` → `LegacySlotContribution`（**不含** `Legacy` 前缀已在的类型定义本身）：

| 文件 | 行 |
|---|---|
| `UiContributionRegistry.cs` | 18、22、35、40、88、92、123、130 |
| `CoreShellUiContribution.cs` | 50、55、60、67 |
| `FilePanelUiContribution.cs` | 15 |
| `Settings.razor` | 179（`_visibleCards` 类型） |
| `SlotHost.razor` | 42（`_visible` 类型） |
| `UiContributionVisibility.cs` | 61（`FilterSettingsCards` 签名） |
| `SessionShellVisibility.cs` | 52（`FilterSlotsForSession` 签名） |
| `UiContributionContractsTests.cs` | 37、50、55、59 |

- [ ] **Step 3: 构建**

```bash
dotnet build
```

Expected: 全绿（`[Obsolete]` 只产生 CS0618 警告；WebUI 已 `NoWarn=CS0618`）。

- [ ] **Step 4: 运行既有测试**

```bash
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```

Expected: 全部通过，与改动前一致。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "refactor(ui): rename two conflicting legacy contribution records"
```

#### Task A2: 删死代码（4 项，互不依赖）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs`（删 `MessageRendererContribution` + `Scenarios` 字段）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionRegistry.cs`（删 `MessageRenderers`）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContribution.cs`（删 `<see cref>`）
- Modify: `samples/Seeing.Agent.WebUI/Services/UiContributionRegistry.cs`（删 4 处）
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionShellVisibility.cs`（删 `FilterNavForProcess`）
- Delete: `samples/Seeing.Agent.WebUI/State/SlotRegistry.cs`
- Modify: `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor`（删 legacy 回退）
- Modify: `tests/spine/Seeing.Agent.Tests/Ui/UiContributionContractsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: 无（纯删除）

- [ ] **Step 1: 删 `MessageRendererContribution`（7 处 + 1 处文档引用）**

| 位置 | 动作 |
|---|---|
| `UiContributionTypes.cs:65-71` | 删类型定义 |
| `IUiContributionRegistry.cs:24` | 删 `MessageRenderers` 属性 |
| `UiContributionRegistry.cs:17` | 删 `_messageRenderers` 字段 |
| `UiContributionRegistry.cs:40-43` | 删属性实现 |
| `UiContributionRegistry.cs:91` | 删 `var renderers = new List<...>` |
| `UiContributionRegistry.cs:123-125` | 删 `case MessageRendererContribution` |
| `UiContributionRegistry.cs:130` | 删 `_messageRenderers = renderers;` |
| `IUiContribution.cs:13-15` | 删文档 `<see cref="MessageRendererContribution"/>` |
| `UiContributionContractsTests.cs:65-71,123-125` | 删两个用例 |

- [ ] **Step 2: 删 `Scenarios` 字段（全仓零使用）**

`NavContribution` 与 `LegacySlotContribution` 各删一个参数。同步删测试里的 `Scenarios` 断言（`UiContributionContractsTests.cs:27-33`、`:53,61`）。

> 注意：`NavContribution` 的位置参数 `Scenarios` 在 `ComponentType` 之前，删参数会改变位置参数顺序。**保留参数顺序**：改为在 `NavContribution` 里删掉该参数并把后续参数前移，同步修正 `CoreShellUiContribution` 里用命名参数传值的位置（那里全用命名参数，安全）。

- [ ] **Step 3: 删 `SessionShellVisibility.FilterNavForProcess`**

```bash
rg "FilterNavForProcess" -g "*.cs" -g "!obj" -g "!bin"   # 确认零调用
```

确认零调用后删除该方法（`SessionShellVisibility.cs:33-47`）。

- [ ] **Step 4: 删 `State/SlotRegistry.cs` + `SlotHost` 的 legacy 回退**

```bash
git rm samples/Seeing.Agent.WebUI/State/SlotRegistry.cs
```

`SlotHost.razor:24-29` 的

```razor
    @{
        var legacy = SlotRegistry.GetSlot(SlotName);
    }
    @if (legacy != null)
    {
        @legacy
    }
```

整段删除。

- [ ] **Step 5: 删 3 处 `@page`**

```
samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor:1
samples/Seeing.Agent.WebUI/Pages/WorkspacesPage.razor:1
samples/Seeing.Agent.WebUI/Pages/ReflectionRunsPage.razor:1
```

> 核实过无副作用：`WorkspacesPage` 由 `ModuleRouter.razor:24` 直接渲染、`WorkspaceGateTests.cs:42` 断言的是 `ModuleRouter` 源码含 `<WorkspacesPage`（与 `@page` 无关）；`MemoryPage` / `ReflectionRunsPage` 由路由表渲染。

- [ ] **Step 6: 删死字段 `ConfigFormComponentType`（6 处）**

| 位置 | 动作 |
|---|---|
| `src/gateway/Seeing.Gateway/Plugins/IGatewayChannelPlugin.cs:26` | 删 DIM |
| `src/gateway/Seeing.Gateway/Plugins/GatewayChannelTypeInfo.cs:18` | 删位置参数 |
| `src/gateway/Seeing.Agent.Gateway/Channels/GatewayChannelRegistry.cs:164` | 删构造实参 |
| `samples/Seeing.Agent.WebUI/Services/GatewayClientConfigService.cs:87,444` | 删搬运 |
| `samples/Seeing.Agent.WebUI/Pages/GatewayClientsPage.razor:286` | 删赋值 |

> 核实过：`GatewayChannelTypeInfo` 唯一构造点是 `GatewayChannelRegistry.cs:154-164`，测试零引用。删最后一个位置参数安全。

- [ ] **Step 7: 构建 + 全量测试**

```bash
dotnet build
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll
```

Expected: 全绿。

- [ ] **Step 8: 手工验证（行为零变化）**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

核对：① 侧栏 6 组导航项与改动前一致；② `/memory`、`/skills`、`/cron-jobs` 直链可达；③ 设置页 4 张卡片正常；④ 会话页 4 个插槽正常（含文件面板）；⑤ 未启用模块直链显示「未启用」提示。

- [ ] **Step 9: 提交**

```bash
git add -A
git commit -m "chore(ui): remove dead contribution code and stray @page directives"
```

---

### 批次 B1：新契约落 `Abstractions/Ui/`（可与 B2 并行）

**目标：** 把验证项目里已验证的契约原样迁入正式位置。**只加文件，不改任何旧契约**，因此可与 B2 并行。

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiPoint.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContribution.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/PageContribution.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/SettingsCardContribution.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionRegistry.cs`（**新文件，文件名冲突**）
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributor.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiViewKeyCatalog.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiRouteMatch.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/EnvelopeContractTests.cs`

**Interfaces:**
- Consumes: 无
- Produces（B2 与 C/D 依赖这些精确签名）：
  - `enum UiPayloadKind { Page, SettingsCard, Slot }`
  - `enum UiVisibilityScope { Process, Session }`
  - `sealed record UiPoint { Id; DisplayName; PayloadKind; Scope; MaxItems }`
  - `interface IUiPointCatalog { Points; Find(string); Supports(string) }`
  - `abstract record UiContribution { Point; Id; Requires; Order }`
  - `sealed record PageContribution : UiContribution { Route; Title; ViewKey; Nav }`
  - `sealed record NavPlacement { Icon; Group; GroupIcon; GroupOrder }`
  - `sealed record SettingsCardContribution : UiContribution { Title; ViewKey }`
  - `sealed record SlotContribution : UiContribution { ViewKey; DisplayName }`
  - `readonly record struct UiContributionEntry(string OwnerId, UiContribution Contribution)`
  - `sealed record UiContributionDiagnostic(UiDiagnosticKind, string, string, string, string)`
  - `enum UiDiagnosticKind { UnsupportedPoint, PayloadMismatch, DuplicateId, DuplicateRoute, ExceededMaxItems, ValidatorRejected, InvalidDeclaration, CrossOwnerDuplicate }`
  - `interface IUiContributionRegistry { AddRange; RemoveByOwner; ReportInvalidDeclaration; All; Get<T>; Pages; Diagnostics; Changed }`
  - `interface IUiContributor { IReadOnlyList<UiContribution> DescribeUi(); }`
  - `interface IUiHostContributor { string OwnerId; IReadOnlyList<UiContribution> DescribeUi(); }`
  - `interface IUiViewKeyCatalog { bool CanResolve(string); IReadOnlyCollection<string> Keys }`
  - `interface IUiContributionValidator { string? Validate(UiContribution, UiPoint) }`
  - `sealed record UiRouteMatch(PageContribution Page, IReadOnlyDictionary<string, object?> Parameters)`

- [ ] **Step 1: 从验证项目复制契约（去 `Probe` 后缀）**

来源：`tests/probe/Seeing.Agent.UiEnvelopeProbe/EnvelopeContracts.cs`。

| 验证项目中的名字 | 正式名字 |
|---|---|
| `PageContributionProbe` | `PageContribution` |
| `SettingsCardContributionProbe` | `SettingsCardContribution` |
| `SlotContributionProbe` | `SlotContribution` |
| 其余同名 | 不变 |

**注意文件拆分**：验证项目把 9 个类型放一个文件，正式版按类型拆成 8 个文件（便于 review 与后续演进）。

**`IUiContributionRegistry.cs` 文件名冲突**：旧文件已存在（内容将随批次 D 删除）。新接口**追加到同一文件的独立区域**，或改用 `IUiContributionRegistryV2.cs` 暂时并存 → **推荐后者**（`IUiContributionCatalog.cs`），批次 D 删旧接口时把新接口改回 `IUiContributionRegistry.cs`。

> 决策：新接口文件名为 `IUiContributionCatalog.cs`，接口名仍叫 `IUiContributionRegistry`（C# 允许文件名与类型名不同）。批次 D 时：
> 1. `git rm .../IUiContributionRegistry.cs`（旧，仅含 `Register/Unregister/NavItems/...`）
> 2. `git mv .../IUiContributionCatalog.cs .../IUiContributionRegistry.cs`

- [ ] **Step 2: 写契约测试**

`tests/spine/Seeing.Agent.Tests/Ui/EnvelopeContractTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

public class EnvelopeContractTests
{
    [Fact]
    public void PageContribution_WithNullNav_IsPureRoute()
    {
        var page = new PageContribution
        {
            Point = "shell.page", Id = "p", Route = "/x", Title = "X", ViewKey = "p"
        };

        page.Nav.Should().BeNull();
        page.Requires.Should().BeEmpty();
        page.Order.Should().Be(0);
    }

    [Fact]
    public void UiContribution_HasNoComponentType()
    {
        // D1 的锚点：契约层不得出现 Blazor 组件类型
        typeof(UiContribution).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(Type), "契约层不得携带组件类型");

        typeof(PageContribution).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(Type));
    }

    [Fact]
    public void UiContribution_HasNoModuleId()
    {
        // 归属由注册表条目持有（E3）
        typeof(UiContribution).GetProperty("ModuleId").Should().BeNull();
    }

    [Fact]
    public void UiContribution_HasNoScenarios()
    {
        // 全仓零使用，已删除（E7）
        typeof(UiContribution).GetProperty("Scenarios").Should().BeNull();
        typeof(PageContribution).GetProperty("Scenarios").Should().BeNull();
    }

    [Fact]
    public void WithExpression_PreservesDerivedType()
    {
        // AddRange 用 with 重建时必须保留派生类型
        UiContribution page = new PageContribution
        { Point = "shell.page", Id = "p", Route = "/x", Title = "X", ViewKey = "p" };

        var stamped = page with { Order = 5 };

        stamped.Should().BeOfType<PageContribution>();
    }

    [Fact]
    public void IUiContributor_DescribeUi_ShouldBeAbstractNotDefault()
    {
        // 单成员接口的 DIM 等于 marker，且 Moq 会返回 default 而非走默认实现
        var method = typeof(IUiContributor).GetMethod(nameof(IUiContributor.DescribeUi))!;

        method.IsAbstract.Should().BeTrue("DescribeUi 必须是抽象成员");
    }

    [Fact]
    public void IUiHostContributor_RequiresHostPrefixInDocs()
    {
        // 契约约定：OwnerId 必须以 host. 开头（Collector 强制）
        typeof(IUiHostContributor).GetProperty(nameof(IUiHostContributor.OwnerId))
            .Should().NotBeNull();
    }

    [Fact]
    public void UiDiagnosticKind_HasCrossOwnerDuplicate()
    {
        Enum.IsDefined(typeof(UiDiagnosticKind), UiDiagnosticKind.CrossOwnerDuplicate)
            .Should().BeTrue();
    }
}
```

- [ ] **Step 3: 构建 + 运行契约测试**

```bash
dotnet build src/abstractions/Seeing.Agent.Abstractions
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~EnvelopeContractTests"
```

Expected: 全绿（8 个用例）。

> **注意**：此时 `SettingsCardContribution` / `SlotContribution` 新类型与 `Legacy*` 并存，编译正常（A1 已改名）。

- [ ] **Step 4: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Ui tests/spine/Seeing.Agent.Tests/Ui/EnvelopeContractTests.cs
git commit -m "feat(ui): add envelope contracts"
```

---

### 批次 B2：Core 实现（可与 B1 并行）

**目标：** 把验证项目的 5 个实现迁入 `src/spine/Seeing.Agent.Core/Ui/`，测试一并迁入。

**依赖说明：** 本批需要 B1 的类型定义。若与 B1 严格并行，可先把验证项目的契约文件复制到 Core 内部命名空间临时使用；**推荐做法是 B1 完成后再开始 B2**（B1 只加文件，耗时可忽略）。

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Ui/UiPayloadKindMap.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiPointCatalog.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiVisibility.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiScenarioGate.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiRouteTemplate.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiContributionRegistry.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiContributionCollector.cs`
- Create: `src/spine/Seeing.Agent.Core/Ui/UiContributionRegistrationExtensions.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiVisibilityTests.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiRouteTemplateTests.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiContributionRegistryTests.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiScenarioGateTests.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiContributionCollectorTests.cs`
- Create: `tests/spine/Seeing.Agent.Tests/Ui/UiSemanticRegressionTests.cs`

**Interfaces:**
- Consumes（B1）：全部契约类型
- Produces：
  - `static class UiPayloadKindMap { bool TryGet(Type, out UiPayloadKind) }`
  - `sealed class UiPointCatalog : IUiPointCatalog`（ctor: `IEnumerable<UiPoint>`）
  - `sealed record UiVisibilityContext(IModuleCatalog? Catalog, IReadOnlySet<string>? ScenarioModuleGate = null)` + `static Process(IModuleCatalog?)`
  - `sealed record UiVisibilityDecision(bool Visible, UiVisibilityReason Reason, string? Detail = null)`
  - `enum UiVisibilityReason { Visible, CatalogUnavailable, ModuleDisabled, InvalidRequire, ScenarioMismatch }`
  - `static class UiVisibility { bool IsVisible(UiContribution, UiPoint, UiVisibilityContext); UiVisibilityDecision Evaluate(...) }`
  - `static class UiScenarioGate { IReadOnlySet<string>? Resolve(string?, IScenarioCatalog?); string? ResolveEffective(string?, string?) }`
  - `static class UiRouteTemplate { string NormalizePath(string?); bool TryMatch(string, string, out IReadOnlyDictionary<string,object?>, out int); UiRouteMatch? Resolve(string, IEnumerable<UiContributionEntry>, Func<UiContributionEntry,bool>? isVisible = null) }`
  - `sealed class UiContributionRegistry : IUiContributionRegistry`（ctor: `IUiPointCatalog?, IEnumerable<IUiContributionValidator>?, ILogger?`；`bool IsEnabled`）
  - `sealed class UiContributionCollector`（ctor: `IUiContributionRegistry, IUiPointCatalog?, ILogger?`；`bool IsEnabled`；`int CollectHostContributions(IEnumerable<IUiHostContributor>)`；`int CollectModuleContributions(IEnumerable<ISeeingModule>)`）
  - `static class UiContributionRegistrationExtensions { IServiceCollection AddSeeingUiPoints(this IServiceCollection, params UiPoint[]) }`

- [ ] **Step 1: 迁入 6 个实现文件（去 `Probe` 后缀）**

复制源与目标：

| 源（`tests/probe/Seeing.Agent.UiEnvelopeProbe/`） | 目标（`src/spine/Seeing.Agent.Core/Ui/`） |
|---|---|
| `EnvelopeImplementations.cs` 的 `UiPayloadKindMap` | `UiPayloadKindMap.cs` |
| `EnvelopeImplementations.cs` 的 `UiPointCatalog` | `UiPointCatalog.cs` |
| `EnvelopeImplementations.cs` 的 `UiVisibilityContext` / `UiVisibilityDecision` / `UiVisibilityReason` / `UiVisibility` | `UiVisibility.cs` |
| `UiScenarioGate.cs` | `UiScenarioGate.cs` |
| `EnvelopeImplementations.cs` 的 `UiRouteTemplate` | `UiRouteTemplate.cs` |
| `EnvelopeImplementations.cs` 的 `UiContributionRegistry` | `UiContributionRegistry.cs` |
| `UiContributionCollector.cs` | `UiContributionCollector.cs` |

命名空间：`Seeing.Agent.UiEnvelopeProbe` → `Seeing.Agent.Core.Ui`；类型名去掉 `Probe` 后缀。

- [ ] **Step 2: 加 DI 注册扩展**

`UiContributionRegistrationExtensions.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>UI 扩展点的 DI 注册辅助。</summary>
public static class UiContributionRegistrationExtensions
{
    /// <summary>
    /// 声明宿主支持的扩展点。
    /// <para>
    /// <b>必须在 Build() 之前调用</b>：模块贡献在结算后收集，若此时目录为空，
    /// 所有贡献都会被判为「未声明的扩展点」而拒收 → UI 全空。
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingUiPoints(
        this IServiceCollection services, params UiPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        services.TryAddSingleton<IUiPointCatalog>(_ => new UiPointCatalog(points));
        return services;
    }

    /// <summary>注册视图键目录（宿主的强类型解析实现通常同时注册为具体类型与接口）。</summary>
    public static IServiceCollection AddSeeingUiViewCatalog<T>(this IServiceCollection services)
        where T : class, IUiViewKeyCatalog
    {
        services.TryAddSingleton<T>();
        services.TryAddSingleton<IUiViewKeyCatalog>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
```

- [ ] **Step 3: 迁入 6 个测试文件**

| 源（`tests/probe/Seeing.Agent.UiEnvelopeProbe.Tests/`） | 目标（`tests/spine/Seeing.Agent.Tests/Ui/`） |
|---|---|
| `VisibilitySemanticsTests.cs` + `SemanticRegressionTests.cs`（可见性部分） | `UiVisibilityTests.cs` |
| `RouteSemanticsTests.cs` + `EquivalenceGapTests.cs`（路由部分） | `UiRouteTemplateTests.cs` |
| `RegistrySemanticsTests.cs` + `SemanticRegressionTests.cs`（注册表部分） | `UiContributionRegistryTests.cs` |
| `EquivalenceGapTests.cs`（场景门部分） | `UiScenarioGateTests.cs` |
| `CollectorTests.cs` | `UiContributionCollectorTests.cs` |

命名空间：`Seeing.Agent.UiEnvelopeProbe.Tests` → `Seeing.Agent.Tests.Ui`；类型名去 `Probe` 后缀。

> **注意**：`SidebarProjectorTests` 与 `DecisionGuardTests` 中的路由集合测试属于 WebUI 侧，留在批次 C。

- [ ] **Step 4: 构建 + 运行**

```bash
dotnet build src/spine/Seeing.Agent.Core
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Seeing.Agent.Tests.Ui"
```

Expected: 全绿（约 60 个用例）。

- [ ] **Step 5: 加不变量测试（Core 不得引 Web）**

`tests/spine/Seeing.Agent.Invariants.Tests/Ui/CoreUiLayerInvariantTests.cs`：

```csharp
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Ui;

public class CoreUiLayerInvariantTests
{
    [Fact]
    public void CoreUiNamespace_MustNotReferenceAspNetCore()
    {
        var coreAssembly = typeof(Seeing.Agent.Core.Ui.UiVisibility).Assembly;

        var offenders = coreAssembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Seeing.Agent.Core.Ui", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m switch
            {
                PropertyInfo p => [p.PropertyType],
                FieldInfo f => [f.FieldType],
                MethodInfo mi => mi.GetParameters().Select(p => p.ParameterType)
                    .Append(mi.ReturnType).ToArray(),
                _ => Type.EmptyTypes
            })
            .Where(t => t.FullName?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty("Core 的 UI 实现不得引用 ASP.NET Core 类型");
    }
}
```

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat(core): add UI envelope registry, visibility, routing"
```

---

### 批次 C：页面通道切换（**不可拆，需一次到位**）

**目标：** 把 15 条模块路由 + 13 条壳层路由切到 `PageContribution`，删除 `ModulePageRouteBinder`，`ModuleRouter` / `AppSidebar` 改消费新契约。

**为什么不可拆：** 一旦 `ModulePageRouteBinder` 被删，15 条路由必须有新的声明者。中间态不存在。

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Shell/WebUiPoints.cs`
- Create: `samples/Seeing.Agent.WebUI/Shell/WebUiViewResolver.cs`
- Create: `samples/Seeing.Agent.WebUI/Shell/WebUiViews.cs`
- Create: `samples/Seeing.Agent.WebUI/Shell/WebUiHostContributor.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/SidebarProjectionService.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/WebUiIconMap.cs`
- Modify: 8 个模块（`DescribeUi` 实现 + 删 `_ui`）
- Modify: 7 处 extension 调用点
- Modify: `samples/Seeing.Agent.WebUI/Components/ModuleRouter.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`
- Delete: `samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs`
- Delete: `samples/Seeing.Agent.WebUI/Services/UiContributionVisibility.cs`
- Delete: `samples/Seeing.Agent.WebUI/Services/UiContributionRegistry.cs`（移到 Core 后）
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`

**Interfaces:**
- Consumes（B1+B2）：全部契约与实现
- Produces：
  - `static class WebUiPoints`：`ShellPage` / `ShellSettingsCard` / `SessionPrompt` / `SessionToolbarActions` / `SessionInputToolbar` / `SessionFilePanel` / `All`
  - `sealed class WebUiViewResolver : IUiViewKeyCatalog`：`void Register(string, Type)` / `Type? Resolve(string)` / `bool CanResolve(string)` / `Keys`
  - `static class WebUiViews`：`void RegisterBuiltIns(WebUiViewResolver)`
  - `sealed class WebUiHostContributor : IUiHostContributor`：`OwnerId => "host.webui"` / `DescribeUi()`（13 页面 + 4 设置卡）
  - `static class SidebarProjectionService`：`SidebarProjection Project(IEnumerable<UiContributionEntry>, Func<string,string>?)` / `IReadOnlyList<string> SelectedKeys(string?, IReadOnlyList<NavView>)` / `string NavKey(string)`
  - `static class WebUiIconMap`：`string Resolve(string token)` / `string? ResolveOptional(string?)`

- [ ] **Step 1: 扩展点声明**

`samples/Seeing.Agent.WebUI/Shell/WebUiPoints.cs`：照规格 §5.1 写 6 个 `UiPoint` 与 `All`。

- [ ] **Step 2: 视图表**

`WebUiViewResolver.cs`：从验证项目 `EnvelopeImplementations.cs` 无对应实现，需新写（约 60 行）：`Dictionary<string, Type>` + `Register` 校验 `IComponent` + `Resolve` 强类型入口。

`WebUiViews.cs`：登记全部 28 个页面的 `ViewKey → Type`（15 模块 + 13 壳层）+ 4 张设置卡。

- [ ] **Step 3: 壳层贡献**

`WebUiHostContributor.cs`：`OwnerId = "host.webui"`，`DescribeUi()` 返回 13 条 `PageContribution`（`Requires = []`，其中 7 条 `Nav = null`、6 条带 `Nav`）+ 4 条 `SettingsCardContribution`。

> **关键**：`Requires = []` + `OwnerId` 以 `host.` 开头——两者缺一都会被注册表拒收。

- [ ] **Step 4: 8 个模块实现 `IUiContributor`**

每个模块（Basic / Skills / Mcp / Scheduler / Memory / Acp / Reflection / Gateway）：

```csharp
public sealed class SkillsModule : ISeeingModule, IUiContributor
{
    // 删：private readonly IUiContributionRegistry? _ui;
    // 删：public SkillsModule(IUiContributionRegistry? uiRegistry)
    // 删：_ui?.Register(this) / _ui?.Unregister(Id)

    /// <inheritdoc />
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.skills",
            Route = "/skills",
            Title = "技能",
            ViewKey = "page.skills",
            Requires = ["skills"],
            Order = 10,
            Nav = new NavPlacement
            {
                Icon = "star",
                Group = NavGroups.Workspace,
                GroupIcon = NavGroups.WorkspaceIcon,
                GroupOrder = 20
            }
        }
    ];
}
```

**15 条路由的完整映射表见规格 §6.1**，逐行照抄 `Route` / `Title` / `Group` / `Order`；带 `{` 或为已知子路径者 `Nav = null`。

- [ ] **Step 5: 删 7 处 extension 调用点的 `uiRegistry` 参数**

| 文件 | 行 |
|---|---|
| `SchedulerServiceCollectionExtensions.cs` | 103、214 |
| `MemoryServiceExtensions.cs` | 75 |
| `AcpServiceCollectionExtensions.cs` | 63 |
| `GatewayServiceCollectionExtensions.cs` | 50、90 |
| `FullCapabilityComposition.cs` | 133 |

- [ ] **Step 6: 删反射构造分支**

`ModuleRegistrationExtensions.cs:74-88` 与 `ServiceCollectionExtensions.cs:197-214` 的 `IUiContributionRegistry` 分支（保留 `IAgentStore` 分支）。

- [ ] **Step 7: `ModuleRouter` 改消费**

```razor
@inject IUiContributionRegistry Registry
@inject IUiPointCatalog Points
@inject WebUiViewResolver Views
@inject IModuleCatalog Catalog

@code {
    private void ResolveCurrent()
    {
        var relative = Navigation.ToBaseRelativePath(Navigation.Uri);
        var normalized = UiRouteTemplate.NormalizePath(relative);

        _isWorkspacesRoute = /* 原样保留 */;
        _forceWorkspaceHome = /* 原样保留 */;
        if (_isWorkspacesRoute || _forceWorkspaceHome) return;

        _locationKey = normalized;

        var point = Points.Find(WebUiPoints.ShellPage.Id)!;
        var ctx = UiVisibilityContext.Process(Catalog);

        var match = UiRouteTemplate.Resolve(
            normalized,
            Registry.All,
            e => e.Contribution is PageContribution p
                 && UiVisibility.IsVisible(p, point, ctx));

        if (match is null)
        {
            _resolution = new(ModuleRouteKind.Fallback);
            return;
        }

        if (Views.Resolve(match.Page.ViewKey) is not Type component)
        {
            // 视图未装配：与「模块未启用」区分文案
            _resolution = new(ModuleRouteKind.NotEnabled,
                FeatureTitle: match.Page.Title,
                ModuleId: match.Page.Requires.FirstOrDefault());
            return;
        }

        _resolution = new(ModuleRouteKind.Found, component, match.Parameters,
            match.Page.Title, match.Page.Requires.FirstOrDefault());
    }
}
```

**必须保留**：`_forceWorkspaceHome` / `_isWorkspacesRoute` / `EmptyLayout` 三条分支（含原注释「导航会抛 NavigationException 打掉 circuit」）。

**必须补**：给 `<DynamicComponent>` 包 `<ErrorBoundary>`（E14）。

- [ ] **Step 8: `AppSidebar` 改用 `SidebarProjectionService`**

`SidebarProjectionService.cs` 从验证项目 `SidebarProjector.cs` 迁入（去 `Probe` 后缀，类型名去掉末尾 `or` 的动词化）。

`AppSidebar.RefreshNav` 改为：

```csharp
private void RefreshNav()
{
    var point = WebUiPoints.ShellPage;
    var ctx = UiVisibilityContext.Process(Catalog);

    _projection = SidebarProjectionService.Project(
        Registry.All.Where(e => e.Contribution is PageContribution p
                                && UiVisibility.IsVisible(p, point, ctx)),
        WebUiIconMap.Resolve);

    _visibleNav = _projection.Visible;
    _ungroupedNav = _projection.Ungrouped;
    _groupedNav = _projection.Groups;
}
```

`UpdateSelectedKey` 改为调 `SidebarProjectionService.SelectedKeys(...)`。

**保持不变**：`_optionsSubscription`（`AgentOptions.OnChange(_ => RefreshNav())`）。

- [ ] **Step 9: `WebUiHost` 接线**

```csharp
// Build 之前
builder.Services.AddSeeingUiPoints(WebUiPoints.All);
builder.Services.AddSingleton<WebUiViewResolver>();
builder.Services.AddSingleton<IUiViewKeyCatalog>(sp => sp.GetRequiredService<WebUiViewResolver>());
builder.Services.AddSingleton<IUiContributionValidator, WebUiUiContributionValidator>();
builder.Services.AddSingleton<IUiHostContributor, WebUiHostContributor>();
builder.Services.AddSingleton<IUiContributionRegistry, UiContributionRegistry>();
builder.Services.AddSingleton<UiContributionCollector>();
```

`Build` 之后、`InitializeSeeingAsync` 之前：

```csharp
var collector = app.Services.GetRequiredService<UiContributionCollector>();
var hosts = app.Services.GetServices<IUiHostContributor>();
var hostCount = collector.CollectHostContributions(hosts);
if (hostCount == 0)
    app.Logger.LogError("未收集到任何宿主 UI 贡献：侧栏与设置页将为空白，请检查 IUiHostContributor 注册");

WebUiViews.RegisterBuiltIns(app.Services.GetRequiredService<WebUiViewResolver>());

var registry = app.Services.GetRequiredService<IUiContributionRegistry>();
if (registry.Diagnostics.Count > 0)
    app.Logger.LogError("UI 贡献收集存在 {Count} 条诊断：{Details}", registry.Diagnostics.Count,
        string.Join("; ", registry.Diagnostics.Select(d => $"{d.Kind}:{d.OwnerId}/{d.ContributionId}")));
```

**删掉** `WebUiHost.cs:254-259` 与 `WorkspaceReadyInitializer.cs:41-46` 的旧注册（两处重复注册）。

- [ ] **Step 10: 删 `ModulePageRouteBinder` / `UiContributionVisibility` / 旧 `UiContributionRegistry`**

```bash
git rm samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs
git rm samples/Seeing.Agent.WebUI/Services/UiContributionVisibility.cs
git rm samples/Seeing.Agent.WebUI/Services/UiContributionRegistry.cs
```

`SessionShellVisibility` 的 `AreRequirementsEnabled` 改为转调 `UiVisibility`。

- [ ] **Step 11: 改测试**

| 文件 | 改动 |
|---|---|
| `ModuleRouteResolverCatchAllTests.cs` | 引用改 `UiRouteTemplate.TryMatch`（5 个断言语义不变） |
| `JobDetailEditNavigationTests.cs` | 改 `UiRouteTemplate`；「编辑子路由不存在」改为断言 `Registry.Pages` 无该路由 |
| `McpModuleLifecycleTests.cs` | 删 `Mock<IUiContributionRegistry>`；改断言 `DescribeUi()` 返回单条 `PageContribution` |
| `SidebarContractTests.cs` | 11 个源码文本断言——因投影保留 `Icon`/`Group`/`Order`/`Route` 成员名，**应当零改动**；若失败说明投影破坏视觉契约 |

新增 `tests/apps/Seeing.Agent.WebUI.Tests/Services/SidebarProjectionServiceTests.cs`（从验证项目 `SidebarProjectorTests.cs` 迁入，22 项）。

- [ ] **Step 12: 构建 + 全量测试 + 手工验证**

```bash
dotnet build
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll
```

手工：① 28 个页面全部可达（逐个直链）；② 侧栏 6 分组 + 顺序与迁移前一致；③ 5 条详情路由**不出现**在侧栏但直链可达；④ 未启用模块直链给「未启用」+ 正确标题；⑤ `/workspaces` 仍走 `EmptyLayout`。

- [ ] **Step 13: 提交**

```bash
git add -A
git commit -m "refactor(webui): switch page channel to UI envelope"
```

---

### 批次 D：设置卡 + 插槽切换

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Settings.razor`
- Modify: `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/FilePanelUiContribution.cs` → 改为 `IUiContributor` 实现
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContribution.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionRegistry.cs`（删旧成员）；`git mv IUiContributionCatalog.cs IUiContributionRegistry.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs`（删 `Legacy*` 与 `NavContribution` / `RouteContribution`）
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionShellVisibility.cs`（改为薄封装）

**Interfaces:**
- Consumes: 批次 C 的产物
- Produces: 无（收尾）

- [ ] **Step 1: `Settings.razor` 改消费**

```razor
@foreach (var (card, component) in _visibleCards)
{
    <TabPane Key="@($"{card.ModuleId}.{card.Id}")" Tab="@card.Title">
        <ErrorBoundary>
            <ChildContent><DynamicComponent Type="@component" /></ChildContent>
            <ErrorContent Context="ex">
                <Alert Type="@AlertType.Error" Message="@($"该设置卡片渲染失败：{ex.Message}")" ShowIcon="true" />
            </ErrorContent>
        </ErrorBoundary>
    </TabPane>
}
```

`_visibleCards` = `Registry.Get<SettingsCardContribution>("shell.settings.card")` 经可见性过滤 + 视图解析。

> `card.ModuleId` 不存在于新契约——用 `UiContributionEntry.OwnerId` 拼 Key。

- [ ] **Step 2: `SlotHost` 改消费**

按 `SlotName` 映射到点 id（4 条映射见规格 §6.2），用 `Registry.Get<SlotContribution>(pointId)` + `UiVisibility`（`Scope=Session`，传 `UiScenarioGate` 算出的 gate）+ 视图解析 + `ErrorBoundary`。

- [ ] **Step 3: `FilePanelUiContribution` 改为模块贡献**

改成 `FileWorkspaceModule` 的 `IUiContributor` 实现（或独立的小类），`Requires = ["files.workspace"]`，`Point = "session.file.panel"`。

- [ ] **Step 4: 删旧类型与旧接口**

```bash
git rm src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContribution.cs
git mv src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionCatalog.cs src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionRegistry.cs
```

从 `UiContributionTypes.cs` 删 `LegacySettingsCardContribution`、`LegacySlotContribution`、`NavContribution`、`RouteContribution`，保留 `NavGroups`。

- [ ] **Step 5: 加守门测试**

```csharp
[Fact]
public void SourceMustNotReferenceLegacyContributionTypes()
{
    // src/ 下不得再出现旧类型名
    var root = FindRepositoryRoot();
    var offenders = Directory
        .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Where(f => File.ReadAllText(f) is var t
                    && (t.Contains("LegacySettingsCardContribution")
                        || t.Contains("LegacySlotContribution")
                        || t.Contains("MessageRendererContribution")))
        .ToList();

    offenders.Should().BeEmpty();
}
```

- [ ] **Step 6: 构建 + 全量测试 + 手工验证**

```bash
dotnet build
```

手工：① 设置页 4 卡片正常（含 Tab key 唯一）；② 4 个插槽正常（含文件面板）；③ 会话场景收窄对插槽生效、对侧栏不生效。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "refactor(webui): switch settings cards and slots to UI envelope"
```

---

### 批次 E：文档

**Files:**
- Create: `docs/architecture/40-ui-extension-unified-architecture-release-notes.md`
- Modify: `docs/architecture/README.md`、`02-modules.md`、`04-development-standards.md`、`05-extension-guide.md`、`06-compliance-audit.md`
- Modify: `AGENTS.md`、`CLAUDE.md`

- [ ] **Step 1: 写 Release Notes**

内容：破坏性变更清单（旧类型删除、`IUiContribution` 删除、`ModulePageRouteBinder` 删除、`Scenarios` 删除、`ConfigFormComponentType` 删除）、新契约要点、三个已定案决策、已知边界（catch-all 仍需宿主映射、`TryMatchKnown` 消失、`UiPoint.Id` 只增不改）、验证记录（118 测试）。

- [ ] **Step 2: 更新索引与规范**

`README.md` 阅读顺序表加 `40`；`06-compliance-audit.md` 补「默认接口方法豁免第 3 类」（`IExecutionOutcomeObserver` / `IExecutionInFlightBoundary` / ——注意 `IUiContributor.DescribeUi` 是抽象成员，不属豁免）。

- [ ] **Step 3: 更新 `AGENTS.md` / `CLAUDE.md` 的 WHERE TO LOOK**

加两行：UI 扩展信封（契约 / Core / 宿主三层路径）、侧栏投影与视图表。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "docs: add UI envelope release notes"
```

---

### 批次 F：清理验证脚手架

- [ ] **Step 1: 删除验证项目**

```bash
rm -rf tests/probe
```

- [ ] **Step 2: 构建确认**

```bash
dotnet build
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```

- [ ] **Step 3: 提交**

```bash
git add -A
git commit -m "chore: remove UI envelope probe project"
```

---

## 并行执行建议

| 并行组 | 任务 | 前置 |
|---|---|---|
| **第 1 组** | 批次 A（A1 → A2 串行，内部 Task 不可拆） | 无 |
| **第 2 组** | 批次 B1 ∥ 批次 B2 | A 完成 |
| **第 3 组** | 批次 C（大，建议单 Agent 串行做完 13 步） | B1 + B2 |
| **第 4 组** | 批次 D ∥ 批次 E1（Release Notes 可提前写） | C |
| **第 5 组** | 批次 F | D + E |

**建议**：第 2 组用两个 subagent 并行（B1 只加文件，B2 迁实现）；第 3 组必须单 agent（改 24 个文件 + 删 3 个文件，跨文件强耦合）。

---

## 自检结果

**1. 规格覆盖**

| 规格章节 | 对应任务 |
|---|---|
| §3 契约 | B1 |
| §4 判定与匹配 | B2 |
| §5 宿主实现 | C（Step 1-3、7-8） |
| §6 迁移映射 | C（Step 4）、D（Step 2） |
| §7 分期 Step 0 | A |
| §7 分期 Step 1 | B1 + B2 |
| §7 分期 Step 2 | C |
| §7 分期 Step 3 | D |
| §7 分期 Step 4 | E |
| §8 宿主保留区 | 不做（已在规格记录理由） |
| §9 已知边界 | E1 记录 |
| §10 验证记录 | 已在前置工作中完成（118 测试） |

**2. 占位符扫描**：无 TBD / TODO / 「类似 Task N」。

**3. 类型一致性**（关键跨批符号）

| 符号 | 定义 | 使用 |
|---|---|---|
| `UiContribution.Requires`（空 = 进程级） | B1 | B2（可见性 + 注册表校验）、C（模块声明）、D |
| `IUiContributionRegistry.AddRange(string ownerId, ...)` | B1 | B2（实现 + Collector）、C（壳层）、D |
| `UiVisibility.IsVisible(UiContribution, UiPoint, UiVisibilityContext)` | B2 | C（ModuleRouter + AppSidebar）、D（SlotHost + Settings） |
| `UiRouteTemplate.Resolve(string, IEnumerable<UiContributionEntry>, Func<...>?)` | B2 | C（ModuleRouter） |
| `UiScenarioGate.Resolve(string?, IScenarioCatalog?)` | B2 | D（SlotHost） |
| `SidebarProjectionService.Project(IEnumerable<UiContributionEntry>, Func<string,string>?)` | C | C（AppSidebar） |
| `UiContributionCollector.CollectHostContributions(IEnumerable<IUiHostContributor>)` | B2 | C（WebUiHost） |
