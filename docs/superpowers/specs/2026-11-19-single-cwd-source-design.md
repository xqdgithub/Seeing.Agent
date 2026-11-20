# 单一工作目录与协同目录设计

- 日期：2026-11-19（2026-11-20 安全/可行性审查后修订）
- 状态：待批准
- 关联：`docs/architecture/03-configuration-lifecycle.md`、`docs/architecture/14-session-inbox-runtime.md`、`docs/architecture/16-file-workspace.md`、`docs/architecture/36-heartbeat-rework-release-notes.md`

## 1. 问题（已核实的代码事实）

### 1.1 一次执行有四个"当前目录"

| # | 来源 | 位置 | 何时变 |
|---|---|---|---|
| 1 | 请求级 `ChatOptions.WorkingDirectory` | `ExecutionRunner.cs:151`、`SessionRuntime.cs:483` | 每次入站消息 |
| 2 | 会话级 `session.WorkingDirectory` | `ExecutionRunner.cs:152`，首轮被固化为**进程 CWD** | 首次执行 |
| 3 | 工作区根 `IWorkspaceProvider.GetProjectRoot()` | `WorkspaceProvider.cs` | 用户切换工作区 |
| 4 | 进程 CWD `Environment.CurrentDirectory` | `LocalExecutionWorld.cs:14` | 工作区切换时被改写 |

执行链算出的 cwd 在 `ExecutionRunner.cs:151-157` 与 `:740-742` **算了两遍**；第一处位于 `try` 内且 `catch` 只 `LogWarning`（`:164-167`），异常会被吞掉。真正喂给工具的是第二处，且仍回落 `executionWorld.Cwd`。

工具层拿不到这个值：

- `ToolCallInvoker.cs:106-108` 调用 `_tools.ExecuteAsync(...)` 时没传 cwd；
- `IToolManager.ExecuteAsync`（`IToolManager.cs:67-75`）签名里没有 cwd；
- `ToolContext`（`ITool.cs:10-58`）没有 cwd 字段；
- 文件工具退回 `IFileSystem.GetFullPath(path)` → `Path.GetFullPath(path)`（`LocalFileSystem.cs:124`）→ **进程 CWD**；
- `BuiltInToolBase.cs:16` 把进程 CWD 在 DI 构造时快照进 `_workingDirectory`；
- `BashTool.cs:72`、`GitService.cs:209`、`GlobTool`、`GrepTool` 默认用 `_world.Cwd`（进程 CWD）；
- `AgentJob.cs:337`（定时/心跳）完全绕开会话 cwd。

### 1.2 实证

`~/.seeing/logs/seeing-desktop-20261119.log` 当天 35 次 write/edit：21 次落 `D:\Projects\rezen-project\...`，13 次落 `D:\wt\...`（该目录现已不存在），同一宿主进程内解析基准发生漂移。

### 1.3 工作区派生状态未参与切换

| 组件 | 问题 | 证据 |
|---|---|---|
| `ScheduleManager` | `_jobsFile` 只在 `StartAsync` 读盘，`_started` 后早退；切换时参与者只重建 Quartz | `ScheduleManager.cs:23,55-67`、`SchedulerWorkspaceParticipant.cs:31-47` |
| Quartz `dbPath` | 连接串在 `AddQuartz` 配置委托里算一次并被单例工厂缓存；`RebuildAsync` 只丢弃 `IScheduler` | `SchedulerServiceCollectionExtensions.cs:152-185`、`QuartzSchedulerEngine.cs:353-378` |
| `ToolManager._projectDisabledTools` | 只在启动 `LoadToolStateAsync` 加载 | `ServiceCollectionExtensions.cs:905-910`、`ToolManager.cs:285-350` |
| `AgentManager` | 只增不删，且没有"上一工作区项目级 agent 名"的记录 | `AgentManager.cs:29,102-128,559-630` |
| `WeComSessionTracker._stateFilePath` | 构造期快照，靠子进程重启兜底 | `WeComSessionTracker.cs:19,36` |
| ACP `_defaultWorkingDirectory` | 构造期快照进程 CWD | `SeeingAcpClient.cs:24,49-50` |

### 1.4 切换流程自身缺陷

- `WorkspaceSwitcher.cs:156` 的 `SetWorkspaceRoot` 跑在 `SuppressAutoReload`（`WorkspaceReloadAdapters.cs:40`）之前，`WorkspaceRootChanged` 已排入去抖；去抖回合不再检查抑制，会在显式两轮之后再跑一轮。
- 抑制期内的真实配置/工作区信号被**直接丢弃**且无补偿轮次（`ReloadOrchestrator.cs:236-266`）。
- `ReloadOrchestrator.cs:307-311` 自动路径把 `OldWorkspace` 填成 `_workspace.StartupDirectory`。

### 1.5 协同目录与多根工作台的现状缺口

- `IFileWorkspaceFactory` 只有单根 `ResolveAsync(sessionId)`（`IFileWorkspaceFactory.cs:11-14`）。
- `FileWorkspace.GetResourceId` 用进程 CWD 解析（`FileWorkspace.cs:64`），越界时产出 `../..` 相对路径。
- `FileWorkspace.GetAbsolutePath` **忽略 `id.RootId`**、只用自身 `RootPath`（`:70-71`）→ 多根后会写错根。
- `FileWorkspaceFactory.Release(sessionId)` 按会话减引用计数，与"(sessionId, root)"键不兼容（`FileWorkspaceFactory.cs:64-77`）。
- 附件准入 `EnsureWithinWorkspace` 明确不含扩权白名单（`IWorkspacePathGate.cs:17-23`），协同目录会发不出附件。

## 2. 目标与非目标

### 目标

1. **一次执行只有一个 cwd**：三级参数结构保留，但本期统一收敛到工作区根；进程 CWD 不再被业务消费，写入点由宿主开关控制（WebUI/Desktop 关闭，CLI/TUI 单工作区保留），兜底消费点从 3 处压到 1 处。
2. **cwd 显式传递到每个工具**，工具不得自行推断基准。
3. **工作区派生状态必须声明依赖**：不缓存，或实现 `IWorkspaceApplyParticipant` / `IReloadHandler<WorkspaceChange>`，切换后从零重建。
4. **编译器 + 不变量测试守住上述两条**。
5. 修复 1.4 的两个切换流程缺陷（含抑制期丢信号）。
6. 会话级**协同工作目录**（多个）：持久化、登记即信任、工具可读写、文件工作台可浏览与编辑。
7. **文件工作台支持多根**：主根（= 会话 cwd = 工作区根）+ 全部协同目录，每根独立、路由明确、越界即错。

### 非目标

