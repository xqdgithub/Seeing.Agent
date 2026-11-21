# 通用后台活动体系设计

- 日期：2026-11-20
- 状态：待批准（已过八轮审查）
- 范围：`Seeing.Agent.Abstractions` / `Seeing.Agent.Core` / 新增 `Seeing.Agent.Tools.Background` / `Seeing.Agent.Tools.Shell` / WebUI / TUI / Gateway 投影
- 关键词：后台活动、作业句柄、级联取消、完成投递、outbox

---

## 1. 为什么做

框架里已存在 **8 套互不相干的后台运行机制**，各自维护状态、取消路径与 UI 呈现：

| # | 机制 | 状态真相 | 重启后 |
|---|---|---|---|
| 1 | `TaskTool background=true` | 子会话 + `ISessionRuntime` 内存 | 完成通知**丢失** |
| 2 | `AcpTool` 的 `ConcurrentDictionary` | 进程内字典 | 丢失 |
| 3 | `bash` 长任务 | 调用方 `await` 到进程退出 | 进程被杀 |
| 4 | Scheduler / Heartbeat | `jobs.json` + `jobs_history/` | 历史保留，执行不留 |
| 5 | Reflection | `.seeing/reflection/` + 内存队列 | journal 保留 |
| 6 | Memory | 内存 + `~/.seeing/memory` | 重扫 |
| 7 | 浏览器宿主 | 独立进程 + lock/发现文件 | 自动回收 |
| 8 | 杂项 `Task.Run` | 无 | 丢失 |

具体损失：

- **`bash` 没有后台能力**：一次调用必须挂到进程退出，默认 120s 硬超时，中途 kill 整棵进程树。
- **子 Agent 后台的完成回投是 `Task.Run` fire-and-forget**（`TaskTool.cs:194`）；`WaitForIdleAsync` 的 10 分钟安全上限会让超长子任务读到错误终态。
- **没有统一 id 空间**：`task_id` 就是 `sessionId`，ACP 自造一套，`bash` 什么都没有。
- **UI 靠特判**：`TaskCardAggregator` 识别 `toolName == "task"` 才聚合。
- **`IsBackground` 是死字段**：3 个类同名字段全仓 0 处读取。一期删除，由活动 `Origin` 取代。

`AGENTS.md:229` 描述的 `BackgroundTaskManager` 已在 `fdd04375` 删除，属文档漂移，本设计不复活它。

---

## 2. 目标 / 非目标 / 反目标

### 2.1 目标

1. 一套统一的后台活动**身份、状态机、查询、等待、取消、事件与治理**。
2. `bash` 支持 `background=true`，启动即返回句柄；**默认完成后唤醒**发起会话。
3. 中断语义树形级联，且**精确到本轮执行**（`ExecutionId` 贯通，不做恒 null 降级）。
4. 现有 8 套机制可逐步收敛到同一活动表，不改各自执行机制。
5. 新 `kind` 可由编译期能力包零框架改动接入。

### 2.2 非目标（一期）

- 运行时动态加载第三方 DLL 插件。
- 脱离宿主进程的后台存活（关闭即中断）。
- 活动跨重启恢复续跑。
- 独立活动列表面板（一期只做聊天区内联卡片）。
- **浏览器宿主接入**（先不动，见 §18）。

### 2.3 反目标

- 活动类型靠继承表达，逼框架或宿主 UI `switch` 类型。
- `Kind` 变成框架行为开关。
- 把活动状态混进 `ToolCallStatus`。
- 用 `Attributes` 做框架决策。
- 后台活动绕过 `tool.execute` 权限门，或依赖"用户恰好开着窗口"。
- 把输出全文塞进唤醒消息或上下文。
- 在循环里发起存储查询。

---

## 3. 核心判断

### 3.1 四种"后台"是正交的

| 语义 | 字段 | 代表 |
|---|---|---|
| 后台于本轮 | `Origin = ToolBackground` | `bash background` |
| 后台于当前会话 | `OwnerSessionId` + `TargetSessionId` | `TaskTool background` |
| 后台于用户注意力 | `Visibility` | ACP、Scheduler 旁路 |
| 后台于宿主进程 | `Lifetime` | 浏览器 `--browser-host`（三期） |

不合并成一个 bool。

### 3.2 活动是控制面与观测面，不是执行面

统一：身份、状态机、取消树、完成投递、观测、治理、授权传递。

执行仍由各自机制完成：`process` → 子进程；`agent-session` → 子会话；`scheduled-run` → Quartz；`external-host` → 独立进程。

> **硬规则：不进活动表 = 不允许后台运行。** 由契约强制，不由内核接管执行。

### 3.3 数据用组合，行为用策略

活动记录要落盘、跨进程投递、跨宿主回放，是纯数据。继承的硬伤：

1. `System.Text.Json` 派生类型注册是编译期的，插件子类型无法被框架序列化器识别。
2. Blazor 渲染必须 `case ProcessActivity p:`，插件加类型必须改宿主。
3. 有子类型后框架必然长出 `is` 判断。
4. 多宿主 + 独立进程 + 持久化 = 必须扁平、可向后兼容的 schema。

因此：**扁平记录 + 能力位 + 开放属性**，行为由 `IBackgroundActivityLauncher` 按 `Kind` 路由。

### 3.4 活动归会话，执行用于精确中断

- 归属是 `OwnerSessionId` + `OwnerExecutionId`。
- owner 执行**正常完成**时活动继续存活（靠会话归属）。
- 执行被中断时按 `OwnerExecutionId` 精确取消；会话被停止时按 `OwnerSessionId` 兜底。

---

## 4. 方案比较

**方案 A（否决）**：统一执行内核。Quartz 自管并发与持久化、浏览器宿主是独立 OS 进程、子会话必须走 `ISessionRuntime` 单飞循环——强行接管的结果是在内核里写三套 `if (kind == ...)`。

**方案 B（采用）**：统一身份与观测层。执行由各自机制完成，但必须先注册活动、挂取消树、经统一出口投递完成通知。

**方案 C（否决）**：全部收敛为子会话。为跑一个进程起一个 LLM 会话，成本与语义都错位。

---

## 5. 分层与落点

