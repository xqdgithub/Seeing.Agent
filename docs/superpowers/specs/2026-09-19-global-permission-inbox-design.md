# 全局权限收件箱与子会话审批冒泡设计（v3）

**日期：** 2026-09-19
**分支：** master
**状态：** ✅ 架构评审已签字通过（APPROVE）；v3.1 固化评审签署意见，可进入实施计划
**关联：** [`08-permission-authorization-release-notes.md`](../../architecture/08-permission-authorization-release-notes.md)、[`04-development-standards.md`](../../architecture/04-development-standards.md)、[`06-compliance-audit.md`](../../architecture/06-compliance-audit.md)

> **版本沿革**
> - v1：初版（含 `HasAnyPresenter` 全局布尔）。
> - v2：按评审 B1–B6 修订——呈出端抽象、View 生命周期、registry 唯一来源、注入点修正、继承默认。
> - v3：按复评 N1/N2 修订——**呈现端瞬态去抖与在途收敛宽限**（N1）、**Gateway 每订阅一 presenter 实例**（N2）；并纳入非阻塞项（P5 修法、`ActiveSessionTracker` 依赖清理、引用清单补全、prerender 守卫、ACP 结论、`GetOrLoadAsync`）。
> - v3.1：按最终复评（APPROVE）固化签署意见——收敛改为"仅呈现端注销时收敛"（S1）、presenter 稳定快照契约、Store 注册/注销触发 `Changed`、View 合并机制、`TaskCardAggregator` 精确插入点、启动竞态加固。

---

## 1. 背景与问题

权限授权子系统重构（2026-09-17）后，审批改为"统一授权引擎 + 在途管理器 + 会话事件流 + 内联卡片"。由此产生两处回归与一类能力缺失：

| 编号 | 问题 | 位置 |
|------|------|------|
| P1 | 非活动会话 `CanPresent(sessionId)=false`，在 `BeginAsync` 之前即 `Deny(NoChannel,"无交互通道")`，请求事件根本不发布 | `PermissionService.cs:231` |
| P2 | 即使登记在途，请求/结果事件只发 origin 会话流，父页面与全局不可见 | `PermissionRequestManager.cs:280/306` |
| P3 | 子会话执行硬编码 `AutoApprove=Enabled`；续跑分支未取父会话 | `TaskTool.cs:183`、`:105-155` |
| P4 | 无全局在途查询与变更通知 | `IPermissionRequestManager` 仅 `GetPending`/`PendingCount` |
| P5 | `TaskCardAggregator` 子流分支对 `Permission*` 事件落入默认 `_ => true`，触发虚假落盘/重渲染 | `TaskCardAggregator.cs:120-140,231-238` |
| P6 | 执行级 override 在自建授权器调用点丢失 | `SessionToolBase.cs:65`、`AcpTool.cs:92` |

### 根因

审批的"可交互性"与"事件可见性"均以**请求自身 SessionId** 为准，而 presence 只对页面当前活动会话记账、事件消费者又按 SessionId 严格过滤。任何非活动会话的审批请求既无法呈现也无法冒泡，被立即 fail-closed 拒绝。

### 治本方向

"可交互性"不是进程级布尔，而是**是否存在能呈现该请求的呈现端**（host 可用 ∧ session 可见/可冒泡）。故引入显式**呈现端（Presenter）契约**，由宿主声明可呈现的会话集合；Core 决策据此判定，UI 侧由收件箱做当前标签投影。

---

## 2. 目标与非目标

### 目标

1. 非活动会话（含子会话）需要审批时不再秒拒：只要存在能呈现该会话的呈现端即登记在途。
2. 子会话（task）默认继承父会话/全局审批策略，不再硬编码自动批准。
3. 当前标签（circuit）内任意会话的在途审批可见、可批：侧边栏徽标、全局审批中心抽屉、父页面内联卡片。
4. 收敛已知边界：删除单 `_sessionId` 的 `PermissionCardAggregator`。

### 非目标

