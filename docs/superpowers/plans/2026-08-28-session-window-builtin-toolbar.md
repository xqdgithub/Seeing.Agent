# 会话窗口内置工具栏实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 SessionWindow（Full 模式）内置核心会话操作工具栏（Agent/模型切换、重命名、分支、清空、出站绑定），使普通会话页与会议大屏自动获得一致的操作能力。

**Architecture:** 新建 `SessionToolbar.razor` 组件（接收 `SessionWindowContext` + `SessionWindowMode`，仅 Full 模式渲染），SessionWindow 在 Full 分支内置渲染；Session.razor 页头精简为页级导航；Conference.razor 不传 Header 自动获得工具栏。

**Tech Stack:** Blazor Server net10.0、AntDesign 1.6.2、xUnit 3 + Moq + FluentAssertions、Playwright

**Spec:** `docs/superpowers/specs/2026-08-28-session-window-builtin-toolbar-design.md`

---

### Task 1: 新建 SessionToolbar.razor 组件

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`

- [ ] **Step 1: 创建组件文件**

从 Session.razor 迁移核心操作 UI。组件接收 `SessionWindowContext` 与 `SessionWindowMode`，仅 Full 模式渲染。包含：状态徽标、Agent 选择器、模型选择器（ACP 透传切 Mode/Model 输入）、重命名/分支/清空/出站绑定按钮、子会话只读视图。含重命名 Modal 与出站绑定 Modal。自加载 Agent/模型选项。

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor
git commit -m "feat(webui): 新增 SessionToolbar 内置工具栏组件"
```

---

### Task 2: SessionWindow 接入内置工具栏

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`

- [ ] **Step 1: 在 Full 分支加工具栏渲染**

在 `SessionWindow.razor` 的 Full 分支（`@if (ViewMode == Services.SessionWindowMode.Full)` 内）Header 之后、`.session-content-wrapper` 之前插入：

```razor
<SessionToolbar Context="Context" Mode="ViewMode" />
```

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "feat(webui): SessionWindow Full 模式内置渲染 SessionToolbar"
```

---

### Task 3: Session.razor 页头精简为页级导航

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`

- [ ] **Step 1: 精简 Header 模板**

Header 模板移除 Agent 选择器、模型选择器、ACP Mode/Model 输入、重命名/分支/清空/出站绑定按钮与 Modal；仅保留：首页按钮、会话标题、状态徽标（执行中/排队/子会话）、工作区徽标、大屏入口（VideoCamera）、新建会话、移动端溢出面板页级项。

- [ ] **Step 2: 清理 @code 中已迁移逻辑**

删除 `_agentNames`/`_models`/`_optionsReady` 加载、`ModelViewModel` 类、`OnAgentChanged`/`OnModelChanged`/`OnAcpModeChanged`/`OnRenameSession`/`OnRenameConfirm`/`OnRenameCancel`/`OnBranchSession`/`OnClearSession`/`OnEditOutbound`/`OnOutboundConfirm`/`OnOutboundCancel`/`NormalizeOutbound` 及相关表单类与 Modal 状态；保留 `GoToHome`/`GoToConference`/`CreateNewSessionAsync`/`SyncCurrentSessionId`/`TruncateWorkspacePath` 等页级方法。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Session.razor
git commit -m "refactor(webui): Session 页头精简，核心操作迁移至内置工具栏"
```

---

### Task 4: Conference.razor 验证（无需改动，仅确认）

**Files:**
- 无改动

- [ ] **Step 1: 确认 Conference 不传 Header**

Conference.razor 已不传 `<Header>` → 主窗口（Full）自动获得内置工具栏；Summary 卡片保持纯只读。无需代码改动。

---

### Task 5: 构建 + 单测回归

**Files:**
- 无新测试文件（工具栏为纯渲染组件，行为由既有 115 单测回归 + Playwright 功能验证覆盖）

- [ ] **Step 1: 构建**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 0 错误

- [ ] **Step 2: 单测回归**

```bash
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```
Expected: 115/115 通过

- [ ] **Step 3: 提交（如构建有格式修正）**

---

### Task 6: Playwright 功能验证

**Files:**
- 临时文件（验证后清理）

- [ ] **Step 1: 启动 WebUI + Playwright 脚本**

验证：普通会话页页头含 首页/标题/大屏入口/新建 + 工具栏含 Agent/模型/重命名/分支/清空；大屏主窗口显示工具栏、Summary 纯只读；切换主窗口工具栏随激活会话变化；工具栏清空会话连带删除子会话。

- [ ] **Step 2: 停止 WebUI、清理临时文件**

---

### Task 7: 整体审查

- [ ] **Step 1: 复核 spec 每项需求均已实现**
- [ ] **Step 2: 提交最终改动**

---

## 自审记录

- **Spec 覆盖**：§4.3 SessionToolbar（Task 1）、§4.2 SessionWindow 接入（Task 2）、§4.4 Session.razor（Task 3）、§4.5 Conference（Task 4）、§6 测试（Task 5-6）、整体审查（Task 7）。
- **范围**：仅涉及 WebUI 前端文件与测试，未触及主库/Seeing.Session。
- **注意**：Task 3 删除大量 @code 后需保证页面无孤立引用（如 `_optionsReady` 在移动端面板的残留），实施时逐项核对。
