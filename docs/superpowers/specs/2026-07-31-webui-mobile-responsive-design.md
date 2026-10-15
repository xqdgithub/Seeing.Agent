# WebUI 移动端适配与侧边栏折叠

**日期:** 2026-07-31  
**范围:** `samples/Seeing.Agent.WebUI` 布局系统与响应式设计  
**状态:** 设计已确认，待实现

---

## 问题

1. **侧边栏无法折叠**：`AppState.SidebarCollapsed` 和 `Collapsed` 参数已存在，但 Sider 未启用 `Collapsible`，用户无折叠入口
2. **移动端侧边栏遮挡内容**：Sider 使用 `position: fixed`，<768px 时内容区 `margin-left: 0`，但 Sider 仍在视图中遮挡
3. **缺少移动端导航入口**：手机上没有汉堡菜单或类似手段唤起导航
4. **页面级响应式不完整**：仅部分页面有 768px 断点适配，缺少 480px/375px 等更小断点
5. **无法区分设备类型**：仅靠 `@media` 窗口宽度将桌面窗口缩窄也误判为移动端

## 目标

1. 桌面端：侧边栏可通过底部 ⇔ 按钮折叠/展开（240px ↔ 80px）
2. 移动设备：通过 `navigator.userAgent` + `maxTouchPoints` 检测真实移动设备，侧边栏默认完全隐藏，通过浮动汉堡按钮唤起 overlay 展开
3. 桌面端缩窄窗口时保持桌面行为（侧边栏不隐藏，仅 CSS 自适应布局）
4. overlay 展开时覆盖半透明遮罩，点击遮罩或菜单项后关闭
5. 折叠状态和宽度过渡动画流畅
6. 主要页面（Session、Workspace、Home）在窄屏下的布局不溢出

非目标：PWA/Service Worker 改造、滑动手势

---

## 方案

在现有 `AppSidebar` + `AppState` 架构上扩展，利用 AntDesign Sider 的 `Collapsible` 属性 + JS 设备检测 + CSS 媒体查询实现三模式。

### 设备检测策略

| 层级 | 机制 | 用途 |
|------|------|------|
| **设备类型检测** | `navigator.userAgent` + `maxTouchPoints` + `screen.width` | 判断真移动设备 → 控制侧边栏显示模式 |
| **窗口宽度适配** | CSS `@media` 768px / 480px | 页面内部布局适配（grid、padding、字体） |
| **触摸友好** | CSS `@media (pointer: coarse)` | 触控友好的间距和按钮尺寸 |

**JS 检测函数（`app.js`）：**

```javascript
function isMobileBrowser() {
    const hasTouch = navigator.maxTouchPoints > 0;
    const ua = navigator.userAgent;
    const isMobileUA = /Mobi|Android|iPhone|iPad|iPod/i.test(ua);
    const smallScreen = window.screen.width <= 1024 && window.screen.height <= 1024;
    return isMobileUA || (hasTouch && smallScreen);
}
```

`MainLayout.OnInitializedAsync()` 通过 `IJSRuntime.InvokeAsync<bool>("isMobileBrowser")` 获取结果写入 `AppState.IsMobile`。

### 布局三模式

```
桌面端（IsMobile = false，>768px）：
  ┌──────────┬──────────────────────────────┐
  │ Sider    │   Content                    │
  │ 240px    │   margin-left: 240px         │
  │          │                              │
  │  [⇔]     │                              │  ← Collapsible 触发器
  └──────────┴──────────────────────────────┘
     折叠 → 80px, margin-left: 80px

桌面端缩窄（IsMobile = false，≤768px）：
  ┌─────┬─────────────────────────┐
  │ Sid │  Content                │
  │ 80px│  margin-left: 80px      │  ← 保留桌面行为，可手动折叠
  │     │                         │
  └─────┴─────────────────────────┘
  CSS @media 调整内部布局（grid 变单列等）

移动端（IsMobile = true）：
  ┌──────────────────────────────┐
  │  Content (margin-left: 0)    │
  │                              │
  │                          [☰] │  ← 浮动汉堡按钮
  └──────────────────────────────┘

  点击汉堡 → overlay 展开：
  ┌──────────────────────────────┐
  │ Sider (240px, fixed, z:200)  │  ← overlay
  │██████████████████████████████│  ← 遮罩 z:180
  │██████  Content  █████████████│
  └──────────────────────────────┘
```

### AppState 改动

