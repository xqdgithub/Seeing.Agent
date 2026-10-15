# WebUI 移动端适配与侧边栏折叠 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 WebUI 添加桌面端侧边栏折叠按钮和移动端汉堡菜单 overlay 导航，通过 JS 设备检测区分真实移动设备。

**Architecture:** 在现有 `AppSidebar` + `AppState` 架构上扩展，AntDesign Sider 的 `Collapsible` 属性提供桌面端折叠，JS `navigator` API 检测移动设备后切换为 hamburger + overlay 模式，CSS `@media` 处理页面内部布局适配。

**Tech Stack:** Blazor Server (.NET 10), AntDesign Blazor 1.6.2, JavaScript Interop, CSS Media Queries

---

## 文件结构

| 文件 | 职责 |
|------|------|
| `wwwroot/js/app.js` | `isMobileBrowser()` 设备检测函数 |
| `State/AppState.cs` | `IsMobile` + `MobileSidebarOpen` 状态 |
| `Components/AppSidebar.razor` | `Collapsible` 折叠、overlay class/style、移动端关闭逻辑 |
| `Shared/MainLayout.razor` | 设备检测初始化、遮罩层、汉堡按钮、动态 content class |
| `wwwroot/css/sidebar.css` | 移动端 Sider 隐藏/overlay 样式、汉堡按钮样式 |
| `wwwroot/css/main-layout.css` | 遮罩层样式、移除过时 @media 规则 |

---

### Task 1: 添加 JS 设备检测函数

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/js/app.js`

- [ ] **Step 1: 在 app.js 末尾添加 `isMobileBrowser()` 函数**

```javascript
/**
 * 判断是否为移动设备浏览器（非桌面端缩窄窗口）
 * 使用 navigator 属性区分，不依赖窗口宽度
 * @returns {boolean}
 */
function isMobileBrowser() {
    var hasTouch = navigator.maxTouchPoints > 0;
    var ua = navigator.userAgent;
    var isMobileUA = /Mobi|Android|iPhone|iPad|iPod/i.test(ua);
    var smallScreen = window.screen.width <= 1024 && window.screen.height <= 1024;
    return isMobileUA || (hasTouch && smallScreen);
}
```

> 注意：使用 `var` 而非 `const/let` 以保持与 app.js 现有代码风格一致（文件第 5-40 行使用 `var` 风格）。

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/wwwroot/js/app.js
git commit -m "feat: 添加移动设备浏览器检测 JS 函数"
```

---

### Task 2: 更新 AppState 模型

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/State/AppState.cs`

- [ ] **Step 1: 在 `AppState.cs` 的 `SidebarCollapsed` 属性后添加两个新属性**

在 `public bool SidebarCollapsed { get; set; } = false;` 之后插入：

```csharp
        /// <summary>是否为移动设备浏览器（非窗口大小判断）</summary>
        public bool IsMobile { get; set; }

        /// <summary>移动端侧边栏 overlay 开关</summary>
        public bool MobileSidebarOpen { get; set; }
```

完整修改后的 `AppState.cs` 属性区：

```csharp
        /// <summary>侧边栏是否折叠</summary>
        public bool SidebarCollapsed { get; set; } = false;

        /// <summary>是否为移动设备浏览器（非窗口大小判断）</summary>
        public bool IsMobile { get; set; }

        /// <summary>移动端侧边栏 overlay 开关</summary>
        public bool MobileSidebarOpen { get; set; }
```

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/State/AppState.cs
git commit -m "feat: AppState 添加 IsMobile 和 MobileSidebarOpen 属性"
```

---

### Task 3: 更新 AppSidebar 组件

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`

- [ ] **Step 1: 修改 Sider 标签，添加 `Collapsible`，改用动态属性**

将 `AppSidebar.razor` 第 8-13 行：

```razor
<Sider Class="app-sider"
       Width="@GetSiderWidth()"
       Collapsed="@Collapsed"
       CollapsedChanged="@OnCollapsedChanged"
       Theme="@SiderTheme.Light"
       Style="overflow: auto; height: 100vh; position: fixed; left: 0; top: 0; z-index: 100;">
```

替换为：

```razor
<Sider Class="@GetSiderClass()"
       Width="240"
       CollapsedWidth="80"
       Collapsed="@EffectiveCollapsed"
       CollapsedChanged="@OnCollapsedChanged"
       Collapsible
       Theme="@SiderTheme.Light"
       Style="@GetSiderStyle()">
