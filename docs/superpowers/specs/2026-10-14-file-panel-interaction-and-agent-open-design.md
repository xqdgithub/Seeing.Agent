# 文件面板交互与 agent 驱动打开设计

> **日期：** 2026-10-14
> **状态：** 已拍板（待实现计划）
> **范围：** `samples/Seeing.Agent.WebUI`、`samples/Seeing.Agent.WebUI.Files`、`src/capabilities/Seeing.Agent.Tools.FileSystem`、`src/capabilities/Seeing.Agent.Agents.BuiltIn`

## 1. 背景与目标

文件浏览器（见 `docs/architecture/16-session-file-explorer-release-notes.md`）已落地，但交互仍不理想：

- 顶部工具栏有一个孤立的「文件」按钮，与工作目录徽标功能重叠。
- 编辑器内「保存」按钮占一整行，视觉笨重。
- 各栏宽度固定（文件树 220px、面板 480px），无法按需调整。
- 目录树无法隐藏，窄屏/专注编辑时占地。
- agent 无法主动把某个文件推到用户眼前。

**目标：**

1. 文件面板开关并入工作目录徽标，移除工具栏「文件」按钮。
2. 保存改为小图标 + `Ctrl+S`。
3. 文件树 ↔ 编辑器、文件面板 ↔ 主聊天区两处分隔可拖动，宽度持久化。
4. 目录树可隐藏（面板内折叠按钮），状态持久化。
5. 新增 `open_file` 工具，让 agent 驱动前端打开/切换/刷新文件编辑器。

**非目标：**

- 不做多栏（>2）拖拽框架，不引入 AntDesign `Splitter`。
- 不改文件读写/权限门闸语义。
- 不新增 Abstractions 事件类型（复用现有 `ToolResult.Metadata` 桥接）。

## 2. 已拍板决策

| # | 议题 | 选择 |
|---|------|------|
| D1 | 面板开关入口 | 工作目录徽标点击切换；移除工具栏「文件」按钮与 `FilePanelToggle` 贡献 |
| D2 | 保存交互 | 标签栏右侧小图标按钮 + `Ctrl+S`；tooltip「保存 (Ctrl+S)」；脏时高亮 + 脏点，干净时禁用 |
| D3 | 拖动范围 | 文件树↔编辑器、面板↔聊天区 两处均支持 |
| D4 | 宽度持久化 | `localStorage`；刷新/重开保留 |
| D5 | 拖拽技术 | CSS 变量 + Pointer Events（自研 `ResizeHandle`），不用 AntDesign `Splitter` |
| D6 | 目录树隐藏入口 | 面板内文件树顶部折叠按钮；状态持久化 |
| D7 | agent 工具形态 | 新增 `open_file` 工具（`Tools.FileSystem` 包），非复用 write/edit 事件 |
| D8 | 「新打开不开树」语义 | agent 新开文件时不显示目录树（仅编辑器）；若树本已展开则尊重现状 |
| D9 | 面板关闭时 | agent `open_file` 自动打开面板 |
| D10 | 已打开行为 | 已是当前文件 → 从磁盘重读刷新；是其他已开标签 → 切换；新文件 → 新标签 |
| D11 | 工具归属 | `Seeing.Agent.Tools.FileSystem`（与 read/write 同包，只依赖 Abstractions） |
| D12 | 权限 | 视作只读工具，与 `read`/`grep`/`glob` 同构：在内置 Agent 白名单加 `Allow(Tool, "open_file")`，无特殊分支 |
| D13 | 桥接方式 | `ToolResult.Metadata["path"]` → 宿主 `AgentFileChangeBridge` 新增 `OpenRequested` 回调 |

## 3. 组件与职责

### 3.1 工作目录徽标（`SessionToolbar.razor`）

- `.session-workspace-badge` 由 `<span>` 改为可点击 `<button>`，注入 `FilePanelState`。
- 点击 `PanelState.Toggle(Context.SessionId)`；打开且会话匹配时加 `.active` 高亮。
- 保留 hover tooltip 显示完整路径；不再弹目录切换。
- 边界：`SessionId` 为空时不响应。

### 3.2 保存（`FileEditor.razor` / `FileTabs.razor`）

- 删除 `.file-editor-toolbar` 整行。
- 保存图标按钮移入 `.file-tabs` 右端（`FileTabs.razor` 或 `FileEditor` 的标签栏区域）。
- 键盘：容器 `@onkeydown` 捕获 `Ctrl+S` → `SaveAsync()` + `preventDefault`（JS 互操作抑制浏览器默认）。
- 冲突对话框（`HasExternalConflict`）逻辑不变。

### 3.3 可拖动分隔线（新增 `ResizeHandle.razor` + JS）

- 参数：`TargetVariable`（CSS 变量名）、`Orientation`（横向=调宽）、`Min`、`Max`、`StorageKey`。
- 行为：`pointerdown` 记录起点 → `pointermove` 实时写 `--var`（clamp 到 min/max）→ `pointerup` 写 `localStorage`。
- 初始化：`OnAfterRenderAsync` 读 `localStorage` 套用变量；缺省用 CSS 默认值。
- 拖拽中给 `body` 加 `.resizing`（`cursor: col-resize` + `user-select: none`）。
- 分隔线 1：文件树 ↔ 编辑器 → `--file-tree-width`（默认 220px）。
- 分隔线 2：文件面板 ↔ 聊天区 → `--file-panel-width`（默认 480px，替换 `session-page.css` 写死的 480px）。

