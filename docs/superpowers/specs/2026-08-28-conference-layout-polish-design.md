# 会议大屏布局优化设计

**日期:** 2026-08-28
**状态:** 已批准（用户确认后）
**范围:** `samples/Seeing.Agent.WebUI`（Conference.razor + SessionWindow.razor + 内联样式）

---

## 1. 背景与问题

会议大屏（`/conference/{id}`）当前存在以下展示问题：

1. **无分割线**：主会话窗口与侧边栏之间只有 `gap: 8px`，无视觉边界，主次关系不清晰。
2. **无子会话时侧栏仍占位**：即使没有任何子会话，`.conference-side-area` 仍渲染（空块），主窗口无法全宽展示。
3. **主/子会话卡片无区分**：当点击某子会话后，主会话会以 Summary 卡片形式进入侧边栏，但与子会话卡片样式完全一致，难以快速定位主会话。
4. **卡片内部层次弱**：卡片只有标题（加粗）+ 摘要两行，标题与摘要之间无分割线，信息层次不分明。
5. **子会话缺乏父会话关联信息**：无法在卡片上看出该子会话属于哪个主会话。

## 2. 设计目标

- 主窗口与侧边栏之间用细分割线区分，主次一目了然。
- 无子会话时隐藏侧栏，主窗口全宽展示；有子会话时恢复双栏。
- 侧边栏中的主会话卡片与子会话卡片在样式上有高区分度（左边条 + 淡蓝底）。
- 卡片内部增加分割线，层次分明；子会话卡片显示父会话关联。

## 3. 设计详情

### 3.1 布局调整（Conference.razor + 内联样式）

**分割线：**

- `.conference-grid` 移除 `gap: 8px`（改由侧栏自身间距承担）。
- `.conference-side-area` 增加 `border-left: 1px solid var(--color-border)`，并保留自身 `padding-left` 以维持原间距观感。

**无子会话隐藏侧栏：**

- Conference.razor 渲染时计算侧栏候选窗口 `SideWindows`（`AllWindows` 中非 active 的节点）。
- 当 `SideWindows` 为空时不渲染 `.conference-side-area`，主窗口（`.conference-main-area`）自然全宽。
- 顶部 topbar 保持不变。

### 3.2 主会话卡片区分 + 置顶

**判定方式：**

- `SessionWindow` 新增私有属性 `IsMainSession`：
  ```
  IsMainSession => EffectiveSessionId == ResolveAggregatorSessionId()
  ```
  （大屏场景 `AggregatorSessionId` 恒为主会话 ID，因此侧栏中主会话卡片该属性为 true，子会话为 false。）

**样式区分：**

- Summary 卡片根元素 `.conference-tile` 条件追加 class `conference-tile--main`（仅主会话）。
- `.conference-tile--main` 样式：
  - `border-left: 3px solid var(--color-primary)`（主题色左边条）
  - 背景淡蓝底（`var(--color-primary)` 低透明度，如 `color-mix(in srgb, var(--color-primary) 8%, var(--color-bg-container))`，无 color-mix 支持时用 rgba 兜底）
- 子会话卡片保持白底无左边条。
- hover 全框蓝边两种卡片均保留（现有 `.conference-tile:hover`）。

**主会话置顶：**

- 侧栏渲染顺序：`SideWindows` 排序，主会话（Root）永远排第一，其余按原序。

### 3.3 卡片内部层次（SessionWindow Summary 分支）

当前结构（`SessionWindow.razor:163-171`）：

```razor
<div class="conference-tile" @onclick="OnTileClick">
    <div class="conference-tile-header">
        <span class="conference-tile-title">@SessionState.Title</span>
        <Badge Status="@GetStatusBadge()" />
    </div>
    <div class="conference-tile-body">
        @GetLatestAssistantPreview()
    </div>
</div>
```

调整为：

```razor
<div class="@($"conference-tile {(IsMainSession ? "conference-tile--main" : "")}")" @onclick="OnTileClick">
    <div class="conference-tile-header">
        <span class="conference-tile-title">@SessionState.Title</span>
        <Badge Status="@GetStatusBadge()" />
    </div>
    <div class="conference-tile-body">
        @GetLatestAssistantPreview()
    </div>
    @if (!IsMainSession && !string.IsNullOrEmpty(GetParentSessionTitle()))
    {
        <div class="conference-tile-parent">
            <Icon Type="@IconType.Outline.Link" />
            <span>父会话：@GetParentSessionTitle()</span>
        </div>
    }
</div>
```

**CSS 调整：**

- `.conference-tile-header` 增加 `border-bottom: 1px solid var(--color-border)` + `padding-bottom`（分割标题区与摘要区）。
- `.conference-tile-body` 增加 `padding-top` 小间距。
- 新增 `.conference-tile-parent`：摘要下置灰小字行，显示父会话标题。

### 3.4 父会话标题反查

`SessionWindow` 新增私有方法 `GetParentSessionTitle()`：

- 取 `SessionState.CurrentSession?.ParentSessionId`。
- 为空（主会话）返回 `string.Empty`（不渲染该行）。
- 非空则经 `SessionManager.Get(parentId)?.Title` 反查，找不到时返回 `string.Empty`。

## 4. 文件改动清单

| 文件 | 改动 |
|------|------|
| `samples/Seeing.Agent.WebUI/Pages/Conference.razor` | 移除 grid gap；side-area 加 border-left + padding-left；按 `SideWindows` 空判定隐藏侧栏；侧栏渲染主会话置顶排序 |
| `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor` | 新增 `IsMainSession` 属性、`GetParentSessionTitle()` 方法；Summary 分支改卡片结构（main class + 分割线 + 父会话行）；内联样式补齐 |
| `samples/Seeing.Agent.WebUI/Pages/Conference.razor`（样式块） | `.conference-tile--main`、`.conference-tile-header` border-bottom、`.conference-tile-parent` |

## 5. 测试策略

- 无 bUnit：`IsMainSession` 判定、排序、父会话反查均为 razor/CSS 层逻辑，无法走组件单测。
- 回归：现有 `Seeing.Agent.WebUI.Tests` 117/117 全绿 + `dotnet build` 0 错误。
- 人工验证（Playwright 或用户手测）：
  1. 有子会话时：双栏 + 分割线 + 主会话卡片左边条淡蓝底且置顶 + 子会话卡片显示「父会话」行。
  2. 无子会话时：侧栏隐藏，主窗口全宽。
  3. 点击子会话：该子会话进入主窗，主会话降为侧栏卡片（带主会话样式），其余子会话保持白底。

## 6. 非目标（本期不做）

- 侧栏可折叠/可拖拽宽度。
- 卡片拖拽排序。
- 子会话树形展开。
- 移动端专门布局（沿用现有 flex 兜底）。
