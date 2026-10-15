# 会话文件浏览器（File Workspace / FileExplorer）设计

**日期:** 2026-10-14（架构返工 v3：2026-10-14）
**状态:** 待评审
**范围:** `Seeing.Agent.Abstractions`（`Files` 契约 + `ISessionWorkspaceResolver` 端口）+ `Seeing.Agent.Core`（门闸支持会话有效根 + 端口实现 + 能力集登记）+ 新能力包 `Seeing.Agent.Files` + 新 Razor 类库 `Seeing.Agent.WebUI.Files` + `Seeing.Agent.WebUI`（UI 贡献 + 挂载 + agent 桥接）
**触发:** 会话页无法查看工作区文件；agent 操作文件后 UI 无感知；文件能力缺乏 agent 友好、UI 与底层分离的统一接口
**约束:** 按产品方惯例，本设计文档不自动提交 git

> **v3 修订说明**：v2 经两路复审仍 REJECTED。本版修复：① `SessionId` 经 **CascadingValue** 送达插槽组件（不改 `SlotHost`）；② agent 变更改为 **Router 消费者（`AgentFileChangeBridge`）+ 组件 `@ref` 直调**，杜绝跨 scope 推送（NB2 根因：`CreateConsumer` 新建 scope，与 `@inject` 实例不同）；③ **编码** 回写损坏（非 UTF-8 只读）；④ **读取字节上限**；⑤ **目录重命名** 标签前缀重写；⑥ `PathWriteLocks` 回收；⑦ 测试策略据实（无 Playwright harness / 不用 bUnit）；⑧ `SecureDisabled`、`Code`/`Work` 场景成员；⑨ `ResolveAsync`；⑩ 补齐 `FilePanelToggle`/`SessionFilePanel`/异常类。

---

## 1. 背景与问题

| 现状 | 事实 | 位置 |
|------|------|------|
| 文件读写 seam | `IFileSystem`，**无路径校验、无变更通知、无原子写**（`WriteAllTextAsync` = `File.WriteAllTextAsync`，UTF-8 固定） | `Execution/IFileSystem.cs`；`LocalFileSystem.cs:42-43` |
| 文件工具 | `read`/`write`/`edit`/`glob`/`grep`/`delete`/`add_workspace_path` | `src/capabilities/Seeing.Agent.Tools.FileSystem/` |
| 路径门闸 | `EnsureAllowed` 仅放行「全局根 ∪ 会话白名单」，**不认会话 `WorkingDirectory`** | `WorkspacePathGate.cs:31-58` |
| 扩权授权 | `add_workspace_path` → `filesystem.workspace_extend` **走审批链** | `DefaultToolPermissionPolicy.cs:37-40` |
| 工具事件 | `ToolCallEvent` 带 `Arguments.path` / `Metadata["path"]`/`["diff"]`，持久化到 `SessionToolCall` | `MessageEventTypes.cs:290`；`ChatEventTracker.cs:109-154` |
| 会话工作目录 | `SessionData.WorkingDirectory`（可变） | `SessionData.cs:71`；`ExecutionRunner.cs:128-134` |
| 变更通知 | **不存在**（全仓无 `FileSystemWatcher`） | — |
| UI 贡献 | `SlotContribution(Requires, Scenarios)` + `SlotHost`；`SlotHost` 用**无参** `<DynamicComponent>`（级联值可穿透）；`SessionEventStreamRouter.CreateConsumer` 经**新建 scope** 解析消费者（跨 scope 实例不同） | `SlotHost.razor:16-31`；`UiContributionTypes.cs:58-62`；`SessionEventStreamRouter.cs:207` |
| 模块守门 | 新 `ISeeingModule`（**须公共无参构造**）必须登记 `BuiltInCapabilitySets.Full` | `BuiltInScenariosTests.cs:44-61`；`SrcModuleScanner.cs:15-41` |
| 测试 | WebUI 侧**无 Playwright 脚手架、无 bUnit**；`WebUI.Tests` 为纯 xUnit/源码扫描 | `tests/apps/Seeing.Agent.WebUI.Tests` |

问题：① 文件能力散落 7 个工具，无统一入口；② 无变更感知；③ 无版本/并发语义；④ 会话页无文件视图；⑤ 门闸不认会话根，语义矛盾。

## 2. 目标 / 非目标

### 目标

| 目标 | 验收要点 |
|------|----------|
| UI 与能力分离 | 契约落 Abstractions；实现落能力包；组件库只消费契约 |
| 职责纯净 | 文件能力只描述磁盘事实，不依赖会话事件/agent 语义；桥接固定宿主层 |
| 门闸语义自洽 | 会话自身有效根经**正式 Core 端口**纳入边界，不新增旁路授权 |
| UI 单一真相源 | 面板/入口经 `IUiContribution` 贡献，受 `IModuleCatalog.IsEnabled` 门控 |
| 公共组件 | 独立 Razor 类库，任何 Blazor 宿主可引用；会话页只是消费者之一 |
| Agent 友好接口 | 结构化读取、全文 revision（乐观并发）、变更订阅流、稳定资源标识、按需行 diff |
| 多会话隔离 | 按会话有效根解析 facade；变更流按有效根过滤 |
| 自动更新 | OS watcher（模块生命周期托管）；agent 改动由宿主桥接（可选） |
| 数据安全 | 截断/非 UTF-8/超限文件**禁止整文件回写**；读取有字节上限 |
| 不破坏守门 | 登记 `Full`/`Code`/`Work`、`SecureDisabled`、`.slnx`、`02-modules.md` |

### 非目标