| 层 | 内容 |
|---|---|
| 契约 `src/abstractions/Seeing.Agent.Abstractions/Background/` | 全部记录/枚举/接口（见 §6、§7） |
| 实现 `src/spine/Seeing.Agent.Core/Background/` | `BackgroundActivityManager`、`BackgroundActivityControl`、`BackgroundActivityOutbox`、`BackgroundActivityNotificationDispatcher`、`BackgroundActivityOutputStore` |
| 事件 | `ActivityEventTypes` + `BackgroundActivityEvent` |
| 工具 | `bash` 增参数；新增能力包 `src/capabilities/Seeing.Agent.Tools.Background/`（模块 id `background`），5 个 `activity_*` 工具 |
| UI | `EventStreamHandler`、`ToolCallViewModel`、TUI 投影、Gateway 投影 |

依赖方向：工具能力包只依赖 Abstractions；实现由 `AddSeeingCore` 注册。**Shell 包不得引用 Core。**

### 5.1 命名

- `IBackgroundActivityManager`：会话面，含启动与查询，按 AGENTS 定义属 **Manager**。
- `IBackgroundActivityControl`：系统面（系统级启动/登记/跨会话取消），只注册给 Core/宿主与模块激活器，能力包工具不得注入。
- `ISessionActivityTracker`（`src/primitives/Seeing.Session/Core/ISessionActivityTracker.cs`）表示"会话最后一次活动的时刻"，与本设计无关。

---

## 6. 数据模型

```csharp
public sealed record BackgroundActivitySnapshot
{
    // Identity（不可变）
    public required string Id { get; init; }              // "act_" 前缀
    public required string Kind { get; init; }            // 开放字符串，带命名空间前缀
    public required string Title { get; init; }
    public string? OriginToolCallId { get; init; }
    public BackgroundActivityOrigin Origin { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string HostInstanceId { get; init; }  // pid + 进程启动时间
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    // Ownership（不可变）
    public required string OwnerSessionId { get; init; }
    public required string OwnerExecutionId { get; init; } // 本轮执行 id；Scheduler 旁路为本次 run id
    public string? ParentActivityId { get; init; }
    public string? TargetSessionId { get; init; }

    // State（可变，Seq 单调）
    public BackgroundActivityStatus Status { get; init; }
    public long Seq { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ResultObservedAt { get; init; }
    public BackgroundActivityProgress? Progress { get; init; }
    public BackgroundActivityCancelReason? CancelReason { get; init; }

    // Capabilities（唯一可驱动框架行为）
    public BackgroundActivityCapabilities Capabilities { get; init; }

    // Result（仅终态，不内联内容）
    public BackgroundActivityResult? Result { get; init; }

    // Policy
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; }
    public TimeSpan? Timeout { get; init; }
    public long OutputLimitBytes { get; init; }
    public BackgroundActivityVisibility Visibility { get; init; }
    public BackgroundActivityLifetime Lifetime { get; init; }

    // 展示与诊断；框架决策不得读取
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>();
}
```

```csharp
public enum BackgroundActivityStatus { Pending, Running, Succeeded, Failed, Cancelled, Interrupted }

public enum BackgroundActivityCancelReason
{
    User, CascadeFromParent, SessionStopped, HostShutdown,
    WorkspaceSwitched, ModuleDeactivated, Timeout, Superseded
}

public enum BackgroundActivityNotifyPolicy { OnComplete, None }
public enum BackgroundActivityVisibility { Session, Project, System }
public enum BackgroundActivityOrigin { ToolBackground, User, Scheduler, Module }
public enum BackgroundActivityLifetime { Host, Detached }
public enum BackgroundActivityResultKind { None, Text, Artifact, Session, ProcessExit }
public enum BackgroundActivityDedupeStrategy { Reject, Supersede }
public enum BackgroundActivityWaitOutcome { Completed, TimedOut, CallerCancelled }

[Flags]
public enum BackgroundActivityCapabilities
{
    None              = 0,
    CanCancel         = 1 << 0,
    CanWait           = 1 << 1,
    CanStreamOutput   = 1 << 2,
    CanAttach         = 1 << 3,
    SignalsCompletion = 1 << 4
}

public sealed record BackgroundActivityProgress(
    double? Fraction, string? UnitsName, long? UnitsDone, long? UnitsTotal, string? Message);

public sealed record BackgroundActivityResult(
    BackgroundActivityResultKind Kind,
    string? ArtifactPath, string? ResultSessionId, int? ExitCode,
    string? ErrorCode, string? ErrorMessage);
```

### 6.1 kind 特有信息

`Pid`、`ExitCode`、`Endpoint`、`AgentId` 走 `Attributes`（展示/诊断）与 `Result`（强类型结果引用）。**框架任何分支不得读取 `Attributes`。**

**入参与诊断分离**：launcher 需要的命令、参数、环境等**入参**走 `StartRequest.Payload`（`JsonElement`，由发起方与 launcher 约定，框架不解释）；`Attributes` 只放事后可展示的诊断信息。`Payload` 一律经 `JsonSerializer.SerializeToElement` 构造、自持 `JsonDocument`（同 `HostRequest.cs:18`）。

插件在自己的程序集里做类型安全投影：

```csharp
public readonly record struct ProcessActivityInfo(int Pid, int? ExitCode, string WorkingDirectory)
{
    public static ProcessActivityInfo From(BackgroundActivitySnapshot a) => ...;
}
```

建议保留键：`pid` / `exitCode` / `workdir` / `endpoint`。

### 6.2 `BackgroundActivityId` 全链路（强类型，不走 Metadata）

活动 id 是执行级语义，必须能被 UI、回放、TUI、Gateway 在编译期识别。**禁止**塞进 `Metadata`。

需要加字段并逐段投影的位置（一处不能漏，否则刷新/fork/远程端丢关联）：

| 段 | 位置 | 改动 |
|---|---|---|
| 工具结果 | `Tools/ToolResult.cs` | 增 `string? BackgroundActivityId` |
| 工具事件 | `Events/MessageEventTypes.cs`（`ToolCallEvent`） | 增字段 |
| 事件构造 | `ToolCallInvoker.cs` 的 4 处出口 + `BuildTerminalEvent`（`:339`）、`AgentExecutor.cs:949` | 从 `ToolResult` 透传 |
| 会话投影 | `ChatEventTracker.cs:126-174` | 建对象、抄字段、落盘条件加 `backgroundActivityFilled` |
| 会话模型 | `SessionMessage.cs`（`SessionToolCall`） | 增字段 + `Clone()` 拷贝（fork/share 依赖） |
| WebUI | `Components/.../ToolCallViewModel.cs:150-171` | 白名单加属性 |
| TUI | `TuiEventInterpreter.ApplyToolCall`、`TuiViewState.BuildToolBlock`、`TuiToolState` | 加字段 |
| Gateway | `GatewayEventData.cs:6`、`GatewayEventMapper.MapToolCall:185-203` | 白名单加字段 |

