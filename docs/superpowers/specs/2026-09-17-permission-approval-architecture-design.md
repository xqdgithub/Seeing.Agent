# 权限审批链路架构重构设计（v3.1）【已废弃】

> **已废弃**：本文是在旧权限通道栈（`IPermissionChannel` + `SerializingPermissionChannel` + 弹窗）上迭代的方案，已被
> [`2026-09-17-permission-authorization-subsystem-design.md`](2026-09-17-permission-authorization-subsystem-design.md)（架构优先 · 绿毯重构）取代。
> 保留本文仅作决策过程记录。

**日期**：2026-09-17
**分支**：feature/modular-architecture
**状态**：已废弃
**关联症状**：会话内切审批模式不生效、改全局自动批准不生效、并行工具审批第二个弹窗闪退后永久等待
**修订**：第三轮评审判「有条件通过」（1 项条件阻断 + 若干非阻断）。v3.1 落实全部修订：删除无法落地的「Gateway 在途模式切换即时生效」声明、重评估回调异常隔离、`DismissAsync` 原子化、`RequestOverride` 入库、契约与文档表述更正。

---

## 1. 背景与症状

WebUI（Blazor Server）权限审批链路的三个症状：

| # | 症状 | 触发条件 |
|---|------|----------|
| S1 | 会话中把三态开关切到「自动批准」后不生效，新发消息仍弹审批框 | 会话底部三态 Segmented（`PromptInput.razor:145-157`） |
| S2 | 修改全局「自动批准所有权限」保存后不生效 | 安全设置页 Switch |
| S3 | 并行执行两个工具：第一个点「始终允许」后，第二个弹窗**闪一下直接关闭**，第二个工具**永久等待** | 单轮多工具调用 |

S3 是 S1/S2 的主要诱因：第二个请求挂起会**占住串行闸门**，此后该 circuit 的所有权限请求排队饿死，用户感知即「切模式不生效、只能重启」。

> S2 的准确表述：全局开关在**新 execution 启动时**已被实时读取（`ExecutionJobService.cs:921` 读 `CurrentValue`；`UnifiedConfigManager.UpdateCache:721-727` 反射同步 `SeeingAgent.Permission`）。因此 S2 = **「执行中变更 + 闸门被挂起请求占用」复合场景下新消息无法推进**，而非全局值永久冻结。

---

## 2. 根因分析（含证据，两轮核查已确认）

### R1｜审批 UI 单槽状态 + Modal 参数抖动竞态

- `samples/.../Components/PermissionHost.razor:15-17`：`_request`/`_visible`/`_decided` 单槽，无法承载多个在途请求。
- `PermissionModal.razor:5` `Visible="@Visible"` 由父参数驱动：A 决策后 `_visible=false` 触发关闭动画，B 请求到达立即置 `_visible=true`，AntDesign Modal 内部动画状态机在快速 false→true 抖动下**不重开** → B 弹窗「闪现后关闭」，再无 UI 承载。
- `PermissionModal.razor:219-227` `Decide()` 先 `OnDecision` 再 `OnVisibleChange(false)`；`PermissionHost.razor:80-86` 在 `_decided=false` 时用**共享 `_request`** 调 `RespondToPermission` → 「响应到错误 id」次级竞态。

### R2｜策略在「执行启动时」快照，非请求时实时解析

- `ExecutionJobService.cs:773-775`：`ResolvePermissionChannel(record.Options?.PermissionChannel, record.Options?.AutoApprove ?? FollowGlobal)` 每 execution 只解析一次，整个工具循环固定该通道（:807）。
- `SessionWindow.razor:1060`：提交时把 `SessionState.CurrentSession.AutoApprove` 复制进 `ChatOptions.AutoApprove`，提交即冻结。
- 与 `docs/architecture/03-configuration-lifecycle.md` §5「热字段经 OptionsMonitor 消费」冲突。

### R3｜串行闸门 + 5 分钟阻塞等待 → 挂起传染

- `SerializingPermissionChannel.cs:22` 为**实例字段** `SemaphoreSlim _gate`：Core 默认 DI 注册为 Singleton（`ServiceCollectionExtensions.cs:578-595`，容器内共享，实际等同进程级）；WebUI 覆盖为 Scoped（`WebHostingServiceCollectionExtensions.cs:47-56`，每 circuit 一个）。:79-111 用它包裹 `_inner.RequestAsync`。
- `BlazorPermissionChannel.cs:47` `WaitAsync(TimeSpan.FromMinutes(5))` 等用户；一旦请求无 UI 承载（R1），闸门被占至超时，同 circuit 后续请求全部排队 → 新执行工具静默挂起。

### R4｜Gateway 路径三处缺陷

1. **配置快照**：快照捕获点 `Hosting/GatewayServer.cs:38`（`var gatewayOptions = _options.Value;`）→ `GatewayServer.cs:51-54` 构造 `GatewayHost` → `GatewayHost.cs:64 new GatewayPermissionChannel(_options)`；通道永久持有该实例，而 `UpdateCache:713-733` 替换的是缓存中的**新实例** → `PermissionMode` 改后必须重启。
2. **通道未接线**：`Core/GatewayOrchestratorV2.cs:305-315` `BuildChatOptions` 未传 `PermissionChannel`（字段 `_permissionChannel` 除赋值外未使用）→ `ExecutionJobService.cs:933-945` 落到 `DenyAllPermissionChannel`，pending/respond 链路整体旁路。
3. **sink 传递断裂**：`Core/IGatewayEventSink.cs:43` 的 `PermissionRunContext` 用 `AsyncLocal`，在**订阅者**侧 `SetRunContext`（`GatewayOrchestratorV2.cs:158-163、212-217`），而 Agent 执行在 `ExecutionJobService` 的独立任务中，AsyncLocal 不向生产者流动 → `runContext?.Sink` 恒 null。

### R5｜三态写入无变更信号

- `SessionWindow.razor:1402-1410` 改字段 + `SessionManager.SaveAsync`（`Management/SessionManager.cs:234-265`）不发布 `SessionEvent`。发布 `Updated` 的是 `AddMessageAsync:329`、`SetTitleAsync:607`、`SaveAndNotifyAsync:780`（`SetModelAsync:626`/`SetThinkingEffortAsync` 不发布 `SessionEvent`，但仍触发 `HookPoints.Updated`）。

### R6｜在途状态三处重复（结构性缺陷）

同一份「待审批」状态存了三份：`BlazorPermissionChannel._pendingRequests:13`、`EventStreamHandler._pendingPermissions:107`、`PermissionHost._request:15`；任何跨层决策都需同步三处，是 R1/R3 竞态的结构性来源。

---

## 3. 目标与非目标

### 目标

