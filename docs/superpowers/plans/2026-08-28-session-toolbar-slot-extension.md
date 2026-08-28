# 会话工具栏插槽扩展实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 `SessionToolbar` 增加左右双插槽（`LeftActions`/`RightActions`），让 `Session.razor` 把页级导航注入工具栏、移除独立 Header 行，同时 Conference 等不传插槽的调用方零改动。

**Architecture:** `SessionToolbar` 新增两个 `RenderFragment<SessionWindowContext>?` 插槽参数（左插槽渲染在左侧最前、右插槽渲染在右侧最后）；`SessionWindow` 新增 `ToolbarLeftActions`/`ToolbarRightActions` 透传参数并转发给工具栏；`Session.razor` 删除 `<Header>` 模板与移动端溢出面板，改传插槽。插槽不提供时工具栏渲染与现状一致。

**Tech Stack:** Blazor Server (net10.0)、AntDesign 1.6.2、xUnit 3 + VSTest、Playwright 1.62.0。

**Spec:** `docs/superpowers/specs/2026-08-28-session-toolbar-slot-extension-design.md`

---

## 注意事项（执行前必读）

- 项目无 bUnit，razor 组件渲染无法用 xUnit 直接测试。每个 Task 的验证方式 = `dotnet build` 0 错误 + 回归单测 115 PASS（仅 Task 5 全量跑）+ Playwright 手动/脚本验证。
- 测试命令（MTP 无法发现测试，一律 VSTest）：
  ```bash
  dotnet build samples/Seeing.Agent.WebUI 2>&1 | Select-String -Pattern "error"
  dotnet build tests/Seeing.Agent.WebUI.Tests
  dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
  ```
- 提交信息用中文，符合项目风格（参考 `git log`）。
- `session-title` 样式在 `SessionsPage.razor`（首页）复用，**禁止删除**。只删 Session 专用的 header 系列。

### Task 1: SessionToolbar 新增左右插槽参数

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor:16-152`（Full 分支渲染区）、`SessionToolbar.razor:193-195`（参数区）

- [ ] **Step 1: 新增插槽参数**

在 `SessionToolbar.razor` 参数区（`Context`/`Mode` 参数附近，约第 194-195 行后）新增：

```csharp
[Parameter] public RenderFragment<SessionWindowContext>? LeftActions { get; set; }
[Parameter] public RenderFragment<SessionWindowContext>? RightActions { get; set; }
```

注意：文件已 `@using Services = Seeing.Agent.WebUI.Services`，参数类型用 `RenderFragment<Services.SessionWindowContext>` 或 `RenderFragment<SessionWindowContext>`（文件顶部第 3 行有别名 using，二选一保持与现有代码一致；现有第 194 行用 `Services.SessionWindowContext`，跟随它）。

- [ ] **Step 2: 渲染左插槽（左侧最前）**

在 `.session-toolbar-left` 容器内、标题 `<Tooltip>` 之前（约第 18 行 `@if (!string.IsNullOrEmpty(Context.Title))` 前）插入：

```razor
@if (LeftActions != null)
{
    <div class="session-toolbar-left-actions">
        @LeftActions(Context)
    </div>
}
```

- [ ] **Step 3: 渲染右插槽（右侧最后）**

在 `.session-toolbar-right` 容器内、清空按钮 `</Tooltip>` 之后（约第 150-151 行 `@if (Context.IsSubAgentView)` 分支结束、`</div>` 前）插入：

```razor
@if (RightActions != null)
{
    <div class="session-toolbar-right-actions">
        @RightActions(Context)
    </div>
}
```

- [ ] **Step 4: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI 2>&1 | Select-String -Pattern "error"
```

Expected: 无输出（0 错误）

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor
git commit -m "feat(webui): SessionToolbar 新增左右插槽参数（LeftActions/RightActions）"
```

### Task 2: SessionWindow 新增透传参数并转发

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:50`（工具栏渲染）、`SessionWindow.razor:177`（Header 参数附近）

- [ ] **Step 1: 新增透传参数**