验收要求：fork 一条含后台活动的会话后，子会话仍能看到关联。

---

## 7. 契约

### 7.1 会话面（能力包工具只注入这个）

```csharp
public interface IBackgroundActivityManager
{
    /// <summary>
    /// requesterSessionId 即归属会话。要求 request.OwnerSessionId == requesterSessionId，
    /// 不相等抛 UnauthorizedAccessException——工具无法冒充他人的归属。
    /// </summary>
    Task<BackgroundActivityHandle> StartAsync(
        BackgroundActivityStartRequest request,
        string requesterSessionId,
        CancellationToken ct = default);

    BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId);
    IReadOnlyList<BackgroundActivitySnapshot> List(BackgroundActivityQuery query, string requesterSessionId);

    Task<bool> CancelAsync(
        string activityId, BackgroundActivityCancelReason reason,
        string requesterSessionId, CancellationToken ct = default);

    Task<BackgroundActivityWaitResult> WaitAsync(
        string activityId, TimeSpan timeout,
        string requesterSessionId, CancellationToken ct = default);

    Task<BackgroundActivityOutputPage> ReadOutputAsync(
        string activityId, long offset, int maxBytes,
        string requesterSessionId, CancellationToken ct = default);

    void MarkResultObserved(string activityId, string requesterSessionId);
}
```

归属语义（逐方法定死，不留解释空间）：

| 方法 | 越权时 |
|---|---|
| `StartAsync` | `OwnerSessionId != requesterSessionId` → 抛 `UnauthorizedAccessException` |
| `Get` | 返回 `null`；仅当 `Visibility ∈ {Project, System}` 时放行 |
| `List` | **只返回 `OwnerSessionId == requesterSessionId` 的活动**（不返回他人活动，也不报错） |
| `CancelAsync` | 返回 `false` |
| `WaitAsync` | 返回 `TimedOut` |
| `ReadOutputAsync` | 返回空页（`EndOfStream = true`） |
| `MarkResultObserved` | 忽略，不改变 `ResultObservedAt` |

**`List` 是 UI 首屏重建（§17）与事件回放（§12.1）的唯一数据来源**，因此它不提供跨会话视图；需要跨会话的只有系统面。

### 7.2 系统面（只注册给 Core/宿主，能力包工具不得注入）

系统面承担一切"可能作用于他人会话"或"改变全局登记"的操作：

```csharp
public interface IBackgroundActivityControl
{
    // 系统级启动/登记：可指定任意 OwnerSessionId
    Task<BackgroundActivityHandle> StartAsSystemAsync(
        BackgroundActivityStartRequest request, CancellationToken ct = default);
    Task<BackgroundActivityHandle> TrackAsync(
        BackgroundActivityTrackRequest request, CancellationToken ct = default);

    void RegisterLauncher(string moduleId, IBackgroundActivityLauncher launcher);
    Task<int> UnregisterLauncherAsync(string kind, CancellationToken ct = default);

    Task<int> CancelBySessionAsync(string sessionId, BackgroundActivityCancelReason reason, CancellationToken ct = default);
    Task<int> CancelByExecutionAsync(string executionId, BackgroundActivityCancelReason reason, CancellationToken ct = default);
    Task<int> CancelAllAsync(BackgroundActivityCancelReason reason, CancellationToken ct = default);
    Task<int> ReconcileOrphanedActivitiesAsync(CancellationToken ct = default);
}
```

一期注入 Control 的只有：`SessionRuntime`、dispatcher、workspace participant、模块激活器、`ProcessBackgroundActivityLauncher` 的注册方（Shell 模块）。**`bash` 与 5 个 `activity_*` 工具只注入 Manager。**

### 7.3 记录与数据

```csharp
public sealed record BackgroundActivityHandle(
    string ActivityId,
    IBackgroundActivityReporter Reporter,
    CancellationToken CancellationToken);

public sealed record BackgroundActivityLaunchContext(
    BackgroundActivityHandle Handle,
    BackgroundActivityStartRequest Request,
    string? WorkingDirectory);

public sealed record BackgroundActivityStartRequest
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string OwnerSessionId { get; init; }
    public required string OwnerExecutionId { get; init; }
    public string? OriginToolCallId { get; init; }
    public string? ParentActivityId { get; init; }
    public string? TargetSessionId { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>kind 专属入参；框架不解释。</summary>
    public JsonElement? Payload { get; init; }

    public BackgroundActivityOrigin Origin { get; init; } = BackgroundActivityOrigin.ToolBackground;
    public BackgroundActivityVisibility Visibility { get; init; } = BackgroundActivityVisibility.Session;
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; } = BackgroundActivityNotifyPolicy.OnComplete;
    public TimeSpan? Timeout { get; init; }
    public long? OutputLimitBytes { get; init; }
    public BackgroundActivityLifetime Lifetime { get; init; } = BackgroundActivityLifetime.Host;
    public string? DedupeKey { get; init; }
    public BackgroundActivityDedupeStrategy DedupeStrategy { get; init; } = BackgroundActivityDedupeStrategy.Reject;
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

public sealed record BackgroundActivityTrackRequest
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string OwnerSessionId { get; init; }
    public required string OwnerExecutionId { get; init; }
    public string? ParentActivityId { get; init; }
    public string? TargetSessionId { get; init; }
    public BackgroundActivityOrigin Origin { get; init; } = BackgroundActivityOrigin.Module;
    public BackgroundActivityCapabilities Capabilities { get; init; }
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; } = BackgroundActivityNotifyPolicy.None;

    /// <summary>二选一：宿主提供完成信号，或宿主自行通过 handle.Reporter 上报终态。</summary>
    public Task? Completion { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

public sealed record BackgroundActivityWaitResult(
    BackgroundActivityWaitOutcome Outcome,
    BackgroundActivitySnapshot? Snapshot);

public sealed record BackgroundActivityOutputPage(
    string Content, long NextOffset, bool EndOfStream, bool Truncated);

public sealed record BackgroundActivityQuery(
    BackgroundActivityStatus? Status = null,
    string? Kind = null,
    bool IncludeCompleted = true);
```