- 不引入 Scoped `IExecutionWorld`。
- 不改 `IFileSystem` 的 I/O 方法语义（只改路径解析入口）。
- 不改 ACP 协议、工作区切换 UI 流程、通联目录已正确的对齐机制。
- 不做"项目级默认协同目录"（跨会话共享列表）。
- 不实现"只读协同目录"（本设计统一可写）。
- **不做会话级基准切换**：本期相对路径基准恒为工作区根；协同目录一律用绝对路径或命令级 `workdir` 访问。基准切换若将来要做，必须满足"只在轮次边界生效 + 产生 `context-update` 叙述 + 系统提示词始终有当前目录行"三条，不在本期范围。

## 3. 约束

- 能力包只依赖 `Abstractions` + 原语 + support 层；契约改动落在 `src/abstractions/`。
- 工具是 Singleton，**禁止**在构造期捕获任何随工作区/执行变化的值。
- 测试禁止固定时长等待（AGENTS 硬规则）。
- 路径边界判定只用 `PathBoundary`（含符号链接 realpath）。
- 原子写只用 `AtomicFileWrite`。

## 4. 方案与取舍

### 选中：单一 cwd 事实 + 显式传递 + 编译期守卫

- cwd 在 `BuildExecutionContextAsync` 计算一次，沿 `ChatExecutionContext → AgentContext → ToolCallInvoker → IToolManager → ToolContext` 传到底。
- `IFileSystem.GetFullPath` 改为必须给基准目录（两阶段迁移）。
- 缓存必须注册工作区变更。
- 文件工作台按"每根一个 facade"扩展，而不是虚拟合并根。

### 已排除

| 方案 | 为什么不用 |
|---|---|
| 工具只接受绝对路径 | 破坏 LLM 使用体验，且无法阻止模型写相对路径 |
| `IFileSystem` 改 Scoped | 工具是 Singleton，装饰器链/缓存/并发模型全要改 |
| 保留进程 CWD，多同步几处 | 治不了多会话并发与跨进程场景，正是要根治的 |
| 文件工作台虚拟合并根 | 跨根移动/复制与边界判定复杂，`FileResourceId` 已支持多根 |
| Quartz 自建裸 `StdSchedulerFactory` | 会丢 DI JobFactory（`AgentJob` 无法实例化）与 serializer 等价性 |

## 5. 详细设计

### 5.1 cwd 的唯一计算点

**唯一入口**：`ExecutionRunner.BuildExecutionContextAsync`（`ExecutionRunner.cs:740`）。删除 `:151-157` 的独立计算；`:235` 的 `executionScope.Cwd` 与 `:740` 使用同一个结果。

新增 `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionCwdResolver.cs`：

```csharp
internal readonly record struct CwdResolution(
    string Path,
    bool NeedsSessionWriteBack,
    string? Warning);

/// <summary>唯一 cwd 解析点：请求级 > 会话级 > 工作区根。进程 CWD 不参与。</summary>
internal static CwdResolution Resolve(
    string? requestCwd,
    string? sessionCwd,
    IWorkspaceProvider workspace);
```

规则：

0. `workspace.IsResolved == false` → 抛 `InvalidOperationException`（执行不得发生在未决议状态）。
1. `projectRoot = workspace.GetProjectRoot()`；**本期不变式：`Path` 恒等于 `projectRoot`**。
2. 请求级非空且 ≠ `projectRoot` → 忽略并 Warning（内部/兼容通道，UI 不暴露）。
3. 会话级非空且 ≠ `projectRoot` → 纠正为 `projectRoot`，`NeedsSessionWriteBack = true`，Warning 记一次。
4. 会话级为空 → 写入 `projectRoot`，`NeedsSessionWriteBack = true`。
5. 返回 `projectRoot`。

`CwdResolution` 是 **internal 实现细节**（`InternalsVisibleTo` 给测试），对外调用方只看到 `(string Path, bool NeedsSessionWriteBack)`；`Warning` 只进日志，不参与契约。`SamePath` 统一用 `PathBoundary.PathComparison` + `PathBoundary.Canonicalize`，禁止在 resolver 里再写一套 `OrdinalIgnoreCase` 比较。

**进程 CWD 写入受宿主开关控制**：`WorkspaceProvider` 增加 `MutatesProcessCwd`（默认 false）；`SetWorkspaceRootInternal` 只在为 true 时 `Directory.SetCurrentDirectory`。WebUI/Desktop 显式 false，CLI/TUI 单工作区宿主显式 true。`LocalExecutionWorld.Cwd` 仍返回进程 CWD，但只允许 1 个消费点（启动期兜底），其余全部走执行级 cwd。

**本期不分辩目录是否存在**：任何 ≠ `projectRoot` 的请求级/会话级值都在第 2/3 步被收敛，不会走到 `Directory.Exists`。因此"请求级目录不存在→失败"这类语义本期不存在；测试与验收也只断言"收敛 + 回写 + 未决议抛"。

**cwd 写入/传入通道必须全部收敛**（除 `ExecutionRunner` 回写外还有三处）：

| 通道 | 处理 |
|---|---|
| `SessionDirectory.CreateAsync(..., workingDirectory, ...)`（`SessionDirectory.cs:72-89`，内部调 `SessionGroupManager.CreateRootAsync` `:646-671`） | **删除该参数**（硬动作，不是"归一化"）；会话创建时 `WorkingDirectory` 直接写 `GetProjectRoot()` |
| WebUI 提交（`SessionWindow.razor:1909` 传 `session.WorkingDirectory`） | 不再传 `WorkingDirectory`；请求级通道只留给内部/兼容调用方 |
| `SessionRuntime.cs:483`（`inbox.working_dir`） | 保留解析，但值同样被第 2 步收敛 |

三级参数与返回结构保留（`CwdResolution`），供未来放开"基准切换"；本期所有越级取值都被收敛到工作区根。历史会话的旧 cwd（`D:\Users\Administrator`、`D:\wt` 等）在第 3 步被纠正并回写；若仍需访问那些目录，必须登记为协同目录。`NeedsSessionWriteBack` 为真时 `ExecutionRunner` 回写并 `SaveAsync`，异常不再位于会被 `LogWarning` 吞掉的块内。

### 5.2 契约改动

**`IFileSystem`**（`IFileSystem.cs:71-72`）分两阶段：

```csharp
// 阶段 1（Task 1）：新增双参，单参转发并只打警告
string GetFullPath(string path, string baseDirectory);

[Obsolete("业务路径解析必须显式给出基准目录。")]
string GetFullPath(string path);

// 阶段 2（最后一个任务）：单参改为 error: true，并清空所有调用点
```