### 3.4 目录树折叠（`FilePanelHost.razor`）

- 文件树顶部加折叠按钮，切换容器类（如 `.file-panel-body--no-tree`），隐藏时树宽归 0。
- 状态持久化（`localStorage`）；agent 新开文件时若树已隐藏则保持隐藏（D8）。

### 3.5 `open_file` 工具（`OpenFileTool.cs`）

- `Id = "open_file"`；参数 `path`（绝对路径，`PathGateHelper` 门闸）。
- 只做「请求前端打开」的语义：不读盘、不写盘；校验路径通过后回写 `ToolResult.Metadata["path"]`。
- 注册：`FileSystemModule` 加入 `ProvidedTools` + `ConfigureServices` + `ActivateAsync`。
- 权限：内置 Agent 白名单加 `Allow(Tool, "open_file")`（`plan`/`explore` 等受限 Agent）。

### 3.6 宿主桥接（`AgentFileChangeBridge.cs`）

- 新增 `event Func<FileResourceId, Task>? OpenRequested`。
- `OnEvent` 识别 `ToolCallEvent.ToolName == "open_file"` 且成功 → 解析 `Metadata["path"]` → 触发 `OpenRequested`。
- 与既有 `AgentChanged`（write/edit/delete）区分：`open_file` 只切/开，不标冲突。

### 3.7 面板处理（`SessionFilePanel.razor` / `FilePanelHost.razor`）

`OpenRequested(id)` 分派：
1. 面板未开 → `PanelState.Open(sessionId)`。
2. `id` 已是当前标签 → 从磁盘 `ReadAsync` 刷新（`ApplyDiskUpdate`）。
3. `id` 已在其他标签 → `Tabs.Activate(id)`。
4. 新文件 → `Tabs.Open(...)`；**不展开目录树**（尊重现状）。

## 4. 数据流

```
agent 调用 open_file(path)
  → OpenFileTool 校验门闸 → ToolResult.Metadata["path"]
  → ToolCallEvent(complete, open_file) 广播
  → AgentFileChangeBridge.OnEvent → OpenRequested(id)
  → SessionFilePanel → FilePanelState/Tabs 分派（开面板/切标签/刷新）
```

## 5. 错误处理

- `open_file` 路径越界 → 返回 `Failure`（门闸拒绝），前端无动作。
- 面板/工作区未就绪（`_workspace is null`）→ 忽略，不抛。
- 拖拽 localStorage 读取失败 → 回退 CSS 默认值。
- 刷新已打开文件读盘失败（如已被删）→ 标记 `IsDeleted`（复用现有逻辑）。

## 6. 测试策略

- 纯逻辑单测：`FilePanelState` 开合、`FileTabsState` 分派（新开/切换/刷新）、`ResizeHandle` 的 clamp 与 storage key 计算。
- `open_file` 工具：门闸拒绝 / 成功回写 `Metadata["path"]`。
- 桥接：`open_file` 事件 → `OpenRequested` 触发；write/edit 仍走 `AgentChanged`。
- 拖拽/折叠/快捷键：浏览器手测（`browser-use`）。
- 回归：`Seeing.Agent.Files.Tests`、`Seeing.Agent.WebUI.Files.Tests`、`Seeing.Agent.Tools.FileSystem.Tests`。

## 7. 影响面

| 文件 | 改动 |
|------|------|
| `samples/Seeing.Agent.WebUI/Services/FilePanelUiContribution.cs` | 移除工具栏 toggle 贡献 |
| `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor` | 徽标改按钮 |
| `samples/Seeing.Agent.WebUI.Files/Components/FilePanelToggle.razor` | 删除或留空 |
| `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor` | 去工具栏、加 Ctrl+S |
| `samples/Seeing.Agent.WebUI.Files/Components/FileTabs.razor` | 右侧保存图标 |
| `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor` | 折叠按钮 + OpenRequested 分派 |
| `samples/Seeing.Agent.WebUI.Files/Components/ResizeHandle.razor` | 新增 |
| `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css` | 变量化宽度、分隔线、折叠态 |
| `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css` | 面板宽度改用变量 |
| `samples/Seeing.Agent.WebUI/wwwroot/js/app.js`（或新 JS） | 拖拽/快捷键互操作 |
| `samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor` | 接 `OpenRequested` |
| `samples/Seeing.Agent.WebUI/Services/AgentFileChangeBridge.cs` | 新增 `OpenRequested` |
| `src/capabilities/Seeing.Agent.Tools.FileSystem/OpenFileTool.cs` | 新增 |
| `src/capabilities/Seeing.Agent.Tools.FileSystem/FileSystemModule.cs` | 注册 |
| `src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs` | 白名单加 `open_file` |
| `docs/architecture/16-session-file-explorer-release-notes.md` | 更新 |

## 8. 关键实现细节

- **不用 AntDesign `Splitter`**：仅两栏、样式受限、绑定 AntDesign 结构；变量方案更轻、可控、可持久化。
- **`Ctrl+S` 抑制浏览器默认**：Blazor `@onkeydown` 不能可靠 `preventDefault`，需 JS 互操作在 capture 阶段拦截。
- **agent 开新文件不展开树**：与 D8 一致；实现为「不主动改树的可见性」，而非强制隐藏。
- **`open_file` 无写盘副作用**：与 `read` 同级只读，权限白名单同构，无需新增豁免机制（D12）。