```csharp
public interface IBackgroundActivityLauncher
{
    string Kind { get; }
    BackgroundActivityCapabilities Capabilities { get; }

    Task<BackgroundActivityLaunchResult> LaunchAsync(
        BackgroundActivityLaunchContext ctx, CancellationToken ct);

    Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason);
}

public sealed record BackgroundActivityLaunchResult(Task Execution);
```

```csharp
public interface IBackgroundActivityReporter
{
    void ReportStarted();
    void ReportProgress(BackgroundActivityProgress progress);
    void Heartbeat();
    Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default);
    Task CompleteAsync(BackgroundActivityResult result, CancellationToken ct = default);
    Task FailAsync(string errorCode, string errorMessage, CancellationToken ct = default);
}
```

```csharp
public interface IBackgroundActivityOutbox
{
    int PendingCount { get; }
    Task<int> PumpOnceAsync(CancellationToken ct = default);   // 内部互斥闸
}

public interface IBackgroundActivityNotifier
{
    Task DeliverAsync(InboxMessage message, CancellationToken ct = default);
}
```

### 7.4 所有权与线程

- **Handle 唯一权威**：只能由 `StartAsync` / `StartAsSystemAsync` / `TrackAsync` 内部创建，经 `LaunchContext` 下发；`LaunchAsync` **必须原样使用 `ctx.Handle`**，不得自建 CTS 或 reporter。
- **CTS 由 manager 持有**：链接超时（`TimeProvider`）与宿主停止。
- **`Running` 由 manager 置**：`LaunchAsync` 成功后 CAS；`ReportStarted` 仅用于延后声明"真正开始"。
- **终态由 manager CAS 裁决**：取消时先写 `Cancelled` 再通知 launcher；后续 `Complete`/`Fail` 丢弃。进入终态后 reporter 为 no-op。
- **`CompleteAsync`/`FailAsync` 异步落盘**：先原子写终结记录、写 outbox，再返回（`await` 完成即已落盘）；取消路径同理，先落 `Cancelled` 再通知 launcher。
- **launcher 不得引用 `ToolContext.CancellationToken` / `ToolContext.EventSink`**（`BashTool.cs:129-131` 的链接方式不能照搬）。
- manager 观察 `LaunchResult.Execution`：正常结束未上报终态 → `Failed("no_terminal_state")`；抛异常 → `Failed`。

---

## 8. 状态机

```
Pending ──started──> Running ──ok────> Succeeded
   │                    ├────────────> Failed
   │                    ├────────────> Cancelled
   │                    └────────────> Interrupted
   ├──cancel──────────> Cancelled
   └──launch failure──> Failed
```

- 终态不可逆，只能进入一次。
- **`Running` 迁移条件：仅当 `Status == Pending`**；若 launcher 已先上报 `Fail`，随后的 Running 迁移必须丢弃。
- `LaunchAsync` 抛异常、或 `Execution` 立即完成但未上报终态 → `Pending → Failed`。
- 每次变更 `Seq++`；订阅方丢弃更旧的快照。
- `Heartbeat` 至少每 30s；超过 3 个周期未更新，UI 标"疑似卡死"（不自动杀）。

---

## 9. 身份与取消

### 9.1 归属

```
OwnerSessionId     必有
OwnerExecutionId   必有（工具发起为本轮执行；Scheduler 旁路为本次 run id）
ParentActivityId   可空
TargetSessionId    可空（kind=agent-session）
OriginToolCallId   可空
```

主键是 `ActivityId`，不是 `toolCallId`。

### 9.2 取消矩阵

| 触发 | 影响范围 |
|---|---|
| owner 执行**正常完成** | 活动继续存活 |
| `InterruptAsync(session, ct, cancelBackgroundActivities: false)` | **不碰**任何后台活动 |
| `InterruptAsync(session, ct, cancelBackgroundActivities: true)` | `CancelByExecutionAsync(本轮 ExecutionId)` |
| `StopAsync(session)` | 对 `targets`（自身 + 整棵子会话子树）逐个 `CancelBySessionAsync` |
| 显式"停止后台活动" | `CancelBySessionAsync`，`CancelReason=User` |
| 会话删除 | 同 `StopAsync`（`SessionDirectory.DeleteAsync` 已先调 `StopAsync`） |
| 工作区切换 | 全部，`WorkspaceSwitched` |
| 模块停用 | 该 launcher 名下全部，`ModuleDeactivated` |
| 宿主关闭 | 全部，`HostShutdown`，落 `Interrupted` |
| `CancelAsync(activityId)` | 该活动 + 子树，`User` |
| 活动自身超时 | 只取消自己，`Timeout` |

### 9.3 `InterruptAsync` 签名变更与调用点语义

```csharp
Task<bool> InterruptAsync(
    string sessionId,
    CancellationToken ct = default,
    bool cancelBackgroundActivities = false);
```

`InterruptAsync` 被多个入口复用，**每个入口的语义必须显式定死**：

| 入口 | 传值 | 理由 |
|---|---|---|
| `SessionWindow.razor:2050`（中断并发送） | `false` | 用户只想插队发一句话 |
| `SessionRuntime.InterruptThroughAsync:1283`（发送到此） | `false` | 同上 |
| `InProcessSessionClient.CancelAsync:79`（IM 取消） | `true` | 用户点取消就是要停 |
| `GatewaySessionService.ResetAsync:52` | `true` | 清空会话后活动不能再存活 |
| `GatewayOrchestratorV2.cs:107,126`（IM 取消） | `true` | 与上一致 |
| `BrowserStreamEndpoints.cs:679`（浏览器接管） | `false` | 抢占的是浏览器，不是后台命令 |

必须同步修改的测试/桩：

| 类型 | 位置 |
|---|---|
| 手写实现 6 处 | `TaskToolTests.cs:360,511`、`ChannelDeliveryScheduleDispatcherTests.cs:300`、`SessionToolTestHarness.cs:202`、`SessionInboxToolsTests.cs:262`、`CommsIntegrationFixture.cs:85` |
| Moq 表达式树 3 处 | `GatewaySessionServiceTests.cs:80`、`InProcessSessionClientTests.cs:108,115`（显式传全三参） |
| 反射契约测试 1 处 | `ExecutionAbstractionsContractsTests.cs:21`（参数类型补 `typeof(bool)`） |

`StopAsync` 当前**没有** manager 调用，需新增，并按 `targets` 逐个取消。

### 9.4 实现约束