- **不改造** 现有 7 个文件工具（本期共存；收敛见 §11 P4）。
- **文件能力不订阅会话事件**：`ToolCallEvent`/diff 消费属宿主层。
- **不新增 HTTP 端点**。
- **不做** 跨进程写协调（写并发为**进程内 best-effort**，见 §8）。
- **不做** 文件历史版本库 / undo 栈。
- **不引入** Playwright / bUnit 新 harness（组件逻辑下沉为纯 C# 服务测试）。
- **不修改** 现有 `WriteTool`/`EditTool` 的 diff 行为（其 `GenerateDiff` 非 unified diff；本期另建 `LineDiff`）。

## 3. 分层与包结构

```
src/abstractions/Seeing.Agent.Abstractions/
  Files/                                    ← 新增契约（零实现）
    IFileWorkspaceFactory.cs / IFileWorkspace.cs
    FileResourceId.cs / FileRevision.cs / FileEntry.cs
    FileReadResult.cs / FileWriteResult.cs / FileChangeEvent.cs
    FileReadMode.cs / FileContentKind.cs / FileChangeKind.cs
  Permissions/ISessionWorkspaceResolver.cs  ← 新增端口契约

src/spine/Seeing.Agent.Core/
  Core/Permission/SessionWorkspaceResolver.cs   ← 端口实现（依赖 ISessionManager）
  Core/Permission/WorkspacePathGate.cs          ← 修改：增加会话有效根校验
  Extensions/ServiceCollectionExtensions.cs     ← 注册端口
  CapabilitySets/BuiltInCapabilitySets.cs       ← 修改：Full/Code/Work + SecureDisabled

src/capabilities/Seeing.Agent.Files/            ← 新增能力包
  Seeing.Agent.Files.csproj
  FileWorkspaceModule.cs                        ← ISeeingModule (Id="files.workspace", 无参构造, DependsOn=["io.local"])
  FileWorkspaceFactory.cs                       ← Singleton，(sessionId,有效根) 缓存
  FileWorkspace.cs                              ← per-session facade
  FileContentClassifier.cs                      ← Kind/Mime/编码/可编辑性判定 + 截断
  RevisionCalculator.cs                         ← 全文 SHA256
  PathWriteLocks.cs                             ← 进程内按路径写锁（引用计数回收）
  FileExceptions.cs                             ← FileTooLargeException / FileNotEditableException
  Watching/FileWatcherPool.cs                   ← 按根池化 watcher + 引用计数
  Watching/RootWatcher.cs                       ← 单根 FileSystemWatcher + 防抖 + 稳定性校验
  Hosting/FileWatcherHostedService.cs           ← IHostedService + IModuleHostedService (ModuleId="files.workspace")

samples/Seeing.Agent.WebUI.Files/               ← 新增 Razor 类库（**被动**，不碰会话事件）
  Components/FilePanelHost.razor                ← 面板壳（级联取 SessionId；公开 NotifyAgentChange）
  Components/FileTree.razor / FileTabs.razor
  Components/FileViewer.razor / FileEditor.razor
  Components/FilePanelToggle.razor              ← 工具栏切换按钮
  Services/FileTabsState.cs / FileViewerCoordinator.cs / FilePanelState.cs

samples/Seeing.Agent.WebUI/                     ← 消费 + UI 贡献 + 桥接（宿主层）
  Program.cs
  Components/SessionWindow.razor                ← 级联 SessionId + 内容区插槽
  Components/SessionToolbar.razor               ← 工具栏插槽
  Components/SessionFilePanel.razor             ← 宿主包装：取桥接消费者 → @ref 调 NotifyAgentChange
  Services/FilePanelUiContribution.cs           ← IUiContribution (ModuleId="files.workspace")
  Services/AgentFileChangeBridge.cs             ← Scoped IStreamConsumer（经 Router 取用）
  wwwroot/css/session-page.css

tests/capabilities/Seeing.Agent.Files.Tests/    ← 新增测试项目
```

**依赖方向：**

| 项目 | 依赖 |
|------|------|
| Abstractions（契约 + 端口） | 原语层 + BCL |
| Seeing.Agent.Files | Abstractions + `Tools.Support` + BCL（`FileSystemWatcher`）；**不引用 Core / Tools.FileSystem / 会话事件模型** |
| Seeing.Agent.WebUI.Files | Abstractions + AntDesign + BlazorMonaco + Markdig |
| Seeing.Agent.WebUI | 上述两者 + 现有依赖 |

**关键决策：**

1. **纯函数上提 `Tools.Support`**：MIME/二进制/限制常量 + **新增 `LineDiff.ComputeUnified`（LCS+hunk）**。`FileSystemHelper` 纯函数按 `04-development-standards.md:132` 上提，薄壳转调，不改现有工具行为。**现有 `GenerateDiff`（`WriteTool.cs:132`/`EditTool.cs:266`）非 unified diff，不上提、不复用**。
2. **Razor 类库 CPM**：`ManagePackageVersionsCentrally=false` + 显式钉 AntDesign 1.6.2 / BlazorMonaco 3.5.0 / Markdig 1.3.2（与 `WebUI.csproj:46-48` 一致）。注：`Directory.Packages.props` 为 nightly/1.4.0，两处长期分叉属既有状态。
3. **放置理由**：`Seeing.Agent.WebUI.Files` 置于 `samples/`（唯一消费者是 WebUI）；**禁止**其绕过 UI 贡献系统（面板必须经 §7 插槽渲染）。
4. **diff 按需**：`GetDiffAsync` 不主动产生、不随变更推送。