```

- [ ] **Step 2: 替换 `@code { }` 块内容**

将 `AppSidebar.razor` 第 130-216 行的整个 `@code` 块替换为：

```csharp
@code {
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback<bool> CollapsedChanged { get; set; }

    private string[] _selectedKeys = Array.Empty<string>();
    private string[] _openKeys = new[] { "control", "workspace", "settings" };

    /// <summary>菜单项到所属 SubMenu 的映射</summary>
    private static readonly Dictionary<string, string> KeyToSubMenu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sessions"] = "control",
        ["cron-jobs"] = "control",
        ["heartbeat"] = "control",
        ["skills"] = "workspace",
        ["tools"] = "workspace",
        ["mcp"] = "workspace",
        ["acp"] = "workspace",
        ["memory"] = "workspace",
        ["agents"] = "settings",
        ["agent-config"] = "settings",
        ["models"] = "settings",
        ["gateway"] = "settings",
        ["gateway-clients"] = "settings",
        ["security"] = "settings",
        ["settings-page"] = "settings",
    };

    /// <summary>有效折叠状态：移动端 overlay 模式时强制展开 Sider</summary>
    private bool EffectiveCollapsed =>
        AppState.IsMobile ? !AppState.MobileSidebarOpen : Collapsed;

    /// <summary>根据设备和 overlay 状态返回不同 CSS class</summary>
    private string GetSiderClass()
    {
        if (!AppState.IsMobile) return "app-sider";
        return AppState.MobileSidebarOpen ? "app-sider sider-overlay" : "app-sider sider-hidden";
    }

    /// <summary>根据设备和 overlay 状态返回不同内联样式</summary>
    private string GetSiderStyle()
    {
        var baseStyle = "overflow: auto; height: 100vh; position: fixed; left: 0; top: 0;";
        if (AppState.IsMobile && AppState.MobileSidebarOpen)
            return baseStyle + " z-index: 200;";
        return baseStyle + " z-index: 100;";
    }

    /// <summary>打开移动端侧边栏 overlay</summary>
    public void OpenMobileSidebar()
    {
        AppState.MobileSidebarOpen = true;
        AppState.NotifyChange();
    }

    /// <summary>关闭移动端侧边栏 overlay</summary>
    public void CloseMobileSidebar()
    {
        AppState.MobileSidebarOpen = false;
        AppState.NotifyChange();
    }

    protected override void OnInitialized()
    {
        AppState.OnChange += OnAppStateChange;
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateSelectedKey();
    }

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChange;
        NavigationManager.LocationChanged -= OnLocationChanged;
    }

    private void UpdateSelectedKey()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).TrimEnd('/');
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        _selectedKeys = segment switch
        {
            "" or "home" => new[] { "chat" },
            "session" => new[] { "chat" },
            "settings" => new[] { "settings-page" },
            _ => new[] { segment }
        };

        if (!string.IsNullOrEmpty(segment) && KeyToSubMenu.TryGetValue(segment, out var subMenuKey))
        {
            if (!_openKeys.Contains(subMenuKey))
            {
                _openKeys = _openKeys.Concat(new[] { subMenuKey }).ToArray();
            }
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateSelectedKey();
        InvokeAsync(StateHasChanged);
    }

    private void OnAppStateChange() => InvokeAsync(StateHasChanged);

    /// <summary>导航到指定路径，移动端 overlay 模式下自动关闭侧边栏</summary>
    private void NavigateTo(string path)
    {
        NavigationManager.NavigateTo(path);
        if (AppState.IsMobile && AppState.MobileSidebarOpen)
            CloseMobileSidebar();
    }

    private string GetChatPath() =>
        !string.IsNullOrEmpty(AppState.CurrentSessionId)
            ? $"/session/{AppState.CurrentSessionId}"
            : "/sessions";

    /// <summary>折叠状态变更回调：移动端忽略 Sider 自带的折叠触发器</summary>
    private async Task OnCollapsedChanged(bool collapsed)
    {
        if (AppState.IsMobile) return;
        Collapsed = collapsed;
        if (CollapsedChanged.HasDelegate)
            await CollapsedChanged.InvokeAsync(collapsed);
    }
}
```

变更要点：
- 移除 `GetSiderWidth()` 方法，改用固定的 `Width="240"` + `CollapsedWidth="80"`
- 新增 `EffectiveCollapsed`、`GetSiderClass()`、`GetSiderStyle()` 计算属性
- 新增 `OpenMobileSidebar()`、`CloseMobileSidebar()` 公开方法
- `NavigateTo()` 中移动端 overlay 模式下自动关闭侧边栏
- `OnCollapsedChanged()` 中移动端忽略折叠事件

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/AppSidebar.razor
git commit -m "feat: AppSidebar 支持 Collapsible 折叠和移动端 overlay 模式"
```