- 跨宿主审批中心——本期仅"当前标签内"。
- 审批历史持久化、审计报表、CLI 控制台通道。
- 改变 ACP `RequireInteraction` 语义。
- 修改宿主通道级自动批准（`GatewayOptions.PermissionMode=auto_approve` 进程级语义，见 §8.7）。

---

## 3. 架构总览

```
┌──────────────────────────── Core (spine) ─────────────────────────────┐
│ PermissionService ── step6: PresentationStore.CanSurface(sessionId)    │
│        │                                                               │
│        ▼                                                               │
│ PermissionRequestManager (Singleton)                                   │
│   GetAllPending() ; event PendingChanged                               │
│   ← 订阅 PresenterUnregistered：宽限复核后收敛在途（仅注销）           │
│                                                                        │
│ PermissionPresentationStore (Singleton, IPermissionPresenter 登记表)    │
└────────▲───────────────────────────────────────────────────────────────┘
         │ Register/Unregister(IPermissionPresenter)
   ┌─────┴───────────────┬───────────────────────────┐
   │ WebUI circuit 呈现端  │ Gateway 订阅呈现端          │ (ACP 无需独立 presenter)
   │ = registry.Windows    │ = 每订阅一实例 {sessionId}  │
   │ (去抖 + 空集确认)      │ (订阅同生命周期)            │
   └───────────────────────┴───────────────────────────┘

WebUI 投影（读取面，与呈现端同源但独立）：
PermissionInbox (Singleton) ── GetAllPending 投影 + Changed
        ▲
PermissionInboxView (Scoped, IDisposable) ── scope = registry.Windows
        ├── AppSidebar 菜单 Badge + PermissionCenterDrawer.razor
        ├── Session/Conference 侧栏 SidebarEntry.PendingPermissions
        └── ToolPermissionSlot / TaskMessageComponent（内联卡片）
```

- **唯一权威**：在途真相源仍是 `IPermissionRequestManager`；`PermissionInbox` 仅投影 + 通知。
- **呈现判定唯一入口**：`IPermissionPresentationStore.CanSurface(sessionId)`。
- **当前标签会话集合唯一来源**：`SessionWindowRegistry.Windows`（circuit 作用域、组驱动、含 Child 成员）。
- **事件流非控制面**：`Permission*Event` 仅保留给 Gateway 推送与时间线刷新。

---

## 4. Core 契约变更

### 4.1 呈现端抽象（新增，替换 `IPermissionPresenceStore`）

`Abstractions/Permissions/IPermissionPresenter.cs`

```csharp
/// <summary>
/// 宿主呈现端：声明在自身上下文中可呈现（含宿主自定义冒泡）的会话集合。
/// 由宿主注册到 IPermissionPresentationStore；集合可随宿主状态变化。
/// 契约约定：SurfaceSessionIds 必须返回<b>已确认的稳定快照</b>（缓存字段），
/// <b>不得</b>实时读取底层易变状态；空集候选须经宿主确认期后才写入缓存。
/// 宿主自行为瞬态去抖，避免短时抖动触发在途收敛或新请求被 fail-closed。
/// </summary>
public interface IPermissionPresenter
{
    /// <summary>当前可呈现的会话集合快照。</summary>
    IReadOnlyCollection<string> SurfaceSessionIds { get; }

    /// <summary>可呈现集合变化（宿主稳定态变化后触发）。</summary>
    event Action? SurfacedChanged;
}
```

`Abstractions/Permissions/IPermissionPresentationStore.cs`

```csharp
public interface IPermissionPresentationStore
{
    void Register(IPermissionPresenter presenter);
    void Unregister(IPermissionPresenter presenter);

    /// <summary>是否存在可呈现指定会话的呈现端。</summary>
    bool CanSurface(string sessionId);

    /// <summary>呈现端注销（身份移除）；用于在途收敛复核。注册不触发。</summary>
    event Action? PresenterUnregistered;

    /// <summary>呈现端集合或可呈现集合内容变化；用于 UI/诊断，不触发在途收敛。</summary>
    event Action? Changed;
}
```

Core `PermissionPresentationStore`（Singleton）：`ConcurrentDictionary<IPermissionPresenter, byte>`；`Register` 仅上抛 `Changed`，`Unregister` 先上抛 `PresenterUnregistered` 再上抛 `Changed`；订阅各 presenter `SurfacedChanged` 上抛 `Changed`；线程安全。