## 4. 契约层

### 4.1 Core 端口：`ISessionWorkspaceResolver`

```csharp
namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>解析会话有效工作目录（绝对路径）；会话不存在或无 cwd 返回 null。</summary>
public interface ISessionWorkspaceResolver
{
    string? ResolveWorkingDirectory(string sessionId);
}
```

**`WorkspacePathGate.EnsureAllowed` 修改**（`WorkspacePathGate.cs:44-52`）：全局根检查后、白名单检查前插入：

```csharp
var sessionCwd = _sessionWorkspace?.ResolveWorkingDirectory(sessionId);
if (!string.IsNullOrEmpty(sessionCwd))
{
    var cwdFull = Path.GetFullPath(sessionCwd);
    if (PathSafety.IsPathWithinDirectory(full, cwdFull))
        return null;
}
```

- **不改授权模型**：会话根是**边界内**（非扩权），不进白名单、不写 `IPermissionGrantStore`；`add_workspace_path` 审批链不变。`PermissionService.cs:127-147` 镜像同一门闸，自动受益。
- **实现** `SessionWorkspaceResolver`（Core，依赖 `ISessionManager`，先例 `EffectivePermissionPolicy.cs:13,19`）：`Get(sessionId)?.WorkingDirectory`；仅当路径非空且 `Directory.Exists` 时返回。
- **信任前提（显式记录）**：`WorkingDirectory` 可由 inbox 元数据写入（`ExecutionRunner.cs:128-134`）。经全仓核对，**当前无任何 `InboxMessage` 生产者设置它**，无远程/网关入口（`GatewayRequest.cs` 无该字段）。本特性将该字段升格为边界输入，属**新的信任依赖**；文档标注，并在 P0 加断言：仅当会话为宿主显式创建（`ISessionDirectory.CreateAsync`）时信任。

### 4.2 文件工作区契约

```csharp
public interface IFileWorkspaceFactory
{
    /// <summary>按会话有效根解析 facade（异步，可回落 LoadAsync）；有效根 = resolver ?? GetProjectRoot()。
    /// 同一 (sessionId, 有效根) 复用；有效根变化重建。</summary>
    Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>引用计数减一，归零回收并释放 watcher 引用；幂等。</summary>
    void Release(string sessionId);
}

public interface IFileWorkspace
{
    string SessionId { get; }
    string RootId { get; }        // 有效根绝对路径规范化哈希
    string RootPath { get; }      // 绝对路径

    FileResourceId GetResourceId(string path);
    string GetAbsolutePath(FileResourceId id);

    Task<FileEntry> StatAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>Display=截断展示（超 MaxReadBytes 只回采样且 Revision=null）；
    /// Full=完整用于编辑（超 MaxEditableSize 抛 FileTooLargeException）。</summary>
    Task<FileReadResult> ReadAsync(string path, FileReadMode mode = FileReadMode.Display, CancellationToken ct = default);

    Task<FileWriteResult> WriteAsync(string path, string content, FileRevision? expectedRevision = null, CancellationToken ct = default);
    Task<FileWriteResult> EditAsync(string path, string oldString, string newString, bool replaceAll = false, FileRevision? expectedRevision = null, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>按需生成统一行 diff（LCS，带 hunk header）：baselineContent → 当前磁盘。</summary>
    Task<string> GetDiffAsync(string path, string baselineContent, CancellationToken ct = default);

    /// <summary>订阅磁盘变更（**仅 OS watcher**）。每个枚举器 Acquire/Dispose 时 Release。</summary>
    IAsyncEnumerable<FileChangeEvent> SubscribeChangesAsync(CancellationToken ct = default);
}
```

### 4.3 DTO / 枚举

| 类型 | 字段 | 说明 |
|------|------|------|
| `FileResourceId` | `string RootId`、`string RelativePath` | `ToString()` = `"{RootId}:{RelativePath}"`，统一 `/` |
| `FileRevision` | `string Hash`（**全文** SHA256）、`long Size`、`DateTime LastWriteUtc` | `Matches(FileRevision?)` |
| `FileEntry` | `FileResourceId Id`、`string Name`、`bool IsDirectory`、`long? Size`、`DateTime? LastWriteUtc` | |
| `FileReadResult` | `FileResourceId Id`、`FileRevision? Revision`、`string? Content`、`FileContentKind Kind`、`string MimeType`、`string? Encoding`、`bool IsEditable`、`long Size`、`int TotalLines`、`bool IsTruncated`、`bool IsSampled`、`FileReadMode Mode` | 二进制 `Content=null`；`Revision=null`=超可哈希/采样；`IsEditable=false`=只读（非 UTF-8/截断/超大） |
| `FileWriteResult` | `FileResourceId Id`、`FileRevision? Revision`、`bool Conflict`、`FileRevision? CurrentRevision`、`string? Diff` | `Conflict=true` 未写入且 `Revision=null`；成功时 `Revision`=新版本 |
| `FileChangeEvent` | `FileResourceId Id`、`FileChangeKind Kind`、`FileRevision? NewRevision`、`string? OldPath`、`DateTime Timestamp` | 仅磁盘事实；**无 `Source`/`Diff`**；`OldPath` 仅 `Renamed` 非空 |

```csharp
public enum FileReadMode { Display, Full }
public enum FileContentKind { Text, Markdown, Image, Binary }
public enum FileChangeKind { Created, Modified, Deleted, Renamed }
```

