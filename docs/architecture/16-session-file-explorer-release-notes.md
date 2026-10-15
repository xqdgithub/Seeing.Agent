# 16 会话文件浏览器（File Workspace / FileExplorer）Release Notes

**日期：** 2026-10-14
**设计规格：** [`2026-10-14-session-file-explorer-design.md`](../superpowers/specs/2026-10-14-session-file-explorer-design.md)
**相关模块：** `Seeing.Agent.Abstractions`（`Files` 契约 + `ISessionWorkspaceResolver` + `IPathWriteLock`）、`Seeing.Agent.Core`（`SessionWorkspaceResolver` / `WorkspacePathGate`）、`Seeing.Agent.Files`（新能力包）、`Seeing.Agent.WebUI.Files`（新 Razor 类库）、`Seeing.Agent.Tools.Support`（`LineDiff` / `FileContentSupport`）、`Seeing.Agent.Tools.FileSystem`（写锁）、`Seeing.IO.Local`（`ProcessPathWriteLock`）、`samples/Seeing.Agent.WebUI`

---

## 1. 概要

在会话页支持类似 VSCode 的文件树 + 多标签查看/编辑，agent 操作文件后自动刷新。文件能力与 UI 分离为三层：**契约**（Abstractions）→ **纯文件能力包**（`Seeing.Agent.Files`）→ **可复用组件库**（`Seeing.Agent.WebUI.Files`）；agent 语义桥接固定在宿主层（WebUI）。

核心边界：**文件能力只描述磁盘事实**，不订阅会话事件、不认识 agent；「谁改的 / diff 是什么」由宿主桥接。

---

## 2. 契约与分层

| 层 | 新增 | 说明 |
|----|------|------|
| Abstractions | `Files/IFileWorkspace`、`IFileWorkspaceFactory`、`FileResourceId`、`FileRevision`、`FileEntry`、`FileReadResult`、`FileWriteResult`、`FileChangeEvent`、`FileEnums` | 零实现；结构化读取、全文 revision（乐观并发）、稳定资源标识、变更订阅流 |
| Abstractions | `Permissions/ISessionWorkspaceResolver` | 会话有效工作目录解析端口 |
| Abstractions | `Execution/IPathWriteLock` | 进程内按路径写序列化端口 |
| 能力包 | `Seeing.Agent.Files`（模块 id `files.workspace`，`DependsOn: io.local`） | 读写/编辑/删除/`GetDiffAsync` + OS watcher（`FileWatcherPool` / `RootWatcher`）+ `FileWatcherHostedService` |
| 组件库 | `Seeing.Agent.WebUI.Files` | `FilePanelHost`/`FileTree`/`FileTabs`/`FileViewer`/`FileEditor`/`ResizeHandle` + `FileTabsState`/`FilePanelState`/`FileViewerCoordinator` + `files-panel.js`（拖拽/快捷键） |
| Core | `SessionWorkspaceResolver`；`WorkspacePathGate` 增加会话有效根校验 | 见 §4 |
| Tools.Support | `LineDiff`（LCS + hunk header）、`FileContentSupport`（MIME/二进制/限制上提） | 供能力包复用，现有 `FileSystemHelper` 行为不变 |
| IO.Local | `ProcessPathWriteLock` | 进程内共享写锁实现（Singleton） |

**依赖方向**：能力包与组件库只依赖 Abstractions（+ Tools.Support / UI 库）；不引用 Core，不引用会话事件模型。

## 3. UI 接入

- 面板经 `IUiContribution` 贡献（`FilePanelUiContribution`，`ModuleId="files.workspace"`），由 `SlotHost` 按 `Requires` + `IModuleCatalog.IsEnabled` 过滤：模块禁用即无面板。
- `SessionWindow` 级联 `SessionId`（`CascadingValue`）送达插槽组件（`SlotHost` 用无参 `<DynamicComponent>`，级联值可穿透）。
- 面板可见性由 `FilePanelState.IsOpen` 驱动（默认关闭）；**开关入口为会话工具栏的工作目录徽标**（点击开合，激活态高亮）。

## 4. 安全与路径边界