**替换范围（局部重构）**：

- 删除 `IPermissionPresenceStore`、`PermissionPresenceStore`。
- WebUI：删除 `ActiveSessionTracker`；`SessionEventStreamRouter` 移除其构造依赖与 `DetachAllForCircuit` 调用（`SessionEventStreamRouter.cs:52/77/262`）；`Program.cs:101` 删除注册；删除 `ActiveSessionTrackerTests`。
- Gateway：`GatewayOrchestratorV2.SubscribeExecutionEventsAsync` 改为**每订阅注册一个 presenter 实例**，`finally` 注销（见 §4.6、N2）。

### 4.2 `IPermissionRequestManager`

```csharp
IReadOnlyList<PermissionRequest> GetAllPending();   // 过滤 !IsCompleted，不做会话过滤
event Action? PendingChanged;
```

- `PendingChanged` 触发点：`BeginAsync`（含溢出拒绝）、`TryResolve` 成功、`WaitAsync` 超时/取消、`Dispose` 清理。
- **语义**：事件是"尽力通知"，最终一致性依据是 `GetAllPending()` 的过滤结果。锁外、try/catch 触发。

### 4.3 `PermissionService.AuthorizeCoreAsync` 步骤 6

```csharp
if (_presentation is null || !_presentation.CanSurface(sessionId))
    return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.NoChannel, "无交互通道");
```

无任何可呈现端（Headless/Core-only/全部页面关闭）⇒ fail-closed（与旧一致）。

### 4.4 在途收敛（N1：仅"呈现端注销"收敛）

`PermissionRequestManager` 仅订阅 `IPermissionPresentationStore.PresenterUnregistered`（`Changed` 不参与收敛）：

1. Store 在 `Register` 时触发 `Changed`；在 `Unregister` 时先触发 `PresenterUnregistered` 再触发 `Changed`；presenter 的 `SurfacedChanged` 仅转发 `Changed`。
2. Manager 仅响应 `PresenterUnregistered`：
   - 快照"注销时刻"已登记的在途 requestId，启动短宽限（默认 **1s**）后复核；仍 `!CanSurface` 的 entry → `TryResolve(Deny, NoChannel, "呈现端已移除")`。
   - **注册**与**仅 `SurfaceSessionIds` 内容收缩** ⇒ 仅 `Changed`，**不收敛**；在途请求保留，最终由 Manager 300s 超时兜底。
3. `Dispose` 时取消计时器。

设计依据：`SessionWindowRegistry.Rebind` 同步清空窗口并立即触发 `WindowsChanged`（`SessionWindowRegistry.cs:92-104`），随后异步填充。若对"集合收缩"即时收敛，切会话即秒拒在途审批；只有"呈现端注销"才代表"确实没人能看了"（circuit 关闭）。

> 该策略为复评建议 S1（最少参数、零误拒）。代价：用户切组后旧组在途请求不可见并悬挂至超时——列为已知边界。

### 4.5 执行级授权器贯通（B6）

`ToolContext` 增加 `IPermissionAuthorizer? PermissionAuthorizer { get; init; }`。

- **注入点**：`ToolManager.ExecuteAsync` 构造 `ToolContext` 处（`ToolManager.cs:593-601`）写入方法入参 `permissionAuthorizer`；AgentExecutor 无需改动。
- `SessionToolBase.AuthorizeAsync`、`AcpTool` 优先用 `context.PermissionAuthorizer`，缺失时回退 `IPermissionAuthorizerFactory`。
- **约束**：工具不得再自行 `GetService<IPermissionAuthorizerFactory>()`（`AcpPermissionBridge` 除外——异步上下文外构造，保留 `RequireInteraction`）。

### 4.6 Gateway 呈现端（N2：每订阅一实例）

`GatewayOrchestratorV2.SubscribeExecutionEventsAsync`（`GatewayOrchestratorV2.cs:76-77/127`）：