1. 审批策略**每次权限请求实时解析**（会话三态 > 全局开关），切换立即影响当前执行的下一个工具调用。
2. 策略变更**触发在途请求重评估**：转为自动批准 → 立即放行并令 UI 关闭对应弹窗。
3. 审批 UI **单弹窗 + 真队列**：并发请求 FIFO，内容切换而非关闭重开；推进只有一个来源。
4. **在途状态单一化**：一份权威状态机，消除三份 pending 同步与挂起传染。
5. Gateway 对齐：配置热更新（新请求实时读取）、审批通道接线、pending 事件可达客户端；去 AsyncLocal。
6. **零语义回归**：会话记忆（「始终允许」）、白名单扩张、filesystem 硬边界、Gateway 通道级 `PermissionMode`/超时语义与现状**等价**。

### 非目标

- 不改审批粒度模型（会话三态 + 全局开关 + Gateway 通道模式）。
- 不做跨 circuit / 跨浏览器窗口的弹窗同步；权限事件仍为**单 circuit 侧信道**（`IPermissionEventSink`）。
- 不改 ACP 审批桥语义（`AcpPermissionBridge` 只用 `RequestAsync`；其 `ResolveDefaultChannel` 的 fresh-scope 行为为现状同病，本期不修）。
- 不实现审批历史持久化；权限事件不进 `ShouldPersistEvent`（`ExecutionJobService.cs:951-959`）。
- 不做 inline 卡片式审批（**已有** `PermissionMessageComponent`/`PermissionBlockRenderer` inline 展示路径不在本设计改动范围）；不做 `loopId` 关联。
- **不做 Gateway `PermissionMode` 运行中切换对在途请求生效**（见 §6.5 限制；新请求实时读取已满足 R4.1）。
- 不把超时配置化（沿用现状常量：注册表默认 5 分钟 / Gateway 120s）。

---

## 4. 架构总览

**关键原则（v2 教训）**：交互通道保留完整「询问」职责（自身预检 + 后处理），注册表只做**权威状态 + 策略 + 重评估 + 呈现时序**。这样记忆/白名单后处理与 Gateway 通道级策略都不离开原主链路。

```
┌──────────────────────────────────────────────────────────────────────────┐
│ IPermissionRequestRegistry（契约 Abstractions / 实现 Core，Singleton）      │
│  · 权威在途状态机（TCS 在 Register 时创建）                                 │
│  · ResolveEffect(sessionId, override) → Allow | Ask                      │
│  · PresentAsync(id,…) 呈现时序（原子 mark + 窗口期关闭）                     │
│  · WaitAsync(id, timeout?, ct) 统一完成（用户/重评估/取消/超时）              │
│  · 变更订阅（会话 Updated + 全局 AutoApproveAll）→ 重评估在途（异常隔离）      │
│  · Complete → 幂等完成 + 定向 presenter.DismissAsync（try/catch 隔离）       │
└────────▲───────────────────────────────────────▲─────────────────────────┘
         │ Register/Present/Wait/Remove          │ Register/Present/Wait/Remove
┌────────┴───────────────────────┐   ┌───────────┴──────────────────────────┐
│ SerializingPermissionChannel   │   │ GatewayPermissionChannel             │
│ （Core，交互通道 = 预检 + 后处理）│   │ （Gateway，交互通道 = 通道级策略+超时）│
│   └─ inner: BlazorPermission    │   │   └─ presenter 即自身                 │
│      Channel（Presenter）        │   │                                      │
└────────────────────────────────┘   └──────────────────────────────────────┘
                 ▲                                     ▲
                 └──────────────┬──────────────────────┘
                    ExecutionPermissionChannel（Core，每 execution）
                        策略分流：Allow / 委托交互通道 / Deny(无通道)
```

| 组件 | 层 | 角色 | 职责 |
|------|----|------|------|
| `IPermissionRequestRegistry` | Abstractions 契约 / Core 实现 | Registry | 权威在途状态、策略解析、变更订阅与重评估、呈现时序、完成幂等 |
| `IPermissionRequestPresenter` | Abstractions | 契约 | `PresentAsync`（发布呈现）/ `DismissAsync`（关闭 + 响应事件，恰好一次） |
| `ExecutionPermissionChannel` | Core | Channel | 每 execution 绑定 sessionId + override；策略分流与委托 |
| `SerializingPermissionChannel` | Core | Channel（保留） | 预检（硬边界/白名单/记忆）+ 询问（注册/呈现/等待）+ **后处理（记忆写入/白名单扩张）** |
| `BlazorPermissionChannel` | Hosting.Web | Presenter（+ 防御性 Channel） | UI 侧信道：发布请求事件、关闭并发布响应事件 |
| `GatewayPermissionChannel` | Gateway | Channel + Presenter | 通道级 `PermissionMode`/超时 + 客户端推送与 respond 回投 |
| `PermissionRequestQueue` | WebUI | Store | 单弹窗 FIFO 展示队列（纯类，可测） |
| `PermissionHost` | WebUI | — | 队列渲染；推进**只**由响应事件驱动 |

**依赖与分层（csproj 核实）**
- Core 引用 Abstractions + `Seeing.Session` + `Seeing.ConfigSchema` → 可承载注册表实现（需 `ISessionManager`/`ISessionEventPublisher`/`IOptionsMonitor`）。
- Hosting.Web 引用 Abstractions + Core。
- Gateway 包引用 `Seeing.Agent.Abstractions`、`Seeing.Session`、`Seeing.Agent.Llm`、`Seeing.Gateway*`，**无 Core 引用**（运行时从根 DI 取注册表实现）。

---

## 5. 契约变更（Abstractions）

`src/abstractions/Seeing.Agent.Abstractions/Permissions/`：

```csharp
// 1) PermissionRequest 增加预分配 id 与执行级策略覆盖
public class PermissionRequest
{
    public string? PermissionId { get; set; }                   // 注册表分配，呈现者/端点共用
    public SessionAutoApprove? RequestOverride { get; set; }    // 执行级覆盖，由 ExecutionPermissionChannel 注入
    // 其余字段不变；现有构造点均不传，靠上层补齐
}

// 2) 完成载荷（不可变；由注册表 TryResolve 的 result/reason 构造）
public sealed record PermissionResolved(
    string PermissionId,
    PermissionChannelAction Action,
    string Reason,
    string? ResourceToRemember = null);

// 3) 权威在途状态机
public interface IPermissionRequestRegistry
{
    /// <summary>实时解析策略：仅返回 Allow（自动批准）或 Ask（需交互）；无交互通道由通道层转 Deny。</summary>
    PermissionEffect ResolveEffect(string sessionId, SessionAutoApprove requestOverride);

    /// <summary>注册在途（采纳 request.PermissionId，缺省生成；override 取自 request 供重评估）。</summary>
    string Register(string sessionId, PermissionRequest request, IPermissionRequestPresenter presenter);

    /// <summary>呈现时序：未解析则原子 mark 后调用 presenter.PresentAsync；呈现期间被解析则补关闭。</summary>
    Task PresentAsync(string permissionId, PermissionRequest request, CancellationToken ct = default);

    /// <summary>等待完成；timeout 为 null 用注册表默认（5 分钟）；取消/超时以 Deny 走统一完成路径。</summary>
    Task<PermissionChannelResult> WaitAsync(string permissionId, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>幂等完成；成功则推送 Dismiss。sessionId 非空时校验归属；reason 进入 PermissionResolved。</summary>
    bool TryResolve(string permissionId, PermissionChannelResult result, string reason, string? sessionId = null);

    /// <summary>执行侧清理（后处理完成后调用）。</summary>
    void Remove(string permissionId);

    bool IsResolved(string permissionId);
    int PendingCount { get; }
    IReadOnlyList<PermissionPendingSnapshot> GetPending(string sessionId);
}

// 4) 呈现者
public interface IPermissionRequestPresenter
{
    /// <summary>发布呈现（请求事件 / 客户端推送）。仅呈现：不等待、不决策、不做通道级策略。</summary>
    Task PresentAsync(string permissionId, PermissionRequest request, CancellationToken ct = default);

    /// <summary>关闭呈现并发布响应事件（实现须以原子移除保证恰好一次）。</summary>
    Task DismissAsync(string permissionId, PermissionResolved resolution, CancellationToken ct = default);
}
```