`LocalFileSystem`：`GetFullPath(path, base)` → `Path.GetFullPath(path, base)`。

**`ToolContext`**（`ITool.cs:10-58`）：

```csharp
/// <summary>本次执行的 cwd（ExecutionRunner 计算、ToolManager 填充）。路径类工具必须以此为准。</summary>
public string? WorkingDirectory { get; set; }
```

**`IToolManager.ExecuteAsync`**（`IToolManager.cs:67-75`）不再追加第 9 个可选参数，改为接收**执行事实的单一载体**：

```csharp
/// <summary>一次执行的不可变事实。由 ToolCallInvoker / AgentJob 两处唯一构造。</summary>
public sealed record ToolInvocation(
    string SessionId,
    string Cwd,
    AgentDefinition? Agent,
    IPermissionAuthorizer? Authorizer,
    IExecutionToolScope? ToolScope,
    SkillCatalogSnapshot? SkillSnapshot,
    IReadOnlyList<CollaborationDirectory> Collaborations,
    Func<IMessageEvent, ValueTask>? Emit);

Task<ToolResult> ExecuteAsync(
    ToolCall toolCall,
    ToolInvocation invocation,
    CancellationToken cancellationToken = default);
```

`ToolContext` 由 `invocation` **投影**（保留现有 setter 字段，兼容全部工具与测试），不再让调用方逐字段填。装饰器链继续用 `MemberwiseClone` 透传。

**`PermissionRequest`**（`src/abstractions/.../Permissions/PermissionRequest.cs`）新增：

```csharp
/// <summary>本次执行的 cwd；用于把相对 Resource 绝对化与审计。</summary>
public string? WorkingDirectory { get; init; }
```

**会话模型**（`SessionData.cs:70` 附近）：

```csharp
/// <summary>会话工作目录：保留并参与解析；本期不可编辑，恒等于工作区根；偏离时执行期纠正并回写。</summary>
public string? WorkingDirectory { get; set; }

/// <summary>协同工作目录：额外信任根，可读写、可浏览。Path 为规范化绝对路径、去重。</summary>
public List<CollaborationDirectory> CollaborationDirectories { get; set; } = new();

/// <summary>一条协同目录。</summary>
public sealed record CollaborationDirectory(string Path, string? Note);
```

`CollaborationDirectory` 是**顶层 record**，与 `SessionData` 同文件（`Seeing.Session` 原语层），不做嵌套；`Abstractions` 通过 `using Seeing.Session.Core` 引用它。

**目录判定（唯一权威，只读）**：新增 `ISessionRootSet`（Abstractions，实现 Core）。

```csharp
public enum SessionRootKind { Project, Collaboration }

public sealed record SessionRoot(
    string Path, SessionRootKind Kind, string? Label, bool Exists);

public interface ISessionRootSet
{
    /// <summary>可浏览的根（项目根 + 协同目录），供工作台/上下文源。</summary>
    IReadOnlyList<SessionRoot> Roots(string sessionId);

    /// <summary>工具/工作台读写：根 ∪ 临时白名单。</summary>
    bool IsAuthorized(string sessionId, string absolutePath);

    /// <summary>仅根（附件/工作台展示）：<b>不含</b>临时白名单。</summary>
    bool IsTrusted(string sessionId, string absolutePath);
}
```

**协同目录配置（唯一写入口）**：新增 `ICollaborationDirectories`（Abstractions，实现 Core）。

```csharp
Task<IReadOnlyList<CollaborationDirectory>> GetAsync(string sessionId, CancellationToken ct = default);
Task AddAsync(string sessionId, string absolutePath, string? note, CancellationToken ct = default);
Task RemoveAsync(string sessionId, string absolutePath, CancellationToken ct = default);
```

**职责边界（按事实分，不按"都叫目录"分）**：

| 概念 | 端口 | 说明 |
|---|---|---|
| 项目根 | `IWorkspaceProvider` | 环境事实，不变 |
| 会话 cwd | `SessionData.WorkingDirectory` | 本期恒等根，执行链纠正 |
| 协同目录 | `ICollaborationDirectories` | 持久**配置**，用户/工具（审批）写 |
| 临时授权 | `IPermissionGrantStore` | 审批**结果**，内存，切工作区清空 |
| 工作台根 | `ISessionRootSet.Roots` | **投影**，不落存储 |
| 判定 | `ISessionRootSet` | `IsAuthorized` 含白名单；`IsTrusted` 不含 |

消费方映射：

| 消费方 | 只用 |
|---|---|
| `WorkspacePathGate` | `IsAuthorized`（薄适配，只留 `RestrictToWorkspace` 与文案） |
| `ChannelAttachmentNormalizer` | `IsTrusted`（白名单永不泄漏） |
| `FileWorkspaceFactory` | `Roots`（不再自己算根） |
| `CollaborationDirectoriesContextSource` | `ICollaborationDirectories.GetAsync` |
| `add_collaboration_directory` | `ICollaborationDirectories.AddAsync`（审批在实现内部） |
| `add_workspace_path` | `IPermissionGrantStore`（临时、不审批，行为不变） |
| WebUI 根列表 / 设置 | `Roots` + `ICollaborationDirectories.RemoveAsync` |

同步方法只查内存缓存；冷会话由调用方先 `TryGetOrLoadAsync`（§5.9）。本期会话 cwd 恒等 `ProjectRoot`，`SessionRootKind` 不单列 `SessionCwd`；未来放开基准切换时再加。

### 5.3 传递链

```
ExecutionCwdResolver.Resolve
  → ChatExecutionContext.WorkingDirectory（唯一赋值点）
  → AgentContext.WorkingDirectory
  → ToolCallInvoker 构造 ToolInvocation（唯一构造点之一）
  → IToolManager.ExecuteAsync(toolCall, invocation)
  → ToolContext 由 invocation 投影
  → 工具
```

`AgentJob` 是第二个、也是最后一个 `ToolInvocation` 构造点（定时/心跳旁路）。

`ExecutionRunner` 与 `SchedulerContextAssembly` 的执行作用域装配收敛到 `ExecutionScopeInitializer.Initialize(scope, invocation)` 一处，避免"某条旁路漏填字段"。

`ToolDecorator` 链用 `MemberwiseClone`，字段自动透传。`CachedToolDecorator` 的缓存键必须并入 `WorkingDirectory`。

WebUI 提交不再传 `WorkingDirectory`（`SessionWindow.razor:1909` 删除该赋值）；请求级通道只保留给内部/兼容调用方。

### 5.4 工具改造清单