```csharp
var presenter = new GatewaySubscriptionPresenter(sessionId); // SurfaceSessionIds = { sessionId }
_presentation.Register(presenter);
try { ... 现有订阅循环 ... }
finally { _presentation.Unregister(presenter); }
```

- 每订阅一个独立 presenter 实例，注册/注销与订阅同生命周期，**等价于原引用计数语义**（同 session 多订阅各持一份，任一订阅结束不影响其它）。
- Gateway 的 `auto_approve` 仍由 `GatewayPermissionChannel.TryAutoApprove` 在步骤 5 生效，不受影响。

### 4.7 ACP 结论（显式声明）

ACP 权限请求的 `SessionId` 恒为执行中的 Seeing 会话（`AcpTool.cs:209`、`AcpPassthroughExecutor.cs:91`、`AcpPermissionBridge.cs:42-51`/`AcpSessionRunner.cs:74-76`）。ACP 自身**不注册** presence/presenter（现状亦然）。因此：

- 承载该执行的会话若被 WebUI/Gateway presenter surface（父会话在 `registry.Windows`；子会话经 `SessionGroupManager.CreateChildAsync:535-542` 以 `Relation=Child` 入组，故在 `Windows` 内），ACP 交互即可用。
- 后台/无呈现端场景原本即 fail-closed，非回归。
- 需补一条 ACP 回归测试（§9）。

---

## 5. 策略继承（B5，2026-09-19 修订：实时父链）

子会话**不再**把父会话三态冻结进执行级 `ChatOptions.AutoApprove`；`TaskTool` 提交子任务时不再写入该字段。
子会话的生效三态由 `EffectivePermissionPolicy.Resolve` 在每次授权时**实时**解析：

1. 执行级显式覆盖（非 `FollowGlobal`）优先；
2. 否则取会话自身 `AutoApprove`；
3. 若会话为 `Kind=SubAgent` 且自身为 `FollowGlobal`，沿会话组父链上溯到最近的非 `FollowGlobal` 三态
   （遇到根会话 / Fork / Handoff 即停止）；父索引来自 `ISessionGroupManager.TryGetParent`（仅内存，无冷存兜底）。

因此在执行中切换父会话「默认 / 自动 / 确认」会即时作用于其子代理；在途请求由
`PermissionRequestManager` 在任一会话 `Updated` 时**全量重评估**（父链实时解析使其能命中子会话请求）。

| 父会话 `AutoApprove` | 子会话结果 |
|----------------------|-----------|
| `Enabled` | 自动批准 |
| `Disabled` | 进入审批 → 冒泡呈现 |
| `FollowGlobal`（默认） | 继续上溯 / 回落全局 `Permission.AutoApproveAll`（默认 false） |

**噪声评估（回归）**：默认 `FollowGlobal`+全局 false 时，子代理在资源门（`DefaultToolPermissionPolicy.cs:25-41`）上与父会话一样进入询问；步骤 3b 对资源类 kind 不短路 Agent Allow（`PermissionService.cs:195-197`）。缓解：父会话可设 `Enabled` 一次性放行；发布说明明确行为变化；补回归测试（`EffectivePermissionPolicyTests`、`PermissionRequestManagerTests`、`TaskToolAutoApproveInheritanceTests`）。

---

## 6. WebUI：Presenter、Inbox、View

### 6.1 `WebUiPermissionPresenter`（Scoped，circuit 维度）

- 依赖：`SessionEventStreamRouter` + `CircuitContext`。
- registry 获取：`Router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId)`（与 `Session.razor:322`、`Conference.razor:431` 同实例、同默认 key）。
- **surface 计算与稳定快照（N1 源头治理）**：
  - `SurfaceSessionIds` 返回**已确认快照**（缓存字段），非实时读 `registry.Windows`；空集候选须经确认期后才写入缓存。
  - 计算集合 = `registry.Windows` 的 SessionId ∪ `registry.AnchorSessionId`（非空），消除启动/刷新窗口的空集（启动竞态加固）。
  - **启动即时收敛**：当已确认快照仍为空（启动/刷新首次填充）时，`WindowsChanged` 直接立即重算并落定（跳过 250ms 去抖），避免 Rebind 后短窗内新请求因 `CanSurface=false` 被 fail-closed；快照非空后恢复去抖。
  - 订阅 `registry.WindowsChanged` → 标记 dirty → 短去抖合并（默认 **250ms**）后重算；与缓存相同则不上报。
  - **空集确认（默认 1s）**：期内变为非空则更新缓存；持续为空才把缓存置空并上报 `SurfacedChanged`（空集候选期间不修改缓存）。