- **复用既有枚举**：策略用 `PermissionEffect { Allow, Deny, Ask }`（`Permissions/PermissionKind.cs:36-46`），不新增枚举；`ResolveEffect` 契约只产 `Allow`/`Ask`。
- **不引入 `Resolved` 事件**：注册表按在途条目**定向调用** `presenter.DismissAsync`，天然异常隔离、无「订阅者抛异常阻断后续」问题。
- `ChatOptions.AutoApprove`/`PermissionChannel` **保留**；注释更新为「`AutoApprove` 为程序化冻结覆盖（子 Agent/嵌入方）；`PermissionChannel` 承载 circuit 级交互通道实例」。

---

## 6. 组件设计

### 6.1 `PermissionRequestRegistry`（Core，Singleton）

**Entry**：`{ SessionId, Request, Presenter, TCS, State(Pending/Presenting/Resolved), Resolution }`（`RequestOverride` 由 `Request.RequestOverride` 提供），`ConcurrentDictionary<string, Entry>`。

**`ResolveEffect(sessionId, override)`**（优先级明确）：

```
override == Enabled                     → Allow   （冻结覆盖：子 Agent / 嵌入方）
override == Disabled                    → Ask     （强制交互）
FollowGlobal（override == null 或 FollowGlobal）:
    session.AutoApprove == Enabled      → Allow
    session.AutoApprove == Disabled     → Ask
    全局 Permission.AutoApproveAll=true → Allow
    否则                                → Ask
```

> 只返回 `Allow`/`Ask`；`Deny` 由通道层产生（无可交互通道）。会话值取 `ISessionManager.Get(sessionId)?.AutoApprove`（实时）；全局取 `IOptionsMonitor<SeeingAgentOptions>.CurrentValue`（实时）。

**`Register(sessionId, request, presenter)`**：id = `request.PermissionId ?? Guid`；`TryAdd` 冲突 → 先以 Deny 完成旧条目，记 Warning；`PendingCount >= 32` → 立即以 `Denied("审批队列已满")` 完成并返回该 id（fail-closed，不呈现）。

**`PresentAsync(id, request, ct)`（原子化，消除 check-then-act 竞态）**：

```
entry = 查表；不存在或已 Resolved → return
if (!entry.TryBeginPresenting()) return          // 原子：已 Resolved 则不呈现（无闪窗）
try { await entry.Presenter.PresentAsync(id, request, ct); }
catch (ex) { Complete(entry, Denied("审批界面不可达"), ex.Message); return; }
if (entry.IsResolved) await SafeDismiss(entry, ct)   // 窗口期：呈现期间被解析 → 立即补关闭
```

**`WaitAsync(id, timeout, ct)`**：`Task.WhenAny(tcs.Task, Delay(timeout ?? 5min))`；取消/超时 → `Complete(Denied("操作已取消"/"权限请求超时"))`。调用方必须传 `ct`（取消语义依赖它）。

**`Complete`（唯一完成路径，幂等）**：`TrySetState(Resolved)` 成功者 → `tcs.TrySetResult` → 定向 `SafeDismiss`（`try/catch` 包裹，fire-and-forget，异常仅记日志）。

**变更订阅（单例一份）**：会话 `ISessionEventPublisher.Events` 按 `SessionEventType.Updated` + `SessionId` 过滤；全局 `IOptionsMonitor.OnChange` 比对 `AutoApproveAll` **值变化**。回调：

```
try {
    effect = ResolveEffect(entry.SessionId, entry.Request.RequestOverride ?? FollowGlobal)
    if (effect == Allow) Complete(Allowed("策略变更为自动批准"))
    // 否则（Ask）保持等待，不打扰用户
} catch (Exception ex) { 记 Error 日志 }
```

> **回调体整体 `try/catch` 是硬要求**：`SessionEventPublisher.Publish`（`SessionEventPublisher.cs:20-31`）同步扇出且无隔离，回调抛异常会反噬 `AddMessageAsync`/`SetTitleAsync` 等会话写入链。
> **已明确的限制**：Gateway 通道级 `PermissionMode` 的运行中切换**不影响已注册的在途请求**（仅新请求实时生效，见 §6.5）；会话三态与全局开关的运行中切换对在途请求生效。

### 6.2 `ExecutionPermissionChannel`（Core，每 execution，无状态、无需 Dispose）

```csharp
public sealed class ExecutionPermissionChannel(
    string sessionId,
    SessionAutoApprove requestOverride,
    IPermissionRequestRegistry registry,
    IPermissionChannel? interactiveChannel,
    ILogger? logger) : IPermissionChannel
{
    public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        request.SessionId ??= sessionId;                       // AcpTool 等不带 SessionId 的调用方兜底
        request.RequestOverride ??= requestOverride;           // 执行级覆盖随请求下传，供注册表重评估复用
        var effect = registry.ResolveEffect(sessionId, requestOverride);
        if (effect == PermissionEffect.Allow) return PermissionChannelResult.Allowed();
        if (effect == PermissionEffect.Deny || interactiveChannel is null)
            return PermissionChannelResult.Denied("未配置交互式审批通道");   // 防御：契约只产 Allow/Ask
        return await interactiveChannel.RequestAsync(request, ct);   // 全权委托：交互通道负责预检/呈现/等待/后处理
    }
}
```

- **委托而非自行注册**：v3 的关键修正——记忆写入、白名单扩张、Gateway 通道级策略都在交互通道的 `RequestAsync` 内完成，主链路语义与现状等价。
- 无状态 → 无需 `IDisposable`、无需在 `ProcessExecutionAsync` 增加 Dispose。
- `ExecutionJobService.BuildExecutionContextAsync` 用本类替换 `ResolvePermissionChannel` 三分支；`ResolvePermissionChannel` 删除（测试改造见 §11）。