| 文件 | 改法 |
|---|---|
| `BuiltInToolBase.cs` | 删除 `_workingDirectory` 与 `workingDirectory` 构造参数；`ResolvePath` 移出基类 |
| `IWorkspacePathGate.ResolveAllowed(...)`（新增） | `string? ResolveAllowed(string sessionId, string path, string? baseDirectory, out string fullPath)`：相对路径按 base 绝对化 + 一次判定；**解析与授权合成一个入口**，不再有独立的 `ToolPath` 约定 |
| `WriteTool` / `EditTool` / `ReadTool` / `DeleteTool` / `OpenFileTool` | 只调一次 `RegateHelper`（薄壳）→ `ResolveAllowed`；失败返回 `Failure` |
| `AddWorkspacePathTool` | 同上；删除构造期快照依赖 |
| `GlobTool` / `GrepTool` | 省略 `path` 时默认根 = `context.WorkingDirectory`；相对路径同基准 |
| `BashTool` | `workdir = args.workdir ?? context.WorkingDirectory`；**并校验 workdir 在 `ResolveTrustedRoots` 内**，否则 `Failure`（见 §5.11） |
| `GitService` / `Git*Tool` | `ExecuteAsync` 增加 `workingDirectory`；工具层传 `context.WorkingDirectory`；同样校验在授权根内 |
| `TranscribeAudioTool` | 同文件工具 |
| `ChannelAttachmentNormalizer` / `SendMessageTool` | 改用双参 `GetFullPath`；附件准入改用 `EnsureWithinTrustedRoots`（§5.6） |
| 其余 `BuiltInToolBase` 子类 | 仅受基类改动影响 |

统一失败语义：路径类工具拿不到 cwd 或越界 → 明确 `Failure`，不静默回落。

### 5.5 非执行链的 cwd

| 位置 | 改法 |
|---|---|
| `AgentJob.cs:337` | `cwd = session.WorkingDirectory`（存在）`?? _workspace.GetProjectRoot()`；赋 `AgentContext.WorkingDirectory` 并沿 §5.3 透传 |
| `SchedulerContextAssembly.cs:82` | 接收调用方 cwd，不取 `_world.Cwd` |
| `PermissionService.cs:289-295` | `BuildPolicyContext` 用 `request.WorkingDirectory ?? ""` |
| `PermissionService.cs:329-341` | `ResolveResourceDirectory` 用 `request.WorkingDirectory` 兜底 |
| `PermissionContext.cs:28` | 默认值改为空字符串 |
| `SkillLoader.cs:97` | 无 `ISeeingDirectories` 时回落工作区根 |
| `Mcp/WorkspaceSeeingDirectories.cs:15` | 空 root 直接抛 `ArgumentException` |
| `AcpFileSystemBridge` / `AcpPassthroughExecutor` / `AcpTerminalBridge` | 显式传入优先，兜底改工作区根；`SeeingAcpClient._defaultWorkingDirectory` 删除，每次 Run 取请求值 |
| `LocalSubprocessFactory.cs:20` | 保留进程 CWD 兜底，注释限定"仅无 cwd 语义的子进程" |
| `FileWorkspace.cs` | 相对路径以 `RootPath` 为 base；`GetResourceId` 越界抛；`GetAbsolutePath` 校验 `RootId`（§5.10） |

### 5.6 授权根与门闸（唯一计算点）

授权判定**只有 `ISessionRootSet` 一个权威**（§5.2）：

- `IsAuthorized(sessionId, absPath)` = 项目根 ∪ 协同目录 ∪ 临时白名单（工具读写）。
- `IsTrusted(sessionId, absPath)` = 项目根 ∪ 协同目录（**不含白名单**，附件/工作台展示）。

`WorkspacePathGate` 退化为薄适配：只保留 `RestrictToWorkspace` 开关与错误文案，内部调 `ISessionRootSet`。会话 cwd 不再作为合法根（本期恒等项目根，已被覆盖）；未来放开基准切换后，若 cwd 落在项目根外，必须同时登记为协同目录。

**附件准入**：`EnsureWithinWorkspace` 改为调 `IsTrusted`，同步修改锁定测试 `WorkspacePathGateSessionRootTests.cs:98-119`（该测试锁定的"白名单不泄漏到附件"语义**保持不变**——这正是 `IsTrusted` 存在的理由）。

**Deny 优先级**（修复现有短路缺陷）：`PermissionService` 步骤 1 只记录 `gateAllowed`，不再立即 `Allow`；步骤 3 若命中文件类 Deny 规则 → 拒绝；否则 `gateAllowed && !RequireInteraction` → `Allow`。这样 `explore`/`plan` 等文件的子代理在协同目录内也不会被门闸放行绕过策略。

**`RequireInteraction` 也要短路生效开关**（第 4 步）：把 `toggle == Allow` 的提前返回改为 `toggle == Allow && !RequireInteraction`，否则 `AutoApproveAll` 会绕过强制交互。

### 5.7 工作区派生状态

| 组件 | 设计 |
|---|---|
| `ScheduleManager` | **不注册** `IReloadHandler`（与参与者顺序互斥）。新增内部方法 `ResetForWorkspace()`（停引擎后清 `_jobsFile`、`_started=false`）与复用 `StartAsync`。由 `SchedulerWorkspaceParticipant` 编排：`OnApplying` → 停引擎 + `ResetForWorkspace`；`OnApplied` → `RebuildAsync` + `StartAsync` |
| Quartz 库位置 | `quartz.db` 改为**用户级固定路径**：`~/.seeing/scheduler/quartz.db`（`Scheduler.Persistence.ConnectionString` 显式给时覆盖）。持久触发器是宿主级缓存，工作区差异放在 job data（`AgentJob` 的 cwd / session）；任务真相仍是每个工作区的 `jobs.json`。`AddQuartz` 原样保留，**不再需要按工作区重建 factory**；`RebuildAsync` 只做"停 → 清 → 按新 `jobs.json` 重建触发器" |
| `ToolManager` | 新增 `IReloadHandler<WorkspaceChange>`：先清空 `_projectDisabledTools`，再 `LoadToolStateAsync` |
| `AgentManager` | **不新增字段**。`ResetProjectOverridesAsync()` = 遍历 `_configCache` 中 `Project:` 前缀的键 → 逐个 `RemoveMdConfigFromStoreAsync` → 清缓存 → 重新应用用户级 + 项目级（与 `SessionData`/`ToolManager` 的"从零重建"口径一致）。`AgentManagerReloadHandler` 调它 |
| `WeComSessionTracker` | `_stateFilePath` 每次现算；或 participant 重置 |
| `QQSessionTracker` | participant `OnApplied` 清空映射 |
| ACP | 长驻租约每次 Run 覆盖 cwd；`_defaultWorkingDirectory` 删除 |