- 不要把活动 token 直接 link 到执行 token（执行正常完成会 cancel）。
- 取消由显式动作驱动：`StopAsync` / 会话删除 / 工作区切换 / 模块停用 / 宿主停止 / 工具调用。
- 执行因异常失败（`Failed`）**不**取消活动。

### 9.5 挂接点

1. **会话删除**：唯一入口 `SessionDirectory.DeleteAsync`（`SessionDirectory.cs:113-121`），已先调 `StopAsync`；随 `StopAsync` 生效。
2. **工作区切换**：manager 实现 `IWorkspaceApplyParticipant`，`OnApplyingAsync` 里 `CancelAll(WorkspaceSwitched)`；同时把旧根中本实例的 outbox pending 标 `dropped`（`LastError="workspace_switched"`）。已知取舍：参与者在 root 变更前执行，回滚后活动不复活。
3. **模块停用**：`UnregisterLauncherAsync(kind)` 必须先取消该 kind 名下全部运行中活动并等终态，在 `DeactivateAsync` 内 `await`。测试只走"无在途执行"的同步默认路径；有在途时 `ForceCancelInFlight` 会抛 `InvalidOperationException`，不得作为测试路径。
4. **`background` 模块禁用**：bash 用 `IModuleCatalog.IsEnabled("background")` 判定；禁用时 `background=true` 直接失败。
5. **`SessionRuntime` 取 manager** 用 `GetRequiredService`；裸 `ServiceCollection` 测试补注册 no-op 假实现，不得降级为 `GetService`。

### 9.6 ExecutionId 贯通（一期前置重构）

现状：`executionId` 只在宿主编排层产生（`SessionRuntime.cs:443` → `ExecutionRecord`），`AgentContext` / `ToolInvocation` / `ToolContext` 都没有。

接缝现成：`ToolInvocation` 注释明确要求"新增执行级事实应加到这个记录里"，全仓只有 `ToolCallInvoker.cs:106` 一处构造。

| # | 改动 | 位置 |
|---|---|---|
| 1 | `AgentContext` 增 `string? ExecutionId` | `Agents/AgentContext.cs` |
| 2 | `BuildAgentContext` 填 `record.ExecutionId`（作为方法参数传入） | `ExecutionRunner.cs:1008` |
| 3 | `AgentJob` 生成 run-scoped id 填入 | `AgentJob.cs:378` |
| 4 | `AcpTool` 填 `ParentContext.ExecutionId`（第三个构造点，易漏） | `AcpTool.cs:218` |
| 5 | `ToolCallInvoker` 传 `context.ExecutionId` | `ToolCallInvoker.cs:106` |
| 6 | `ToolInvocation` 增 `string? ExecutionId` | `Tools/ToolInvocation.cs` |
| 7 | `ToolManager` 映射进 `ToolContext.ExecutionId` | `ToolManager.cs:757-770` |

同时修正 `ToolInvocation.cs:12` 的注释（现写"两处构造"，实际生产代码只有一处）。

**不给 `IContextExecutionScope` 加 `ExecutionId`**：工具读不到当前 scope（`ToolContext.Services` 是根容器），加了只会多一个真相；`SchedulerContextAssembly` 是绕过初始化器的第二填充点。

**`ToolContext.MessageId` 不在本期**：`AgentContext.MessageId` 与 `ExecutionRecord.LastMessageId` 全仓从未赋值，没有语义来源；硬填是错的。记入 §21 架构债。

---

## 10. 完成投递

### 10.1 设计

```
活动进入终态（manager CAS）
  ├─ 1. 原子写终结记录（权威）
  ├─ 2. NotifyPolicy=OnComplete 且 SignalsCompletion → 写 outbox（pending）
  └─ 3. 发布 activity.completed / failed / cancelled 事件（实时 UI，非权威）

BackgroundActivityNotificationDispatcher
  └─ 循环调用 outbox.PumpOnceAsync()：
       ├─ 只取 hostInstanceId == 本实例 的活动，按 OwnerSessionId 分组
       ├─ 合并窗口内合成一条 digest，**先落盘 digestId**
       ├─ DeliverAsync(IdempotencyKey = $"activity-digest:{digestId}")
       └─ 成功 → delivered；目标会话已不存在 → dropped（须批量查会话目录）
```

### 10.2 幂等

- 批次先固化为 `digestId`（落盘），重试只重放同一 `digestId`。
- dispatcher 对同一会话串行投递。
- `SessionRuntime.cs:219-226` 的 `inbox.idem:` 预检保留为第二道防线；写入点 `:263-266` 无清理逻辑，必须加 TTL 或数量上限。

### 10.3 合并与降噪

- 合并窗口默认 300ms（`TimeProvider` 注入）。
- 唤醒消息只给摘要 + 句柄：

```
【后台活动完成】
- build（kind=process，exit 0，输出 12.3KB）
- test （kind=process，exit 1，输出 4.1KB，已截断）
用 `activity_output` 查看详情；`activity_status` 查看状态。
```

- 形态：`Wake` + `System` + `SourceTag="background.activity"` + `InboxSender { Kind = System, Id = "background" }`。

### 10.4 可靠性范围

- **进程内 at-least-once**：先落盘再投递。
- **跨重启不补发**：启动时只把已死实例的 pending 标 `dropped(HostShutdown)`；存活实例的不触碰。
- 真正修复：`TaskTool` 的 10 分钟安全上限、空 catch、宿主关闭窗口内的丢消息。
- 不承诺"崩溃后续投"。

### 10.5 唤醒后的出站（IM/渠道）

- 无 UI 会话**照常唤醒**。
- 不依赖 Gateway 自动回帖轮次门控（`Channels/GatewayAssistantReplyCollector.cs:19`）；唤醒轮由 agent 显式调用 `send_channel_message` 出站。

### 10.6 已观察结果不回投

- `activity_wait` / `activity_status` 读到终态时调用 `MarkResultObserved`。
- dispatcher 发现 `ResultObservedAt != null` 直接标 `delivered`。

### 10.7 去重

- `DedupeKey` 由**调用方**计算，manager 只比字符串，绝不读 `Payload`。
- `DedupeStrategy`：`Reject`（默认）或 `Supersede`。

---

## 11. 状态落盘与输出存储

### 11.1 目录布局

```
{活动根}/
  {activityId}.json          # 快照：运行中最小记录，终态完整记录
  {activityId}.output.log    # 输出，追加写
  outbox/
    {digestId}.json          # 待投递批次（含 hostInstanceId）
```