- **生命周期**：circuit 打开时 `Register`；`Dispose`（电路作用域结束）退订并 `Unregister`。
- **prerender 守卫**：`CircuitContext.Id` 为空时不注册、不创建 registry（返回空 surface 且不上报），避免以 circuit `""` 污染 store。
- 承载：由布局中的全局组件（`AppSidebar`）注入并持有（`AppSidebar` 在 circuit scope 内，可注入 `CircuitContext`），确保任意页面（含非会话页）均注册呈现端。

### 6.2 `PermissionInbox`（Singleton）

- 输入：`IPermissionRequestManager.GetAllPending()` + `PendingChanged`。
- 输出：`GetAll()` / `GetBySession(sessionId)` / `GetByCallId(callId)` / `CountBySession` / `TotalCount` / `event Changed`。
- 每次 `PendingChanged` 全量重建投影（在途 ≤32），天然对账。
- 内部 `lock`；`Changed` 锁外触发；**不持有任何 Scoped 引用**。

### 6.3 `PermissionInboxView`（Scoped，`IDisposable`）

- 依赖：`PermissionInbox` + `SessionEventStreamRouter` + `CircuitContext`（同 §6.1 取 registry 实例）。
- **作用域唯一来源**：`registry.Windows` 的 SessionId 集合（同步内存快照，已含子会话）。
- 订阅：`Inbox.Changed`、`registry.WindowsChanged`；触发后置脏标记。
- **合并机制（可落地最小实现）**：`Interlocked.Exchange` 单飞标志防止重入；用零延时/短延时 `Timer`（或 `Task.Run`）做一次重算，重算完成（含锁内更新缓存）后在**锁外**触发 `Changed`；期间再次触发只置脏，由下一次重算覆盖。View 是服务而非组件，不使用 `InvokeAsync`；订阅组件在 `InvokeAsync(StateHasChanged)` 内消费。
- 输出：`CardsForScope`（按 `SessionId ∈ scope` 过滤并按会话分组）、`CountBySession`、`TotalCount`、`event Changed`。
- **生命周期**：`Dispose` 退订两事件；scoped 服务随 circuit DI scope 结束自动 `Dispose`。
- prerender 守卫同 §6.1。

### 6.4 收敛与删除

- 删除 `PermissionCardAggregator`（职责并入 Inbox/View）。
- `LoopGroupItem.razor` 删除权限绑定逻辑。
- `Program.cs`：新增 `PermissionInbox`（Singleton）、`PermissionInboxView`（Scoped）、`WebUiPermissionPresenter`（Scoped）；移除 `PermissionCardAggregator`、`ActiveSessionTracker`（`Program.cs:101/104`）。
- `SessionEventStreamRouter` 移除 `ActiveSessionTracker` 依赖（`:52/77/262`）。
- 不引入 `SessionEventStreamRouter.GetCircuitSessions`。

---

## 7. UI 呈现

### 7.1 侧边栏徽标

- `SidebarEntry` 新增 `int PendingPermissions`。
- `Session.razor.RebuildSidebar()` 从 `PermissionInboxView.CountBySession` 填充；订阅 `View.Changed`。`Conference.razor` 同构。
- Summary 卡右上角 `Badge`（>0 显示）；分区标题显示合计。

### 7.2 全局审批中心抽屉

- 入口：`AppSidebar.razor` 在"聊天"下新增 `MenuItem`「审批中心」，`Badge` = `View.TotalCount`。
- `PermissionCenterDrawer.razor`：按会话分组的在途列表；每项含会话标题（点击 `NavigateTo($"/session/{SessionId}")`）、子会话归属、权限摘要；动作复用 `PermissionCard.razor` → `PermissionInteractionService`。
- 移动端：`AppState.IsMobile` 且侧栏隐藏时，`MainLayout` 显示悬浮按钮（Badge 计数）打开同一抽屉。

