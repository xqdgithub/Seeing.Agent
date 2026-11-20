# 多根文件树 + 协同目录归位到会话

日期：2026-11-20
状态：待批准

## 背景与问题

上一轮把「协同目录」接进了文件工作台：顶部一条根工具条（下拉切根 + `+` / 移除）。
实际使用暴露出两个问题：

1. **入口错位**：协同目录是会话的授权配置，却被放进了文件面板。文件组件因此依赖
   `ICollaborationDirectories`（写端口），把一个可选的 UI 扩展和会话核心能力绑在一起。
2. **单根视图无法并列**：根之间靠下拉切换，用户看不到全貌；切根后旧标签还会因 RootId 不匹配而失效。

## 目标

1. 协同目录的增删**只在会话层**（会话信息弹窗 → 协同目录管理），文件工作台不再提供任何管理入口。
2. 文件树把**所有根作为顶层节点并列展示**：项目根标「项目」，协同目录标「协同」，一眼可见归属。
3. 文件工作台只消费只读根列表（`IFileWorkspaceFactory.ListAsync`），不注入写端口。

## 非目标

- 不做协同目录改名/备注编辑（只增删）。
- 不引入目录选择器（继续文本路径输入；选择器需要宿主回调注入，单独做）。
- 不改 cwd 语义、权限判定、`ISessionRootSet` 的判定规则。

## 方案

### A. 会话层承载管理（采纳）

- `SessionToolbar` 顶部右侧新增一个直达按钮（链接/文件夹图标，tooltip「协同目录」），点击打开管理弹窗。
  （用户决定：不放会话信息弹窗，进工具栏一眼可见。）
- 新增 `CollaborationDirectoriesModal`：项目根只读展示，协同目录列出完整路径（不存在时灰显「不可用」），
  每项带「移除」；底部输入绝对路径 + 「添加」。
- 写操作直接调 `ICollaborationDirectories.AddAsync/RemoveAsync`：UI 的显式动作即授权，
  与 agent 侧走审批工具是两条入口，落库同源。
- 写后刷新列表；关闭弹窗不改变文件面板，下一次打开工作台自然看到新根。

B 方案（保留文件面板入口）被否决：文件组件会长期持有写端口，正是本轮要拆掉的依赖。

### B. 文件树多根并列（采纳）

- `FileTree` 参数由 `IFileWorkspace Workspace` 改为 `IReadOnlyList<IFileWorkspace> Roots`。
- 每个根渲染为一个顶层目录节点：
  - 节点名 = 根标签（项目根用项目目录名，协同目录用其显示名或路径尾段）。
  - 徽标区分：`项目`（蓝）/ `协同`（橙）；协同根用链接图标；不存在时追加红色「不可用」。
  - 根节点只显示名称 + 徽标，完整路径放在悬浮提示里（避免窄面板被长路径挤满）。
  - 根节点之下仍用现有 `FileTreeNode` 递归，传该根自己的 `Workspace`。
- 选中联动与变更广播改为按 `FileResourceId`（含 `RootId`）比较，跨根同名路径不再串。
- `FilePanelHost`：
  - 去掉根下拉、`+`、移除按钮与 `ICollaborationDirectories` 依赖。
  - 持 `_roots` + `_byRootId`；给 `FileEditor` 传 `ResolveWorkspace`（按 `id.RootId` 取 facade）。
  - 每个根起一个 `FileViewerCoordinator`（订阅各自根），统一投递给 `FileTreeState.Notify`。
  - 打开路径（agent 桥接 / 打开载荷）改走 `IFileWorkspaceFactory.ResolveAsync(sessionId, absolutePath)`，
    由绝对路径路由到正确根，不再默认主根。
- `FileEditor`：`Workspace` 参数改为 `Func<FileResourceId, IFileWorkspace?>`；读写/保存/差异全部按标签 id 解析。

C 方案（保留下拉 + 树内并列）被否决：两套入口并存，用户仍需理解「当前根」这个多余概念。

### C. 冷会话预热（顺带修）

`FileWorkspaceFactory.ListAsync` 读的是 `ISessionRootSet.Roots`（同步、只读内存）。
进程重启后直接打开旧会话时，会话尚未加载 → 协同目录不显示。

在 `ListAsync` 内先 `ISessionManager.TryGetOrLoadAsync(sessionId)` 预热（工厂是根列表的唯一出口，
把它放在这里比让每个调用方记得预热更可靠）。门闸是同步判定端口，保持在执行链内（会话必然已加载）。

## 影响面

| 文件 | 变更 |
|---|---|
| `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor` | 右侧工具栏加「协同目录」直达按钮 |
| `samples/Seeing.Agent.WebUI/Components/CollaborationDirectoriesModal.razor` | 新增：列表 + 添加 + 移除 |
| `samples/Seeing.Agent.WebUI.Files/Components/FileTree.razor` | 多根顶层节点 + 徽标 |
| `samples/Seeing.Agent.WebUI.Files/Components/FileTreeNode.razor` | 按 `FileResourceId` 选中/变更判定 |
| `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor` | 去管理 UI；多根加载与路由；多订阅 |
| `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor` | 按标签 id 解析 workspace |
| `samples/Seeing.Agent.WebUI.Files/Services/FileTreeState.cs` | 选中状态改 `FileResourceId`；跨根比较 |
| `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css` | 根节点徽标/颜色；移除根工具条样式 |
| `src/capabilities/Seeing.Agent.Files/FileWorkspaceFactory.cs` | 冷会话预热 |
| 测试 | `FileTreeState` 跨根不串；工厂冷会话返回协同根；`FilePanelHost` 无写端口依赖 |

## 验收

1. 文件工作台里同时看到项目根与所有协同目录，各自作为顶层节点，带「项目」/「协同」标识。
2. 在不同根下展开同名目录/文件，点击、编辑、保存都落到各自根，互不影响。
3. 会话信息弹窗能添加/移除协同目录；添加后重新打开文件工作台即出现，移除后消失。
4. 文件工作台程序集不再引用 `ICollaborationDirectories`（编译期即可验证）。
5. 重启进程后直接打开旧会话，文件树仍显示其协同目录。
6. 现有测试全绿。