### 6.3 `SerializingPermissionChannel`（Core，交互通道；保留职责与公开语义）

构造新增 `IPermissionRequestRegistry`（**破坏性签名变更**，见 §12）。

`RequestAsync`（在**现有 :38-112 结构**上做最小改造）：

```
1. 预检（注册前，保持现状 :40-76 语义）：
   Restrict && filesystem.* && 无 SessionId → Denied
   filesystem.* 在工作区/白名单内 → Allowed + ExpandWhitelistOnAllow
   IPermissionMemory 命中 → Allowed / Denied("会话记忆拒绝") + Allow 时 ExpandWhitelistOnAllow
2. presenter = _inner as IPermissionRequestPresenter
   非 Presenter（第三方/测试替身）→ 直连 _inner.RequestAsync（旧语义，无重评估；记 Debug）
3. 询问：
   id = registry.Register(request.SessionId!, request, presenter)
   try {
       await registry.PresentAsync(id, request, ct)
       result = await registry.WaitAsync(id, null, ct)
       // 4. 后处理（保留现状 :85-104 语义）：
       result.ResourceToRemember 非空且有 SessionId → 写 IPermissionMemory（Allow/Deny）
       result.Action == Allow → ExpandWhitelistOnAllow(request)
       return result（剥离 ResourceToRemember，与现状 :96-100 一致）
   } finally { registry.Remove(id) }
```

- **删除 `SemaphoreSlim _gate`**（:22、:79-111）：串行化改由 UI 队列承担；闸门是 R3 挂起传染来源。已确认闸门**不承担安全职责**（硬边界/记忆预检在闸门之外，白名单 `Add` 幂等），删除无安全回归；但需**删除/改写断言串行的既有测试**（见 §11）。
- `IPermissionRequestPresenter` 由 `_inner`（Blazor）实现；本类不实现 Presenter 接口。
- 后处理安全性：重评估 `Allowed` 的 `ResourceToRemember=null` → 不写记忆，仅幂等扩白名单；取消/超时 `Denied` → 两者皆不做。

### 6.4 `BlazorPermissionChannel`（Hosting.Web，Presenter + 防御性 Channel）

- `IPermissionRequestPresenter`：
  - `PresentAsync(id, request, ct)` → `_presented[id] = 0` → 经 `IPermissionEventSink` 发布 `PermissionRequestEvent`（`PermissionId=id`，`LoopId` 留空——非目标）；发布失败/异常 → 抛出，由注册表兜底为 `Denied("审批界面不可达")`。
  - `DismissAsync(id, resolution, ct)` → **原子 `_presented.TryRemove(id, out _)`**，成功者才发布 `PermissionResponseEvent`（`Decision` 由 `resolution.Action` 映射，`Reason` 透传）→ 保证恰好一次（`Complete` 推送与 `PresentAsync` 复查补推送可能并发到达）；失败（未呈现/已关闭）为 no-op。
- `IPermissionChannel.RequestAsync`：**非 WebUI 主链路的防御实现**（WebUI DI 下 `Serializing` 判定 `_inner` 为 Presenter 后不会调用它），仅服务 DI 直解析方/ACP 回退；实现走「Register → PresentAsync → WaitAsync → Remove」（本类无预检/后处理，逻辑最薄）。
- `RespondToPermission(id, action, resourceToRemember)`（UI 调用）→ `registry.TryResolve(id, ...)`。
- `PendingCount`：保持**本 circuit 已呈现且未关闭**的计数（不委托全局注册表，避免语义漂移）。
- 实现 `IDisposable`：清理 `_presented`；disposed 后 `PresentAsync`/`DismissAsync` 静默 no-op（退订竞态无害化）。
- `IPermissionEventSink.PublishAsync` 参数放宽为 `IMessageEvent`（请求/响应复用同一侧信道）。**Hosting.Web 公开接口破坏性变更**；影响面已核实：唯一实现 `EventStreamHandler`（`samples/.../Services/EventStreamHandler.cs:59,190`，内部 `ProcessEventAsync(IMessageEvent)` 已支持响应分支 `:237-239`），无测试替身；`Program.cs:109-111` 的 Scoped 注册语义不变。

### 6.5 `GatewayPermissionChannel`（Gateway，Channel + Presenter）

| 项 | 变更 |
|----|------|
| 依赖 | 构造改 `(IOptions<GatewayOptions> options, IPermissionRequestRegistry registry, ILogger?)`；**注册表必须由 `_rootServices.GetRequiredService<IPermissionRequestRegistry>()` 显式注入**（`GatewayHost` 子容器不含 `AddSeeingCore`） |
| 配置实时 | `PermissionMode`/`PermissionTimeoutSeconds` **每次读取** `_options.Value`（`GatewayOptionsMonitor.cs:18` 已实时读 store）；`PermissionTimeoutSeconds` 默认 120 |
| 通道级策略 | `RequestAsync` 内**注册前**读 `_options.Value.PermissionMode`：`auto_approve` → 直接 `Allowed()`（不注册、不呈现） |
| 询问流 | `Register` → `PresentAsync` → `WaitAsync(id, TimeSpan.FromSeconds(PermissionTimeoutSeconds), ct)` → `Remove`（保留 Gateway 专属超时；**必须传 ct**） |
| 呈现 | `PresentAsync`：向该 session 的**sink 集合**推送 `MapPendingPermission` |
| sink 集合 | `RegisterSink(sessionId, sink)/UnregisterSink(...)`（`ConcurrentDictionary<string, ConcurrentDictionary<...>>`）取代 `AsyncLocal`；删除 `PermissionRunContext` 与 `SetRunContext` |
| 关闭 | `DismissAsync` → 向 sink 集合推送 `PermissionResponseEvent`（`ChatEventMapper` 需补映射） |
| 决策回投 | `Respond(sessionId, id, allow, reason)` → `registry.TryResolve(id, ..., sessionId)`（**校验 sessionId 归属**） |
| 查询 | `GetPendingPermissions(sessionId)` 改为投影 `registry.GetPending(sessionId)`（端点路径不变：`GET /api/gateway/permissions/pending`、`POST /api/gateway/permissions/{id}/respond`，`GatewayEndpoints.cs:31-32`） |

- 叠加规则：`ResolveEffect` 的会话三态 Enabled/Disabled 优先；FollowGlobal 且全局 `AutoApproveAll=true` → 直接 Allow（不经 Gateway 通道）；仅当解析为 `Ask` 时才由 Gateway 的 `auto_approve` 放行。
- **限制（已明确）**：`PermissionMode` 运行中切换只对**新请求**生效（`_options.Value` 实时读取 → 修复 R4.1 的「必须重启」）；对**已注册在途**请求不生效（注册表重评估只覆盖会话三态与全局开关）。后续若需在途生效，可让 Gateway 订阅自身配置变更后调用注册表重评估入口（本期不做）。
- WS/端点解析：子容器继续 `AddSingleton(permissionChannel)` 供端点与 `GatewayWebSocketHandler` 解析同一实例；`GatewayWebSocketHandler` 注入的具体 `GatewayOptions`（WS 路径/保活等静态字段）保持现状快照。