---

### Task 4: 更新 MainLayout

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`

- [ ] **Step 1: 添加 `@inject IJSRuntime` 和 `AppSidebar` 引用**

在 `MainLayout.razor` 第 15 行 (`@inject IModelConfigManager ModelManager`) 之后添加：

```razor
@inject IJSRuntime JSRuntime
```

- [ ] **Step 2: 添加遮罩层和汉堡按钮，使用动态 content class**

将 `MainLayout.razor` 第 21-31 行：

```razor
<Layout Class="app-layout" Style="min-height: 100vh;">
    <!-- 左侧边栏 -->
    <AppSidebar Collapsed="@AppState.SidebarCollapsed"
                CollapsedChanged="@OnSiderCollapse" />
    
    <!-- 主内容区域 -->
    <Layout Class="@(AppState.SidebarCollapsed ? "main-layout-content collapsed" : "main-layout-content")">
        <Content Class="content-area">
            @Body
        </Content>
    </Layout>
</Layout>
```

替换为：

```razor
<Layout Class="app-layout" Style="min-height: 100vh;">
    <AppSidebar @ref="_sidebarRef"
                Collapsed="@AppState.SidebarCollapsed"
                CollapsedChanged="@OnSiderCollapse" />

    @if (AppState.IsMobile && AppState.MobileSidebarOpen)
    {
        <div class="sidebar-overlay-mask visible"
             @onclick="CloseMobileSidebar"
             @onclick:stopPropagation="true" />
    }

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
        <Content Class="content-area">
            @Body
        </Content>
    </Layout>
</Layout>
```

- [ ] **Step 3: 更新 `@code` 块**

将 `MainLayout.razor` 第 36-106 行的 `@code` 块替换为：

```csharp
@code {
    private bool _hasRendered;
    private AppSidebar? _sidebarRef;

    protected override async Task OnInitializedAsync()
    {
        // 检测设备类型
        AppState.IsMobile = await JSRuntime.InvokeAsync<bool>("isMobileBrowser");

        AppState.OnChange += OnAppStateChange;
        ModelManager.ModelConfigChanged += OnModelConfigChanged;
        await LoadAgentsAndModelsAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            _hasRendered = true;
    }

    private void OnAppStateChange()
    {
        RequestRender();
    }

    private void OnModelConfigChanged(object? sender, ModelConfigChangedEventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            await LoadAgentsAndModelsAsync();
            StateHasChanged();
        });
    }

    private void RequestRender()
    {
        if (!_hasRendered)
            return;
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task LoadAgentsAndModelsAsync()
    {
        try
        {
            var primaryAgents = await AgentRegistry.GetPrimaryAgentsAsync();
            AppState.AvailableAgents = primaryAgents.Where(a => !string.IsNullOrEmpty(a.Name)).ToList();
            AppState.AvailableModels = ModelManager.GetModelsByType();
            AppState.NotifyChange();
        }
        catch (Exception ex)
        {
            AppState.ErrorMessage = string.Format("加载失败: {0}", ex.Message);
        }
    }

    /// <summary>计算内容区 CSS class：移动端始终无 margin-left，桌面端根据折叠状态切换</summary>
    private string GetContentClass()
    {
        var baseClass = "main-layout-content";
        if (AppState.IsMobile)
            return baseClass;
        return AppState.SidebarCollapsed ? baseClass + " collapsed" : baseClass;
    }

    private void OnSiderCollapse(bool collapsed)
    {
        AppState.SidebarCollapsed = collapsed;
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

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChange;
        ModelManager.ModelConfigChanged -= OnModelConfigChanged;
    }
}
```

变更要点：
- `OnInitializedAsync` 首行调用 `isMobileBrowser()` 检测设备类型
- 移除旧的 `margin-left` 条件判定，改用 `GetContentClass()` 方法
- 新增 `ToggleMobileSidebar()` / `CloseMobileSidebar()` 方法
- `AppSidebar` 增加 `@ref="_sidebarRef"` 引用

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Shared/MainLayout.razor
git commit -m "feat: MainLayout 集成移动设备检测、遮罩层和汉堡按钮"
```