- 所有路径操作过 `IWorkspacePathGate.EnsureAllowed(sessionId, path)`。
- `WorkspacePathGate` 新增**会话自身有效根**放行：`ISessionWorkspaceResolver.ResolveWorkingDirectory(sessionId)` 解析（实现依赖 `ISessionManager`，仅当目录存在时返回）。会话根是**边界内**（非扩权），**不写** `IPermissionGrantStore`；`add_workspace_path` 审批链不变。`PermissionService` step 1 镜像同一门闸，自动受益。
- 信任前提：`SessionData.WorkingDirectory` 被升格为边界输入（当前无 inbox/网关生产者写入该字段）。

## 5. 正确性契约

| 项 | 设计 |
|----|------|
| 截断回写 | `Display` 读取超 50KB 标 `IsTruncated` → 只读禁保存；编辑走 `Full`，超 5MB 抛 `FileTooLargeException` |
| 编码回写 | 仅接受 UTF-8/ASCII；非 UTF-8 → `IsEditable=false`，写入抛 `FileNotEditableException`（避免 `File.WriteAllTextAsync` 固定 UTF-8 损坏原编码） |
| 读取上限 | 超 20MB 只读前缀（`IsSampled`），revision 置 null，不整文件入内存 |
| revision | 全文 SHA256；超 10MB 置 null（不参与乐观并发） |
| 写并发 | `IPathWriteLock` 进程内按路径串行（工具与 facade **共享同一 Singleton**），消除同进程 TOCTOU |
| watcher 稳定性 | `RootWatcher` 对 Created/Modified 做 size+mtime 双采样，不稳定退避重试（≤2s），避免半写读取 |
| 冲突 | 干净标签外部变更→静默重载；脏标签→**不覆盖**，`HasExternalConflict` 触发对话框（重新加载 / 保留我的修改） |
| 编辑器刷新 | Monaco `@key` 含 `DiskVersion`（仅磁盘内容被应用时自增）→ 外部更新后重建编辑器，防止残留旧文本导致保存覆盖 |

## 6. 关键实现决策（含偏差）

- **agent 变更桥接在宿主层**：`AgentFileChangeBridge`（WebUI，Scoped `IStreamConsumer`）经 `Router.GetOrCreateCircuitConsumer` 取回与组件**同一实例**，组件 `@ref` 调 `NotifyAgentChangeAsync`；组件库**不订阅会话事件**。
  - 关键约束：`SessionEventStreamRouter.CreateConsumer` 经**新建 scope** 解析消费者，与 `@inject` 实例不同——故必须由消费方组件经 Router 取回同一实例，禁止反向跨 scope 推送。
- **P4 修订（偏离原规格字面）**：**不**将 `read/write/edit/glob/grep/delete` 改为 facade 适配器（会回退 agent 能力：非 UTF-8 写拒绝、5MB 上限、结构化读取与行式输出不兼容、丢失 `EditTool` 多策略匹配）。P4 实际收敛为**统一进程内路径写锁真相源**（`IPathWriteLock`）。**读写逻辑单一真相源**未收敛，记为后续可选项。
- **diff**：`GetDiffAsync` 按需生成（LCS + hunk header），不主动推送；agent 语义 diff 仅来自工具事件 `Metadata["diff"]`。

## 7. 交互增强与 agent 驱动打开（2026-10-14 增补）

设计规格：[`2026-10-14-file-panel-interaction-and-agent-open-design.md`](../superpowers/specs/2026-10-14-file-panel-interaction-and-agent-open-design.md)