### 6.6 `GatewayOrchestratorV2` / `GatewayHost` / `GatewayServer`（Gateway）

- `GatewayOrchestratorV2.BuildChatOptions`（:305-315）补 `PermissionChannel = _permissionChannel`（修 R4.2）；`AutoApprove` 不传（FollowGlobal，交实时解析）。
- 流式订阅开始/结束（:158-163、:212-217 与 `finally`）改 `RegisterSink/UnregisterSink`，删 `SetRunContext`（修 R4.3）。
- `GatewayServer.cs:38,51-54`：不再固化 `_options.Value` 传给 `GatewayHost`，改传 `IOptions<GatewayOptions>`（`GatewayOptionsMonitor` 实时实现）。
- `GatewayHost.cs:64`：`new GatewayPermissionChannel(options, registry, logger)`，`registry` 取自 `_rootServices`；子容器同步注册该实例供端点/WS 处理器解析。
- `Core/ChatEventMapper.cs`（文件名 `GatewayEventMapper.cs`，类名 `ChatEventMapper`，:104-122 仅映射 Request）补 `PermissionResponseEvent` 映射，使客户端可关闭本地 pending 展示。

### 6.7 三态写入单一路径（primitives）

- `ISessionManager` 新增 `Task SetAutoApproveAsync(string sessionId, SessionAutoApprove value, CancellationToken ct = default);`，实现**对齐 `SetTitleAsync`（:586-621）**：改字段 → `HookPoints.Updated` → 发布 `SessionEvent{Type=Updated}` → `SaveAsync`。
- `SessionWindow.razor:1402-1410` 改为调用 `SetAutoApproveAsync`；`PromptInput` 无需改动。
- `SessionWindow.razor:1060` 不再复制三态进 `ChatOptions.AutoApprove`（传默认 `FollowGlobal`）。
- `TaskTool.cs:171-178` 保持 `AutoApprove = Enabled`（冻结覆盖）。
- `AgentLoopSchedulerHostedService.cs:44` 未传 AutoApprove → 改为跟随实时策略（期望行为，回归测试覆盖）。

### 6.8 审批 UI（WebUI）

`PermissionRequestQueue`（新纯类，`samples/Seeing.Agent.WebUI/Services/`）：

| 成员 | 语义 |
|------|------|
| `Enqueue(evt)` | 无 active → 置 active；否则入队；达上限 32 → 返回 `Overflow`（调用方 `RespondToPermission(id, Deny)`） |
| `Active` / `PendingCount` | 当前展示项 / 队列长度 |
| `TryRemove(id)` | 命中 active → 移除并置 `NeedsAdvance`；命中队列 → 移除 |
| `Advance()` | 仅 `NeedsAdvance` 时推进下一项（幂等，防双推进） |

`PermissionHost.razor`：

- **入队/响应均包 `InvokeAsync`**（请求来自工具线程，响应来自重评估/超时线程）。
- **推进唯一来源**：`OnPermissionResponse` → `TryRemove(evt.PermissionId)` → `Advance()`。`OnDecision` 只调 `RespondToPermission`（→ 注册表完成 → Dismiss → 响应事件 → host 推进），**不自行推进**。
- 弹窗**不关闭重开**：active 变更时切换内容并在**使用点**（`PermissionHost` 渲染 `PermissionModal` 处）以 `@key="@Active.PermissionId"` 重建详情区，`_visible` 保持 true；队列清空才置 false。
- 移除 `PermissionModal` 的 `[Parameter] OnVisibleChange` 及 `Decide()` 中的调用（唯一使用方为 `PermissionHost`）；拒绝一律走显式按钮/`OnCancel`。
- 兜底：响应事件丢失（如 sink 异常）时用户仍可点按钮（`RespondToPermission` 幂等，`TryResolve` 返回 false 则日志提示）。

---

## 7. 关键时序

### 7.1 单请求（交互式，验证零语义回归）

```
Tool → ExecutionPermissionChannel.RequestAsync
  ResolveEffect=Ask → request.RequestOverride 注入 → 委托 Serializing.RequestAsync
  → 预检（硬边界/白名单/记忆）未命中
  → registry.Register(id, presenter=Blazor) → registry.PresentAsync
     → 原子 TryBeginPresenting → Blazor.PresentAsync → 请求事件（_presented += id）
  → UI 入队 → 弹窗
用户「始终允许」→ Blazor.RespondToPermission → registry.TryResolve(Allow, remember=resource)
  → Complete: TCS + SafeDismiss → Blazor.DismissAsync（原子 TryRemove）→ 响应事件 → UI TryRemove+Advance
  → registry.WaitAsync 返回 Allow
  → Serializing 后处理：写 IPermissionMemory(resource) + ExpandWhitelistOnAllow
  → finally registry.Remove(id) → 返回工具
（记忆/白名单写入路径与现状 :85-104 一致；`WorkspacePathGate` 依赖的白名单扩张得以恢复）
```

### 7.2 并行两请求（S3 修复验证）

```
A、B 并行 → 各自 Register（天然并发，无实例闸门）
A 先呈现（UI active=A、B 入队）
允许 A → TryResolve(A) → Dismiss → 响应事件 → UI TryRemove(A)+Advance → active=B（内容切换，无关闭重开）
允许 B → 同上
（无 false→true 抖动；无共享 _request 误响应；无闸门排队）
```

### 7.3 模式变更重评估（S1/S2 修复验证）

```
a) 执行中切三态「自动批准」
   UI → SetAutoApproveAsync → SessionEvent(Updated)
   → 注册表回调：ResolveEffect=Allow → Complete(Allowed("策略变更为自动批准"))
   → SafeDismiss → 响应事件 → UI 关闭弹窗
   → 该执行后续工具：ResolveEffect=Allow → 直接放行（不注册、不弹窗）
b) 执行中打开全局开关并保存
   SaveSections → OnConfigChanged → OptionsMonitor.OnChange（值变化）→ 同上
c) 呈现窗口期被解析（TOCTOU 闭合）
   PresentAsync 内 TryBeginPresenting 成功 → 解析发生（Complete 推送 Dismiss，但 id 尚未入 _presented → no-op）
   → PresentAsync 记录 id 并发布请求 → 注册表复查 IsResolved → SafeDismiss → 弹窗立即关闭、无残留
d) Gateway 运行中把 PermissionMode 改为 auto_approve
   仅对新请求立即生效（RequestAsync 实时读 _options.Value）；已在途请求不受影响（明确限制，见 §6.5）
```

### 7.4 取消执行

任一交互通道路径：`WaitAsync(id, timeout, ct)` 的 ct 回调 → `Complete(Deny("操作已取消"))` → SafeDismiss → UI 关闭 → `finally Remove`。

### 7.5 Gateway pending 推送