---

### Task 5: 更新 sidebar.css

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/sidebar.css`

- [ ] **Step 1: 在 `sidebar.css` 末尾（第 123 行之后）追加移动端样式**

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
        flex: 0 0 240px !important;
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
    box-shadow: var(--shadow-base);
    transition: transform 0.15s ease, box-shadow 0.15s ease;
}

.mobile-hamburger:hover {
    transform: scale(1.05);
    box-shadow: var(--shadow-lg);
}

@media (max-width: 768px) {
    .mobile-hamburger {
        display: flex;
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/wwwroot/css/sidebar.css
git commit -m "style: 添加移动端侧边栏隐藏/overlay 和汉堡按钮样式"
```

---

### Task 6: 更新 main-layout.css

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/main-layout.css`

- [ ] **Step 1: 移除过时的 @media (max-width: 768px) 中 margin-left 规则**

将 `main-layout.css` 第 97-110 行：

```css
/* ========== 响应式调整 ========== */
@media (max-width: 768px) {
    .main-layout-content {
        margin-left: 0;
    }
    
    .main-layout-content.collapsed {
        margin-left: 0;
    }
    
    .status-bar {
        padding: 0 16px;
    }
}
```

替换为：

```css
/* ========== 响应式调整 ========== */
@media (max-width: 768px) {
    .status-bar {
        padding: 0 16px;
    }
}
```

> `margin-left` 现在由 `MainLayout.GetContentClass()` 的 C# 逻辑控制，CSS @media 不再覆盖。

- [ ] **Step 2: 在文件末尾（第 138 行之后）追加遮罩样式**

```css
/* ========== 移动端侧边栏遮罩 ========== */
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

/* ========== 深色模式遮罩 ========== */
@media (prefers-color-scheme: dark) {
    .sidebar-overlay-mask {
        background: rgba(0, 0, 0, 0.65);
    }
}
```

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/wwwroot/css/main-layout.css
git commit -m "style: 移除过时 margin-left @media 规则，添加移动端遮罩样式"
```

---

### Task 7: 构建验证

**Files:**
- （无文件变更）

- [ ] **Step 1: 构建项目确认编译通过**

```bash
dotnet build samples/Seeing.Agent.WebUI -c Debug
```

预期: Build succeeded，0 errors

- [ ] **Step 2: 运行 WebUI 测试**

```bash
dotnet test tests/Seeing.Agent.WebUI.Tests
```

预期: All tests passed

- [ ] **Step 3: 最终提交（如有未提交变更）**

```bash
git status
```

---

## 验证清单（手动测试）

以上构建通过后，启动 WebUI 进行以下手动验证：

| # | 验证项 | 操作 |
|---|--------|------|
| 1 | 桌面端 Sider 底部有 ⇔ 折叠按钮 | 打开 http://localhost:5000，观察左侧栏底部 |
| 2 | 折叠/展开过渡正常 | 点击 ⇔ 按钮，Sider 宽度 240→80 或 80→240 |
| 3 | 折叠状态导航后保持 | 折叠后切换页面，侧边栏仍保持折叠 |
| 4 | 桌面缩窄窗口侧边栏仍可见 | 缩小浏览器到 600px，侧边栏仍以桌面模式存在 |
| 5 | 移动端模拟 Sider 隐藏 | Chrome DevTools → 切换移动设备模拟（如 iPhone SE）→ 刷新 |
| 6 | 移动端汉堡按钮可见 | 左下角出现圆形浮动按钮 |
| 7 | 汉堡→overlay 展开+遮罩 | 点击汉堡，Sider 浮层出现，背景有半透明遮罩 |
| 8 | 点击遮罩→关闭 | 点击遮罩区，Sider 消失 |
| 9 | 点击菜单项→导航+关闭 | 点击任意菜单项，导航到对应页且 Sider 自动关闭 |
| 10 | 375px 窄屏布局正常 | iPhone SE 模拟下检查 Session 页面消息区不被遮挡 |