> **`Revision` 作用域（修复）**：`Hash` 始终对**全文**计算；`Display` 在 `MaxReadBytes` 内先读全文算 hash 再截断；超 `MaxReadBytes` 时 `IsSampled=true`、`Revision=null`。超 `MaxHashableSize` 同样 `Revision=null`。

## 5. 能力包实现（Seeing.Agent.Files）

### 5.1 FileWorkspaceFactory（Singleton）

- 缓存键 = `sessionId`，值含有效根；`ResolveAsync` 每次经 `ISessionWorkspaceResolver` 重算有效根，变化则重建（**修复 B5**）。
- 订阅 `IWorkspaceProvider.WorkspaceRootChanged` → 清缓存。
- `Release(sessionId)` 减引用计数，归零回收（幂等）。

### 5.2 FileWorkspace（per session）

- **门闸**：所有路径方法先 `EnsureAllowed(sessionId, fullPath)`，拒绝抛 `UnauthorizedAccessException`。
- **绝对化**：`RootPath` 与入参先 `Path.GetFullPath`（`GetProjectRoot()` 可能是相对路径，`WorkspaceProviderExtensions.cs:22-23`）。
- **读取**：按 `MaxReadBytes`（默认 20MB）封顶读取；`FileContentClassifier` 判 Kind/Mime/**编码**/可编辑性；全文 SHA256；`Display` 截断（50KB/2000 行/2000 字符）。
- **写入**：`PathWriteLocks` 路径锁内 → `expectedRevision` 比对，不匹配返回 `Conflict` → 匹配则写 → 新 revision + `LineDiff`。
  - **编码保护**：仅当 `FileReadResult.IsEditable && Encoding ∈ {UTF-8, ASCII}` 才允许写；非 UTF-8 拒绝（`FileNotEditableException`），避免 `WriteAllTextAsync` 固定 UTF-8 造成的编码损坏。
- **编辑**：读全文 → 字符串替换（同 `EditTool`）→ 写入路径。
- **diff**：`LineDiff.ComputeUnified(baselineContent, currentFull)`。
- **变更订阅**：`FileWatcherPool.Acquire(RootPath)` 转发 OS 事实事件；仅 `RootPath` 内路径。每个 `SubscribeChangesAsync` 枚举器启动时 Acquire、Dispose 时 Release（配对定义）。

### 5.3 PathWriteLocks（进程内写序列化）

- `ConcurrentDictionary<string /*abs path*/, (SemaphoreSlim, int refCount)>`；写/编辑/删除按路径串行。
- **回收**：操作结束 `refCount--`，归零移除（**修复泄漏**）。上限 `MaxLocks`（默认 4096），超限先回收 `refCount=0` 条目，仍超则淘汰最久未用的空闲锁（不打断进行中的写）。
- **best-effort**：仅序列化**经 facade** 的写；agent 工具本期不走 facade，agent↔UI 并发写**不被锁阻止**，靠 revision 冲突检测 + watcher 感知。跨进程不保证。P4 收敛后锁才覆盖全路径。

### 5.4 FileWatcherPool（Singleton）

- `ConcurrentDictionary<string /*normalizedRoot*/, RootWatcher>` + 引用计数；同根多会话共用**一个** `FileSystemWatcher`（`IncludeSubdirectories=true`）。
- `Acquire`/`Release`；归零 dispose。忽略 `.git`/`bin`/`obj`/`node_modules`/`.vs`（可配）。
- **由 HostedService 托管**（§5.6），非 UI 引用计数驱动。

### 5.5 RootWatcher（单根）

- `FileSystemWatcher`（Created/Changed/Deleted/Renamed）。
- **防抖 + 稳定性校验**：`Modified` ~200ms 防抖后，确认 size+mtime 两次采样不变才发；不稳定退避重试（≤2s），仍不稳定只发 `Modified`（`NewRevision=null`）。`Created` 需存在且 size>0；`Deleted` 直接发（`NewRevision=null`）；`Renamed` 带 `OldFullPath`。
- 输出 `(path, kind, oldPath?)`；`NewRevision` 由 FileWorkspace 读盘计算。

### 5.6 FileWatcherHostedService（`IHostedService` + `IModuleHostedService`，ModuleId="files.workspace"）

- `AddModuleHostedService<T>` 注册（`ModuleHostedServiceServiceCollectionExtensions.cs:12-20`，要求 `T : class, IHostedService, IModuleHostedService`）。
- `StartAsync`：`IModuleCatalog.IsEnabled("files.workspace")` 门控（同 `ScheduleHostedService.cs:50-54`）；持 `ModuleHostedRunGate`。
- `StopAsync`：dispose 池内全部 watcher、清引用计数。
- 由 `ModuleLifecycleManager.cs:97-98,159-160` 编排（Activate 先模块后 HostedService；Deactivate 反之）。**修复 B4**。

### 5.7 FileWorkspaceModule

```csharp
public sealed class FileWorkspaceModule : ISeeingModule   // 公共无参构造（守门扫描要求）
{
    public string Id => "files.workspace";
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();  // 非独占 seam，不声明
    public IReadOnlyList<string> DependsOn { get; } = ["io.local"];
    // ConfigureServices: TryAddSingleton<IFileWorkspaceFactory, FileWorkspaceFactory>();
    //                     TryAddSingleton<FileWatcherPool>(); AddModuleHostedService<FileWatcherHostedService>();
    // ActivateAsync/DeactivateAsync: 无重活
}
```

> id `files.workspace` 区别于既有 `filesystem`（`FileSystemModule.cs:26`）。

## 6. 组件库（Seeing.Agent.WebUI.Files）

### 6.1 FilePanelHost（组件库，**被动**，不碰会话事件）