```
WS submit → ExecutionJobService（ChatOptions.PermissionChannel=GatewayPermissionChannel）
  → ExecutionPermissionChannel.ResolveEffect=Ask → 委托 Gateway.RequestAsync
  → 读 PermissionMode=interactive → Register → PresentAsync
  → sink 集合推送 MapPendingPermission（SSE + WS PushEvent(sessionId)）
客户端 respond（POST /api/gateway/permissions/{id}/respond 或 WS PermissionRespond）
  → registry.TryResolve(id, ..., sessionId 校验) → Dismiss → 推送 PermissionResponseEvent（ChatEventMapper 补映射）
```

---

## 8. 并发与生命周期

| 关注点 | 设计 |
|--------|------|
| 权威状态 | 仅注册表持有 TCS 与 State；呈现者/UI 均为投影；`presenter` 在 `Register` 时绑定（同 id 唯一） |
| 呈现竞态 | `TryBeginPresenting` 原子转换；呈现后复查 `IsResolved` 补关闭（§7.3c） |
| 完成幂等 | `TrySetState(Resolved)` 守护；`Complete` 唯一路径；`DismissAsync` 以原子 `TryRemove` 保证响应事件恰好一次 |
| 回调隔离 | 重评估回调整体 `try/catch`；`SafeDismiss` 独立 `try/catch` |
| 订阅 | 注册表单例一份（会话事件 + 全局选项），进程生命周期；无 per-execution 订阅/Dispose |
| 执行通道 | `ExecutionPermissionChannel` 无状态 |
| 呈现者生命周期 | `BlazorPermissionChannel`（Scoped）Dispose 清 `_presented`，后续调用静默 no-op |
| UI 线程 | 请求与响应路径均 `InvokeAsync` |
| 上限 | 注册表 32 / UI 队列 32，均 fail-closed |
| 超时 | 注册表默认 5 分钟；Gateway 传 `PermissionTimeoutSeconds`（120s），保留通道专属语义 |
| sessionId 校验 | `TryResolve` 支持归属校验（Gateway Respond 使用） |
| 同 session 多 circuit | 请求 id 全局唯一、presenter 注册时绑定 → 不会一请求两呈现；非阻断，无需额外约束 |

---

## 9. 错误处理与边界

1. **无交互通道**（headless/embed/CLI）：`interactiveChannel == null` → `Denied("未配置交互式审批通道")`（等价现状 DenyAll）。
2. **调用方通道非 Presenter**（第三方/嵌入方/`Mock<IPermissionChannel>`）：`Serializing` 直连 `_inner.RequestAsync`（旧语义可用，无重评估）；记 Debug。
3. **呈现失败 / circuit 断开**：注册表捕获异常 → `Complete(Denied("审批界面不可达"))`，不等待 5 分钟。
4. **id 冲突 / 队列溢出**：fail-closed（§6.1）。
5. **重复决策**：`TryResolve` 幂等；UI 侧 `TryRemove`/`Advance` 幂等；`DismissAsync` 恰好一次。
6. **ACP**：正常路径 `ctx.PermissionChannel`（`AcpSessionRunner.cs:80`）在 v3 中为 `ExecutionPermissionChannel` → 走注册表与重评估；`AcpTool.cs:91-107` 请求不带 `SessionId` → 执行通道兜底。**未覆盖**：`AcpPermissionBridge.ResolveDefaultChannel`（:65-69）用 `_scopeFactory.CreateScope()` 解析 Scoped `Serializing(Blazor)`，得到新作用域实例且 `EventStreamHandler` 无 UI 订阅者 → 无弹窗、等超时；**现状同病，非本设计引入**，本期不修（记录）。
7. **`DynamicPermissionChannel`**：作为 Core 默认 `IPermissionChannel` 的内层被 `Serializing` 包裹（`ServiceCollectionExtensions.cs:586-594`），Ask 时抛 `PermissionRequiredException`（现状不变），保留。
8. **`DefaultPermissionChannel.AutoApproveInstance`**：删除 `ResolvePermissionChannel` 后生产侧不再引用（自动批准改由注册表 `ResolveEffect=Allow` 短路）；类型为 Core 公开 API，**保留不删**，标记为后续清理候选。
9. **Gateway 无 sink 订阅者**：pending 在注册表，`GET /pending` 可查；`RegisterSink` 后新请求可达。
10. **配置节登记**：`Gateway` 节已注册（`GatewayServiceCollectionExtensions.cs:31-34`）；`Permission` 为脊柱属性，`UpdateCache:721-727` 反射同步已生效。
11. **`AddLlmProviders` 分支**（`ServiceCollectionExtensions.cs:309-353`）不经过 `RegisterCoreServices` → 若宿主**只**调该分支则无注册表；若其后仍调 `AddSeeingCore`（WebUI/CLI 现状）则可用。仅 `AddSeeingCore` 路径受支持（文档注明）。

---

## 10. 依赖方向与规范合规

| 规则 | 合规性 |
|------|--------|
| 契约只进 Abstractions | 注册表/呈现者契约 + `PermissionRequest` 新增字段 + `PermissionResolved` 均在 Abstractions |
| Gateway 不引用 Core | csproj 事实：引用 `Seeing.Agent.Abstractions`、`Seeing.Session`、`Seeing.Agent.Llm`、`Seeing.Gateway*`，**无 Core 引用**；运行时从根 DI 取注册表。可新增 csproj 可见性测试固化（现状 `HostingGatewayNoSchedulerTests.cs:46-47` 断言的是「Hosting.Gateway 不引用 Agent.Gateway」，与本条无关） |
| 脊柱零工具不变量 | 未涉及工具注册 |
| schema 单点 | 未触碰 |
| 热重载经 OptionsMonitor | 注册表读 `CurrentValue` + `OnChange`；Gateway 通道实时读，符合 `03 §5` |
| 单一写入路径 | 会话三态经 `SetAutoApproveAsync` |
| 角色命名 | 新增 `Presenter` 角色：**实施时同步补入 `docs/architecture/04-development-standards.md` 角色表** |
| 去 AsyncLocal 跨任务传递 | 显式 sink 集合取代 `PermissionRunContext` |

---

## 11. 测试策略

> 先写失败测试；运行遵循 AGENTS.md（MTP 发现不到测试，用 `dotnet vstest <程序集> --TestCaseFilter:...`）。

**`tests/spine/Seeing.Agent.Tests`（Core 实现）**
1. `PermissionRequestRegistryTests`
   - `ResolveEffect` 优先级：override(Enabled/Disabled) > 会话三态 > 全局；会话/全局变更后立即变化
   - `TryResolve` 早于 `PresentAsync` → `WaitAsync` 立即返回且**不呈现**（`TryBeginPresenting` 返回 false）
   - 呈现期间被解析 → `PresentAsync` 返回前补 `DismissAsync`（TOCTOU 回归，§7.3c）
   - `TryResolve` 幂等、`Complete` 唯一、已呈现 id 的 `DismissAsync` 恰好一次
   - 会话 `Updated` / 全局值变化触发重评估；无关变更不触发
   - **override 优先**：`Request.RequestOverride=Disabled` + 会话 Enabled → 重评估**不得**自动放行
   - **回调异常隔离**：`ResolveEffect` 抛异常时不外溢到 `SessionEventPublisher.Publish` 调用链（抛异常的假 `ISessionManager`）
   - 溢出 fail-closed；id 冲突；`WaitAsync` 取消与超时 → Deny；`sessionId` 归属校验
