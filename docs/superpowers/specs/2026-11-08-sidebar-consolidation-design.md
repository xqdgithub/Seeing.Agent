# 侧栏整理设计（折叠状态 / 宽度 / 样式）

**日期：** 2026-11-08
**范围：** `samples/Seeing.Agent.WebUI` 的主侧栏（`AppSidebar` + 相关 CSS）
**状态：** 待评审

---

## 1. 为什么做

近两轮修的都是症状：「折叠再展开后浮层残留」「hover 控制中心不弹出」「折叠态工作区图标不居中」。逐条打补丁的根因是三个结构性问题：

### 1.1 折叠状态有两份副本

`AppState.SidebarCollapsed`（Singleton）是真实来源，但 `AppSidebar` 又有一份 `[Parameter] Collapsed` 副本：

```
MainLayout  Collapsed="@AppState.SidebarCollapsed"
            CollapsedChanged="@OnSiderCollapse"
                 ↓                            ↑
AppSidebar  [Parameter] Collapsed ←  OnCollapsedChanged 里直接赋值
```

组件在 `OnCollapsedChanged` 里同时写自己的 `[Parameter]` 和回调，`MainLayout` 再写回 `AppState`。同一状态被写两次，而 `Session.razor` 又独立读 `AppState.SidebarCollapsed` 控制工具栏文字。三处随时可能不同步。

### 1.2 折叠态仍在传「默认展开」

`_openKeys` 默认含 `NavGroups.Control`。AntDesign 的 `Menu` 在折叠（`inline-collapsed`）时不再内联展开，而是**按 OpenKeys 把子菜单渲染成悬浮层**。于是折叠瞬间「控制中心」被自动弹出并长期占据；之后 hover 时浮层已处于展开态，`Overlay.Show()` 直接返回 → 用户看到「hover 没反应」；该浮层也不随折叠/展开清除 → 展开后残留。

当前代码用两个补丁绕过：`@key` 强制重建菜单、`EffectiveOpenKeys` 折叠态置空。方向对，但语义混在补丁里。

### 1.3 宽度分散在 5 处，样式靠强制覆盖

- `Sider Width="240"` / `CollapsedWidth="80"`
- `main-layout.css`：`margin-left: 240px` / `.collapsed { margin-left: 80px }`
- `sidebar.css` `.sider-overlay`：`width/min-width/max-width/flex-basis: 240px`
- `design-system.css` 的 `--sidebar-width: 200px` / `--sidebar-collapsed-width: 64px` **从未被引用**（与真实值不一致，是漂移的坑）

`sidebar.css` 有 44 个 `!important`，其中多条打在 Ant 内部类上（`.antblazor-overlay-trigger-wrapper`、`.ant-menu-title-content`、`.ant-layout-sider-children`），Ant 换版本会静默失效。另有三条死样式：`.sider-agent-selector`、`.agent-option`、`.app-logo-collapsed`。深色模式并存两套机制：`sidebar.css`/`main-layout.css` 用 `@media (prefers-color-scheme: dark)`，其余全站用 `[data-theme="dark"]`。

---

## 2. 目标与非目标

**目标**

1. 折叠状态只剩一个来源：`AppState.SidebarCollapsed`。
2. 折叠态不保留默认展开；hover 弹出子菜单保留；展开态恢复用户展开的分组。两种模式行为一致。
3. 宽度只定义一次，内容区外边距由它派生，不可能漂移。
4. 删死样式，侧栏深色模式统一到 `[data-theme="dark"]`。
5. 现有功能全部不变：桌面折叠、移动端抽屉、审批中心、工作区切换、会话页工具栏文字收缩。

**非目标**

- 不自绘子菜单浮层（继续用 Ant 的）。
- 不改导航贡献 / 注册表模型（`IUiContributionRegistry`）。
- 不动会话页的双栏（`session-sidebar.css` 的 `SessionSidebarVisibility`），那是另一个功能。

**反目标（做成了也算失败）**

- 为了少改而保留两份状态副本。
- 用更多 `!important` 压住新出现的问题。

---

## 3. 方案

| 方案 | 内容 | 取舍 |
|------|------|------|
| **A（选定）** | 状态收敛到 `AppState`；`AppSidebar` 去掉折叠参数；折叠态 `OpenKeys` 恒空；宽度单一来源 | 改动 `AppSidebar` 对外契约，`MainLayout` 跟着调整；根因消除 |
| B | 只收敛开合语义，保留 `Collapsed` 参数，不动宽度体系 | 范围最小；5 处宽度和 44 个 `!important` 原样留着，下次改宽度再踩 |
| C | 自绘子菜单浮层 | 根治浮层不复位，但要自己做定位/动画/键盘/边界翻转，回归风险远超本次 |