### 5.8 切换流程修复

1. **抑制范围**提升到整个 `ApplyAsync`：`WorkspaceSwitcher` 在 `SetWorkspaceRoot` 之前获取抑制作用域，`finally` 释放。
2. **抑制期记账而非丢弃**：编排器在抑制中置 `_suppressedSignal`；作用域释放时若为真，补一次去抖全量轮（`WorkspaceChange` + `ConfigChange`）。
3. **`OldWorkspace` 正确来源**：`OnWorkspaceChanged` 暂存 `e.OldWorkspace ?? ""` 到字段，跨去抖传给 `WorkspaceChange`；不再读 `StartupDirectory`。

### 5.9 协同工作目录

#### 概念

| 概念 | 字段 | 数量 | 作用 |
|---|---|---|---|
| 会话工作目录 | `SessionData.WorkingDirectory` | 1 | 相对路径基准、shell cwd |
| 协同目录 | `SessionData.CollaborationDirectories` | N | 额外信任根、工具可读写、工作台可浏览 |

协同目录不改变相对路径基准。

#### 同步清单（新增字段必须同时改这 6 处）

1. `SessionData.Clone()`（`SessionData.cs:531-582`）深拷贝。
2. `SessionSummary` 投影（`SessionSummary.cs:32-51`）。
3. `SessionGroupManager.CreateChild`（`:729` 附近）。
4. `SessionGroupManager.CreateHandoffSuccessorAsync`（`:850-859`）。
5. `SessionForker`（`:49` 附近）。
6. `FileSessionStore` 摘要读分支（`:552-595`，不认识字段会 `reader.Skip()`）。

推荐把 3/4/5 统一到一个 `CopySemanticFieldsTo(SessionData target)`，避免下次漏改。

#### 校验与规范化

- 写入必须是绝对路径；相对路径按项目根归一化并记录。
- 去重与落盘统一使用 `PathBoundary.Canonicalize`（`Path.GetFullPath` + realpath），审批卡片显示同一规范化结果。
- 目录不存在：允许登记但标记失效，UI 灰显；执行期跳过并 Warning；**不回落主根**。

#### 生命周期

- 新建为空；用户 UI 编辑或 agent 工具（审批后）添加。
- 子会话 / fork / handoff 继承。
- 会话删除时一起消失。
- 切换工作区：持久化字段保留；内存白名单清空。
- **share / import / restore 必须清空或重新确认**：`SessionSharer` 会整份序列化会话（`SessionSharer.cs:27-45,74-89`），否则信任随分享复活。

#### 统一写入口

`ICollaborationDirectories`（§5.2）是协同目录的**唯一写入口**（审批 + 落盘在实现内部）；工具与 UI 都经它，`SessionData.CollaborationDirectories` 只是它的持久化细节。`ISessionRootSet` 从它取协同目录（`Roots`），不再各自读 `SessionData`。上下文源走 `GetAsync`，并在执行级钉住快照。

`add_workspace_path`（临时白名单、不审批）与 `add_collaboration_directory`（持久、审批）**保留两个工具**：差异由生命周期决定，不合并为 `scope` 参数（避免改变既有工具行为与提示词）；但目录的读写与校验都收敛到上述端口 / `IPermissionGrantStore`，不再各自实现。

#### 工具与审批

新增 `add_collaboration_directory(path, note?)`：

1. **不登记** `DefaultToolPermissionPolicy`（避免与工具自审重复）；工具内直接使用 `context.PermissionAuthorizer`。
2. `context.PermissionAuthorizer == null` → `Failure`（定时/心跳等无审批通道的执行不得持久化信任根）。
3. 发起 `AuthorizeAsync(new PermissionRequest { PermissionKind = "filesystem.collaboration_directory", Resource = absPath, RequireInteraction = true, WorkingDirectory = context.WorkingDirectory })`。
4. 只有 `Decision == Allow && Scope != Once` 才写入会话并 `SaveAsync`；`Once` 不持久化。
5. `PermissionKindMapper` 增加该 kind 映射，使 agent 的 Deny 规则可表达。

移除只由 UI 完成（agent 只加不减）。

#### 冷会话

`ResolveCollaborationDirectories` 只查内存会话；门闸/工作台在会话未加载时先 `SessionManager.TryGetOrLoadAsync`（异步），避免"冷会话协同根不授权、不显示"。

#### 上下文呈现（模型必须知道有哪些协同目录）

新增 `src/spine/Seeing.Agent.Core/Context/CollaborationDirectoriesContextSource.cs`（`IContextSource`）：

| 属性 | 值 |
|---|---|
| `Key` | 类型全名 |
| `Scope` | `ContextSourceScope.Session`（仅影响源排序；基线本身按会话落在 `session.Metadata`，换 `Project` 也不会跨会话共享） |
| `Target` | `ContextSourceTarget.System` |
| `ValueSchemaVersion` | `1` |
| `MaxBaselineChars` | `4_000`（源内按行边界自行截断；框架只诊断、不二次截断） |

**不渲染当前工作目录**：`## Environment` 分节已由 `EnvironmentPromptSectionContributor`（`EnvironmentPromptSectionContributor.cs:28`）**每请求实时**渲染 `Working directory` / `Workspace root`。系统基线是快照、切换工作区不会重写，重复渲染会产生两个互相矛盾的 cwd。本源标题只写明"相对路径以 Environment 分节的工作目录为基准"。

**执行级钉住**：`IContextExecutionScope` 新增 `IReadOnlyList<CollaborationDirectory>? CollaborationDirectories`（与 `SkillSnapshot` 同构），由 `ExecutionRunner` 在构建执行作用域时填入。**定时/心跳旁路同样要填**：`SchedulerContextAssembly`（`SchedulerContextAssembly.cs:71-85`）自己 `CreateScope` 并调用同一装配器，若不填会落到"快照为 null = 接线缺失 → `Unavailable`"，导致定时执行里协同目录永远不可见。

**成员集合钉住，存在性是实时探针**：`ReadAsync` 时对每个 `Path` 做 `Directory.Exists`，封装为带取消令牌与短超时的帮助方法（UNC / 可移动盘不能阻塞装配热路径；按会话做短 TTL 缓存）。快照里不存 `Exists`。

**空列表**：读取成功但列表为空时返回 `ContextValueState.Value` + `Canonical = {"directories":[]}`，**不得返回 `Removed`**——`Removed` 在首次装配会被 `ContextAssembler` 直接 `continue`（`ContextAssembler.cs:277-283`），连"（无）"都不会渲染。`Removed` 仅用于"源整体失效"；`Unavailable` 仅用于钉住快照为 `null`（宿主接线缺失）。