```razor
@inject IFileWorkspaceFactory WorkspaceFactory
@inject FilePanelState PanelState
[CascadingParameter(Name = "SessionId")] private string? SessionId { get; set; }

@* 公开命令式方法：宿主桥接经 @ref 调用；组件不订阅会话事件 *@
// public Task NotifyAgentChangeAsync(FileResourceId id, string? diff);
```

- `SessionId` 经 **CascadingValue** 获取（§7.2 由 `SessionWindow` 提供），不依赖 `SlotHost` 传参（`SlotHost.razor:21` 无参 `<DynamicComponent>`，但级联值可穿透）。
- `OnInitializedAsync`：若 `string.IsNullOrEmpty(SessionId)` 则进入**空态**（不调 `ResolveAsync`，避免空串解析）；否则 `_workspace = await WorkspaceFactory.ResolveAsync(SessionId)`。
- `NotifyAgentChangeAsync(id, diff)`：转给 `FileViewerCoordinator`（命中已打开文件则刷新并按需渲染 diff）；宿主不喂则等同纯文件浏览。内部经 `InvokeAsync` 编组（后台线程调用安全）。
- `Dispose`：`_coordinator.Dispose()`；`WorkspaceFactory.Release(SessionId)`；`PanelState.Close()`。
- 经 `CascadingValue` 传 `IFileWorkspace`/`FileTabsState`。

### 6.2 FilePanelState（Scoped，circuit 维度，**仅开合状态**）

```csharp
public sealed class FilePanelState
{
    public bool IsOpen { get; private set; }
    public string? SessionId { get; private set; }
    public event Action? Changed;
    public void Open(string sessionId); public void Close(); public void Toggle(string sessionId);
}
```

- **不再承载 agent 变更事件**（消除 NB2 的跨 scope 隐患）：agent 变更走 §7.3 的消费者 → 组件 `@ref` 直调。
- 同 circuit 内多 Full 窗口共享单一 `IsOpen`（与 `AppState` 同源约束，见 §12 风险）；本期约束「同时仅一个 Full 面板打开」。

### 6.3 FileTree / FileTabsState / FileViewer / FileEditor

- `FileTree`：AntDesign `Tree` 懒加载；过滤忽略目录。
- `FileTabsState`（纯 C#，可测）：`OpenFiles`/`ActiveId`/`IsDirty`/`Changed`；`Open`/`Close`/`Activate`/`MarkDirty`/`ApplyDiskUpdate`/`ApplyRename(oldId,newId)`/`MarkDeleted(id)`。
- `FileViewer`：按 Kind 分派（Text→Monaco 只读或 `<pre>`；Markdown→Markdig；Image→`<img>`；Binary→占位）；**diff 视图**仅宿主传入时渲染。
- `FileEditor`：Monaco + 保存；`WriteAsync(path, content, expectedRevision: openedRevision)`；`IsEditable=false`（截断/非 UTF-8/超大）禁用保存并提示；`Conflict` → 冲突弹窗（§8）。

### 6.4 FileViewerCoordinator

- 订阅 `_workspace.SubscribeChangesAsync(ct)`（仅磁盘事实）。
- 事件经 `InvokeAsync` 编组回 Blazor 同步上下文（避免后台线程改 UI 集合）：
  - 未打开 → 忽略。
  - 已打开且干净 → 重读 `ApplyDiskUpdate`，闪「已更新」。
  - 已打开且脏 → **不覆盖**，冲突提示，`重新加载`/`保留我的修改`。
  - `Deleted` → `MarkDeleted`；`Renamed` → `ApplyRename`（**含目录重命名：对已打开文件前缀重写** `OldPath`→`Id`）。

## 7. WebUI 接入

### 7.1 注册

```csharp
builder.Services.AddSeeingModule<FileWorkspaceModule>(registry);
builder.Services.AddScoped<FilePanelState>();        // 仅开合状态
builder.Services.AddScoped<AgentFileChangeBridge>(); // Scoped IStreamConsumer（经 Router 取用，见 §7.3）
```

`Seeing.Agent.WebUI.csproj` 增加对 `Seeing.Agent.Files`、`Seeing.Agent.WebUI.Files` 的 `ProjectReference`。

### 7.2 UI 贡献 + 级联会话

`FilePanelUiContribution : IUiContribution`（WebUI，`ModuleId="files.workspace"`，仿 `CoreShellUiContribution`）：

```csharp
public IReadOnlyList<object> Contribute() =>
[
    // 注意：贡献的是 WebUI 宿主包装组件 SessionFilePanel（内含 FilePanelHost + 桥接绑定），
    // 而非组件库的 FilePanelHost 本身——桥接属宿主层。
    new SlotContribution("session_file_panel", typeof(SessionFilePanel), Requires: ["files.workspace"]),
    new SlotContribution("session_toolbar_actions", typeof(FilePanelToggle), Requires: ["files.workspace"]),
];
```

- `Program.cs:218-222` 处 `uiRegistry.Register(new FilePanelUiContribution())`。
- **`SessionWindow.razor`**：把内容区包进级联会话值，并加插槽（**修复 NB1**）：
  ```razor
  <CascadingValue Name="SessionScenario" Value="@SessionState.CurrentSession?.Scenario">
  <CascadingValue Name="SessionId" Value="@SessionId">
      <SessionToolbar ... />
      <div Class="session-content-wrapper">
          ...
          <SlotHost SlotName="session_file_panel" />
      </div>
  </CascadingValue>
  </CascadingValue>
  ```
  `SlotHost`（`:21`）渲染的 `FilePanelHost` 作为后代**自动接收** `CascadingValue`。