选 A：用户明确要求「避免打补丁再出新问题」，根因就在状态副本与分散常量。

---

## 4. 具体设计

### 4.1 状态：单一来源

`AppSidebar` 删除 `[Parameter] Collapsed` 与 `[Parameter] CollapsedChanged`，改为内部读写 `AppState`：

```csharp
/// <summary>桌面端折叠态。唯一来源是 AppState（Singleton），组件不再持副本。</summary>
private bool Collapsed => AppState.SidebarCollapsed;

/// <summary>
/// 菜单是否处于折叠渲染。移动端 overlay 打开时菜单始终是展开态，
/// 只有桌面端折叠才切到折叠渲染。
/// </summary>
private bool IsMenuCollapsed => !AppState.IsMobile && Collapsed;

private void ToggleCollapse()
{
    AppState.SidebarCollapsed = !Collapsed;
    AppState.NotifyChange();   // 触发 MainLayout / Session 页一起重渲染
}
```

`MainLayout` 不再传这两个参数：

```razor
<AppSidebar @ref="_sidebarRef" />
```

`MainLayout` 的内容区 class、`Session.razor` 的工具栏文字判断继续读 `AppState.SidebarCollapsed`（本来就读这一份，行为不变）。

移动端开合仍走 `@ref` 调用 `OpenMobileSidebar()` / `CloseMobileSidebar()`，写 `AppState.MobileSidebarOpen` —— 这条路径不变。

### 4.2 开合语义：折叠态不保留默认展开

```csharp
/// <summary>
/// 菜单展开的分组键。折叠态恒为空：折叠时 AntDesign 不再内联展开，
/// 而是按 OpenKeys 把子菜单渲染成浮层 —— 传默认值会让浮层在折叠瞬间自动弹出，
/// 既造成 hover 失效（浮层已处于展开态），也会在展开后残留。
/// 折叠态交给 hover 自行弹出，展开态恢复用户维护的分组。
/// </summary>
private string[] MenuOpenKeys => IsMenuCollapsed ? [] : _openKeys;

private void OnOpenKeysChanged(string[] keys)
{
    // 折叠态的展开/收起是浮层的瞬时状态，不是用户偏好，不回写。
    if (IsMenuCollapsed) return;
    _openKeys = keys;
}
```

`Menu` 上保留随折叠状态变化的 `@key`：
```razor
<Menu @key="@(IsMenuCollapsed ? "collapsed" : "expanded")" ... />
```

**保留 `@key` 的理由**（与之前不同，这次写成契约而非补丁）：`OpenKeys` 置空解决的是「不该自动打开」，但不保证「hover 打开过、尚未收起时立刻点折叠按钮」这一瞬间的浮层能被 Ant 关闭 —— Ant 只在 `inline` 模式下 `Close()` 子菜单，不隐藏已渲染到 body 的浮层。重建让旧实例 `Dispose` 时把浮层从 body 移除，这是唯一不依赖 Ant 内部时序的保证。代价是一次菜单重渲染（切换时本来就要重排），可接受。

**必须同时传 `DefaultOpenKeys`**：AntDesign 的 `Menu.OnInitialized` 执行

```csharp
OpenKeys = DefaultOpenKeys?.ToArray() ?? OpenKeys;
```

`DefaultOpenKeys` 默认值是空集合而非 null，所以 `??` 不会兜底。菜单因 `@key` 重建时，若不传 `DefaultOpenKeys`，展开态会被覆盖成空 —— 表现为「切回展开后默认展开的分组丢了」。实测复现过，故 `OpenKeys` 与 `DefaultOpenKeys` 传同一个 `MenuOpenKeys`。

### 4.2.1 菜单项图标与角标的位置约束

折叠态 AntDesign 会自动给菜单项套 `Tooltip`，其内容取自 `MenuItem` 的 content（`Title` 或 `ChildContent`）。这带来两条约束：

1. **图标必须走 `MenuItem.Icon` 参数**，不能写在 `ChildContent` 里。`Icon` 渲染在 content 之外，不会进 tooltip；写进 `ChildContent` 则会让 tooltip 变成「图标 + 图标 + 文字」。
2. **`Badge` 必须放进 `IconTemplate`**，不能包在文字外层。Ant 的 `.ant-badge` 规则自带 `color: rgba(0,0,0,0.85)`，被复制进深色 tooltip 后会让该项文字变黑，而其它项是白字。