```csharp
/// <summary>侧边栏是否折叠</summary>
public bool SidebarCollapsed { get; set; } = false;

/// <summary>新增：是否为移动设备浏览器</summary>
public bool IsMobile { get; set; } = false;

/// <summary>新增：移动端侧边栏 overlay 开关</summary>
public bool MobileSidebarOpen { get; set; } = false;
```

### AppSidebar 改动

**Sider 属性调整：**

| 属性 | 值 | 说明 |
|------|-----|------|
| `Collapsible` | **新增** | 桌面端 Sider 底部显示 ⇔ 折叠触发按钮 |
| `Width` | 240 | 不变 |
| `CollapsedWidth` | 80 | 折叠后图标宽度 |
| `Collapsed` | `EffectiveCollapsed` | 计算属性：移动端 overlay 展开时强制 `false` |
| `CollapsedChanged` | `OnCollapsedChanged` | 仅在非移动端响应 |

**新增计算属性与方法：**

```csharp
/// <summary>有效折叠状态：移动端 overlay 模式时强制展开</summary>
private bool EffectiveCollapsed =>
    AppState.IsMobile ? !AppState.MobileSidebarOpen : Collapsed;

/// <summary>移动端 overlay 可见时隐藏 Sider</summary>
private string GetSiderClass()
{
    if (!AppState.IsMobile) return "app-sider";
    return AppState.MobileSidebarOpen ? "app-sider sider-overlay" : "app-sider sider-hidden";
}

/// <summary>移动端 overlay 模式下的额外样式</summary>
private string GetSiderStyle()
{
    var baseStyle = "overflow: auto; height: 100vh; position: fixed; left: 0; top: 0;";
    if (AppState.IsMobile && AppState.MobileSidebarOpen)
        return baseStyle + " z-index: 200;";
    return baseStyle + " z-index: 100;";
}

public void OpenMobileSidebar()  => AppState.MobileSidebarOpen = true;
public void CloseMobileSidebar() => AppState.MobileSidebarOpen = false;

private async Task OnCollapsedChanged(bool collapsed)
{
    if (AppState.IsMobile) return;  // 移动端不响应 Sider 自身折叠
    Collapsed = collapsed;
    if (CollapsedChanged.HasDelegate)
        await CollapsedChanged.InvokeAsync(collapsed);
}

private void NavigateTo(string path)
{
    NavigationManager.NavigateTo(path);
    if (AppState.IsMobile && AppState.MobileSidebarOpen)
        CloseMobileSidebar();
}
```

### MainLayout 改动

新增遮罩层和汉堡按钮：

```razor
<Layout Class="app-layout" Style="min-height: 100vh;">
    <AppSidebar @ref="_sidebarRef"
                Collapsed="@AppState.SidebarCollapsed"
                CollapsedChanged="@OnSiderCollapse" />

    <!-- 新增：移动端遮罩 -->
    @if (AppState.IsMobile && AppState.MobileSidebarOpen)
    {
        <div class="sidebar-overlay-mask visible"
             @onclick="CloseMobileSidebar" />
    }

    <!-- 新增：移动端汉堡按钮 -->
    @if (AppState.IsMobile)
    {
        <Button Class="mobile-hamburger"
                Icon="@IconType.Outline.Menu"
                Shape="@ButtonShape.Circle"
                Size="@ButtonSize.Large"
                OnClick="ToggleMobileSidebar"
                OnClickStopPropagation="true" />
    }

    <Layout Class="@GetContentClass()">
        <Content Class="content-area">@Body</Content>
    </Layout>
</Layout>

@code {
    private AppSidebar? _sidebarRef;

    protected override async Task OnInitializedAsync()
    {
        // 检测设备类型
        AppState.IsMobile = await JSRuntime.InvokeAsync<bool>("isMobileBrowser");
        AppState.OnChange += OnAppStateChange;
        ModelManager.ModelConfigChanged += OnModelConfigChanged;
        await LoadAgentsAndModelsAsync();
    }

    private string GetContentClass()
    {
        var baseClass = "main-layout-content";
        if (AppState.IsMobile)
            return baseClass;
        return AppState.SidebarCollapsed ? baseClass + " collapsed" : baseClass;
    }

    private void ToggleMobileSidebar()
    {
        if (AppState.MobileSidebarOpen)
            _sidebarRef?.CloseMobileSidebar();
        else
            _sidebarRef?.OpenMobileSidebar();
    }

    private void CloseMobileSidebar()
        => _sidebarRef?.CloseMobileSidebar();
}
```

### CSS 新增规则

**sidebar.css 追加：**

