# 会话页双栏化设计

**日期:** 2026-08-28
**状态:** 已批准（用户确认后）
**范围:** `samples/Seeing.Agent.WebUI`（Session.razor + 内联样式）

---

## 1. 背景与目标

会议大屏（`/conference`）验证了"主会话 + 侧栏子会话卡片"双栏布局的价值。现决定将双栏布局合入会话页（`/session/{id}`），使日常会话页面直接获得子会话侧栏能力。

**关键约束（用户确认）：**

- 双栏布局独立实现于会话页，**不抽共享组件**；大屏页保留现状，后续用于新特性开发，二者互不影响。
- 侧栏子会话来源复用 `ConferenceRegistry`（订阅主会话流，自动枚举/识别 task 子会话）。
- 头部继续使用会话页现有的 `SessionToolbar`（Agent/模型切换、首页/分支/大屏/新建插槽），**不使用**大屏的 conference-topbar。
- 侧栏卡片点击行为与大屏一致：原地切换主窗 active 会话，URL 不变。
- 大屏页 `/conference` 保留不动，跳转入口保留。
- 无子会话时隐藏侧栏，主窗全宽。

## 2. 设计详情

### 2.1 Session.razor 布局改造

当前结构（单栏）：

```
<div class="session-page-container">
    <SessionWindow ViewMode="Full" ...>
        <ToolbarLeftActions>首页</ToolbarLeftActions>
        <ToolbarRightActions>分支/大屏/新建</ToolbarRightActions>
    </SessionWindow>
</div>
```

改造为双栏：

```
<div class="session-page-container">
    <div class="session-dual-grid">
        <div class="session-main-area">
            <SessionWindow ViewMode="Full" SessionId="@_activeSessionId" AggregatorSessionId="@_mainSessionId" ...>
                <ToolbarLeftActions>首页（SyncCurrentSessionId）</ToolbarLeftActions>
                <ToolbarRightActions>分支/大屏/新建</ToolbarRightActions>
            </SessionWindow>
        </div>
        @if (SideWindows.Count > 0)
        {
            <div class="session-side-area">
                @foreach (var node in SideWindows)
                {
                    <SessionWindow @key="@node.SessionId" ViewMode="Summary"
                                   SessionId="@node.SessionId" AggregatorSessionId="@_mainSessionId"
                                   OnClick="@OnTileClick" ... />
                }
            </div>
        }
    </div>
</div>
```

### 2.2 数据流（接线 ConferenceRegistry）

Session.razor 新增注入：

- `SessionEventStreamRouter Router`
- `CircuitContext CircuitContext`

新增私有字段/逻辑：

- `_mainSessionId`：URL 路由主会话 ID（`SessionId` 参数）
- `_activeSessionId`：当前主窗展示的会话 ID，默认 = `_mainSessionId`，点击侧栏卡片原地切换（URL 不变）
- `_windows`：子会话窗口集合（registry.Windows）
- `InitRegistry(mainId)`：按 circuit 获取 `ConferenceRegistry`、`Rebind(mainId)`、订阅 `WindowsChanged`
- `SideWindows`：非 active 的窗口，主会话（Root）置顶
- `OnTileClick(string id)`：`_activeSessionId = id` + `StateHasChanged`
- 路由参数变化（`OnParametersSetAsync`）时 `Rebind` 换父

### 2.3 主窗 SessionWindow 参数

- `SessionId="@_activeSessionId"`：主窗随 active 切换
- `AggregatorSessionId="@_mainSessionId"`：恒为 URL 主会话，供 Task 聚合器定位父流
- `@key="_activeSessionId"`：切换时重建组件加载新会话
- 原 `AggregatorSessionId="@(SessionId ?? "")"` 语义保持一致

### 2.4 样式

会话页内联 `<style>` 新增（不共享大屏 CSS）：

- `.session-dual-grid`：flex 容器，`flex:1; overflow:hidden`
- `.session-main-area`：`flex:1; min-width:0; padding-right:8px`
- `.session-side-area`：`width:320px; min-width:280px; flex column; gap:8px; overflow-y:auto; border-left:1px solid var(--color-border); padding-left:8px`
- 侧栏 Summary 卡片复用 SessionWindow 已有的 `.conference-tile*` 样式（Summary 模式渲染同款结构，含 `conference-tile--main` 主会话样式、header 分割线、父会话行）

**说明**：`.conference-tile*` 样式目前定义在 Conference.razor 内联 `<style>` 中，SessionWindow 的 Summary 分支依赖这些类。会话页双栏化后同样需要这些样式 → 需将 `.conference-tile*` 系列样式迁移到共享 CSS 文件（如 `session-page.css` 或独立文件），供两个页面共用；或两页各自内联。为满足"独立、互不影响"约束，采用**共享 CSS 文件**承载 `.conference-tile*`（纯样式无逻辑，不影响页面独立演进）。

## 3. 文件改动清单

| 文件 | 改动 |
|------|------|
| `samples/Seeing.Agent.WebUI/Pages/Session.razor` | 单栏改双栏；注入 Router/CircuitContext；接线 ConferenceRegistry；active 切换 |
| `samples/Seeing.Agent.WebUI/Pages/Conference.razor` | 移除内联 `.conference-tile*` 样式（迁至共享 CSS），其余不动 |
| `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css` | 新增共享 `.conference-tile*` 系列样式 + 会话页双栏布局样式 |

## 4. 测试策略

- 无 bUnit：接线/排序/切换为 razor 层逻辑，无法走组件单测。
- 回归：`Seeing.Agent.WebUI.Tests` 117/117 全绿 + `dotnet build` 0 错误。
- 人工验证（Playwright 或手测）：
  1. 会话页有子会话时：双栏 + 分割线 + 主会话卡片置顶（淡蓝底左边条）+ 子会话卡片「父会话」行。
  2. 无子会话时：侧栏隐藏，主窗全宽。
  3. 点击侧栏卡片：该会话切换为主窗，URL 不变；原主会话降为侧栏卡片。
  4. 大屏页 `/conference` 仍可用。

## 5. 非目标（本期不做）

- 移除大屏页。
- 侧栏可折叠/拖拽宽度。
- 卡片拖拽排序。
- 子会话树形展开。
