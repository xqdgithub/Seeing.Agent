# 会话窗口内置工具栏设计（SessionWindow Built-in Toolbar）

**日期:** 2026-08-28
**状态:** 已签字（设计评审通过）
**关联:** 2026-08-28-multi-agent-conference-design.md（大屏）

---

## 1. 背景与问题

多 Agent 会议大屏（`/conference/{sessionId}`）渲染 `SessionWindow`（Full/Summary 模式）时未提供 `Header` 模板，导致大屏上没有任何会话操作控件——无法切换 Agent/模型、清空会话、重命名等。

现状：
- `Session.razor` 的 `<Header>` 模板承载所有操作 UI（Agent/模型选择、ACP Mode/Model、重命名、分支、清空、出站绑定等）
- `Conference.razor` 不传 Header → 大屏操作缺失
- `SessionWindow` 内部**已实现**全部操作逻辑（`OnAgentChanged`/`OnModelChanged`/`OnClearSession` 等），仅 UI 由外层页面提供

## 2. 目标

让 `SessionWindow`（Full 模式）**内置**核心会话操作工具栏，使任何使用场景（普通会话页、会议大屏）自动获得一致的操作能力。

## 3. 非目标（YAGNI）

- Summary 摘要卡片不做任何操作（保持纯只读，点击切换主窗口）
- 不加 `ShowToolbar` 开关参数（暂无关闭场景）
- 不改动 SessionWindow 内部已有的操作逻辑（复用 Context 委托）

## 4. 设计决策

### 4.1 需求确认（用户已选定）

| 决策点 | 选择 |
|--------|------|
| 内置信息与操作范围 | 标题 + 工作文件夹徽标 + 状态徽标 + Agent/模型选择、重命名、清空、出站绑定 |
| 显示模式 | 仅 Full 模式（Summary 纯只读） |
| Session 页处理 | 统一迁移到内置工具栏（避免两套 UI） |
| 页级导航保留 | 首页/大屏入口/新建会话/分支（标题、工作区徽标、状态徽标均由内置工具栏承载） |
| 边界场景 | 完整处理（出站绑定进工具栏；子会话只读按钮保留；分支移出工具栏回页头） |
| 实现方案 | 方案 A：内置 + 可覆盖（Header 模板可覆盖默认工具栏区域） |

### 4.2 布局结构

```
SessionWindow (Full 模式)
├── [可选] Header 模板（外层页提供页级导航区，无则跳过）
├── [内置] SessionToolbar（默认渲染）
│   ├── 左：标题 + 工作文件夹徽标 + 状态徽标 + Agent 选择器 + 模型选择器（ACP 透传时切 Mode/Model 双输入）
│   └── 右：重命名 / 清空 / 出站绑定 /（子会话时: 返回主会话 + 分叉为独立会话）
├── 消息区（现有）
├── 输入区（现有）
└── Todo 侧栏（现有）
```

- `Header` 模板语义变为**页级导航区**（放首页/大屏入口/新建/分支等页级内容）
- 内置工具栏为**默认渲染**：外层提供 Header 则 Header 在上、工具栏在下叠加；不提供则仅工具栏
- 工具栏操作复用已有的 `Context` 委托（`SetAgentAsync`/`SetModelAsync`/`SetAcpMode`/`RenameAsync`/`BranchAsync`/`ClearAsync`/`GetWorkspace`/`SaveOutboundAsync`），**不改动窗口内部操作逻辑**

### 4.3 SessionToolbar 组件

新建 `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`：

**输入：**
- `SessionWindowContext Context`（状态 + 操作委托，`[EditorRequired]` 必填）
- `SessionWindowMode Mode`（仅 Full 渲染；Summary 不渲染）

