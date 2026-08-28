# 会话工具栏插槽扩展设计（SessionToolbar Slot Extension）

**日期:** 2026-08-28
**状态:** 待签字（设计评审）
**关联:** 2026-08-28-session-window-builtin-toolbar-design.md（内置工具栏）

---

## 1. 背景与问题

内置工具栏（`SessionToolbar`）落地后，`Session.razor` 仍有**两行头**：

1. 第一行：`SessionWindow` 的 `<Header>` 模板（页级导航：首页/分支/大屏入口/新建会话）
2. 第二行：`SessionToolbar`（标题/工作文件夹/Agent/模型/重命名/清空/出站绑定）

希望让 `Session.razor` 把自己的页级导航**注入到工具栏插槽**，合并为一行；而 `Conference` 等其他使用方**不传插槽也能正常使用**（工具栏保持默认渲染，与现状完全一致）。

## 2. 目标

- `SessionToolbar` 增加左右两个扩展插槽（`RenderFragment<SessionWindowContext>`）
- `Session.razor` 移除 `<Header>` 模板，把页级导航注入插槽，消除两行头
- 不传插槽的调用方（Conference 等）零改动、零影响

## 3. 非目标（YAGNI）

- 不做移动端设备分支/自动溢出折叠（工具栏 `flex-wrap` 自然换行兜底即可）
- 不新增 `ShowToolbar`/`ShowActions` 开关
- 不改动 SessionToolbar 已有的操作逻辑（复用 Context 委托）

## 4. 设计决策

### 4.1 需求确认（用户已选定）

| 决策点 | 选择 |
|--------|------|
| 插槽方案 | 方案 A：左右双插槽（`LeftActions` + `RightActions`） |
| 插槽类型 | `RenderFragment<SessionWindowContext>`（带 Context 上下文，按钮可用 `ctx.IsSubAgentView`/`ctx.IsExecuting` 等状态） |
| 渲染位置 | `LeftActions` 在工具栏左侧**最前**（标题之前）；`RightActions` 在工具栏右侧**最后**（重命名/清空/出站绑定之后） |
| SessionWindow.Header 参数 | **保留**（作为扩展能力），但 `Session.razor` 本次实现不再使用 |
| 移动端 | 不区分设备，插槽按钮直接平铺渲染，渲染不下由 flex-wrap 换行兜底，不需要特殊处理 |

### 4.2 布局结构（改造后）

```
SessionWindow (Full 模式)
├── [可选] Header 模板（保留参数，本次无调用方；无则跳过）
├── [内置] SessionToolbar
│   ├── 左：LeftActions 插槽（若提供）→ 标题 → 工作文件夹徽标 → 状态徽标 → Agent/模型选择器
│   └── 右：返回主会话/重命名/分支/清空/出站绑定 → RightActions 插槽（若提供）
├── 消息区（现有）
├── 输入区（现有）
└── Todo 侧栏（现有）
```

### 4.3 SessionToolbar 组件改动

新增参数（`samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`）：

```csharp
[Parameter] public RenderFragment<SessionWindowContext>? LeftActions { get; set; }
[Parameter] public RenderFragment<SessionWindowContext>? RightActions { get; set; }
```

渲染位置（仅 Full 分支）：

- `LeftActions`：`.session-toolbar-left` 容器最前，位于标题 `<Tooltip>` 之前
- `RightActions`：`.session-toolbar-right` 容器最后，位于清空按钮之后

两参数均为 `null` 时（Conference 等），工具栏渲染与现状完全一致。

### 4.4 SessionWindow 改动

新增透传参数（`samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`）：

```csharp
[Parameter] public RenderFragment<SessionWindowContext>? ToolbarLeftActions { get; set; }
[Parameter] public RenderFragment<SessionWindowContext>? ToolbarRightActions { get; set; }
```

Full 分支渲染工具栏时透传：

```razor
<SessionToolbar Context="Context"
                Mode="ViewMode"
                LeftActions="ToolbarLeftActions"
                RightActions="ToolbarRightActions" />
```

`Header` 参数保留不动（向后兼容），本次实现不新增调用方。

### 4.5 Session.razor 改造