- **`SessionToolbar.razor`**：右侧按钮区（`:141-189`）加 `<SlotHost SlotName="session_toolbar_actions" />`；`FilePanelToggle` 同样经级联取 `SessionId`，读写 `FilePanelState`。
- 模块禁用 → `Requires` 过滤（`SessionShellVisibility.cs:111-125`）→ 无面板、无按钮。

### 7.3 宿主桥接（agent 变更，可选）

**关键正确性约束（修复 NB2）**：`SessionEventStreamRouter` 为 Singleton，其 `CreateConsumer` 经 `_scopeFactory.CreateScope()`（`SessionEventStreamRouter.cs:207`）在**新 scope** 解析消费者；而 `@inject` 的组件在 **circuit scope**。二者实例不同 → 若桥接把事件推给一个「另一 scope 的 `FilePanelState`」，订阅方永远收不到。

因此采用项目既有、且已被验证的 **`TaskCardAggregator` 实例共享模式**：**由消费方组件经 Router 取回同一实例**（`SessionWindow.razor:983` `Router.GetOrCreateCircuitConsumer<TaskCardAggregator>(circuitId)`），而非让 Router 侧反向推给组件。禁止「Router 新建的消费者去写 `@inject` 的 Scoped 服务」这一跨 scope 通道。

**`AgentFileChangeBridge : IStreamConsumer`（Scoped，circuit 维度）**：
- 持 `IFileWorkspaceFactory`（解析有效根以构造 `FileResourceId`）与 `string? _boundSessionId`；提供 `Rebind(sessionId)`（同 `TaskCardAggregator.Rebind`，`TaskCardAggregator.cs:51`）。`SessionId` 属性返回 `_boundSessionId ?? ""`（Router 不读该属性，`IStreamConsumer.cs:6`，仅作标识）。
- `OnEvent(evt)`：过滤 `ToolCallEvent`（`write`/`edit`/`delete` 成功），路径 `Metadata["path"]` → 兜底 `Arguments.path`（**`delete` 不写 `Metadata["path"]`**；`Arguments` 为 `object?`，需处理 `JsonElement`）。
- **构造 `FileResourceId`**：用 `evt.SessionId`（`ToolCallEvent` 自带）→ `await _factory.ResolveAsync(evt.SessionId)` → `workspace.GetResourceId(absPath)`。**不依赖 `_boundSessionId`**（它只用于 `AttachConsumer` 的订阅键）。
- 解析出 `FileResourceId` 与 `diff`（`Metadata["diff"]`，仅 write/edit）后，触发 `event Func<FileResourceId, string?, Task>? AgentChanged`。

**`SessionFilePanel.razor`（WebUI 宿主包装组件，渲染 `FilePanelHost`）**：
```razor
@inject Services.SessionEventStreamRouter Router
@inject CircuitContext CircuitContext
<FilePanelHost @ref="_host" SessionId="@SessionId" />

@code {
    [CascadingParameter(Name = "SessionId")] private string? SessionId { get; set; }
    private FilePanelHost? _host;
    private AgentFileChangeBridge? _bridge;

    protected override void OnInitialized()
    {
        // 经 Router 取回 circuit 维度消费者（与 TaskCardAggregator 同法），实例在 circuit 内共享
        _bridge = Router.GetOrCreateCircuitConsumer<AgentFileChangeBridge>(CircuitContext.Id ?? "");
        _bridge.Rebind(SessionId ?? "");
        _bridge.AgentChanged += OnAgentChanged;
        Router.AttachConsumer(SessionId ?? "", _bridge);   // 订阅父会话事件
    }

    // Router 广播在消费 loop 线程；必须 InvokeAsync 编组回 Blazor 同步上下文
    private Task OnAgentChanged(FileResourceId id, string? diff) => InvokeAsync(() =>
        _host is null ? Task.CompletedTask : _host.NotifyAgentChangeAsync(id, diff));

    public void Dispose()
    {
        if (_bridge is not null)
        {
            _bridge.AgentChanged -= OnAgentChanged;
            Router.DetachConsumer(SessionId ?? "", _bridge);
        }
    }
}
```
- 组件实例经 **`@ref`** 持有 → 直接调用 `NotifyAgentChangeAsync`，不依赖跨 scope 的事件总线。
- **线程编组**：`OnAgentChanged` 必须 `InvokeAsync(...)`（Router 广播在后台线程，同 `SessionWindow.razor:1112-1114`），`FilePanelHost.NotifyAgentChangeAsync`/`FileViewerCoordinator` 内部亦不假设已在 UI 线程。
- **职责归属**：桥接（宿主层，订阅会话事件）与「取用/转发」均在 WebUI；组件库（`FilePanelHost`）只暴露 `NotifyAgentChangeAsync`，**不订阅任何会话事件**。

> 说明：也可由宿主直接以 `@ref` 调 `NotifyAgentChangeAsync`，此时 `AgentFileChangeBridge` 只是把「会话事件 → 文件语义」的转换集中到一处；两种调用方（Router 消费者或宿主）都只经组件公开方法，不跨界写 Scoped 状态。

### 7.4 样式与布局

- `.session-file-panel`：仿 `.session-todo-panel`（`session-page.css:557`）：`flex-shrink:0; border-left:1px solid var(--color-border); overflow:hidden;`；默认宽 480px。
- **双右侧面板**（Todo 320px + 文件 480px）：`@media (max-width: 1200px)` 下文件面板改**覆盖式**（绝对定位覆盖消息列）或自动折叠 Todo。