```css
/* ========== 移动端侧边栏 ========== */
@media (max-width: 768px) {
    .app-sider.sider-hidden {
        display: none !important;
        width: 0 !important;
        min-width: 0 !important;
    }

    .app-sider.sider-overlay {
        width: 240px !important;
        min-width: 240px !important;
        max-width: 240px !important;
        box-shadow: var(--shadow-lg);
        z-index: 200 !important;
    }

    .app-sider.sider-overlay .ant-layout-sider-zero-width-trigger {
        display: none !important;
    }
}

/* ========== 移动端汉堡按钮 ========== */
.mobile-hamburger {
    position: fixed;
    left: 12px;
    bottom: 20px;
    z-index: 150;
    display: none;
}

@media (max-width: 768px) {
    .mobile-hamburger {
        display: flex;
    }
}

.mobile-hamburger.ant-btn-circle {
    width: 44px;
    height: 44px;
    box-shadow: var(--shadow-base);
}
```

**main-layout.css 追加：**

```css
/* ========== 移动端遮罩 ========== */
.sidebar-overlay-mask {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.45);
    z-index: 180;
    display: none;
    animation: fadeIn 0.2s ease;
}

@media (max-width: 768px) {
    .sidebar-overlay-mask.visible {
        display: block;
    }
}

@keyframes fadeIn {
    from { opacity: 0; }
    to   { opacity: 1; }
}
```

**main-layout.css 修改（移动端 margin-left 调整）：**

原规则在 ≤768px 时强制 `margin-left: 0`（第 98-101 行）。由于现在移动端行为由 C# 的 `GetContentClass()` 控制（见 MainLayout 代码块），该 CSS 规则已无用，**需移除**。

---

## 数据流

```
用户操作                    Blazor 状态                          DOM / CSS
───────                    ───────────                          ────────
[页面初始化]  ──isMobileBrowser──→ AppState.IsMobile ──────────→ 控制汉堡按钮/遮罩渲染

[折叠按钮]  ──CollapsedChanged──→ AppState.SidebarCollapsed ──→ Sider width 240→80
                                                    ↕
[汉堡按钮]  ──ToggleMobile──→ AppState.MobileSidebarOpen ──→ sider-hidden ⇄ sider-overlay
                                                    ↕
[遮罩点击]  ──CloseMobile───→ AppState.MobileSidebarOpen ──→ sider-hidden
                                                    ↕
[菜单点击]  ──NavigateTo────→ CloseMobileSidebar()  ──→ sider-hidden
```

- `navigator.userAgent` + `maxTouchPoints` 在首次渲染时一次性判断设备类型
- 页面内部布局仍由 CSS `@media` 根据窗口宽度动态适配
- 无窗口 `resize` 监听，降低性能开销

---

## 改动文件清单

| 文件 | 改动类型 | 改动量估计 |
|------|----------|-----------|
| `Components/AppSidebar.razor` | 修改 | +40 行 |
| `Shared/MainLayout.razor` | 修改 | +35 行 |
| `State/AppState.cs` | 修改 | +5 行 |
| `wwwroot/css/sidebar.css` | 修改 | +40 行 |
| `wwwroot/css/main-layout.css` | 修改 | +25 行（+新增遮罩样式） |
| `wwwroot/js/app.js` | 修改 | +15 行 |

**总计：6 个文件，约 160 行改动，0 个新文件**

---

## 验证清单

| # | 验证项 | 方法 |
|---|--------|------|
| 1 | 桌面端（>768px）Sider 底部有 ⇔ 折叠按钮 | 手动检查 |
| 2 | 桌面端折叠/展开过渡动画正常 | 手动检查 |
| 3 | 折叠状态在页面导航后保持 | 切换页面验证 |
| 4 | 移动设备 Sider 默认隐藏 | Chrome DevTools 移动模拟 |
| 5 | 移动端左下角出现浮动汉堡按钮 | 手动检查 |
| 6 | 点击汉堡 → Sider overlay 展开 + 遮罩出现 | 手动检查 |
| 7 | 点击遮罩 → Sider 关闭 | 手动检查 |
| 8 | 点击菜单项 → 导航 + Sider 自动关闭 | 手动检查 |
| 9 | 桌面端缩窄窗口（600px）侧边栏不隐藏 | 缩窄浏览器窗口验证 |
| 10 | 375px（iPhone SE）极窄屏布局正常 | Chrome DevTools 模拟 |
| 11 | Session 页面消息区域不被 Sider 遮挡 | 多种宽度测试 |