### 7.3 内联卡片

- 主会话工具卡：`ToolPermissionSlot` 改 `PermissionInboxView.GetByCallId(callId)`。
- 子会话：`TaskMessageComponent` 展开区（默认展开）用 `View.GetBySession(ToolCall.TaskId)` 渲染 `PermissionCard`（标题「子任务待审批」）。
- 三处入口共享权威在途态。

---

## 8. 失败模式与边界

| 场景 | 行为 |
|------|------|
| 无任何呈现端 | 步骤 6 fail-closed `Deny(NoChannel)`（不变） |
| 呈现端瞬态抖动（Rebind/切会话） | presenter 去抖 + 空集确认；Manager 收敛宽限复核 ⇒ 不误拒（N1） |
| 呈现端确已移除（circuit 关闭） | presenter `Unregister` → 宽限后复核仍不可呈现 → `Deny(NoChannel,"呈现端已移除")` |
| 在途溢出（>32） | `Deny(NoChannel,"队列已满")` + 终态事件（不变） |
| Inbox 与 Manager 短暂不一致 | 每次 `PendingChanged` 全量重建，天然对账 |
| 多标签页 | 各 View 独立过滤；`TryResolve` 进程级幂等，先批先得 |
| 子会话父会话不在本标签 | 不在本标签 registry 窗口集合内 → 不显示（符合范围） |
| 同 session 多 Gateway 订阅 | 每订阅一 presenter，注销互不影响（N2） |
| prerender（`ServerPrerendered`，空 circuitId） | presenter/View 守卫，不注册、不创建 registry |
| Blazor 线程 | `Changed` 由订阅组件在 `InvokeAsync` 内消费 |
| `PendingChanged`/`Changed` 高频 | View 帧级/微任务合并 |
| Gateway `auto_approve` 进程级 | 既有边界（release notes 3.7），不改变 |
| `TaskCardAggregator` 处理 Permission 事件（P5） | `OnEvent` 父分支 `return` 之后、子流分支判断（`TaskCardAggregator.cs:110`）之前插入 `if (evt is PermissionRequestEvent or PermissionResolvedEvent) return;`，不进入 `ApplyChildEvent`；`ApplyChildEvent` 返回值语义不变（勿改成 `true`） |

---

## 9. 测试策略

### Core（`tests/spine/Seeing.Agent.Tests/Permission`）

- `PermissionPresentationStore`：Register/Unregister/多 presenter/`CanSurface`；`Changed` 上抛。
- 步骤 6：无 presenter → Deny；有 presenter 且可呈现 → `WaitAsync`；有 presenter 但不可呈现 → Deny。
- **N1 回归**：在途请求存在时，presenter 上报"瞬态空集"随即恢复 → 宽限后复核仍可呈现 → **不 Deny**；presenter 持续不可呈现 → 收敛 Deny。
- `GetAllPending` 过滤 `!IsCompleted`；`PendingChanged` 触发点。
- `ToolContext.PermissionAuthorizer` 传递 override（SessionToolBase 路径）。
- 迁移 `FakePermissionRequestManager`；删除 `PermissionPresenceStoreTests`。

### WebUI（`tests/apps/Seeing.Agent.WebUI.Tests`）

- `PermissionInbox` 投影/检索/重建对账。
- `PermissionInboxView`：scope=registry.Windows 过滤；范围外排除；`Dispose` 退订（无泄漏）。
- `WebUiPermissionPresenter`：去抖 + 空集确认（Rebind 瞬态不上报空集）；`WindowsChanged` 后 surface 收敛；prerender 空 Id 守卫。
- 徽标计数；抽屉动作（`expectedSessionId` 为 origin 子会话）。
- `TaskMessageComponent` 渲染子会话在途卡。
- 删除 `ActiveSessionTrackerTests`。

### Gateway（`tests/gateway`）