```razor
<MenuItem Key="permission-center" OnClick="OpenPermissionCenter">
    <IconTemplate>
        <Badge Count="@PermissionView.TotalCount">
            <Icon Type="@IconType.Outline.SafetyCertificate" />
        </Badge>
    </IconTemplate>
    <ChildContent><span>审批中心</span></ChildContent>
</MenuItem>
```

注意：`IconTemplate` 渲染出的图标位于 `.ant-menu-title-content` **之外**，折叠态需要一条独立居中规则（见 4.5），否则图标会贴到侧栏左缘。

### 4.3 宽度：单一来源

`AppSidebar` 暴露两个常量，`Sider` 参数与 `Layout` 的 CSS 变量都取自它们：

```csharp
public const int ExpandedWidth = 240;
public const int CollapsedWidth = 80;
```

```razor
<Sider Width="@ExpandedWidth" CollapsedWidth="@CollapsedWidth" ... />
```

`MainLayout` 把同一对值注入为 CSS 变量（CSS 变量可被子元素继承，内容区与移动端覆盖都能用）：

```razor
<Layout Class="app-layout"
        Style="@($"--sider-width:{AppSidebar.ExpandedWidth}px;--sider-collapsed-width:{AppSidebar.CollapsedWidth}px;min-height:100vh;")">
```

CSS 全部改为引用变量：

```css
.main-layout-content { margin-left: var(--sider-width); }
.main-layout-content.collapsed { margin-left: var(--sider-collapsed-width); }
.app-sider.sider-overlay {
    width: var(--sider-width) !important;
    min-width: var(--sider-width) !important;
    max-width: var(--sider-width) !important;
    flex: 0 0 var(--sider-width) !important;
}
```

`design-system.css` 里那对从未被引用、且数值已经漂移的 `--sidebar-width: 200px` / `--sidebar-collapsed-width: 64px` 删除，避免再次被误用。新增回归测试断言「CSS 里不再出现硬编码 240/80 的侧栏宽度」。

### 4.4 工作区入口触发器

折叠态需要一个 `Tooltip` 承载完整工作区名（80px 窄栏放不下文字），而 `Tooltip` 必须包裹「按钮」本身才能正确挂到触发器上。若把折叠/展开合并成单个按钮再条件渲染文字，`Tooltip` 就要再包一层，Ant 的两层触发器包裹会互相干扰（实测按钮宽度塌缩、居中失效）。

因此保留两个分支，仅在 `Collapsed` 上切换：

```razor
@if (Collapsed)
{
    <Tooltip Title="@($"工作区：{CurrentName}")" Placement="@Placement.Right">
        <Button ... Block="true"><Icon Type="@IconType.Outline.FolderOpen" /></Button>
    </Tooltip>
}
else
{
    <Button ... Block="true">
        <Icon Type="@IconType.Outline.FolderOpen" />
        <span class="ws-switcher-label">@CurrentName</span>
    </Button>
}
```

触发器居中问题用一条规则覆盖两种模式：AntDesign 的 Dropdown 包裹层默认 `inline-block + fit-content`，不拉满则按钮的居中/Block 都无效（展开态文字不占满整行、折叠态图标贴左缘）：

```css
.ws-switcher .antblazor-overlay-trigger-wrapper {
    display: block !important;
    width: 100% !important;
}
```

`Dropdown` 上保留随折叠状态变化的 `@key`（同 4.2 的浮层清理理由）。

### 4.5 样式清理

- 删除死样式：`.sider-agent-selector`、`.agent-option`、`.app-logo-collapsed` 及其深色模式分支。
- 深色模式统一为 `[data-theme="dark"]`（`sidebar.css`、`main-layout.css`）。理由：`app.js` 的 `initializeTheme()` 已在加载时把 `data-theme` 写到 `<html>`，`prefers-color-scheme` 只作为首次初始化的输入，其余全站已用 `data-theme`。改成同一机制后，用户手动切主题也能生效（现在侧栏跟不上）。
- 颜色改为引用 `design-system.css` 的令牌（`--color-bg-secondary`、`--color-border-secondary` 等），消除硬编码 `#fafafa` / `#e8e8e8` / `#303030`。
- `!important` 只保留在「覆盖 Ant 内联样式」与「Ant 类特异性更高」两处，逐条加一行说明它压的是什么。
- 折叠态菜单项居中需要两条互补规则：`.ant-menu-title-content`（图标写在 ChildContent 的老写法）与 `.ant-menu-item > .anticon`（`Icon` / `IconTemplate` 渲染，位于 title-content 之外）。后者还要让 title-content `display: none`，否则它占着宽度把图标挤向左缘（实测图标中心 15px vs 侧栏中心 40px）。