| # | 变更 | 说明 |
|---|------|------|
| 1 | 面板开关并入工作目录徽标 | 移除工具栏「文件」按钮（`FilePanelToggle` 删除）；`SessionToolbar` 徽标改按钮，点击 `FilePanelState.Toggle`，激活高亮 |
| 2 | 保存改小图标 + `Ctrl+S` | 移除编辑器整行工具栏；`FileTabs` 右侧保存图标（脏时高亮+脏点，干净禁用）；`files-panel.js` 在 Monaco 容器聚焦时拦截 `Ctrl+S` |
| 3 | 两处可拖动分隔线 | 文件树↔编辑器（`--file-tree-width`）、面板↔聊天区（`--file-panel-width`）；`ResizeHandle` 组件 + Pointer Events；宽度持久化 `localStorage` |
| 4 | 目录树可隐藏 | 面板内折叠按钮，切换 `FilePanelState.TreeVisible`（不持久化，会话级偏好） |
| 5 | `open_file` 工具 | `Seeing.Agent.Tools.FileSystem` 新增；只读无磁盘副作用，回写 `ToolResult.Metadata["path"]`（可含 `line`/`column`） |
| 6 | agent 驱动打开 | `AgentFileChangeBridge.OpenRequested(id, line, column)` → `SessionFilePanel` → `FilePanelState.RequestOpen`（未开则自动开、默认隐藏树）→ `FilePanelHost` 消费 `PendingOpen` 分派 |
| 7 | 行/列跳转 | `FileEditor.NavigateToAsync(line, column)`（1-based）经 `RevealLineInCenter` + `SetPosition` + `Focus`；`FilePanelHost.NavigateToAsync` 供宿主调用；agent 经 `open_file` 的 `line`/`column` 参数驱动 |
| 8 | 面板关闭 | 面板头 `×` 按钮；无打开文件且目录树收起时自动关闭 |
| 9 | diff 预览升级 | `write`/`edit` 工具经 `FileDiffMetadata.Build` 回写 `oldContent`/`newContent`（>256KB 仅回写 unified diff）；新增 `FileDiffEditor`（`StandaloneDiffEditor` 单栏内联、只读）+ 关闭按钮；超大文件回退纯文本 diff |
| 10 | 文件树实时刷新 | 新增 `FileTreeState`（Scoped 事件总线）；`FilePanelHost` 将 watcher 事件转发给树，`FileTree`/`FileTreeNode` 按 `IsDirectChildOf` 精确重列受影响目录 |
| 11 | 会话内标签持久 | `FilePanelState.GetTabs(sessionId)` 按会话缓存 `FileTabsState`（导航往返不丢失） |

**`open_file` 语义（分派）**：面板关闭→自动打开；新文件→新标签（不展开树）；已是其他标签→切换；就是当前文件→从磁盘刷新（`ApplyDiskUpdate`）。带 `line`/`column` 时在打开/切换后定位（1-based）。

**权限**：`open_file` 视作只读工具（`tool.execute` 非资源类），在 `plan`/`explore` 白名单加 `Allow(Tool,"open_file")` 即放行，无需特殊分支。

**UI 贡献**：`session_file_panel` 槽（`SessionFilePanel`）保留；`session_toolbar_actions` 的 `FilePanelToggle` 贡献移除。

## 8. 已知边界 / 后续

| 项 | 状态 |
|----|------|
| `.git`/`bin`/`obj`/`node_modules`/`.vs` 下的 agent 改动不触发刷新 | 已知（watcher 忽略规则） |
| Markdown 预览仅对只读 `.md` 生效（可编辑 `.md` 走 Monaco） | 设计选择 |
| Markdown 渲染未做 HTML sanitize（本地文件场景） | 已知 |
| 同 circuit 多 Full 窗口共享单一 `FilePanelState` | 本期约束「同时仅一个面板」 |
| `ProcessPathWriteLock` 的 GetOrAdd/TryRemove 极窄竞态 | best-effort（契约明示） |
| 工具与 facade 读写逻辑并存 | 后续可选项（P4 未收敛） |

> 已修复：watcher 事件与磁盘状态竞态（文件在事件到达前被删除）曾导致 `ReadAsync` 抛 `FileNotFoundException` 冒泡到渲染器；现 `FileViewerCoordinator.ApplyAsync` 将 `FileNotFoundException`/`DirectoryNotFoundException` 视为已删除、`IOException` 跳过本次刷新。

## 8.1 错误处理与异常归属（2026-10-15 增补）

**问题**：打开被其他进程占用的文件（如 SQLite `memory.db`）时，`FilePanelHost` 打开路径无异常捕获，`IOException` 冒泡进 Blazor 渲染器 → circuit 崩溃白屏。同时 `FileTooLargeException` / `FileNotEditableException` 原定义在能力包 `Seeing.Agent.Files`，UI 组件库（只依赖 Abstractions）无法强类型捕获，只能用 `GetType().Name` 字符串匹配——重命名即静默失效，违反依赖方向。