活动根：工作区已解析 → `{workspace}/.seeing/background-activities/`；未解析 → `~/.seeing/background-activities/`。边界判定走 `PathBoundary`。

```csharp
public enum BackgroundActivityOutboxStatus { Pending, Delivered, Dropped }

public sealed record BackgroundActivityNotification
{
    public required string DigestId { get; init; }
    public required string HostInstanceId { get; init; }
    public required string OwnerSessionId { get; init; }
    public required IReadOnlyList<string> ActivityIds { get; init; }
    public BackgroundActivityOutboxStatus Status { get; init; } = BackgroundActivityOutboxStatus.Pending;
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
```

### 11.2 写入时机

- **`Running`**：写最小记录（id / kind / status / hostInstanceId / owner / 时间）。`Pending` 不落盘。
- **终态**：原子覆盖为完整记录。
- **`Progress` 不落盘**，只发节流事件。
- 孤儿扫描的"读 → 判 → 覆盖"整体包在 `AtomicFileWrite.GetLock` 内，覆盖前重读校验 `Status` / `HostInstanceId`。

### 11.3 输出

- 追加写用 `IPathWriteLock` + `FileStream.Append`；不用 `AtomicFileWrite` 逐块重写。
- 内存只保尾部 64KB 与字节/行数统计。
- `OutputLimitBytes` 默认 5 MiB，超限截断 + `[truncated]`。
- `ReadOutputAsync` 单页最多 32KB，且 `activity_output` 声明 `output.skip=true`。
- 已知偏差：`ISubprocess.StandardOutput/StandardError` 是 `TextReader`，字节口径按 UTF-8 重编码计算。

---

## 12. 事件

```csharp
public static class ActivityEventTypes
{
    public const string Started   = "activity.started";
    public const string Progress  = "activity.progress";
    public const string Completed = "activity.completed";
    public const string Failed    = "activity.failed";
    public const string Cancelled = "activity.cancelled";
}

public record BackgroundActivityEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required string Type { get; init; }
    public required BackgroundActivitySnapshot Activity { get; init; }
}
```

### 12.1 发布与回放

- manager 直接 `Publish`，不经过 `ExecutionRunner` 的 `ShouldPersistEvent`。
- **不做事件落盘**。UI 首屏用 `manager.List` 重建，事件只做增量。
- `activity.progress` 必须节流（≥1s）；回放缓冲按 `ExecutionOptions.EventBufferSize`（默认 100）。
- 事件不唤醒执行（`Inject` 语义）。

---

## 13. 工具面

### 13.1 `bash` 扩展

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `background` | bool | false | 启动活动并立即返回 |
| `activity_timeout` | number(ms) | 1800000 | `background=true` 时取代 `timeout` |

`background=true` 时：

- 返回 `ToolResult.Success` + `ToolResult.BackgroundActivityId`
- 原 tool call 终态仍是 `Success`（不新增 `ToolCallStatus.Deferred`）
- UI 通过 `SessionToolCall.BackgroundActivityId` 渲染"已转后台"
- manager 通过**构造函数注入**（显式依赖），调用 `StartAsync(request, context.SessionId)`；现有 `new BashTool(6 参)` 测试改为传假实现
- manager 不可用或 `background` 模块禁用 → 直接失败

### 13.2 新增工具

| 工具 | 说明 | 能力位 |
|---|---|---|
| `activity_status` | 给 id 查单个；不给 id 列本会话 | `loop.skip` |
| `activity_output` | 分页读（`offset` / `max_bytes`≤32KB） | `output.skip` |
| `activity_wait` | 阻塞到终态或超时 | `timeout.skip` + `loop.skip` |
| `activity_cancel` | 取消活动与子树 | — |
| `activity_list` | 列出活动 | — |

- 必须拆开：权限判定不含参数。
- 工具只注入 `IBackgroundActivityManager`（会话面）；`StartAsync` 传 `context.SessionId`，归属校验由 manager 强制。系统面 `IBackgroundActivityControl` 不进工具依赖。
- `loop.skip` 需要新增 `ToolCapabilityKeys.LoopSkip` 并在 `AgentExecutor` 的检测点消费（检测器 `Detection/LoopDetector.cs:56,96`；阈值 `AgentExecutor.cs:1146-1147`）。实现要点：① 用 canonical 工具名查询（归一化晚于检测点）；② 跳过时不得 `RecordCallCore`；③ `activity_wait` 也必须豁免，否则同参反复超时会终止本轮。

### 13.3 `activity_wait` 的中断语义

- 只观察 `context.CancellationToken`；被取消返回 `CallerCancelled`，不得取消活动本身。
- 活动随后完成照常唤醒，与 §10.6 对齐。

---

## 14. 权限

- 启动活动的 tool call 已过 `tool.execute` 门；活动运行期不再申请权限。
- 后台活动禁止交互式审批；需要审批的操作直接失败。
- 子 agent 后台（二期）延续 `SubagentPermissionDeriver` 的权限快照继承。
- `Visibility=System` 的活动不对用户呈现，仍受 `tool.execute` 门约束。

---

## 15. 治理

| 项 | 默认 | 超限 |
|---|---|---|
| 每会话并发活动 | 8 | 拒绝启动 |
| 单活动输出 | 5 MiB | 截断 + 标记 |
| 单活动 TTL | 30 min（可覆盖） | 取消，`Timeout` |
| 单活动最大 TTL | 24 h | 拒绝启动 |
| 合并窗口 | 300 ms | — |
| outbox 重试 | 5 次 | 丢弃 + 日志 |
| 终结记录保留 | 7 天 | 清理任务删除 |

配置节 `BackgroundActivities`，`IOptionsMonitor` 热重载。二期接入 agent-session 后补"按根会话计数或全局上限"。

---

## 16. 重启、孤儿与子进程生命周期