**Canonical**（稳定排序，落盘、用于 diff）：

```json
{"directories":[
  {"path":"D:/data/reports","note":"报表输出","exists":true},
  {"path":"D:/shared/specs","note":null,"exists":true}
]}
```

其中 `exists` **不是快照字段**，而是 `ReadAsync` 实时探针的结果；同一路径在"成员未变、目录被删/恢复"时正是靠它产生增量叙述。

**RenderInitial**（首次进入系统提示词，字节稳定）：

```
协同工作目录（可读写；相对路径以 Environment 分节的工作目录为基准）:
- D:\data\reports（报表输出）
- D:\shared\specs
```

空列表渲染 `- （无）`。

**Render**（previous → current，无变化返回 `null`，不推进基线）：

- `协同目录已添加：D:\data\reports（报表输出）`
- `协同目录已移除：D:\shared\specs`
- `协同目录不可用：D:\old\missing（目录不存在）`
- `协同目录已恢复：D:\old\missing`
- 备注变化：`协同目录备注已更新：D:\data\reports（新备注）`

**时机**：源在一次执行内钉住，当轮不漂移；工具与门闸读会话实时列表，所以 `add_collaboration_directory` 成功后新根**本次执行内即可用**，但模型只能从工具返回值得知；`context-update` 在**下一次执行**开始时的装配中插入（不是下一轮工具调用）。设计与验收按这个时机写。

**边界**：只渲染路径与备注，不渲染目录内容；不因协同目录存在而改变相对路径基准（与"本期不做基准切换"一致）。

**注册**：Core 在 `AddContextPipeline` 内追加 `services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, CollaborationDirectoriesContextSource>())`。**必须 scoped**：源构造注入 `IContextExecutionScope`（scoped），单例源会在 `ValidateScopes` 下解析即抛，或在关闭校验时泄漏某次执行的钉住事实（`ServiceCollectionExtensions.cs:781-786` 的硬约束）。

### 5.10 多根文件工作台

#### 契约（`Abstractions.Files`）

```csharp
public enum FileWorkspaceRootKind { Project, Session, Collaboration }

/// <summary>根描述；由 facade 拥有，不再单独引入 Scope 包装类型。</summary>
public sealed record FileWorkspaceRoot(
    string RootId, string RootPath, string Label, FileWorkspaceRootKind Kind, bool Exists);

public interface IFileWorkspace
{
    FileWorkspaceRoot Root { get; }        // RootId / RootPath 由它转发，消除同义字段
    string SessionId { get; }
    // ...其余成员不变
}
```

`IFileWorkspaceFactory`（**列表、精确取、按路径路由、释放全在一个契约里**，不再另设 `ISessionFileRootResolver`）：

```csharp
Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default);                 // 主根（兼容）
Task<IReadOnlyList<IFileWorkspace>> ListAsync(string sessionId, CancellationToken ct = default);     // 全部根
Task<IFileWorkspace?> GetAsync(string sessionId, string rootId, CancellationToken ct = default);     // 精确取
Task<IFileWorkspace?> ResolveAsync(string sessionId, string absolutePath, CancellationToken ct = default); // 路径→根
void Release(string sessionId);                                                                      // 幂等全清
```

#### 根集合与主根

- **项目根**：必有（`Project`）。
- **会话 cwd**：本期恒等于项目根，故不单独成根；`FileWorkspaceRootKind.Session` 结构保留，供未来放开基准切换时使用。
- **协同目录**：全部加入（`Collaboration`），失效项 `Exists = false`。
- `ResolveAsync(sessionId)` 的"主根"**本期恒为项目根**（会话 cwd 恒等于它）。未来放开基准切换后，主根 = 会话 cwd（若在授权根内）否则项目根。

#### 缓存与生命周期

每会话一个 Entry，**无引用计数**。现有计数本来就配不平：`Resolve*` 是读操作却带 +1 副作用，`Release` 只由面板 `Dispose` 调一次，而 `AgentFileChangeBridge` 从不 Release；多根后每次刷新再加 N，缓存与 watcher 永不回收。

```csharp
sealed record Entry(Dictionary<string, IFileWorkspace> Workspaces);  // key = RootId
```

- `ListAsync` 每次按当前根集合增量对齐（新增创建、移除回收）。
- `Release(sessionId)` **幂等全清**；watcher 生命周期由 `FileWatcherPool` 自己的根级引用负责。

#### 路由与边界

- `FileWorkspace.GetResourceId(path)`：以 `Root.RootPath` 为 base；结果越界（`../`）→ 抛 `ArgumentException`。
- `FileWorkspace.GetAbsolutePath(id)`：`id.RootId != Root.RootId` → 抛；结果不在根内 → 抛。
- `IFileWorkspaceFactory.ResolveAsync(sessionId, absolutePath)`：按最长前缀匹配 + `PathBoundary` 同根判定，路由到根。`AgentFileChangeBridge` 与 UI 统一走它，不再"只有当前会话那一个 ws"。

#### 监听与消费方

| 位置 | 改法 |
|---|---|
| `FileWatcherPool` | 按根订阅（池已按根 key）；失效根取消 |
| `AgentFileChangeBridge` | 用 `factory.ResolveAsync(sessionId, absolutePath)` 归属根；UI 标注根名 |
| `UploadedFileService` | 默认落主根；需要协同根时显式传 `rootId` |
| `FilePanelHost.razor` / `FileEditor.razor` / `FileViewerCoordinator` | 由"单 facade"改为"根列表 + 每根 facade"；标签带根标识 |

#### UI

左侧根列表（本期：项目根 / 协同目录；`Session` 根结构保留但本期不出现），每根一棵树；打开文件标签带根标识；失效根灰显、点击给出明确错误。

### 5.11 安全模型汇总（不可违反）

1. **cwd ≠ 授权**：本期 cwd 恒为工作区根；未来放开基准切换后，cwd 只能在授权根内取值，且 cwd 本身不产生任何读写权限。
2. **授权只来自三处**：项目根、用户显式登记的协同目录、审批通过的会话白名单。
3. **agent 不能自授权**：`add_collaboration_directory` 必须 `RequireInteraction` 且 `Scope != Once` 才落盘；无 `PermissionAuthorizer` 直接失败。
4. **工具类边界**：文件工具与文件工作台受授权根约束；`bash` / `git` 的 `workdir`/cwd 必须在授权根内（命令本身仍由 `shell.execute` 审批），否则拒绝——不能靠"命令级审批"替代路径边界。
5. **Deny 优先于门闸**：任何文件类 Deny 规则都在"工作区内放行"之前生效。
6. **规范化一致**：登记、审批卡片、落盘、边界判定使用同一 realpath 规范化结果。
7. **分享/导入/恢复**：`CollaborationDirectories` 必须清空或重新确认。