- 每订阅一 presenter：同 session 两并发订阅，一个结束不影响另一个的 `CanSurface`。
- 迁移 `GatewayOrchestratorPresenceTests`。

### 集成

- `TaskTool` 继承：父 `Disabled` → 子 Ask 可批；父 `Enabled` → 不询问；`FollowGlobal`+全局 false → 资源门询问。
- 子会话审批在父会话页面打开时：不秒拒、View 可见、抽屉/task 卡可批。
- **ACP**：承载会话被 presenter surface 时 ACP 交互可提交与决议；无呈现端时 fail-closed（非回归）。

---

## 10. 迁移与文档

**破坏性/契约变更**（追加至 `08-permission-authorization-release-notes.md`）：

- 删除：`IPermissionPresenceStore`、`PermissionPresenceStore`、`PermissionCardAggregator`、`ActiveSessionTracker`。
- 新增：`IPermissionPresenter`、`IPermissionPresentationStore`、`GetAllPending`、`PendingChanged`、`ToolContext.PermissionAuthorizer`。

**引用清单（须逐项处理）**：

- 源码：`SessionEventStreamRouter.cs:52/77/262`、`PermissionServiceExtensions.cs:28`、`GatewayOrchestratorV2.cs:76-77/127`、`Program.cs:101/104`。
- 测试：`ActiveSessionTrackerTests.cs`、`PermissionPresenceStoreTests.cs`、`GatewayOrchestratorPresenceTests.cs:25/39/54`、`PermissionRequestManagerTests.cs:405`、`PermissionAuthorizationTests.cs:76/219/289/328/358/402/579`、`PermissionCardAggregatorTests.cs`、`PermissionInteractionServiceTests.cs`、`FakePermissionRequestManager`。
- 文档：根 `AGENTS.md:82/119`、`02-modules.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`06-compliance-audit.md`、`src/spine/Seeing.Agent.Core/Core/AGENTS.md`。

**其它**：关闭 `AGENTS.md` "Conference 同 circuit 多窗口共用单 `_sessionId`"已知边界；`EventStreamHandler.OnPermissionRequest`/`OnPermissionResolved` 标注为"仅展示/无生产消费者"。

---

## 11. 已决项

1. `PermissionInboxView` 作用域来源：`SessionWindowRegistry.Windows`（唯一来源），无异步展开。
2. 抽屉移动端入口：`MainLayout` 悬浮按钮。
3. task 卡内联区块默认：展开。
4. `Changed` 高频：帧级/微任务合并。
5. 通知端口归属：`PendingChanged` 随 `IPermissionRequestManager`；`IPermissionPresentationStore` 独立负责呈现判定与收敛。
6. 收敛策略：**仅呈现端注销时收敛**（宽限 1s 复核）；surface 内容收缩不收敛（超时兜底）——复评建议 S1。
7. Gateway 呈现端：每订阅一实例；`Register`/`Unregister` 幂等，`Unregister` 退订 `SurfacedChanged`；固定单会话时 `SurfacedChanged` 不触发。
8. ACP：无需独立 presenter，由承载会话的 presenter 覆盖；补回归测试。
9. prerender：空 circuitId 守卫（不注册、不创建 registry、不上报）；Scoped 可能两次构造，Register/Unregister 需幂等。
10. `PermissionPresentationStore`：`Register`/`Unregister` 也触发 `Changed`（否则 Unregister 收敛不触发）。
11. presenter 启动竞态加固：surface 计算并入 `registry.AnchorSessionId`。

### 实施计划须固化的契约点（评审签署意见）

- `IPermissionPresenter.SurfaceSessionIds` 返回已确认稳定快照（缓存），空集确认期内保持旧非空集合。
- `PermissionInboxView` 合并：`Interlocked` 单飞 + 零/短延时重算，锁外触发 `Changed`。
- `TaskCardAggregator` 精确插入点见 §8；默认分支可顺带收窄为仅 `ToolCallEvent` 返回 true（P5 范围外的顺带优化）。
- Gateway presenter 注入 `IPermissionPresentationStore` 经根容器可用（`GatewayOrchestratorV2._services` 根容器 + `AddSeeingCore` 注册）。