- **多实例**：`HostInstanceId` = pid + 进程启动时间；扫描先判进程存活。
- **扫描触发点**：宿主启动后一次；工作区切换完成后一次。不做定时扫描（他实例遗留的 `Running` 需等该根下次启动才收敛，属已知边界）。
- `Status == Running` 且宿主已死 → `Interrupted`，写完整记录。
- **孤儿收敛只落记录、不入 outbox、不发事件。**
- **子进程生命周期**：`SubprocessSpec` 新增 `SubprocessLifecycle { HostBound, Detached }`（默认 `HostBound`）；`LocalSubprocessFactory` 对 `HostBound` 在 Windows assign **每进程独立** Job Object（`KILL_ON_JOB_CLOSE`），`Detached` 不 assign。`ISubprocess` 不变。
  - 已知语义变化：`bash -c "npm run dev &"` 这类前台命令遗留的后台子进程，会在宿主关闭时被回收。这是期望行为。
  - 实现前提：每进程一个 job（共享 job 会互相误杀）；assign 与 `Process.Start` 之间有竞态，失败降级为记录 pid；Unix 用 process group / `setsid`，不用 Job Object。
  - **非 Windows 平台（已知边界）**：`HostBound` 暂与 `Detached` 等效（不回收）。POSIX 实现需 `setsid()` / `setpgid()` 建独立进程组并在宿主退出时 `kill -- -pgid`；当前环境仅 Windows，不写未验证的 POSIX 代码，代码内以 `TODO(POSIX)` 标注。
  - 现有 `SubprocessSpec` 使用点：`BashTool`、`DefaultShellService`（where/which）、`GitService`、`RipgrepSearch`、`WorkspaceGitExcluder`——全是短命进程，默认 `HostBound` 无副作用。hooks node / channel host / ACP / Browser 走的是裸 `Process.Start`，不受影响。
- `ReconcileIncompleteTasksAsync` 保持现状；活动整理由 `IBackgroundActivityControl.ReconcileOrphanedActivitiesAsync` 负责。

---

## 17. UI（一期）

- `EventStreamHandler.ProcessEventAsync` 增 `case ActivityEventTypes.*`。
- 卡片按 `OriginToolCallId` 关联；无关联时在时间线末尾新增条目。
- 展示：标题、状态、进度、输出大小、取消按钮（仅 `CanCancel`）。
- 首屏用 `manager.List` 重建，事件只增量。
- 不改 `TaskCardAggregator`（二期）。
- 不新增独立页面、不新增侧栏项。

---

## 18. 收敛矩阵与分期

### 一期

**前置重构：**

- `ExecutionId` 贯通（§9.6，7 处）
- `InterruptAsync` 签名变更 + 6 个非测试调用点语义 + 10 个测试点（§9.3）
- `BackgroundActivityId` 全链路（§6.2，8 段）
- `SubprocessSpec.Lifecycle` + Job Object（§16）
- `ToolCapabilityKeys.LoopSkip` + 检测器消费
- 删除 3 个 `IsBackground` 死字段

**主体：**

- 契约 + manager + control + outbox + dispatcher + output store
- 事件类型 + 载荷 + 节流
- `kind=process` launcher（Shell 包）
- `bash background` + 5 个 `activity_*` 工具（新能力包 `background`，登记 `BuiltInCapabilitySets` 与 `AddSeeingFullCapabilities`）
- WebUI 内联卡片
- §9.5 各项挂接

### 二期

- `TaskTool background` → `kind=agent-session`，删除 `Task.Run` 回投与 `WaitForIdleAsync` 安全上限
- `TaskCardAggregator` 改为按活动事件聚合

### 三期

- `AcpTool` 字典 → `kind=acp-task`
- Scheduler → `kind=scheduled-run`
- Reflection → `kind=reflection-run`
- Memory → `kind=memory-job`（`Visibility=System`）
- **浏览器宿主：暂不接入**

---

## 19. 红线

1. 框架任何分支不得 `switch (Kind)` 或读 `Attributes`——只允许读 `Capabilities`。
2. `ToolCallStatus` 不得新增成员。
3. 活动记录不得包含输出正文。
4. 后台活动不得进入交互式审批等待。
5. `bash background` 在 manager 或 `background` 模块缺失时必须失败。
6. 终态必须先落盘再投递。
7. 文件写入走 `AtomicFileWrite` / `IPathWriteLock`；路径判定走 `PathBoundary`。
8. 合并窗口必须用 `TimeProvider` 注入，测试不得用固定 `Task.Delay`。
9. 能力包不得引用 `Seeing.Agent.Core`。
10. 新能力包必须登记 `BuiltInCapabilitySets`。
11. launcher 不得引用 `ToolContext.CancellationToken` / `ToolContext.EventSink`。
12. 终态只能由 manager CAS 写入；reporter 在终态后为 no-op。
13. `activity.progress` 必须节流；不得进入事件落盘白名单。
14. `InterruptAsync` 必须带 `cancelBackgroundActivities` 参数（默认 `false`）。
15. 框架不得解释 `StartRequest.Payload`；launcher 不得自建 `BackgroundActivityHandle` 或 CTS。
16. `UnregisterLauncherAsync` 必须在返回前取消该 kind 名下全部**运行中**活动。
17. 快照写入不得跳过"进入 Running 时落最小记录"。
18. 会话面接口的 `requesterSessionId` 不得为可空或省略；`RegisterLauncher` / `UnregisterLauncherAsync` / `TrackAsync` / `StartAsSystemAsync` / `CancelBy*` 不得出现在 `IBackgroundActivityManager` 上。
19. 活动 id 不得经由 `Metadata` 字符串约定传递。

---

## 20. 验收标准（全部可自动化，禁止等待型断言）

测试基建：

- 子进程：现有 `FakeSubprocess` 是即时完成型，**不能**验证"仍在运行"；须新写可控 fake。
- 时间：`FakeTimeProvider`；`tests/spine/Seeing.Agent.Tests` 需补 `Microsoft.Extensions.TimeProvider.Testing`。
- 合并窗口：`outbox.PumpOnceAsync()` 单步驱动。