### 5.12 守卫与测试

**不变量测试**（`tests/spine/Seeing.Agent.Invariants.Tests/Invariants/PathResolutionInvariantTests.cs`）：

1. **扫描事实而非形状**：禁止业务代码调用 `Directory.SetCurrentDirectory`（唯一允许点：`WorkspaceProvider`，且必须受 `MutatesProcessCwd` 开关保护）；禁止 `Directory.GetCurrentDirectory()` 出现在字段初始化、构造函数赋值或业务路径解析。允许清单只剩启动期与执行世界兜底，且必须注释理由。
2. 禁止 `_workingDirectory` 标识符。
3. 禁止单参 `GetFullPath(`（阶段 2 起由编译期 `Obsolete(error: true)` 保证，测试作回归网）。
4. `IExecutionWorld.Cwd` 消费者**只能有 1 处**（启动期兜底）；`Bash` / 执行器 / ACP 均不得再消费。
5. 工作区响应组件断言：按 `(ComponentId, ChangeTypes.Contains(typeof(WorkspaceChange)))` 判定，或存在同 id 的 `IWorkspaceApplyParticipant`。目标：`agent-md`、`session`、`session.groups`、`markdown-commands`、`scheduler`（participant）、`tool-state`。
6. 禁止 `FileWorkspaceFactory` 出现 `int RefCount`（防回退到"读操作带引用计数"）。

**行为测试**：

- `ExecutionCwdResolverTests`：未决议抛；请求级/会话级任何偏离都被收敛为工作区根并置 `NeedsSessionWriteBack`；不读磁盘（虚构路径同样收敛）；`SessionDirectory.CreateAsync` 已无 `workingDirectory` 参数。
- `ProcessCwdSwitchTests`：`MutatesProcessCwd=false` 时切换工作区不改进程 CWD；`=true` 时才改。
- `ToolInvocationTests`：`ToolCallInvoker` 与 `AgentJob` 构造的 invocation 字段一致；`ToolContext` 投影完整；执行作用域经 `ExecutionScopeInitializer` 只填一次。
- 文件工具：`ResolveAllowed` 同时完成解析与授权；缺失 cwd / 越界明确失败。
- `BashToolTests` / `GitServiceTests`：workdir 越界拒绝；默认执行级 cwd。
- `AgentJobTests`：定时任务使用会话 cwd。
- `PermissionServiceTests`：三段式（否决 → 自动放行块 → 询问）；`RequireInteraction` 不进入自动放行块；文件类 Deny 先于门闸放行；相对 `Resource` 被绝对化。
- `SessionRootSetTests`：`IsAuthorized` 含临时白名单；`IsTrusted` **不含**白名单（保持 `WorkspacePathGateSessionRootTests:98-119` 的既有语义）；协同目录两个判定都放行。
- `AddCollaborationDirectoryToolTests`：无 authorizer 失败；`Once` 不落盘；`SessionDirectory` 落盘；经 `ICollaborationDirectories.AddAsync` 写入。
- `CollaborationDirectoriesTests`：Get/Add/Remove 是唯一写入口；上下文源、门闸、UI 取到的列表一致；`add_workspace_path` 仍走 grant store、行为不变。
- `FileWorkspaceFactoryTests`：`ListAsync` 多根、`GetAsync(rootId)` 精确取、`ResolveAsync(absolutePath)` 路由、`Release` 幂等全清（无引用计数）。
- `FileWorkspaceTests`：`GetResourceId` 越界抛；`GetAbsolutePath` 错 RootId 抛。
- `SessionDataTests`：`Clone` 深拷贝、子会话/fork/handoff 继承、旧 JSON 缺字段得空列表。
- `CollaborationDirectoriesContextSourceTests`：初始渲染逐字节稳定；**空列表返回 `Value` 并建立基线、渲染"（无）"**（不得返回 `Removed`）；增删/失效/恢复/备注变更各产生一条增量；无变化返回 `null`；`Unavailable` 只在钉住快照为 `null` 时出现。
- `ContextSourceLifetimeTests`：协同目录源是 scoped，单例注册会在 `ValidateScopes` 下失败（防回退）。
- `ExecutionContextScopeTests`：源读到执行级钉住的协同目录快照；工具/门闸走会话实时列表（两处口径不同，测试分别锁定）。
- `ScheduleManagerWorkspaceTests`：切换后加载新 `jobs.json`，旧任务不写回。
- `WorkspaceSwitcherTests`：抑制期信号补跑；无第三轮重复。
- `QuartzPersistenceTests`：`quartz.db` 固定在 `~/.seeing/scheduler/`（不受工作区影响）；切换工作区后按新 `jobs.json` 重建触发器；重启后触发器可反序列化。
- `AgentManagerResetTests`：`ResetProjectOverridesAsync` 从 `_configCache` 的 `Project:` 键反向清理，不新增记账字段；用户级同名覆盖不被误删。

### 5.13 不变量：什么不做

- 不自动把"会话 cwd"变成授权根。
- 不把 `glob`/`grep` 的默认搜索范围扩展到协同目录。
- 不在 share/import 时保留协同目录信任。
- 不为协同目录提供"只读"模式（本期统一可写）。

## 6. 迁移顺序（每一步都可独立编译、可审查、可回滚）