| 变更 | 说明 |
|------|------|
| **异常上提契约层** | `FileTooLargeException` / `FileNotEditableException` 移至 `Abstractions/Files/FileExceptions.cs`；删除能力包同名文件。二者是 `IFileWorkspace.ReadAsync/WriteAsync` 的契约承诺（spec §178/§226），归契约层。 |
| **错误契约映射** | 新增 `Seeing.Agent.WebUI.Files/Services/FileErrorPresenter`，按规格「错误契约 UI 映射」把异常 → 文案（强类型匹配）：`UnauthorizedAccessException`→路径不在工作区、`FileNotFound`/`DirectoryNotFound`→文件不存在、`FileTooLarge`→文件过大仅预览、`FileNotEditable`→编码不支持编辑、`IOException`→读取失败请重试、`OperationCanceled`→操作已取消、未知→无法打开该文件。 |
| **打开路径防崩溃** | `FilePanelHost`：`ResolveAsync` 失败显示可重试错误条；`ReadAsync` 经 `TryReadAsync` 收敛为 `(File, Error)`，失败仅展示错误条（不冒泡）。`FileEditor` 保存/回读/冲突重载同样捕获并展示错误条。 |
| **引用计数修正** | `FilePanelHost.Dispose` 仅在实际解析成功（持有引用计数）时 `Release`，解析失败从未获取引用。 |
| **目录列举防崩溃** | `FileTree.ReloadAsync` / `FileTreeNode.ToggleAsync` 补 `catch`，目录被删除/占用等竞态不再冒泡。 |

## 8.2 布局与交互（2026-10-15 增补）

| 变更 | 说明 |
|------|------|
| **默认半屏** | 面板宽度改百分比 `var(--file-panel-width, 50%)`，clamp `[280px, 80%]`；`ResizeHandle` 新增 `Percent` 模式（相对 `.session-content-wrapper` 换算）。 |
| **可拖至 80%** | 移除 `@media (max-width:1200px)` 中硬编码 `max-width:480px` 的覆盖层（拖动卡在半屏的根因）。 |
| **左右互换** | 面板头 ⇄ 按钮 → `FilePanelState.ToggleSide()`；`SessionFilePanel` 以 flex `order` 重排（左：`[面板][聊天]`，右：`[聊天][面板]`），分隔线随侧别换位。默认 **Left**。 |
| **持久化** | 宽度存 `seeing-files-panel-width-pct`（百分比，越界视为脏数据回退默认）；侧别存 `seeing-files-panel-side`。默认侧别由 `FilePanelState`（C#）单点决定，JS 仅回传存储值。 |

## 9. 测试

| 套件 | 覆盖 |
|------|------|
| `Seeing.Agent.Files.Tests`（30） | 工厂缓存/引用计数/回落、RootId 稳定性、截断/编码/revision、乐观并发、共享写锁序列化、watcher 稳定性、`LineDiff`、门闸、列举跳过不可读条目 |
| `Seeing.Agent.WebUI.Files.Tests`（22） | `FileTabsState` 开/关/脏/磁盘更新/重命名/删除、冲突裁决、`FileViewerCoordinator` 刷新与冲突跳过、`DiskVersion` 回归、`FilePanelState` 开合/树可见/待处理打开 |
| `Seeing.Agent.WebUI.Tests`（+3） | `AgentFileChangeBridge` 的 `open_file` → `OpenRequested` 与 `write` → `AgentChanged` 分流 |
| `Seeing.Agent.Tools.FileSystem.Tests`（52） | 既有工具行为不回归（写锁注入后）、`OpenFileTool` 门闸/元数据/无副作用、`FileSystemModule` 含 `open_file` |
| `Seeing.Agent.Tools.Support.Tests`（8） | `LineDiff` + `FileContentSupport` |
| `Seeing.Agent.Tests` 守门 | `files.workspace` 登记进 `BuiltInCapabilitySets.Full` |