## 8. 并发 / 正确性 / 冲突

| 项 | 设计 |
|----|------|
| **截断回写** | `Display` 的 `IsTruncated=true` → `IsEditable=false`，禁保存。编辑走 `Full`；超 `MaxEditableSize`（默认 5MB）抛 `FileTooLargeException`。 |
| **编码回写** | `WriteAsync` 仅接受 UTF-8/ASCII；`FileContentClassifier` 按 BOM/启发式判编码，非 UTF-8 → `IsEditable=false`。杜绝 `WriteAllTextAsync`（固定 UTF-8）损坏原编码。**注意**：UTF-8 BOM 文件写回将丢失 BOM（`File.WriteAllTextAsync` 不写 BOM）——可接受，文档标注。 |
| **读取上限** | `MaxReadBytes`（默认 20MB）；超限 `IsSampled=true`、`Revision=null`、内容采样（头+尾），不整文件入内存。 |
| **revision** | 全文 SHA256；`MaxHashableSize`（默认 10MB）以上 `Revision=null`（不可乐观并发）。 |
| **写并发** | `PathWriteLocks` 路径锁 + revision 比对；**best-effort**（agent 工具本期不经 facade，跨进程不保证）。 |
| **半写读取** | watcher 防抖 + size/mtime 双采样稳定后才发事件。 |
| **目录重命名** | `Renamed` 时若 `OldPath` 是目录，对已打开文件做前缀重写。 |

**错误契约（UI 映射）**

| 异常/状态 | UI |
|-----------|-----|
| `UnauthorizedAccessException` | 「路径不在工作区」 |
| `FileNotFoundException` | 「文件不存在」 |
| `FileTooLargeException` | 「文件过大，仅预览」 |
| `FileNotEditableException` | 「该文件编码不支持编辑」 |
| `IOException` | 「读取失败，请重试」 |
| `Conflict` | 冲突弹窗（重载/保留） |

**diff 语义**：`LineDiff.ComputeUnified(old, new)`（LCS + hunk header，baseline→current）；`GetDiffAsync` 无状态；脏编辑器冲突时用 `GetDiffAsync(编辑器内容, 磁盘)` 展示差异。

## 9. 安全

- 所有路径操作过 `IWorkspacePathGate.EnsureAllowed`。
- 会话有效根经**正式端口**纳入边界；**不再由 `Resolve` 静默扩权**。
- `add_workspace_path` 扩权仍走审批链。
- 组件库不直接调 `System.IO`，只经契约。
- **信任前提**：`WorkingDirectory` 升格为边界输入；P0 加「仅宿主显式创建会话才信任」断言。

## 10. 测试策略

**Core（`tests/spine/Seeing.Agent.Tests`，xUnit）：**
- `SessionWorkspaceResolver`：有/无 cwd；不存在会话返回 null；目录不存在返回 null。
- `WorkspacePathGate`：全局根内、会话根内放行；两者外非白名单拒绝；`RestrictToWorkspace=false` 全放行。

**能力包（`tests/capabilities/Seeing.Agent.Files.Tests`，xUnit）：**
- 工厂：按 (sessionId, 有效根) 缓存/重建；`Release` 引用计数；`ResolveAsync` 回落 `LoadAsync`。
- `RootId` 稳定性；`Revision` 全文哈希与 `Display` 截断后一致。
- 截断：`Display` 标 `IsTruncated`/`IsSampled`；`Full` 超限抛 `FileTooLargeException`。
- 编码：UTF-8/ASCII 可编辑；GBK/UTF-16 无 BOM → `IsEditable=false`；写非 UTF-8 抛 `FileNotEditableException`。
- 乐观并发：`expectedRevision` 不匹配 → `Conflict` 且不写。
- `PathWriteLocks`：同路径串行；引用计数归零回收。
- `LineDiff`：增删改/多 hunk/空文件；与基线一致为空。
- `FileWatcherPool`：同根共享、引用计数、归零释放、忽略目录。
- `RootWatcher`：防抖合并、半写稳定性、Renamed 带 OldPath。
- 变更订阅：OS 事件 → `FileChangeEvent`；根外过滤。**不涉及会话事件**。
- 门闸：越界拒绝。

**组件库（xUnit，纯 C# 服务；`.razor` 渲染不自动化）：**
- `FileTabsState`：开/关/激活/脏/磁盘更新/重命名跟随（含目录前缀）/删除标记。
- `FileViewerCoordinator`：磁盘变更→刷新；脏冲突不覆盖；`InvokeAsync` 编组。
- `FilePanelState`：开合状态。
- `FilePanelHost.NotifyAgentChangeAsync`：喂入 diff 后命中已打开文件则刷新（用纯 C# 逻辑断言，不渲染）。

> **测试据实**：仓库无 bUnit、无 Playwright 脚手架。组件逻辑全部下沉为纯 C# 服务（上述），`.razor` 渲染层保持极薄，由手工验证覆盖；**不新增浏览器 harness**。

## 11. 分期与收敛

| 阶段 | 内容 | 交付 |
|------|------|------|
| **P0** | Core 端口 + 门闸改造 + 测试；`Tools.Support` 上提纯函数 + `LineDiff`；`BuiltInCapabilitySets` 加 `files.workspace`（Full/Code/Work + SecureDisabled）；`.slnx` 加 3 项目（Files / WebUI.Files / Files.Tests）；`02-modules.md` §3 加行 | 依赖与守门前置 |
| **P1** | 契约 + 能力包（stat/list/read/write + `GetDiffAsync` + watcher + HostedService + 测试） | 无 UI，能力可用 |
| **P2** | 组件库只读（树/标签/查看）+ UI 贡献插槽 + 级联会话 + 挂载 | 只读文件浏览器 |
| **P3** | 编辑器 + 保存 + 冲突/编码对话框 + 宿主桥接（`NotifyAgentChange` + diff） | 可编辑 + agent 改动可视化 |
| **P4（后续）** | 现有文件工具改为 facade 薄适配器（此时 `PathWriteLocks` 覆盖全写路径） | 能力统一 |