- **移除** `<Header Context="ctx">` 整个块（含移动端溢出面板 `.session-header-mobile-panel`）
- **移除** `_showMobileHeaderPanel`、`ToggleMobileHeader` 及对应样式
- 改为传插槽：

```razor
<SessionWindow ...>
    <ToolbarLeftActions Context="ctx">
        @{ SyncCurrentSessionId(ctx); }
        <Button Type="ButtonType.Text" Size="ButtonSize.Small" Icon="IconType.Outline.Home" OnClick="GoToHome">
            @if (!AppState.SidebarCollapsed && !AppState.IsMobile) { <span>首页</span> }
        </Button>
    </ToolbarLeftActions>
    <ToolbarRightActions Context="ctx">
        <Space>
            @if (!ctx.IsSubAgentView)
            {
                <SpaceItem><Tooltip Title="分支会话">
                    <Button Type="ButtonType.Text" Size="ButtonSize.Small" Icon="IconType.Outline.Copy" OnClick="OnBranchSession" />
                </Tooltip></SpaceItem>
            }
            <SpaceItem><Tooltip Title="多 Agent 会议大屏">
                <Button Type="ButtonType.Text" Size="ButtonSize.Small" Icon="IconType.Outline.VideoCamera" OnClick="GoToConference" />
            </Tooltip></SpaceItem>
            <SpaceItem><Tooltip Title="新建会话">
                <Button Type="ButtonType.Text" Size="ButtonSize.Small" Icon="IconType.Outline.Plus" OnClick="CreateNewSessionAsync" Disabled="@(ctx.IsExecuting || ctx.IsSubAgentView)" />
            </Tooltip></SpaceItem>
        </Space>
    </ToolbarRightActions>
</SessionWindow>
```

- `SyncCurrentSessionId(ctx)` 移入 `ToolbarLeftActions` 插槽闭包（插槽带 ctx）
- **保留**：`GoToHome`、`GoToConference`、`CreateNewSessionAsync`、`OnBranchSession`、`SyncCurrentSessionId`、`LoadToolsAndSkillsAsync`
- **移除**：`ToggleMobileHeader`、`_showMobileHeaderPanel`

### 4.6 样式

- 删除 `.session-header`、`.session-header-left`、`.session-header-right`、`.session-header-mobile-panel`、`.mobile-panel-*` 等不再使用的样式（`wwwroot/css/session-page.css`）
- 新增最小样式 `session-toolbar-left-actions` / `session-toolbar-right-actions`（flex 容器，gap 与工具栏一致；如插槽内含 `Space` 可酌情不加容器类）

### 4.7 Conference.razor

不传插槽，零改动。主窗口（Full）自动获得内置工具栏，无页级导航按钮。

## 5. 错误处理

- 插槽为空（null）时不渲染任何占位——无影响
- 插槽内容异常由 Blazor 渲染管线兜底，不新增 try/catch

## 6. 测试

### 6.1 单元测试（xUnit + VSTest）

> 决策记录：本项目测试栈无 bUnit，razor 组件渲染无法用 xUnit+Moq 直接测试。
> 插槽为纯渲染结构，单测保持既有 115 项回归（Context 委托、SessionWindow 周边服务不变）。

### 6.2 功能验证（Playwright + Edge）

| 用例 | 预期 |
|------|------|
| T1 普通会话页 | 页面顶部只有**一行**工具栏：含 首页/分支/大屏入口/新建（插槽）与 标题/Agent/模型/重命名/清空（内置）；无 `.session-header` 独立行 |
| T2 插槽按钮可用 | 首页/新建/大屏入口/分支 点击行为正确 |
| T3 大屏 | 主窗口工具栏无扩展插槽按钮，内置操作完整（回归） |
| T4 移动端（视口 < 768px） | 插槽按钮平铺渲染，工具栏可换行，无溢出面板 |
| T5 回归 | 既有 115 项单测全绿 |

## 7. 范围边界

| 纳入 | 不纳入 |
|------|--------|
| SessionToolbar 双插槽参数 | 移动端自动折叠/溢出下拉（后续按需增强） |
| SessionWindow 双插槽透传 | SessionWindow.Header 复用（保留参数，本次不用） |
| Session.razor 移除 Header 行 | 其他页面注入插槽（Conference 不注入） |
| 样式清理与最小插槽容器样式 | 新会话操作功能 |