2. `ExecutionPermissionChannelTests`：Allow/Ask 分流；Ask → 注入 `RequestOverride` 并委托交互通道；`interactiveChannel == null` → Deny；`request.SessionId` 兜底
3. `SerializingPermissionChannelTests`（扩展 + **改写**）
   - **必须删除/改写** `RequestAsync_ShouldSerializeConcurrentAsks`（:15-48，断言 `maxConcurrent==1`）——闸门删除后必失败；改为「并发两请求均不阻塞、各自完成」
   - **零回归**：记忆写入（`ResourceToRemember`）、白名单扩张、硬边界 Deny、工作区/白名单自动 Allow —— 与现状 :40-104 逐条等价（含「用户允许后 `IWorkspaceWhitelist` 已扩张」断言）
   - 非 Presenter inner → 直连旧语义
4. `App/ExecutionJobServicePermissionChannelTests.cs`（改造）：现有 6 个 `ResolvePermissionChannel` 断言（:23-84）迁移到 `ExecutionPermissionChannel`

**`tests/primitives/Seeing.Session.Tests`**
5. `SessionManagerTests` 扩展：`SetAutoApproveAsync` 改值 + 发布 `Updated` + 落盘（对照既有 `Updated` 断言 `:355`）

**`tests/apps/Seeing.Agent.WebUI.Tests`**
6. `BlazorPermissionChannelTests`：`PresentAsync` 发布请求事件；`DismissAsync` 并发调用**恰好发布一次**响应事件（原子 `TryRemove`）；对未呈现 id no-op；`RespondToPermission` → `TryResolve`；Dispose 后静默；`PendingCount` 为 circuit 语义
7. `PermissionRequestQueueTests`：FIFO、active/队列命中、溢出、`Advance` 幂等（防双推进）

**`tests/gateway/Seeing.Gateway.Tests`（已确认引用 `Seeing.Agent.Gateway`）**
8. `GatewayPermissionChannelTests`：`PermissionMode=auto_approve` → 注册前放行（R4.1 新请求实时）；运行中变更对**新请求**生效、对在途不生效（限制固化）；`RegisterSink` 多订阅者均收到 pending、`UnregisterSink` 后不再收（R4.3）；Gateway 超时用 `PermissionTimeoutSeconds`；`Respond` 的 sessionId 校验；`GetPendingPermissions` 投影注册表
9. `ChatEventMapperTests`（新增；Agent.Gateway 的 `ChatEventMapper` 原先无测试，注意与协议层 `Seeing.Gateway.Mapping.GatewayEventMapperTests` 区分）：`PermissionResponseEvent` 映射

**编译破坏同步（必改，否则构建失败）**
10. `tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs:125` 的 `FakeSessionManager : ISessionManager` 需实现 `SetAutoApproveAsync`
11. `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:589` 的 Core `IPermissionChannel` 工厂需注入注册表
12. `tests/spine/Seeing.Agent.Tests/Core/SerializingPermissionChannelTests.cs:39,64,83,104,149` 构造需补注册表参数

**回归确认（不改动，仅验证）**
13. `tests/apps/.../Services/EventStreamHandlerTests.cs`（现用 `ProcessEventAsync`，预期不受 `PublishAsync` 签名变更影响）
14. `samples/.../Services/SessionWindowTimelineSync.cs:110-111`（权限请求/响应事件消费）
15. `samples/.../Pages/SecurityPage.razor:81`（`BlazorPermissionChannel.PendingCount`，保持 circuit 语义）
16. `samples/.../Components/Messaging/PermissionMessageComponent.razor:46`、`Rendering/Renderers/PermissionBlockRenderer.cs:27`、`Models/Messaging/ContentBlock.cs:234/375`（**既有 inline 权限展示路径**，非本设计范围）
17. 通道透传链仅验证指向 `ExecutionPermissionChannel`：`Core/Models/DefaultExecutionContext.cs:42`、`Hosting/Execution/ChatExecutionContext.cs:40`、`Abstractions/Agents/AgentContext.cs:32`、`Abstractions/Tools/ITool.cs:29`、`Models/ChatOptions.cs:49`

---

## 12. 文件级改动清单

**primitives/Seeing.Session**
- `Core/ISessionManager.cs`：+`SetAutoApproveAsync`
- `Management/SessionManager.cs`：+实现（对齐 `SetTitleAsync`）

**abstractions/Seeing.Agent.Abstractions**
- `Permissions/IPermissionChannel.cs`：`PermissionRequest.PermissionId` + `RequestOverride`
- `Permissions/IPermissionRequestRegistry.cs`（新）：`PermissionResolved` + 注册表契约
- `Permissions/IPermissionRequestPresenter.cs`（新）
- `Models/ChatOptions.cs`：注释更新（无语义变更）

**spine/Seeing.Agent.Core**
- `Core/Permission/PermissionRequestRegistry.cs`（新，Singleton）
- `Core/Permission/ExecutionPermissionChannel.cs`（新）
- `Core/Permission/SerializingPermissionChannel.cs`：删除 `_gate`；预检前置；注册/呈现/等待/后处理；+注册表依赖（**构造签名破坏性变更**）
- `Extensions/ServiceCollectionExtensions.cs`：注册 `IPermissionRequestRegistry`；`:589` 工厂注入注册表

**hosting/Seeing.Agent.Hosting**
- `Execution/ExecutionJobService.cs`：用 `ExecutionPermissionChannel`；删除 `ResolvePermissionChannel`

**hosting/Seeing.Agent.Hosting.Web**
- `Permissions/IPermissionEventSink.cs`：`PublishAsync(IMessageEvent, ct)`
- `Permissions/BlazorPermissionChannel.cs`：实现 Presenter（`PresentAsync`/`DismissAsync` 原子）；`RespondToPermission`→`TryResolve`；`_presented` + `PendingCount`；IDisposable
- `WebHostingServiceCollectionExtensions.cs`：工厂注入注册表

**samples/Seeing.Agent.WebUI**
- `Services/PermissionRequestQueue.cs`（新）
- `Services/EventStreamHandler.cs`：`PublishAsync` 签名放宽（`_pendingPermissions` 仅内部登记、无读取方，**保留不删**以缩小改动面）
- `Program.cs`：无改动（Scoped `EventStreamHandler("")` + `IPermissionEventSink` 映射保持「全局权限事件总线」语义，:109-111）
- `Components/PermissionHost.razor`：队列化、响应事件驱动推进、`InvokeAsync`、使用点 `@key="@Active.PermissionId"`
- `Components/PermissionModal.razor`：移除 `OnVisibleChange` 参数与 `Decide()` 中的调用
- `Components/SessionWindow.razor`：`SetAutoApproveAsync`；不再复制三态进 `ChatOptions`