在 `SessionWindow.razor` 参数区（`Header` 参数附近，第 177 行后）新增：

```csharp
[Parameter] public RenderFragment<SessionWindowContext>? ToolbarLeftActions { get; set; }
[Parameter] public RenderFragment<SessionWindowContext>? ToolbarRightActions { get; set; }
```

`SessionWindowContext` 在文件顶部已 `@using Services = Seeing.Agent.WebUI.Services`，类型写 `RenderFragment<Services.SessionWindowContext>?`（与第 177 行 Header 声明一致）。

- [ ] **Step 2: 转发到 SessionToolbar**

将第 50 行：

```razor
<SessionToolbar Context="Context" Mode="ViewMode" />
```

改为：

```razor
<SessionToolbar Context="Context"
                Mode="ViewMode"
                LeftActions="ToolbarLeftActions"
                RightActions="ToolbarRightActions" />
```

- [ ] **Step 3: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI 2>&1 | Select-String -Pattern "error"
```

Expected: 无输出

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "feat(webui): SessionWindow 透传 ToolbarLeftActions/ToolbarRightActions 至工具栏"
```

### Task 3: Session.razor 移除 Header 行，改用插槽

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor:42-129`（Header 块→插槽）、`Session.razor:135-136`、`Session.razor:242-246`

- [ ] **Step 1: 替换 Header 块为插槽**

将 `Session.razor` 第 42-129 行的 `<Header Context="ctx">...</Header>` 整个块替换为：

```razor
<ToolbarLeftActions Context="ctx">
    @{
        // 幂等同步 AppState.CurrentSessionId（供首页"继续会话"导航定位）
        SyncCurrentSessionId(ctx);
    }
    <Button Type="@ButtonType.Text"
            Size="@ButtonSize.Small"
            Icon="@IconType.Outline.Home"
            OnClick="@GoToHome">
        @if (!AppState.SidebarCollapsed && !AppState.IsMobile)
        {
            <span>首页</span>
        }
    </Button>
</ToolbarLeftActions>
<ToolbarRightActions Context="ctx">
    <Space>
        @if (!ctx.IsSubAgentView)
        {
            <SpaceItem>
                <Tooltip Title="分支会话">
                    <Button Type="@ButtonType.Text"
                            Size="@ButtonSize.Small"
                            Icon="@IconType.Outline.Copy"
                            OnClick="@OnBranchSession" />
                </Tooltip>
            </SpaceItem>
        }
        <SpaceItem>
            <Tooltip Title="多 Agent 会议大屏">
                <Button Type="@ButtonType.Text"
                        Size="@ButtonSize.Small"
                        Icon="@IconType.Outline.VideoCamera"
                        OnClick="@GoToConference" />
            </Tooltip>
        </SpaceItem>
        <SpaceItem>
            <Tooltip Title="新建会话">
                <Button Type="@ButtonType.Text"
                        Size="@ButtonSize.Small"
                        Icon="@IconType.Outline.Plus"
                        OnClick="@CreateNewSessionAsync"
                        Disabled="@(ctx.IsExecuting || ctx.IsSubAgentView)" />
            </Tooltip>
        </SpaceItem>
    </Space>
</ToolbarRightActions>
```

**要点：**
- 原 Header 块内 `@* 页级导航区注释 *@` 一并删除
- 原桌面端移动端 if/else（第 60-112 行 `@if (!AppState.IsMobile)` 分支）**全部删除**——移动端不再有"新建+更多菜单"独立分支，插槽按钮平铺由工具栏 flex-wrap 兜底
- 原第 116-128 行移动端溢出面板块删除

- [ ] **Step 2: 删除移动端溢出面板状态与切换方法**

删除 `Session.razor` 第 136 行：

```csharp
private bool _showMobileHeaderPanel;
```

删除第 242-246 行：

```csharp
/// <summary>移动端切换溢出面板</summary>
private void ToggleMobileHeader()
{
    _showMobileHeaderPanel = !_showMobileHeaderPanel;
}
```

- [ ] **Step 3: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI 2>&1 | Select-String -Pattern "error"
```