> **P4 实施修订（2026-10-14）**：经评估**不**将 `read/write/edit/glob/grep/delete` 改为 facade 适配器——那会**回退 agent 能力**（facade 拒绝非 UTF-8 写、5MB 编辑上限、结构化读取与工具行式输出不兼容、丢弃 `EditTool` 多策略模糊匹配）。P4 实际收敛目标为**统一进程内路径写锁真相源**：新增 `IPathWriteLock`（Abstractions）→ `ProcessPathWriteLock`（IO.Local，Singleton），由 `WriteTool`/`EditTool`/`DeleteTool` 与 `FileWorkspace` 共享，消除 agent 写与 UI 写的同进程竞态。**读写逻辑单一真相源**（工具与 facade 并存）仍未收敛，记为后续可选项。

## 12. 风险 / 开放问题

| 风险 | 说明 | 缓解 |
|------|------|------|
| 双源真相 | P1–P3 工具与 facade 并存 | P4 收敛；写并发本期 best-effort |
| 会话根信任 | `WorkingDirectory` 升格为边界输入 | 显式记录；P0 断言仅宿主显式创建会话才信任 |
| watcher 跨平台 | 递归差异、inotify 上限、大仓库开销 | 忽略规则 + 单根池化 + 防抖 + 稳定性校验；HostedService 可停 |
| 场景覆盖 | `files.workspace` 需入 Full/Code/Work，否则面板在对应场景隐藏 | P0 明确登记；Secure 档列入 `SecureDisabled` |
| 单面板约束 | 同 circuit 多 Full 窗口共享 `FilePanelState` | 本期约束「同时仅一个 Full 面板」；多窗口隔离留后续 |
| Blazor Server + Monaco | `ServerPrerendered` 须 `OnAfterRenderAsync` 初始化；RCL 宿主需自带 AntDesign/Monaco 脚本 | Dispose 销毁 editor；文档化宿主前提；手工验证 |
| 组件渲染测试 | 无 bUnit/Playwright harness | 逻辑下沉纯 C# 服务测试；渲染手工验证 |
| 组件库放置 | `samples/` 无可复用 UI 库先例 | 记录张力；禁作绕过贡献系统入口 |
| 非驻留会话 | `ISessionManager.Get` 未命中 | `ResolveAsync` 回落 `LoadAsync`；**回落契约**：会话不存在 → 抛错；存在但无 cwd → 用 `GetProjectRoot()`（§4.2 的 `resolver ?? GetProjectRoot()` 即此语义），两者不矛盾 |

---

## 附：关键既有代码引用

| 关注点 | 位置 |
|--------|------|
| `IFileSystem`（无原子写/编码固定） | `Execution/IFileSystem.cs`；`LocalFileSystem.cs:14-15,42-43` |
| `IWorkspacePathGate` | `Permissions/IWorkspacePathGate.cs` |
| `WorkspacePathGate` 实现 | `Core/Permission/WorkspacePathGate.cs:31-58` |
| `ISessionManager` | `src/primitives/Seeing.Session/Core/ISessionManager.cs:27,48,123` |
| `SessionData.WorkingDirectory` | `src/primitives/Seeing.Session/Core/SessionData.cs:71` |
| `WorkingDirectory` 写入点 | `ExecutionRunner.cs:128-134`；`SessionGroupManager.cs:597-619` |
| `GetProjectRoot()`（可能相对路径） | `Configuration/WorkspaceProviderExtensions.cs:14-24` |
| `ToolCallEvent` | `Events/MessageEventTypes.cs:290` |
| `IUiContribution` / `SlotContribution` | `Ui/UiContributionTypes.cs:58-62`；`IUiContribution.cs` |
| `SlotHost`（无参 DynamicComponent，级联可穿透） | `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor:16-31` |
| 插槽过滤 | `Services/SessionShellVisibility.cs:73-125` |
| UI 贡献先例 | `Services/CoreShellUiContribution.cs` |
| 模块 HostedService 模式 | `ScheduleHostedService.cs:50-54`；`MemoryPipelineWorker.cs:45-49`；`ModuleHostedServiceServiceCollectionExtensions.cs:12-20` |
| 模块生命周期编排 | `Core/Modules/ModuleLifecycleManager.cs:97-98,159-160` |
| 能力集守门 | `BuiltInCapabilitySets.cs:76-101,147-156`；`BuiltInScenariosTests.cs:44-61`；`SrcModuleScanner.cs:15-41` |
| `PermissionService` step 1 同源门闸 | `Core/Permission/PermissionService.cs:124-147` |
| 文件工具参考 | `src/capabilities/Seeing.Agent.Tools.FileSystem/{Read,Write,Edit,Delete}Tool.cs`、`FileSystemHelper.cs` |
| 现有劣质 diff（不复用） | `WriteTool.cs:132`；`EditTool.cs:266` |
| 挂载点 | `SessionWindow.razor:59,65`；`SessionToolbar.razor:141-189` |
| 模块注册先例 | `samples/Seeing.Agent.WebUI/Program.cs:61-62,218-222` |