| # | 标准 | 断言方式 |
|---|---|---|
| 1 | `bash background=true` 启动后立即返回，进程仍在运行 | 不变量：工具返回时 `HasExited == false` 且得到句柄 |
| 2 | 分页完整 / 截断标记 / 单页 ≤32KB | 注入小 `OutputLimitBytes`；UTF-8 字节断言；分别验 `output.skip` 与 32KB 钳制 |
| 3a | `StopAsync` 后活动 `Cancelled` | `await StopAsync` 返回后直接读快照 |
| 3b | 进程树被 kill | fake 断言 `Kill()` 被调用；真实树 kill 单独 Windows-only 并允许 Skip |
| 4 | `InterruptAsync(false)` 不杀后台；`(true)` 精确杀本轮 | 双分支对照 |
| 5 | 5 个活动同时完成只产生 1 条投递；重复投递不重复 | 推进 `FakeTimeProvider` → `PumpOnceAsync` → 断言 1 次；再泵仍 1 次 |
| 6 | `activity_wait` 读到终态后不再唤醒 | `MarkResultObserved` → `PumpOnceAsync` → 断言 0 次 |
| 7 | 上一代 `Running` → `Interrupted` 且不唤醒；多实例不互杀 | 伪造快照文件 + `ReconcileOrphanedActivitiesAsync` |
| 8 | 无 UI 会话不阻塞、不等待审批、完成后唤醒、可出站 | 记录型 authorizer/presenter；断言 `SourceTag` |
| 9 | `kind=fake` launcher 跑通全生命周期 | 直接 `RegisterLauncher` + `StartAsync` |
| 10 | 工作区切换 / 模块停用取消活动；切换后不再追加输出 | 切换：参与者返回即断言；停用：走无在途的同步默认路径 |
| 11 | 终态 CAS 唯一 | 并发 `Complete` + `Cancel`，断言只一条终态 |
| 12 | 先落盘再投递 | 投递桩抛异常，断言快照仍终态、outbox 仍 pending |
| 13 | `activity.progress` 节流 | `FakeTimeProvider` 推进，断言 1s 窗口计数上限 |
| 14 | 多实例泵取隔离 | 两条 pending（本实例 / 存活他实例），泵后只投本实例 |
| 15 | `ExecutionId` 贯通 | `ToolContext.ExecutionId == 本轮 ExecutionRecord.ExecutionId` |
| 16 | 精确中断 | 两条活动分属不同 `OwnerExecutionId`，`InterruptAsync(true)` 只取消当前轮那条 |
| 17 | 归属校验 | 用其他会话 id 调 `Get`/`Cancel`/`Wait` 返回 `null`/`false` |
| 18 | 全链路投影 | fork 一条含后台活动的会话，子会话仍能看到 `BackgroundActivityId`；Gateway / TUI 事件带该字段 |
| 19 | 工具无法跨会话启动 | `StartAsync(request, 其他会话 id)` 抛 `UnauthorizedAccessException` |
| 20 | `List` 不泄露他人活动 | 两个会话各一条活动，用 A 的 id 查 `List` 只返回 A 那条 |

---

## 21. 已知架构债（不在本设计范围）

- `ToolContext.MessageId` 恒空串：`AgentContext.MessageId` 与 `ExecutionRecord.LastMessageId` 从未赋值。需要先定义"消息身份"的语义来源，再接。
- `ToolContext.Services` 是根容器而非执行级 scope：工具解析 scoped 服务会拿到错误实例。本设计不依赖它（manager 单例，`BashTool` 构造函数注入）。
- `ISubprocess` 不暴露进程句柄：Job Object 的 assign 在工厂内完成，调用方无法自行把已有进程加入 job。

---

## 22. 留给 builder 的决定

- 合并窗口具体实现（`Channel` + `TimeProvider` + 批量取）。
- 终结记录清理任务挂哪个 `IHostedService`。
- 内联卡片样式与组件拆分。
- `DedupeKey` 的规范化细节。
- 输出文件目录分片策略。
- Unix 下 process group 的具体实现（`setsid` / `kill -- -pgid`）。

---

## 23. 统一执行路径与"转后台"（二期重构）

### 23.1 目标

前台与后台不再是两套进程管理：`bash` 的每一次执行都走 `kind=process` launcher，差别只在工具是否等待。

```
StartAsync ──┬─ background=true  → 立即返回句柄
             └─ 前台             → WaitAsync → 读输出 → 作为 ToolResult 返回
                                    │
                                    └─ 用户"转后台" → DetachAsync → 等待以 Detached 返回
                                                      → 工具返回 Success + activityId
                                                      → 活动继续跑
```

"转后台"因此不是新机制，而是"取消等待、保留活动"。

### 23.2 契约变更

1. `BackgroundActivityWaitOutcome` 增加 `Detached`。
2. `IBackgroundActivityControl` 增加：
   ```csharp
   Task<bool> DetachAsync(string activityId, CancellationToken ct = default);
   ```
   语义：让当前**前台等待者**以 `Detached` 返回；活动状态不变、不取消、不发终态事件。
3. `BackgroundActivityOrigin` 增加 `ToolForeground`（与 `ToolBackground` 区分）。
   - UI 据此判断卡片是"前台运行中"（不显示后台 hint、不显示取消）还是"后台"。

### 23.3 BashTool 流程

```
1. 路径门闸 / 危险命令拦截（不变）
2. 构造 StartRequest：
   Kind=process, Origin=background ? ToolBackground : ToolForeground
   Timeout = background ? activity_timeout : timeout
   Payload = { command, workdir }
3. handle = await manager.StartAsync(request, sessionId)
4. if (background) return Success + handle.Id
5. result = await manager.WaitAsync(handle.Id, timeout, sessionId, ct)
   switch:
     Completed       → 读输出 → Success(output)
     TimedOut        → 取消活动 → Failure("超时")
     CallerCancelled → 取消活动 → Failure("已取消")
     Detached        → Success + BackgroundActivityId（活动继续）
```

删除 `BashTool` 自己的 `PumpStreamAsync`、超时 CTS、`StreamEmitGate`；流式进度统一走 `activity.progress`。

### 23.4 输出落盘

**前台也落盘**（统一走 output store）。理由：转后台时无需迁移已产生的输出，前台/后台只有一份真相。

- 前台命令同样受 `OutputLimitBytes` 与 7 天回收约束。
- 前台工具结果从 `ReadOutputAsync` 读回，超过 `ToolOutputLimiter` 阈值时按其既有规则外溢。

### 23.5 UI

- 前台活动（`Origin == ToolForeground`）：
  - 不显示底部 hint 行
  - 输出块取 `LiveOutput`
  - 运行中显示「转后台」按钮
- 后台活动：维持现状（hint 行 + 取消）。
- 「转后台」调用：
  ```
  POST /api/webui/sessions/{sessionId}/activities/{activityId}/detach
  ```

### 23.6 风险

- 前台每次 `bash` 都会写 `output.log`（短命令也有一次小文件写入），靠回收任务兜底。
- 工具结果改为"等待终态后读盘"，比现在多一次读；大输出要注意分页上限。
- 权限语义不变：仍然只有一次 `tool.execute`，活动是内部实现。
- 现有 `bash` 前台测试需要改为"经 manager + fake launcher"的路径。