**职责：**
- 自加载选项：Agent 名称列表（`AgentRegistry.GetPrimaryAgentsAsync()`）、模型列表（`LlmService.GetAvailableModels()` + `AppState.AvailableModels`）；30s 静态缓存防大屏切换重建时重复查询
- 渲染控件：
  - 左侧：标题（`Context.Title`）+ 工作文件夹徽标（`Context.GetWorkspace()`）+ 状态徽标（执行中/排队/子会话）+ Agent 选择器 + 模型选择器；`Context.IsAcpPassthrough` 时切换为 ACP Mode/Model 双输入框
  - 右侧：重命名 / 清空 / 出站绑定按钮；`IsSubAgentView` 时重命名/清空禁用，显示"返回主会话" + "分叉为独立会话"
  - 分支按钮不在此（非会话强相关，归页级导航，回 Session 页头）
- 重命名 Modal、出站绑定 Modal 由 Session.razor 迁入 SessionToolbar 内部；出站绑定经 `Context.SaveOutboundAsync` 委托持久化（保持纯 UI 契约）

**SessionWindow 接入：** 在 Full 分支加 `<SessionToolbar Context="Context" Mode="ViewMode" />`。

### 4.4 Session.razor 改造

- `<Header>` 模板精简为页级导航区：首页按钮、大屏入口（VideoCamera）、新建会话、分支会话
- 标题、工作区徽标、状态徽标均不再在页头渲染（由内置工具栏承载）
- 移除：Agent 选择器、模型选择器、ACP Mode/Model 输入、重命名、清空、出站绑定（全部由内置工具栏接管）
- `_agentNames`/`_models` 加载逻辑移至 SessionToolbar；页面删除 `LoadOptionsAsync` 中的相关代码
- 移除重命名/出站绑定 Modal 及对应方法（迁至 SessionToolbar）
- 保留：`GoToHome`、`GoToConference`、`CreateNewSessionAsync`、`OnBranchSession`、`SyncCurrentSessionId`、移动端溢出面板（仅保留页级导航项）

### 4.5 Conference.razor 改造

- 不传 Header 模板 → 主窗口（Full）自动获得内置工具栏
- Summary 卡片不变（纯只读）
- 顶栏保留：返回会话页、标题、实时徽标

### 4.6 数据流验证

大屏主窗口切换（点击 Summary 卡片 → `OnTileClick` 改 `_activeSessionId`）后，主窗口 `SessionWindow` 的 `@key` 变化触发重建，工具栏随新激活会话加载——操作作用于当前主窗口会话。

## 5. 错误处理

- Agent/模型选项加载失败：`catch` 后置空列表（与现有 Session.razor `LoadOptionsAsync` 一致），不阻塞会话渲染
- 重命名/出站绑定表单校验：沿用现有 Session.razor 逻辑（非空校验）

## 6. 测试

### 6.1 单元测试（xUnit + VSTest）

> **决策记录（2026-08-28）**：本项目测试栈无 bUnit，razor 组件渲染无法用 xUnit+Moq 直接测试。
> SessionToolbar 为纯渲染组件（无独立业务逻辑），§6.1 原 5 个渲染用例改为由 Playwright 功能验证（§6.2）覆盖；
> 单测保持既有 115 项回归（Context 委托、ConferenceRegistry、SessionWindow 周边服务不变）。

### 6.2 功能验证（Playwright + Edge）

| 用例 | 预期 |
|------|------|
| T1 普通会话页 | 页头含 首页/分支/大屏入口/新建；工具栏含 标题/工作文件夹/Agent/模型/重命名/清空 |
| T2 切换 Agent/模型 | 会话持久化（刷新保留） |
| T3 大屏 | 主窗口（Full）显示内置工具栏，Summary 卡片纯只读 |
| T4 大屏切换主窗口 | 点击 Summary → 工具栏随激活会话变化 |
| T5 工具栏清空会话 | 子会话连带删除 |
| T6 回归 | 既有 115 项单测全绿 |

## 7. 范围边界

- 涉及文件：`SessionToolbar.razor`（新建）、`SessionWindow.razor`、`Session.razor`、`Conference.razor`、WebUI 测试
- 不涉及：主库 `Seeing.Agent`、`Seeing.Session`、SessionWindow 内部操作逻辑