**gateway/Seeing.Agent.Gateway**
- `Permission/GatewayPermissionChannel.cs`：`IOptions` 实时读 + 注册表（root 注入）+ Presenter + sink 集合 + sessionId 校验
- `Core/GatewayOrchestratorV2.cs`：`BuildChatOptions` 补通道；sink 注册/注销；删 `SetRunContext`
- `Core/IGatewayEventSink.cs`：删 `PermissionRunContext`
- `Core/GatewayEventMapper.cs`（类 `ChatEventMapper`）：补 `PermissionResponseEvent` 映射
- `Hosting/GatewayHost.cs` / `Hosting/GatewayServer.cs`：实时 Options + 注册表注入
- `Endpoints/GatewayWebSocketHandler.cs`：`PermissionRespond` 改走注册表（`Respond` 语义保持）；其注入的具体 `GatewayOptions`（静态字段）保持现状

**docs**
- `docs/architecture/04-development-standards.md`：角色表补 `Presenter`

---

## 13. 修订记录

### 第四轮（v3 → v3.1，第三轮评审「有条件通过」后）

| 来源 | 问题 | 处置 |
|------|------|------|
| 条件 [阻断] N2 | 「Gateway `PermissionMode` 运行中切换对在途请求生效」无触发源（`GatewayOptions` 变更不驱动注册表重评估） | **删除该声明**（评审建议 B）：仅保留「新请求实时读取」；§6.5/§7.3d 明确限制；不引入 Gateway 配置订阅（YAGNI） |
| 非阻断 | 重评估回调未异常隔离，而 `SessionEventPublisher.Publish`（:20-31）无 try/catch，会反噬会话写入链 | §6.1 强制回调体 `try/catch` + Error 日志；§11.1 加测试 |
| 非阻断 | Blazor `DismissAsync`「查集合→发布→移除」非原子 → 可能重复响应事件 | 改原子 `_presented.TryRemove` 先行；恰好一次；§11.6 加并发测试 |
| 非阻断 | 删闸门使 `RequestAsync_ShouldSerializeConcurrentAsks`（`SerializingPermissionChannelTests.cs:15-48`）必失败 | §11.3 明确要求删除/改写该断言 |
| 非阻断 | Blazor `IPermissionChannel.RequestAsync` 在 WebUI DI 下不可达、职责含糊 | 标注为「非主链路防御实现（DI 直解析/ACP 回退）」 |
| 非阻断 | 重评估无法复现 `Disabled` 强制交互（Entry 未存 override） | `PermissionRequest.RequestOverride` + 执行通道注入；§11.1 加「override 优先」测试 |
| 非阻断 | `ResolveEffect` 返回域与 `Deny` 处理含糊 | 契约明确「只产 Allow/Ask」；§6.2 增加 Deny 防御分支 |
| 非阻断 | Gateway `WaitAsync` 未传 `ct` → 取消语义缺失 | §6.5 修正为 `WaitAsync(id, timeout, ct)` |
| 非阻断 | §9.6 ACP 回退表述过于乐观（fresh scope、无 UI 订阅者） | 更正为「现状同病、本期不修（记录）」 |
| 非阻断 | `HostingGatewayNoSchedulerTests` 被误引为「Gateway 不引用 Core」不变量 | §4/§10 更正为 csproj 事实 + 可选可见性测试；Gateway 依赖清单补 `Seeing.Agent.Llm` |
| 非阻断 | `_pendingPermissions`「供时间线展示」表述错误；`@key` 定位错误；`§2 R3` 与「进程级」并置 | §12/§6.8/§2 R3 逐条更正 |
| 非阻断 | §11/§12 遗漏回归面 | 补「回归确认」清单（`EventStreamHandlerTests`、`SessionWindowTimelineSync`、`SecurityPage`、inline 展示路径、通道透传链、`AutoApproveInstance`、`AddLlmProviders`） |

### 第三轮（v2 → v3，第二轮评审「不通过」后）

| 来源 | 问题 | 处置 |
|------|------|------|
| [阻断] N1 | v2 把「等待」移入 flow，切断 `SerializingPermissionChannel` 的记忆写入（`:85-100`）与白名单扩张（`:103-104`）→「始终允许」失效，且 `WorkspacePathGate` 仍拒绝已批准越界路径 | **恢复交互通道自主询问**：`ExecutionPermissionChannel` 对 Ask 全权委托交互通道 `RequestAsync`；注册表只管状态/策略/重评估/呈现时序 |
| [阻断] N2 | v2 主链路走 `PresentAsync`，Gateway `auto_approve` 短路与 120s 超时失效 | 交互通道自持询问流 + Gateway 注册前读 `PermissionMode` + `WaitAsync(id, timeout, ct)` |
| [阻断] B1′ | v2 `IsResolved` 前置检查为非原子 check-then-act | `TryBeginPresenting` 原子转换 + 呈现后复查补 `DismissAsync`（§7.3c） |
| [阻断/GAP] B4′ | v2 未定义 Gateway 子容器如何取得注册表 | `_rootServices.GetRequiredService<IPermissionRequestRegistry>()` 显式注入 |
| [非阻断] | `Resolved` 事件链异常阻断；`PendingCount` 语义漂移；`Presenter` 未入角色表；`PreResolve` 参与重评估却无触发源等 | 定向 `DismissAsync` 取代事件；`PendingCount` 保持 circuit 语义；补 `04-development-standards.md`；`TryPreResolve` 直接移除 |
| 事实 | `GatewayServer.cs:51-54` 才是构造 `GatewayHost` 处；映射类名 `ChatEventMapper`；`ChatEventMapper` 无既有测试 | 更正；测试放 `tests/gateway/Seeing.Gateway.Tests` |

### 第二轮（v1 → v2，第一轮评审后）

- 结构性重写：注册表单一权威状态取代「路由器 + 通道挂起 + UI 队列」三层叠加（修 R6）。
- 删除 `IPermissionChannelResponder`（裸名越界）与 `PermissionPolicyMode`（复用 `PermissionEffect`）；`loopId` 移出范围；Gateway sink 集合；订阅收敛为单例；Gateway 叠加规则重写。
- 证据修正 E1（`SetModelAsync` 不发 `SessionEvent`）、E2（`IPermissionEventSink` 为破坏性变更）、E3（`UpdateCache:713-733`）、E4（S2 表述收窄）。

**遗留（非阻断）**：跨窗口同步、审批历史持久化、超时配置化、inline 卡片审批、ACP fresh-scope 回退、Gateway `PermissionMode` 在途切换均为后续演进；`Seeing.Agent.Core`/`Hosting.Web` 公开类型签名变更需在 release notes 记录。