### 4.6 测试

改造现有 `SidebarOverlayResetTests` 为侧栏契约测试（`SidebarContractTests`）：

| 断言 | 防的问题 |
|------|----------|
| `AppSidebar` 没有 `Collapsed` / `CollapsedChanged` 参数 | 状态副本复活 |
| 折叠态 `OpenKeys` 恒空（源码断言 `IsMenuCollapsed ? [] : _openKeys`） | 默认展开回潮导致 hover 失效 |
| `OnOpenKeysChanged` 折叠态提前返回 | 浮层瞬时状态污染用户偏好 |
| `Menu` / `Dropdown` 的 `@key` 依赖折叠态 | 浮层残留回潮 |
| 菜单图标走 `MenuItem.Icon` 参数 | 图标被复制进 tooltip 造成重复装饰 |
| `Badge` 在 `IconTemplate` 内而非 `ChildContent` | tooltip 文字被 `.ant-badge` 的深色规则染黑 |
| `main-layout.css` 用 `var(--sider-width)`，无硬编码 240/80 | 宽度漂移 |
| `sidebar.css` 含 `.antblazor-overlay-trigger-wrapper` 拉满规则 | 折叠态图标贴边 |
| `sidebar.css` 含 `.ant-menu-item > .anticon` 居中规则 | `Icon` 参数图标贴左缘 |
| `sidebar.css` 不再出现 `@media (prefers-color-scheme` | 深色机制分裂 |

`StylesheetIntegrityTests` 的大括号配平继续覆盖 `sidebar.css` / `main-layout.css`。

---

## 5. 影响面

| 文件 | 改动 |
|------|------|
| `Components/AppSidebar.razor` | 去折叠参数、常量、开合语义、`DefaultOpenKeys`、图标/角标位置、`@key` 契约注释 |
| `Components/WorkspaceSwitcherButton.razor` | 保留折叠/展开两分支 + `@key` |
| `Shared/MainLayout.razor` | 不再传折叠参数；`Layout` 注入宽度 CSS 变量 |
| `wwwroot/css/sidebar.css` | 删死样式、深色统一、颜色令牌、宽度变量、图标居中、`!important` 标注 |
| `wwwroot/css/main-layout.css` | 宽度变量、深色统一 |
| `wwwroot/css/design-system.css` | 删除废弃的 `--sidebar-*` 令牌，新增 `--sider-*` 兜底 |
| `tests/.../SidebarOverlayResetTests.cs` | 改造为 `SidebarContractTests` |

`Pages/Session.razor`、`AppState.cs` 不改。

---

## 6. 风险与验证

| 风险 | 缓解 |
|------|------|
| 删 `Collapsed` 参数漏改调用点 | 编译期报错兜底；`rg` 全量确认只有 `MainLayout` 引用 |
| 深色模式改为 `data-theme` 后侧栏跟主题走，观感变化 | 属修正（现在切主题侧栏不变）；暗色下实拍对比 |
| `@key` 重建导致默认展开分组丢失 | 同时传 `DefaultOpenKeys`；实测三次往返后仍展开「控制中心」 |
| `Icon` 参数渲染的图标在 title-content 之外，折叠态偏心 | 补 `.ant-menu-item > .anticon` 居中规则 + title-content 让位；实测图标中心 = 侧栏中心 |
| CSS 变量未注入时宽度为 0 | `design-system.css` 的 `:root` 保留同名默认值兜底 |

**验证清单（浏览器实测 + 测试）**

1. 桌面折叠 → 展开：无残留浮层，内容区边距 240↔80 同步。
2. 折叠态逐个 hover 三个分组：均弹出对应子菜单；鼠标移开自动收起。
3. 展开态：默认「控制中心」展开；点击可折叠/展开其它分组；折叠再展开后用户展开状态保留。
4. 折叠态工作区图标几何居中（图标中心 = 侧栏中心）。
5. 移动端（DevTools 移动仿真）：汉堡开合、遮罩、点击导航后自动关闭。
6. 深色模式：切换 `data-theme` 后侧栏底色/边框跟随。
7. `dotnet vstest` WebUI 测试全绿。

---

## 7. 留给实现者

- `!important` 逐条精简到什么程度：以「删掉后实测样式不坏」为准，不做纯理论清理。
- 深色模式令牌映射的具体取值：与 `design-system.css` 暗色段保持一致，不新造颜色。