1. **IFileSystem 双参 + 单参转发**（只加方法 + `Obsolete` 警告，不改调用点；全仓可编译）。
2. **执行事实载体与 cwd**：`ToolInvocation`、`IToolManager.ExecuteAsync` 改签名、`ToolContext` 投影、`ExecutionScopeInitializer`、`ExecutionCwdResolver`、`ExecutionRunner` 唯一计算点、`MutatesProcessCwd` 开关与 `SetCurrentDirectory` 收敛。
3. **解析与授权合一**：`IWorkspacePathGate.ResolveAllowed`、`BuiltInToolBase` 删除快照、文件工具、glob/grep/bash/git/speech。
4. **权限链**：`PermissionRequest.WorkingDirectory`、`ToolManager` 预检绝对化、`PermissionService` 三段式（否决 → 自动放行块 → 询问）与 `BuildPolicyContext`。
5. **非执行链**：`AgentJob`（构造 `ToolInvocation`）、`SkillLoader`、MCP、ACP、`PermissionContext`、`FileWorkspace` 路径解析。
6. **协同目录数据层与端口**：`SessionData.CollaborationDirectories` + 6 处同步 + `ICollaborationDirectories`（写）+ `ISessionRootSet`（判定/根投影）+ `IContextExecutionScope` 快照（同步 `ContextExecutionScope` 与 5 个测试替身）+ `CollaborationDirectoriesContextSource`（scoped 注册，含增量叙述）；`ResolveWorkingDirectory` 改名 `ResolveProjectRoot`。
7. **授权与工具**：`WorkspacePathGate` 退化为 `ISessionRootSet` 适配、附件改 `IsTrusted`、`add_collaboration_directory` 经 `ICollaborationDirectories`。
8. **多根文件工作台**：`FileWorkspaceRoot` 由 facade 拥有、`IFileWorkspaceFactory` 的 List/Get/Resolve/Release（**无引用计数**）、监听、桥接、上传、UI。
9. **工作区派生状态与切换修复**：`SchedulerWorkspaceParticipant` + `ScheduleManager.ResetForWorkspace`、Quartz 库改用户级固定路径、`ToolManager` tool-state、`AgentManager` 从零重建、WeCom/QQ；抑制记账、`OldWorkspace`。
10. **收口**：单参 `GetFullPath` 改 `error: true`、清理剩余调用点、不变量测试（含"禁止 `RefCount`""`SetCurrentDirectory` 唯一允许点"）、`ExecutionWorldContractsTests` 改断言双参。

## 7. 风险与回退

| 风险 | 缓解 |
|---|---|
| Quartz 改造丢 serializer/DI JobFactory | 照测试夹具 `SchedulerIntegrationTestsFactory` 的做法显式挂 JobFactory；加持久化重启测试；失败则回退到"禁用 sqlite 持久化"路径 |
| `ScheduleManager` 与参与者合并后顺序出错 | 只保留参与者一条路径，`ResetForWorkspace` 幂等；测试覆盖"停 → 清 → 重建 → 起" |
| 多根缓存泄漏 | **不使用引用计数**：`Entry` 只持有根字典，`Release` 幂等全清；watcher 生命周期由 `FileWatcherPool` 的根级引用负责 |
| `FileResourceId` 路由错误导致写错根 | `ResolveAsync(rootId)` + `GetAbsolutePath` 校验 RootId + `PathBoundary`，并加测试 |
| 附件契约改动影响既有安全测试 | 明确新语义并同步改 `WorkspacePathGateSessionRootTests`，不保留双轨 |
| 抑制期补跑引入重复重载 | 补跑走同一去抖入口；handler 必须幂等；`AgentManager`/`Session` 已改为从零重建 |
| 旧会话 JSON 缺 `CollaborationDirectories` | 属性初始化器给空列表；`FileSessionStore` 摘要分支同步 |
| `RequireInteraction` 改动影响所有审批 | 只对显式设置为 true 的请求生效；既有请求默认 false |

## 8. 留给实现者的判断

- 抑制器抽新接口还是给 `IWorkspaceReloadInvoker` 加方法：按最小改动，只要覆盖 `SetWorkspaceRoot`。
- `AgentManager._appliedMdNames` 的粒度：按 level 记录即可；备份消耗语义以"不再被任何 level 覆盖"为准。
- `ISessionFileRootResolver` 的最长前缀匹配实现：按根长度降序 + `PathBoundary`，避免 `D:\a` 命中 `D:\ab`。
- UI 多根布局（左侧列表 vs 分栏）：默认左侧列表；不影响契约。

## 9. 验收标准

1. 新工作区切换后，`write`/`edit`/`read`/`delete`/`glob`/`grep`/`bash`/`git` 的相对路径全部落在**当次执行的 cwd**。
2. `src/` 中不存在"构造期捕获 cwd"或"单参 `GetFullPath` 业务调用"；`Directory.SetCurrentDirectory` 只出现在 `WorkspaceProvider` 且受 `MutatesProcessCwd` 保护。
3. 切换工作区后：`jobs.json` 重新加载、`tool-state.json` 生效、旧工作区项目 agent 消失；`quartz.db` 固定在用户级路径、按新 `jobs.json` 重建触发器。
4. 显式切换不再触发第三轮自动重载；抑制期内的真实信号不丢。
5. 未决议工作区不执行任何会话（显式失败）。
6. 任何偏离工作区根的请求级/会话级 cwd 都被收敛并回写；未决议工作区执行显式失败。
7. 未登记的目录不会自动成为合法根；首次写入被拒或走审批。本期 cwd 恒为工作区根。
8. 协同工作：协同目录 B 用绝对路径或命令级 `workdir` 访问；工作区根 A 内文件仍可读写；指令扫描仍以 A 为准（本期不做基准切换）。
9. 审批卡片、授权记忆、工具实际写入三处使用同一绝对路径与同一 realpath 规范化结果。
10. 外部目录首次写入触发审批；批准后本会话免问；无呈现端宿主明确拒绝。
11. 协同目录随会话持久化；子会话、fork、handoff 继承；`Clone` 深拷贝；旧 JSON 兼容。
12. 协同目录自动成为授权根；工具可直接读写；文件工作台显示主根 + 全部协同根，可读写；失效根灰显且不回落主根。
13. agent 通过工具添加协同目录必须审批，`Once` 不持久化；UI 添加即时生效。
14. 切换工作区后持久化协同目录仍在；share/import/restore 不携带信任。
15. `explore`/`plan` 等 Deny 文件的子代理在协同目录内仍被 Deny 拦住。
16. 多根工作台：`GetAsync(rootId)` 精确取根、`ResolveAsync(absolutePath)` 正确路由；`GetAbsolutePath` 错 RootId 抛错；跨根写不可能发生；`Release` 幂等全清且无引用计数。
17. `## Environment` 每请求显示当前工作目录；协同目录源初始渲染列表（空列表渲染"（无）"）；增删/失效/恢复各产生一条 `context-update`，在**下一次执行**的装配中可见（同一次执行内不漂移，工具侧实时可用）。
18. 本期相对路径基准恒为工作区根；请求级/会话级的偏离值被收敛并回写，不产生第二套基准。
19. WebUI/Desktop 切换工作区不改进程 CWD；CLI/TUI 单工作区宿主才允许改。
20. 授权判定只有 `ISessionRootSet` 一个权威：`IsAuthorized` 含白名单、`IsTrusted` 不含；附件准入不因协同目录而获得白名单豁免。
21. 执行事实只经 `ToolInvocation` 传递；`ToolCallInvoker` 与 `AgentJob` 是仅有的两个构造点，执行作用域经 `ExecutionScopeInitializer` 一次填充。
22. 协同目录的写只经 `ICollaborationDirectories`；根投影只经 `ISessionRootSet.Roots`；上下文源、门闸、UI 看到同一份列表。