Expected: 无输出。若报 `ToggleMobileHeader`/`_showMobileHeaderPanel` 未使用警告，确认已删干净（razor 中无残留引用）。

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Session.razor
git commit -m "feat(webui): Session 页移除 Header 行，页级导航注入工具栏插槽"
```

### Task 4: 样式清理与插槽容器样式

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css:13-71`（header 系列）、`session-page.css:279-326`（responsive 与移动端面板）

- [ ] **Step 1: 删除 Session 专用 header 样式**

删除 `session-page.css` 中以下**仅 Session 使用**的样式（第 13-31 行 `.session-header`、`.session-header-left`；第 68-71 行 `.session-header-right`；第 302-326 行 `.session-header-mobile-panel`、`.mobile-panel-row`、`.mobile-panel-actions`、`.mobile-header-more-btn`）：

```
.session-header { ... }          // 删除
.session-header-left { ... }     // 删除
.session-header-right { ... }    // 删除
.session-header-mobile-panel { ... }  // 删除
.mobile-panel-row { ... }        // 删除
.mobile-panel-actions { ... }    // 删除
.mobile-header-more-btn { ... }  // 删除
```

响应式块（第 279-286 行）内 `.session-header`、`.session-title` 两段：
- `.session-header` 段删除
- `.session-title` 段**保留**（SessionsPage 首页复用）

**保留**（勿删）：`.session-title`（第 33-44 行，首页复用）、`.session-workspace-badge`（工具栏复用）、`.subagent-meta-*`（工具栏复用）、`.session-toolbar-*`。

- [ ] **Step 2: 新增插槽容器样式**

在 `.session-toolbar-right` 规则（第 143-148 行）之后追加：

```css
.session-toolbar-left-actions {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    flex-shrink: 0;
}

.session-toolbar-right-actions {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    flex-shrink: 0;
}
```

- [ ] **Step 3: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI 2>&1 | Select-String -Pattern "error"
```

Expected: 无输出（CSS 改动不影响编译，验证 build 仍通过即可）

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css
git commit -m "style(webui): 清理 Session 页 header 样式，新增工具栏插槽容器样式"
```

### Task 5: 回归测试 + Playwright 功能验证

**Files:**
- No code changes

- [ ] **Step 1: 全量回归单测**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```

Expected: 115/115 PASS

- [ ] **Step 2: Playwright 验证 T1（普通会话页单行工具栏）**

启动 WebUI（用户已自行启动则复用）后打开 `/session/{id}`，验证：
- 页面顶部只有**一行**工具栏，无独立 `.session-header` 行
- 工具栏含 首页/分支/大屏/新建（插槽）+ 标题/Agent/模型/重命名/清空（内置）
- 切换 Agent/模型不闪回（回归上次修复）

- [ ] **Step 3: Playwright 验证 T2（插槽按钮可用）**

点击 首页/新建/大屏入口/分支，验证导航行为正确。

- [ ] **Step 4: Playwright 验证 T3（Conference 零改动）**

打开 `/conference/{id}`，验证主窗口工具栏无扩展插槽按钮、内置操作完整。

- [ ] **Step 5: Playwright 验证 T4（移动端视口）**

视口设为 375×667，打开 `/session/{id}`，验证插槽按钮平铺渲染、工具栏可换行、无 `.session-header-mobile-panel`。

- [ ] **Step 6: 完成确认**

汇报验证结果，无问题后本计划完成。

---

## 自审记录

- **Spec 覆盖：** §4.3（Task 1）、§4.4（Task 2）、§4.5（Task 3）、§4.6（Task 4）、§6（Task 5）——全部覆盖。
- **占位符：** 无 TBD/TODO，所有代码/命令完整给出。
- **类型一致性：** `LeftActions`/`RightActions`（SessionToolbar）与 `ToolbarLeftActions`/`ToolbarRightActions`（SessionWindow 透传）在 Task 1/2/3 中一致；插槽均渲染 `@xxx(Context)` 传 `SessionWindowContext`。
