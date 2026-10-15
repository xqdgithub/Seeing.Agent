# 权限授权子系统设计（架构优先 · 绿毯重构 · 无兼容约束）v4.2

**日期**：2026-09-17
**分支**：feature/modular-architecture
**状态**：**设计定稿（五轮双评审收敛，最终判定「有条件进入实施」的阻断项已修正）**
**取代**：`2026-09-17-permission-approval-architecture-design.md`（旧通道栈迭代版，已废弃）
**v4 修订**：① 落实第三轮双评审的收敛项；② **用户明确「无需兼容」** → 删除全部兼容性妥协（见 §2.2/§13）

**已读依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`05-extension-guide.md`、`06-compliance-audit.md`
- `docs/superpowers/specs/2026-08-03-tool-permission-unification-design.md`（两层权限模型 · 权威）
- `docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md`（端口-适配器 / 后缀即职责 · 权威）
- `docs/superpowers/specs/2026-08-04-message-timeline-rendering-design.md`（Timeline · 权威）
- `docs/gateway/README.md`

---

## 1. 问题与动机

### 1.1 症状

| # | 症状 | 精确归因 |
|---|------|---------|
| S1 | 会话内三态切「自动批准」不生效 | 策略在 execution 启动时快照（`ExecutionJobService.cs:773-775`）+ 三态写入不发布 `SessionEvent`（`SessionWindow.razor:1402-1410` → `SaveAsync:234-265`） |
| S2 | 改全局开关后不生效/需重启 | **复合**：全局值每 execution 实时读（`:921`），但（a）规则结果缓存 5 分钟（`PermissionService.cs:67-73/107-110`）致陈旧判断；（b）既有挂起请求占住串行闸门，新消息无法推进 |
| S3 | 并行两工具：第二个弹窗闪现即关并永久等待 | 单槽 UI（`PermissionHost.razor:15-17`）+ Modal 参数抖动（`PermissionModal.razor:5/219-227`）+ 串行闸门被占（`SerializingPermissionChannel.cs:22/79-111`、`BlazorPermissionChannel.cs:47` 5 分钟等待） |

### 1.2 结构性缺陷

| 缺陷 | 证据 |
|------|------|
| **A 在途状态三处重复**（UI 持权威） | `BlazorPermissionChannel._pendingRequests:13`、`EventStreamHandler._pendingPermissions:107`、`PermissionHost._request:15` |
| **B 策略快照** | `ExecutionJobService.cs:773-775`；`SessionWindow.razor:1060` |
| **C 挂起传染** | `SerializingPermissionChannel.cs:22/79-111` + 5 分钟阻塞 |
| **D 弹窗竞态** | 见 S3 |
| **E Gateway 配置快照** | `GatewayServer.cs:38` → `GatewayHost.cs:64` |
| **F Gateway 链路断裂** | `GatewayOrchestratorV2.cs:305-315` 未传通道；`IGatewayEventSink.cs:43` AsyncLocal 跨任务失效 |
| **G 结果缓存陈旧** | `PermissionService` 5 分钟 TTL + 清理定时器 |
| **H 写入无信号** | 三态写入不发布 `SessionEvent.Updated` |

> **双询问说明**：能力门（`AgentExecutor.cs:937-954`）与资源门（`ToolManager.cs:545-569`）**在能力门解析为 Ask 时**会各询问一次（条件性现状；默认 `build` Agent 的 `Allow(Tool,"*")` 规则下能力门 Allow，仅资源门询问一次——`BuiltInAgents.cs:89`）。本设计保持该行为等价，**不合并、不消除**（非目标）。

---

## 2. 架构约束与合规矩阵

### 2.1 必须保留（既有权威设计）

| 约束 | 来源 | 落实 |
|------|------|------|
| 两层权限模型：能力门在 `AgentExecutor`、资源门在 `ToolManager`；**工具零权限代码** | 2026-08-03 §Principle | §6（插入点 = hook 后、执行前，不变） |
| `IToolPermissionPolicy` 资源映射不变 | 同上 + decoupling §12.3 | §5/§6 |
| 工作区边界 + 会话记忆 + **越界可经审批扩权** | 2026-08-03 §Key architectural benefit | §5.1/§5.4 |
| 端口-适配器；后缀即职责；Abstractions 零实现 | decoupling §3/§4/§5.1、04 §1 | §3/§4 |
| 单一真相源；禁第二套开关/旁路 | 01 §5、README 原则 7 | §9 |
| 热字段经 `IConfigSectionRegistry` + OptionsMonitor/ReloadHandler | 03 §5/§7、04 §4 | §8 |
| Timeline：渲染无副作用、Key/Revision、投影模型、不改 `ToolCallViewModel` | timeline §5.1/§6.2、05 §11 | §7.1 |
| 依赖方向 | 01 §2、02、gateway README | §3.3/§7/§10 |

### 2.2 显式 supersede / 文档同步（**无兼容约束**）

| 项 | 处置 |
|----|------|
| 2026-08-03 §Exclusions 4「`ToolBase.RequestPermissionAsync` 保留给第三方/MCP」 | **supersede**：该方法是「工具携带权限代码」的残留，与该文 §Principle「工具零权限代码」自相矛盾；经核实**零调用点**（`ToolBase.cs:53` 仅定义）→ **删除**。MCP/插件如需资源级检查，经 `IToolPermissionPolicy` 映射集中到 `ToolManager`。 |
| decoupling §6.2/§6.3「`ToolContext.PermissionChannel`/`AgentContext.PermissionChannel` 保留」 | **supersede**：`ToolContext` 权限字段**删除**（工具零权限）；`AgentContext` 改 `PermissionAuthorizer`（执行级，供能力门）。 |
| decoupling §6.3「`ChatOptions.PermissionChannel` 保留」 | **supersede**：**删除**（宿主通道由 DI 解析；可交互性由 `IPermissionPresenceStore`）。 |
| decoupling §12.3 保留 `IWorkspaceWhitelist` | **supersede**：**删除**，白名单并入 `IPermissionGrantStore` 的目录面（单一存储）。`IWorkspacePathGate`/`IToolPermissionPolicy`/`IRuleEvaluator`/`IPermissionService`（精简后）保留。 |
| `SettlementEngine.ExclusiveSeams`（`Modules/SettlementEngine.cs:24-25`）含 `"permissionChannel"` | 已核实**无模块提供**（仅 `executionWorld`/`agents`/`llm`/`mcp`/`skills`），且 `HasSeamConsumer:427-428` 无提供方即返回 false → 遗留项，**移除**并同步 `03 §3`。 |
| 后缀表（`04 §1`）无 `Aggregator` / `Factory` / `Authorizer` / `Mapper` | 先例：`TaskCardAggregator`（`samples/.../TaskCardAggregator.cs:22`）、`IMcpClientWrapperFactory`/`LocalSubprocessFactory`/`OpenAiLlmClientFactory`、`LlmClientFactoryResolver`。**增补**入 `04 §1` 与 AGENTS.md：`Aggregator`（UI 投影聚合，Scoped/circuit 维度，不持权威执行态，允许落盘 UI 缓存）、`Factory`（构造器端口，Provider 语义）、`Authorizer`（授权判定端口，请求-响应）、`Mapper`（纯函数映射，如 `PermissionKindMapper`）。 |
| `02 §5` Host Shape 表列 `BlazorPermissionChannel` | 更新为 `EventStreamPermissionChannel`。 |
| `IPermissionService` 的 4 个无生产调用重载（`EvaluateAsync`/`EvaluateAgentAsync`/`EvaluateFileAsync`/`EvaluateMcpToolAsync`） | decoupling §2.2 曾列为「后续可选优化」；无兼容约束下**本期合并删除**（保留 `EvaluateToolAsync` 能力门、`EvaluateSkillAsync` 供 `Skills/Pulling/SkillPermissionFilter.cs:56`、`GetPolicyAsync`/`MergePolicies`/`InvalidateCache`/`LogAuditAsync`）。 |

### 2.3 防回潮

Core 硬编码能力包；第二套读 json；能力包引 Core；Gateway 引 Core/Hosting；UI 持权威审批状态；模块页 `@page`；**为旧契约保留垫片**。

---

## 3. 目标架构

### 3.1 一句话

**审批从「通道 + 阻塞等待 + 侧信道 + 弹窗」改为「统一授权引擎 + 在途管理器 + 会话事件流 + 内联卡片投影」**：请求时实时解析策略；在途状态服务端唯一权威；呈现经事件（单向），决策经管理器请求-响应回传。

### 3.2 组件与角色

| 组件 | 层 | 后缀角色 | 职责 |
|------|----|---------|------|
| `IPermissionService` / `PermissionService` | Abstractions / Core | Service | 能力门 `EvaluateToolAsync` + 技能评估 + 规则/策略 API + `AuthorizeAsync` 资源门编排 |
| `EffectivePermissionPolicy` | Core | public 具体类（**必须 public**：被 public `PermissionService` 构造注入；`PathSafety`/`PermissionIntegrity` 为 internal，不可直接类推） | 生效开关解析（覆盖 > 会话三态 > 全局），供 Service 与 Manager 共用，破环 |
| `IPermissionAuthorizer` / `ExecutionContextPermissionAuthorizer` | Abstractions / Core | 窄端口 | 每 execution 绑定 `sessionId` + 覆盖 + 按 kind 填充 `AllowedScopes` |
| `IPermissionAuthorizerFactory` | Abstractions / Core | Factory | 非执行链调用方（ACP）按需构造授权器 |
| `IPermissionRequestManager` / `PermissionRequestManager` | Abstractions / Core | **Manager** | 在途唯一权威：登记/等待/幂等完成/清理、策略重评估、事件唯一发布点；`IDisposable` |
| `IPermissionGrantStore` / `PermissionGrantStore` | Abstractions / Core | Store（纯存取） | 授权记忆 + 白名单目录面（单一存储；取代 `IPermissionMemory` + `IWorkspaceWhitelist`） |
| `IPermissionPresenceStore` / `PermissionPresenceStore` | Abstractions / Core | Store | 会话「可交互」计数（**仅活动会话页面级 UI 与外部客户端订阅**记账） |
| `IPermissionChannel` | Abstractions | Channel | 宿主审批通道：`TryAutoApprove` / `PresentAsync` / `DismissAsync`（实现须幂等） |
| `EventStreamPermissionChannel` | Hosting.Web | Channel | WebUI 通道（事件流宿主；Singleton） |
| `GatewayPermissionChannel` | Gateway | Channel | Gateway 通道（`TryAutoApprove` 读 `PermissionMode`；挂起表转 Manager；Singleton，根容器注册） |
| `DenyAllPermissionChannel` | Core | Channel | 无交互宿主默认（`TryAutoApprove` 恒 null） |
| `IExecutionEventPublisher` | Abstractions（上提） | （既有） | 审批请求/结果唯一出口 |
| `PermissionKindMapper` | Core | 具体类（先例 `PathSafety`） | 权限 kind 字符串 → `PermissionKind` 枚举（规则匹配） |
| `PermissionCardAggregator` + `PermissionCardModel` | WebUI | **Aggregator**（新增后缀） | UI 投影：`CallId → PermissionCardModel` |
| `PermissionInteractionService` | WebUI | Service | 卡片动作 → `IPermissionRequestManager.TryResolve` |

### 3.3 分层落点

```
原语层   Seeing.Session（SessionData.AutoApprove / SessionEvent）
契约层   Seeing.Agent.Abstractions
          ├─ Permissions/（IPermissionService 精简扩充、IPermissionAuthorizer(+Factory)、
          │                IPermissionRequestManager、IPermissionGrantStore、
          │                IPermissionPresenceStore、IPermissionChannel、请求/结果/授权模型）
          └─ Events/（PermissionRequestEvent 扩展、PermissionResolvedEvent 新增、
                     IExecutionEventPublisher 由 Core 上提）
脊柱层   Seeing.Agent.Core（引擎 + 生效策略 + Manager + 存储 + 授权器/工厂 + KindMapper + 硬边界）
宿主运行时 Seeing.Agent.Hosting（ExecutionJobService 构造授权器；删 ResolvePermissionChannel）
Host Shape Seeing.Agent.Hosting.Web（EventStreamPermissionChannel）
Gateway   Seeing.Agent.Gateway（通道 + 事件映射 + respond/pending 转 Manager；仅 Abstractions + Seeing.Session + Seeing.Gateway*）
WebUI     samples/Seeing.Agent.WebUI（投影聚合器 + 内联卡片 + 活动会话 Presence 记账）
```

---

## 4. 契约（Abstractions/Permissions & Events）

### 4.1 保留 / 迁移 / 删除

**保留**：`PermissionKind`、`PermissionEffect`、`PermissionRuleEntry`、`PermissionContext`、`PermissionResult`、`PermissionDecision`、`ResourceIdentifier`、`AgentPermissionPolicy`、`IRuleEvaluator`、`IToolPermissionPolicy`、`IWorkspacePathGate`。
- `FileOperation`：仅被删除的 `EvaluateFileAsync` 使用 → **删除**。
- `ConditionLogic`：仍被 `PermissionRuleEntry.cs:233/239/245` 引用 → **保留**。

**迁移**：`IExecutionEventPublisher`：`Core/Execution/` → `Abstractions/Events/`（实现 `ExecutionEventPublisher` 留 Core；注册仍在 `HostingServiceCollectionExtensions.cs:83`）。

**删除（无垫片）**
| 类型 | 位置 | 替代 |
|------|------|------|
| 旧 `IPermissionChannel`、旧 `PermissionRequest`、`PermissionChannelResult`、`PermissionChannelAction`、`PermissionRequiredException` | `Abstractions/Permissions/` | §4.2/§4.4 新契约 |
| `IWorkspaceWhitelist` | `Abstractions/Permissions/` | `IPermissionGrantStore` 目录面 |
| `IPermissionService` 的 `EvaluateAsync`/`EvaluateAgentAsync`/`EvaluateFileAsync`/`EvaluateMcpToolAsync` | `Abstractions/Permissions/IPermissionService.cs:15/34/44/54` | 统一 `AuthorizeAsync` + 保留 `EvaluateToolAsync`/`EvaluateSkillAsync` |
| `ChatOptions.PermissionChannel`（**删除，不更名**） | `Abstractions/Models/ChatOptions.cs:49`（注释 :44-47） | 宿主通道经 DI；可交互性经 PresenceStore |
| `ToolContext.PermissionChannel`（`ITool.cs:27-29`）、`IExecutionContext.PermissionChannel`（`:34`）、`AcpPermissionContext.PermissionChannel`（`:18`） | Abstractions / ACP | 工具零权限；ACP 经 `IPermissionAuthorizerFactory` |
| `ToolBase.RequestPermissionAsync`（零调用） | `Tools.Support/ToolBase.cs:51-70` | 资源门集中在 `ToolManager` |
| `SerializingPermissionChannel`、`DynamicPermissionChannel`、`DefaultPermissionChannel`（含 `Instance`/`AutoApproveInstance`）、旧 `DenyAllPermissionChannel`、`IPermissionMemory`、`SessionPermissionMemory`、`PermissionMemoryEntry`、`SessionWorkspaceWhitelist`、`IPermissionCache`、`PermissionCache`、`PermissionCacheKey`、`PermissionMiddleware` | `Core/Core/Permission/**`、`Core/Middlewares/` | 引擎 + 存储 + Manager + 新 `DenyAllPermissionChannel` |
| `BlazorPermissionChannel`（旧形态）、`IPermissionEventSink` | `Hosting.Web/Permissions/` | `EventStreamPermissionChannel` + 事件总线 |
| `PermissionHost.razor`、`PermissionModal.razor` 及其 ViewModel | WebUI | 内联卡片 |
| `PermissionRunContext` + `SetRunContext` | Gateway | 事件流 + Manager |

### 4.2 请求 / 结果 / 授权入口

```csharp
public sealed record PermissionRequest
{
    /// <summary>由 Manager 分配；调用方可留空。</summary>
    public string? RequestId { get; init; }

    public required string SessionId { get; init; }
    public string? CallId { get; init; }          // 工具调用 id：内联卡片关联键（不用于跨门合并）
    public string? LoopId { get; init; }
    public string? AgentName { get; init; }
    public required string PermissionKind { get; init; }
    public string? Resource { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();
    /// <summary>展示参数（Arguments 供 UI/客户端展示；Metadata 供策略与记忆）。</summary>
    public object? Arguments { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public string RiskLevel { get; init; } = "medium";
    public string? Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>本请求等待超时（秒）；null = Manager 默认（300s）。本期无宿主单独设置（见 §7.2 超时统一）。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>执行级冻结覆盖（来自 ChatOptions.AutoApprove）；null=FollowGlobal。</summary>
    public SessionAutoApprove? Override { get; init; }

    /// <summary>强制交互：跳过规则/开关的免询问放行分支（ACP 声明）。</summary>
    public bool RequireInteraction { get; init; }

    /// <summary>允许的动作集合（呈现用）。由执行级授权器按 kind 注入（§5.1）；此处为缺省。</summary>
    public IReadOnlyList<PermissionGrantScope> AllowedScopes { get; init; } =
        [PermissionGrantScope.Once, PermissionGrantScope.Session];
}

public enum PermissionGrantScope { Once = 0, Session = 1, SessionDirectory = 2 }
public enum PermissionResolvedBy { User = 0, Policy = 1, Cancellation = 2, Timeout = 3, NoChannel = 4 }

public sealed record PermissionResolution
{
    public required string RequestId { get; init; }
    public required string SessionId { get; init; }
    public string? CallId { get; init; }
    public required PermissionEffect Decision { get; init; }          // Allow | Deny
    public PermissionGrantScope Scope { get; init; } = PermissionGrantScope.Once;
    public PermissionResolvedBy ResolvedBy { get; init; } = PermissionResolvedBy.User;
    public string? Reason { get; init; }
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>执行级授权入口（窄端口）。</summary>
public interface IPermissionAuthorizer
{
    string SessionId { get; }
    Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default);
}

/// <summary>按需构造执行级授权器（ACP 等非执行链调用方）。</summary>
public interface IPermissionAuthorizerFactory
{
    IPermissionAuthorizer Create(string sessionId, SessionAutoApprove? @override = null);
}

/// <summary>宿主/规则 API（精简后）；新增资源门编排入口。</summary>
public interface IPermissionService
{
    Task<PermissionResult> EvaluateToolAsync(string toolName, string? ns, PermissionContext context, CancellationToken ct = default);
    Task<PermissionResult> EvaluateSkillAsync(string skillName, PermissionContext context, CancellationToken ct = default);
    Task<AgentPermissionPolicy> GetPolicyAsync(string agentName, CancellationToken ct = default);
    AgentPermissionPolicy MergePolicies(AgentPermissionPolicy global, AgentPermissionPolicy agent);
    void InvalidateCache(string? agentName = null, string? resourcePattern = null);
    Task LogAuditAsync(PermissionResult result, PermissionContext context, CancellationToken ct = default);

    Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default);
}
```

- `ExecutionContextPermissionAuthorizer`：`AuthorizeAsync` → `SessionId 回填/校验` + `Override ??= 绑定覆盖` + **按 kind 填充 `AllowedScopes`**（`filesystem.* → [Once, Session, SessionDirectory]`；其余 `[Once, Session]`）→ 委托 `IPermissionService.AuthorizeAsync`。**不注册 DI**（经 Factory 或 ExecutionJobService 直接构造）。
- 现状 UI 的 `DecisionScope.SessionFile` 语义由 `Session` 承接（见 §7.1 映射表）。

### 4.3 在途管理器 / 存储

```csharp
public interface IPermissionRequestManager : IDisposable
{
    /// <summary>登记在途并发布请求事件（分配 RequestId）；在途 ≥ 32 → 立即 Deny(NoChannel,"队列已满")。</summary>
    Task<PermissionTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default);

    Task<PermissionResolution> WaitAsync(PermissionTicket ticket, CancellationToken ct = default);

    /// <summary>幂等完成（UI/Gateway/重评估/取消共用）；expectedSessionId 非空时校验归属。</summary>
    bool TryResolve(string requestId, PermissionEffect decision, PermissionGrantScope scope,
                    PermissionResolvedBy resolvedBy, string? reason = null, string? expectedSessionId = null);

    IReadOnlyList<PermissionRequest> GetPending(string sessionId);
    int PendingCount { get; }
}

public readonly record struct PermissionTicket(string RequestId, string SessionId);

/// <summary>唯一授权存储（记忆 + 白名单目录面）；匹配/优先级判定在 IPermissionService。</summary>
public interface IPermissionGrantStore
{
    // 决策记忆
    void Add(string sessionId, PermissionGrant grant);
    void Remove(string sessionId, string permissionKind, string? resource);
    IReadOnlyList<PermissionGrant> Lookup(string sessionId, string permissionKind, string? resource);

    // 白名单目录面（取代 IWorkspaceWhitelist）
    void AddSessionDirectory(string sessionId, string directoryPath);
    bool ContainsSessionPath(string sessionId, string path);

    void ClearSession(string sessionId);
    void ClearAll();
}

public sealed record PermissionGrant(string PermissionKind, string? Resource,
                                     PermissionGrantScope Scope, PermissionEffect Effect);

/// <summary>会话可交互计数。Attach 来源受限：
/// WebUI=**活动会话的页面级 UI**；Gateway=**外部客户端**会话订阅。
/// 不得以 TaskCardAggregator/子会话/编排器内部订阅/TaskTool 后台订阅记账。</summary>
public interface IPermissionPresenceStore
{
    void Attach(string sessionId);
    void Detach(string sessionId);
    bool CanPresent(string sessionId);
    void ClearAll();
}
```

### 4.4 宿主通道 / 事件

```csharp
public interface IPermissionChannel
{
    /// <summary>宿主级自动批准策略（Gateway auto_approve）；null=不短路。
    /// 实现必须幂等（PresentAsync/DismissAsync 可能被广播及重复调用）。</summary>
    PermissionEffect? TryAutoApprove(PermissionRequest request);

    ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default);
    ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default);
}
```

- `IExecutionEventPublisher`（Abstractions/Events）：成员不变（**不新增** `HasSubscribers`）。
- `PermissionRequestEvent`（扩展）：`RequestId`、`SessionId`、`CallId`、`LoopId`、`PermissionKind`、`Resource`、`Arguments`、`RiskLevel`、`Message`、`AllowedScopes`、`TimeoutSeconds`、`Timestamp`。
- `PermissionResolvedEvent`（新增，取代 `PermissionResponseEvent`）：`RequestId`、`SessionId`、`CallId`、`Decision`、`Scope`、`ResolvedBy`、`Reason`、`Timestamp`。
- 二者**不落盘**（`ExecutionJobService.ShouldPersistEvent:951-959` 不含）。
- **协议与客户端同步**：`MessageEventType`（`PermissionResponse` → `PermissionResolved`）、`PermissionRequestEvent.PermissionId` **改名** `RequestId`（`MessageEventTypes.cs:362`、`GatewayEventMapper.cs:203`、`EventStreamHandler.cs:612` 等消费点同步）、`Seeing.Gateway/Mapping/GatewayEventMapper.cs:33-34/195-224`、`Seeing.Agent.Gateway/Core/GatewayEventMapper.cs`（类 `ChatEventMapper`，:104-122 补 Resolved）、`GatewayEventData.PermissionId` 继续填充（`WeComChannelBridge.cs:432/466/485`、`QQPermission.cs:35`、`Seeing.Gateway.Client/WebSocketGatewayClient.cs:183`、`HttpGatewayClient.cs:80/89/108`、`GatewayWsFrame.cs:154`）。

---

## 5. 引擎设计（Core）

### 5.1 `PermissionService.AuthorizeAsync` 决策链

```
0. 规范化：SessionId 缺失回填执行会话；RequestId 由 Manager 分配
1. 工作区边界预检（仅 filesystem.* 且 RestrictToWorkspace=true；判定与 WorkspacePathGate.EnsureAllowed:33-49 同源）：
     1a. 无 SessionId → Deny(Policy,"缺少会话 ID")
     1b. 工作区内   → Allow(Policy)
     1c. 白名单内   → Allow(Policy)
     1d. 越界（含 filesystem.workspace_extend）→ 继续 2/3/5（可经审批扩权）
   Restrict=false → 跳过
2. 授权记忆（GrantStore.Lookup + Service 匹配）→ 命中 Allow/Deny 短路
3. Agent 规则（AgentPermissionPolicy；kind 经 PermissionKindMapper 映射）：
     3a. Deny → Deny(Policy)
     3b. Allow 且 !RequireInteraction 且 kind 非资源类（filesystem/shell/network）→ Allow(Policy)
     3c. Ask → 继续
4. 生效开关（EffectivePermissionPolicy.Resolve，实时）：
     覆盖 Enabled→Allow；覆盖 Disabled→强制 Ask
     会话 Enabled→Allow；会话 Disabled→强制 Ask
     FollowGlobal 且全局 AutoApproveAll=true→Allow
     RequireInteraction=true → 跳过以上 Allow 分支
5. 宿主通道策略（`RequireInteraction=true` 时同样跳过）：任一 IPermissionChannel.TryAutoApprove 返回 Allow → Allow(Policy)
6. 询问：PresenceStore.CanPresent(sessionId)=false → Deny(NoChannel,"无交互通道")
        否则 manager.BeginAsync → channel.PresentAsync → manager.WaitAsync
7. 决策后处理（等价现状 SerializingPermissionChannel:85-104/117-137）：
     7a. Decision=Allow 且 Kind 为 filesystem.* 且 RestrictToWorkspace=true
         （复刻 ExpandWhitelistOnAllow:117-137 的 IsRestricting():119 前置门）：
         **无论 Scope 是否 Once**，将 resource 所在目录写白名单
         （Path.GetFullPath → 取目录 → TrimEnd → GrantStore.AddSessionDirectory；
          形状对齐 SessionWorkspaceWhitelist.Add:23-27）
     7b. Scope != Once → GrantStore.Add(kind, resource, scope, Decision)
         （Session=精确资源；SessionDirectory=目录前缀；可与 7a 同时发生）
     7c. Kind 容错：`filesystem.external` 等无生产写入点的历史串按前缀映射为 File
8. 审计：LogAuditAsync
```

> **步骤 3b 依据**：资源类 kind 的 Agent 规则 `Allow` **不**短路——旧 `EvaluateFileAsync`/`EvaluateMcpToolAsync`/`EvaluateAgentAsync` 无生产调用者（死代码），资源门（`ToolManager`）从不套用 Agent 规则；工作区内已由 1b 放行，越界须走 4/5/6 审批，不得被 `Allow(File/Shell/Network,"*")` 绕过。`Deny` 对所有 kind 生效（fail-safe）。

### 5.2 `PermissionRequestManager`

- 依赖：`IExecutionEventPublisher`、`IPermissionPresenceStore`、`EffectivePermissionPolicy`、`IEnumerable<IPermissionChannel>`、`ISessionEventPublisher`、`IOptionsMonitor<SeeingAgentOptions>`、`ILogger`。**不依赖 `IPermissionService`**（破环）。
- **前置依赖**：`IExecutionEventPublisher` 由 `HostingServiceCollectionExtensions.cs:83`（`AddExecutionEngine`）注册；Core-only（`AddSeeingCore`）由 `NullExecutionEventPublisher`（全部 no-op）兜底注册，故解析 Manager **不会**失败，真实宿主由 `AddExecutionEngine` 后注册覆盖。Manager 只被执行链消费。
- `BeginAsync`：分配 `RequestId` → 登记 → `Publish(PermissionRequestEvent)`（唯一发布点）；≥32 → 立即 `Deny(NoChannel,"队列已满")` 并补发 `PermissionResolvedEvent`。
- `WaitAsync`：`Task.WhenAny(tcs, Delay(5 分钟))`；取消/超时 → `TryResolve(Deny, Cancellation/Timeout)`。
- `TryResolve`：`TrySetResult` 幂等 → `Publish(PermissionResolvedEvent)` → 广播 `DismissAsync`（逐通道 `try/catch`）。
- **策略变更重评估（单例一份）**：订阅 `ISessionEventPublisher.Events`（`Updated` + SessionId 命中）+ `IOptionsMonitor.OnChange`（比对 `AutoApproveAll` 值变化）；回调整体 `try/catch`（`SessionEventPublisher.Publish:20-31` 无隔离）→ `EffectivePermissionPolicy.Resolve` 重算：**仅** `Allow` 收敛为放行；`Ask`/`null` 一律保持等待（不打扰用户；`Deny` 不由重评估产生）。
- `IDisposable`：释放两个订阅；对全部在途请求经 `TryResolve(Deny, Cancellation,"管理器已释放")` 收敛——**发布 `PermissionResolvedEvent`** 并广播 `DismissAsync`。
- **不持有串行锁**（修缺陷 C）。

### 5.3 `EffectivePermissionPolicy`（public 具体类，Singleton）

```csharp
/// <summary>生效开关解析（被 public PermissionService 构造注入，必须 public）。
/// 返回值语义：Allow=自动批准；Ask=强制交互（会话/覆盖 Disabled）；null=无开关级判定（继续按 Ask 处理）。
/// 不返回 Deny（拒绝由规则/边界/记忆等上游环节产生）。</summary>
public sealed class EffectivePermissionPolicy(
    ISessionManager sessions,
    IOptionsMonitor<SeeingAgentOptions> options)
{
    public PermissionEffect? Resolve(PermissionRequest request);
}
```

- 优先级：`request.Override`（非 FollowGlobal）> 会话三态 > 全局 `AutoApproveAll`；`RequireInteraction=true` 时恒返回 `null`（不放行）。
- 被 `PermissionService`（步骤 4）与 Manager（重评估）单向依赖 → 无循环。
- 会话取 `ISessionManager.Get(sessionId)?.AutoApprove`（同步、缓存未命中回落全局值——用例覆盖）。
- **重评估收敛规则**：仅 `Allow` 收敛为放行；`Ask`/`null` 一律保持等待（不打扰用户）。

### 5.4 存储与硬边界

- `PermissionGrantStore`：记忆（`Add/Remove/Lookup`）+ 白名单目录面（`AddSessionDirectory/ContainsSessionPath`）+ `ClearSession/ClearAll`；`Contains` 前缀语义沿用 `SessionWorkspaceWhitelist.cs:23-44`（`TrimEnd` 归一 + `PathSafety.IsPathWithinDirectory`），属**数据形状查询**（非策略）。
- `WorkspacePathGate` 白名单依赖改 `IPermissionGrantStore.ContainsSessionPath`（`Restrict=false` 一律放行，现状）；工具侧硬门禁（`PathGateHelper.cs:17`，`ReadTool.cs:101` 等）保留为**防御性冗余**，策略真相在 `AuthorizeAsync`（步骤 1/7）。
- `WorkspaceBoundaryLifecycle` 改依赖 `IPermissionGrantStore.ClearAll`。

### 5.5 DI 注册、生命周期、seam

| 组件 | 生命周期 | 注册点 |
|------|---------|--------|
| `IPermissionService` | Singleton | `AddSeeingCore`（`AddPermissionService` 扩展改造） |
| `EffectivePermissionPolicy` | Singleton | `AddSeeingCore` |
| `IPermissionRequestManager` | Singleton（`IDisposable`；需 `AddExecutionEngine` 的 `IExecutionEventPublisher`） | `AddSeeingCore` |
| `IPermissionGrantStore` | Singleton | `AddSeeingCore`（替换 `IPermissionMemory`/`IWorkspaceWhitelist` 注册） |
| `IPermissionPresenceStore` | Singleton | `AddSeeingCore` |
| `IPermissionAuthorizerFactory` | Singleton | `AddSeeingCore` |
| `IPermissionChannel` | **Singleton** | Core 兜底 `TryAddSingleton<IPermissionChannel, DenyAllPermissionChannel>()`（删旧工厂 `ServiceCollectionExtensions.cs:574-595`）；**WebUI：`AddSingleton`**（现状 `WebHostingServiceCollectionExtensions.cs:48` 为 Scoped，须改，否则根解析/引擎注入失败）；**Gateway：具体类型 + 接口映射**（`AddSingleton<GatewayPermissionChannel>()` + `AddSingleton<IPermissionChannel>(sp => sp.GetRequiredService<GatewayPermissionChannel>())`，见 §7.2） |
| `ExecutionContextPermissionAuthorizer` | 执行级实例（Factory 或 ExecutionJobService 构造） | 不注册 DI |
| `PermissionCardAggregator` / `PermissionInteractionService` | Scoped（circuit） | WebUI sample |

- **多通道**：引擎/Manager 经 `IEnumerable<IPermissionChannel>`；`TryAutoApprove` 任一 Allow 即放行（**进程级全局语义**：WebUI 与 Gateway 同进程并存时，Gateway `auto_approve` 对 WebUI 会话同样生效——显式声明；按来源隔离属后续演进）；`PresentAsync`/`DismissAsync` 广播（实现须幂等）。
- **captive dependency**：引擎/Manager 为 Singleton，不得注入 Scoped。
- **seam**：移除 `SettlementEngine.ExclusiveSeams` 的 `"permissionChannel"`，同步 `03 §3`。

---

## 6. 两层权限模型

```
AgentExecutor.EvaluatePermissionAsync                            ← 能力门
  ├─ IPermissionService.EvaluateToolAsync(...)                   Allow/Deny（规则，不变）
  └─ NeedsConfirmation → context.PermissionAuthorizer.AuthorizeAsync(
         kind="tool.execute", resource=toolName, callId=tc.Id)

ToolManager.ExecuteAsync                                         ← 资源门（唯一插入点，hook 后 / 执行前）
  ├─ IToolPermissionPolicy.Evaluate(toolId, args) → PermissionResourceCheck?
  └─ IPermissionAuthorizer.AuthorizeAsync(check → request, callId=toolCall.Id)
        └─ §5.1 决策链
```

- **工具零权限**：`ToolContext` 不再暴露任何权限入口；`ToolBase.RequestPermissionAsync` 删除（零调用）；工具的资源级检查一律经 `IToolPermissionPolicy` 映射到 `ToolManager`（MCP/插件如需，由策略实现补映射；`2026-08-03 §Exclusions 3` 的「MCP 独立路径」保持不变）。
- `IToolManager.ExecuteAsync(..., IPermissionAuthorizer?)`（`IToolManager.cs:51` 类型替换）。
- 字段替换/删除：`AgentContext.cs:32` → `PermissionAuthorizer`；`ITool.cs:27-29`（`ToolContext`）**删除**；`IExecutionContext.cs:34` **删除**（`DefaultExecutionContext` 权限属性随之删除，含 `:4/:50/:69/:71`）；`ChatExecutionContext.cs:40/117` → `PermissionAuthorizer`；`ToolTimeoutDecorator.cs:74` 删除该拷贝；`ChatOptions.cs:44-49` **删除属性与注释**；`SessionWindow.razor:34/1058` 删除 inject/赋值；`AgentExecutor.cs:154-155` 删除 `DefaultPermissionChannel.Instance` 兜底。

---

## 7. 宿主适配

### 7.1 WebUI：事件 → 投影 → 内联卡片

```
呈现：AuthorizeAsync=Ask → manager.BeginAsync → PermissionRequestEvent
      → SessionEventStreamRouter（按 session 订阅，:342 → ExecutionJobService.SubscribeEvents:437）
      → EventStreamHandler → PermissionCardAggregator（Scoped，按 CallId 归纳）→ 工具卡片内联按钮
决策：按钮 → PermissionInteractionService → manager.TryResolve(requestId, decision, scope, User, expectedSessionId)
收敛：TryResolve → PermissionResolvedEvent → 聚合器清除待办 + 显示结果
```

- **可交互性（以活动会话为准）**：`Session.razor`（已有 `IDisposable:37`、`OnInitializedAsync:208`、`Dispose:416`）：
  `OnInitializedAsync` Attach 当前活动会话；切换（`OnTileClick:312-316` / `OnParametersSetAsync`）先 Detach 旧再 Attach 新；`Dispose` Detach。
  **显式排除**：Summary 侧栏（`Session.razor:143`）与 Conference 页（`Conference.razor:37-39`）复用的 `SessionWindow`；`TaskCardAggregator`（`TaskCardAggregator.cs:74/166/220`）；`ConferenceRegistry.cs:61`；router 其它 consumer（`AttachConsumer:91`/`DetachConsumer:229`）；`TaskTool.cs:200` 的后台子会话订阅。
  推荐实现：单例 `ActiveSessionTracker`（refcount + circuit 维度，circuit 关闭统一 Detach），避免页面级遗漏。
- **投影模型**：`PermissionCardModel { RequestId, CallId, Kind, Resource, RiskLevel, Message, Status }`；不向 `ToolCallViewModel` 加字段（05 §11）；渲染只读、无副作用（timeline §5.1）。
- **动作↔作用域映射**（对齐现状 `PermissionHost.razor:67-74`）：本次允许→`Once`；始终允许→`Session`（承接现状 `SessionFile`）；允许此目录→`SessionDirectory`（仅 `filesystem.*` 呈现）；本次拒绝→`Deny(Once)`；始终拒绝→`Deny(Session)`。动作集合取自 `PermissionRequest.AllowedScopes`（授权器按 kind 注入）。
- **缓冲重放对账**：新订阅者会收到缓冲重放（`ExecutionEventPublisher.cs:81-86`；`CircularBuffer` 上限 `:39`，终态 `ClearBuffer`）→ 投影器以 `manager.GetPending(sessionId)` 为准，仅渲染在途请求。
- **刷新重建**：会话加载时以 `GetPending` 重建未决卡片。
- 删除 `PermissionHost.razor`/`PermissionModal.razor` 与 `MainLayout.razor:52` 挂载；**同时删除旧内联权限展示路径**（`Components/Messaging/PermissionMessageComponent.razor`、`Rendering/Renderers/PermissionBlockRenderer.cs`、`Models/PermissionRequestViewModel.cs`）——它们渲染独立权限块，与新「工具卡片内联卡片（按 `CallId`）」重复，由 W5 取代。`EventStreamHandler` 处理 `PermissionResolvedEvent`（替换 `OnPermissionResponse:155`）、移除 `_pendingPermissions:107` 与 `IPermissionEventSink` 实现（`:59/190-191`）；`SessionWindowTimelineSync.cs:110-111` 改事件；`SecurityPage.razor:11/81` 的 `PendingCount` 改读 Manager。

### 7.2 Gateway

| 项 | 变更 |
|----|------|
| 通道注册（**同一实例 + 具体类型 + 接口映射**） | `AddSeeingGatewayServer` 注册**具体类型** `GatewayPermissionChannel`（Singleton）到根容器，**并必须显式** `AddSingleton<IPermissionChannel>(sp => sp.GetRequiredService<GatewayPermissionChannel>())`——否则具体类型注册不进 `IEnumerable<IPermissionChannel>`，`TryAutoApprove` 会被 Core 兜底 `DenyAllPermissionChannel` 遮蔽（Gateway `auto_approve` 失效）。`GatewayHost.cs:64` 的 `new` **删除**，子容器改 `_rootServices.GetRequiredService<GatewayPermissionChannel>()` 并 `AddSingleton` 该实例（端点/WS 同一对象）；`:91/105` 同步。子容器按**具体类型**解析以避开同进程 WebUI 通道 |
| 通道能力 | `TryAutoApprove` 读 `IOptions<GatewayOptions>.Value.PermissionMode`（`auto_approve`→Allow，实时、仅新请求）；`PresentAsync`/`DismissAsync` = no-op（事件流已推送） |
| 挂起表 | 删除通道内 `_pending`；`GET /api/gateway/permissions/pending`（`GatewayEndpoints.cs:98-107`）投影 `manager.GetPending(sessionId)`，`GatewayPendingPermission` 由 `PermissionRequest` 全字段映射（`RequestId↔PermissionId`、`SessionId`、`LoopId`、`PermissionKind`、`Resource`、`Arguments`、`Message`、`RiskLevel`、`CreatedAt`——`CreatedAt` 由 `DateTimeOffset.UtcDateTime` 转 `DateTime`，`GatewayPendingPermission.cs:24`） |
| 回传 | `POST /api/gateway/permissions/{id}/respond`（`:112-121`）与 WS `PermissionRespond`（`GatewayWebSocketHandler.cs:280-302`）→ `manager.TryResolve(..., expectedSessionId)` |
| 事件推送 | 审批事件随执行流 → `GatewayOrchestratorV2.SubscribeExecutionEventsAsync` → `ChatEventMapper` 补 Request/Resolved → SSE/WS；**删除 `PermissionRunContext`/`SetRunContext`（AsyncLocal）** |
| 可交互性 | `SubscribeExecutionEventsAsync`（仅 WS 处理器与 HTTP SSE 端点调用 = 外部客户端）入口 `PresenceStore.Attach`，`finally` `Detach` |
| 断连清理 | `GatewayConnectionManager.Unregister`（连接断开）→ 对该连接订阅过的会话执行 `manager.TryResolve(..., Deny(Cancellation))`（补上现状零调用点） |
| 配置实时 | `GatewayServer.cs:38` 不再固化 `_options.Value`；`GatewayHost.cs:64` 改传 `IOptions<GatewayOptions>`（`GatewayOptionsMonitor` 实时） |
| 客户端 SDK | `Seeing.Gateway.Client`（`WebSocketGatewayClient.cs:183`、`HttpGatewayClient.cs:80/89/108`）、`GatewayWsFrame.cs:154` 字段名保留，实现随协议层同步 |
| 超时统一 | 等待超时统一由 `PermissionRequestManager` 负责（默认 300s，`PermissionRequest.TimeoutSeconds` 可覆盖）；`GatewayOptions.PermissionTimeoutSeconds` **废弃并忽略**（保留字段避免配置破坏，记入 release notes） |

### 7.3 Headless / Embed / CLI

- Core 兜底 `DenyAllPermissionChannel`（`TryAutoApprove`=null）→ §5.1 步骤 6 因 `PresenceStore.CanPresent=false` 直接 `Deny(NoChannel)`（等价现状）。
- 可选后续：CLI 控制台通道。

### 7.4 ACP

- `AcpPermissionBridge`：注入 `IPermissionAuthorizerFactory`（Abstractions），以 `AcpPermissionContext.SeeingSessionId`（`:10`，**非** `SessionId`）`Create(sessionId)`；请求带 `RequireInteraction=true`（保留「后端工具调用需确认」）。删除 `AcpPermissionContext.PermissionChannel`（`:18`）与 `ResolveDefaultChannel`（`:65-69`）。
- `AcpTool`（`:63` 的 `ToolContext` 已无权限字段）：从 `context.Services` 取 `IPermissionAuthorizerFactory`，以 `context.SessionId` 构造授权器做前置检查（`PermissionRequest.SessionId` 必填，由授权器回填）；`:206-214` 构造 `AcpPermissionContext` 时不再传通道。
- `AcpSessionRunner.cs:80` 不再传 `PermissionChannel`。

---

## 8. 配置与热重载

| 配置 | 热路径 |
|------|--------|
| `Permission.AutoApproveAll` | `IOptionsMonitor.OnChange` + `UpdateCache:721-727`（既有） |
| `Workspace.RestrictToWorkspace` | `IOptionsMonitor`（既有） |
| `Gateway.PermissionMode`/`PermissionTimeoutSeconds` | `GatewayOptionsMonitor` 实时读（新请求生效） |
| 会话三态 | 新 `ISessionManager.SetAutoApproveAsync`（对齐 `SetTitleAsync:586-621`：改字段 → Hook → 发 `SessionEvent{Updated}` → `SaveAsync`） |

- 删除 `PermissionService` 5 分钟结果缓存与清理定时器。
- 规则编译快照：键 = 配置版本 + Agent 策略版本；仅缓存解析结果。`InvalidateCache` 语义更新为「清除该快照」。

---

## 9. 单一真相源矩阵

| 视图 | 唯一真相源 | 禁止 |
|------|-----------|------|
| 在途审批状态 | `IPermissionRequestManager` | UI 自持权威；多处 `_pending*` |
| 授权记忆/白名单 | `IPermissionGrantStore` | 分散 `IPermissionMemory` + 独立白名单 |
| 审批事件 | `IExecutionEventPublisher`（非落盘） | 侧信道与事件流双通道 |
| 生效策略 | `EffectivePermissionPolicy`（实时） | 执行启动快照；结果缓存 |
| 可交互性 | `IPermissionPresenceStore`（活动会话 UI / 外部客户端） | 「有事件订阅者」代替「有人可答」 |

---

## 10. 影响面

### 10.1 新增
Core：`PermissionRequestManager`、`PermissionGrantStore`、`PermissionPresenceStore`、`EffectivePermissionPolicy`、`PermissionKindMapper`、`ExecutionContextPermissionAuthorizer`、`DefaultPermissionAuthorizerFactory`、新 `DenyAllPermissionChannel`。
Abstractions：`IPermissionAuthorizer`、`IPermissionAuthorizerFactory`、`IPermissionRequestManager`、`IPermissionGrantStore`、`IPermissionPresenceStore`、`IPermissionChannel`（新）、`PermissionRequest`、`PermissionResolution`、`PermissionGrant`、`PermissionTicket`、`PermissionResolvedEvent`。
Hosting.Web：`EventStreamPermissionChannel`。WebUI：`PermissionCardAggregator`/`PermissionCardModel`/卡片组件/`PermissionInteractionService`/`ActiveSessionTracker`。

### 10.2 删除
见 §4.1；另删 `ServiceCollectionExtensions.cs:212`（`PermissionMiddleware` DI）、`:574-595`（旧通道工厂）、`MainLayout.razor:52` 挂载、`SecurityPage.razor:11/81` 的 `BlazorPermissionChannel` 依赖、`Program.cs:109-111` 的 `IPermissionEventSink` 注册、`Hosting.Web/WebHostingServiceCollectionExtensions.cs:47-56` 旧注册。

### 10.3 迁移/改造
| 文件 | 改动 |
|------|------|
| `ExecutionJobService.cs` | 删 `ResolvePermissionChannel`（:773-775/892-945）与 `:1220` 透传；构造授权器（`record.Options?.AutoApprove`）放入 `AgentContext` |
| `ToolManager.cs`（:480/:545-585） | 资源门改 `IPermissionAuthorizer.AuthorizeAsync`（`callId=toolCall.Id`） |
| `IToolManager.cs:51` | 参数类型改 `IPermissionAuthorizer?` |
| `ToolBase.cs:51-70` | **删除** `RequestPermissionAsync`（零调用） |
| `ITool.cs:27-29`、`IExecutionContext.cs:34`、`DefaultExecutionContext.cs:4/42/50/69/71` | 删除权限字段/别名 |
| `AgentContext.cs:32`、`ChatExecutionContext.cs:40/117` | → `PermissionAuthorizer` |
| `ChatOptions.cs:42-49` | 删除 `PermissionChannel` 属性与注释 |
| `AgentExecutor.cs:154-155/481/627/652/722/746/791/802/827/930/937-954` | 能力门改授权器；删 `DefaultPermissionChannel.Instance` |
| `ToolTimeoutDecorator.cs:74` | 删除通道拷贝 |
| `AcpPermissionBridge.cs:44/52/65-69`、`AcpPermissionContext.cs:10/18`、`AcpSessionRunner.cs:80`、`AcpTool.cs:91-107/206-214` | Factory + `SeeingSessionId` + `RequireInteraction`；删通道字段 |
| `IExecutionEventPublisher.cs` → Abstractions/Events；`ExecutionEventPublisher.cs`（实现不动）；`GlobalUsings`、Hosting/Gateway/samples 引用 | 契约搬迁 |
| `MessageEventTypes.cs`、`Seeing.Gateway/Mapping/GatewayEventMapper.cs:33-34/195-224`、`Agent.Gateway/Core/GatewayEventMapper.cs:104-122`、`WeComChannelBridge.cs:432/466/485`、`QQPermission.cs:35`、`Seeing.Gateway.Client/*`、`GatewayWsFrame.cs:154` | 事件与协议映射 |
| `GatewayServer.cs:38`、`GatewayHost.cs:64/91/105`、`GatewayOrchestratorV2.cs:22/31/158-163/212-217/305-315`、`IGatewayEventSink.cs:43`、`GatewayEndpoints.cs:98-121`、`GatewayWebSocketHandler.cs:20/27/280-302`、`GatewayConnectionManager.cs` | 根容器具体类型注册、实时 Options、事件映射、respond/pending 转 Manager、删 AsyncLocal、Presence 与断连清理 |
| `SessionManager.cs`/`ISessionManager.cs` | +`SetAutoApproveAsync` |
| `SessionWindow.razor:34/1058/1060/1402-1410`、`PromptInput.razor`、`Session.razor`（`ActiveSessionTracker` 记账）、`MainLayout.razor:52` | 删 channel inject/赋值；三态走 `SetAutoApproveAsync`；Presence 记账；移除弹窗挂载 |
| `EventStreamHandler.cs:59/107/155/190-191/237-239`、`SessionWindowTimelineSync.cs:110-111`、`SecurityPage.razor:11/81`、`Program.cs:109-111`、`Models/PermissionRequestViewModel.cs`、`Rendering/Renderers/PermissionBlockRenderer.cs:72`、`Models/Messaging/ContentBlock.cs:238/375` | 新事件/移除本地 pending/投影迁移 |
| `Core/Extensions/ServiceCollectionExtensions.cs:212/516/570/574-595`、`Extensions/PermissionServiceExtensions.cs`、`Hosting.Web/WebHostingServiceCollectionExtensions.cs:47-56`、`Hosting.Headless:39`、`Hosting.Embed:40` | DI 改造（含 WebUI 通道改 Singleton、Core 兜底、`IWorkspaceWhitelist` 注册移除） |
| `ToolOutputLimiterDecorator.cs:28/35/41/80` | `IWorkspaceWhitelist` → `IPermissionGrantStore` 目录面（装饰器工厂 `ServiceCollectionExtensions.cs:516` 同步） |
| `Core/Modules/SettlementEngine.cs:24-25` | 移除 seam |
| `WorkspacePathGate.cs`、`WorkspaceBoundaryLifecycle.cs`、`AddWorkspacePathTool`（若用白名单） | 依赖改 `IPermissionGrantStore` 目录面 |
| `docs/architecture/02 §5`、`03 §3`、`04 §1`（Aggregator/Factory）、`06`、`README.md`（扫描命令） | 文档同步 |

### 10.4 测试影响
- **删除/重写**：`SerializingPermissionChannelTests`（:15-48 并发断言、:39/64/83/104/149 构造）、`ExecutionJobServicePermissionChannelTests:23-84`、`PermissionServiceTests`（`EvaluateAsync`/缓存断言）、`PermissionCacheTests`、`SessionPermissionMemoryTests`、`SessionWorkspaceWhitelistTests`、`ToolInvokerTests:171/205/239`、`ToolOutputLimiterDecoratorTests`、`Wave1IntegrationTests`、`GatewayEventMapperTests:196/221`、`MessageEventTypeStringTests:19`（`PermissionResponse` 断言）、`EventStreamHandlerTests`、`SessionWindowTimelineSyncTests`、`AcpToolTests:125`（`FakeSessionManager` 补 `SetAutoApproveAsync`）。
- **命名空间迁移**：全部 `Mock<IExecutionEventPublisher>`（约 37 处/十余文件）。
- **新增**：§11 + Invariants（能力包无 Core、Gateway 无 Core/Hosting、`AddSeeingCore` 零工具、无第二处 schema、`Permissions/` 无实现类、无 `permissionChannel` seam 残留）。

---

## 11. 测试策略

1. `PermissionAuthorizationTests`：决策链顺序；**越界路径经审批放行后 `WorkspacePathGate` 放行**（含 Once）；工作区内自动 Allow；无 SessionId + Restrict→Deny；记忆短路（精确/目录前缀/作用域）；规则 + `PermissionKindMapper`；`Override`/会话三态/全局实时值（含 `ISessionManager.Get` 未命中回落全局）；`RequireInteraction`；无 Presence→`Deny(NoChannel)`；`TryAutoApprove` 落点与 DenyAll=null；`AllowedScopes` 按 kind 注入；**能力门 Ask 时两门各自询问（现状等价）**。
2. `PermissionRequestManagerTests`：Begin/Wait/TryResolve 幂等；取消/超时；策略变更重评估（会话 `Updated`/全局值变化）；回调异常隔离；上限 fail-closed；`GetPending`；`Dispose` 退订。
3. `EffectivePermissionPolicyTests`：优先级 + `RequireInteraction` 只产 null/Deny。
4. `PermissionGrantStoreTests`：记忆作用域/前缀；白名单目录面（`AddSessionDirectory/ContainsSessionPath`，`TrimEnd` 形状）+ `WorkspacePathGate` 端到端；`ClearSession/ClearAll`。
5. `PermissionPresenceStoreTests`：attach/detach、多会话/circuit、ClearAll。
6. `SetAutoApproveAsync`（primitives）：改值 + 发 `SessionEvent.Updated` + 落盘。
7. `PermissionCardAggregatorTests`（WebUI）：CallId 归纳/Resolved 收敛/`GetPending` 对账/忽略陈旧重放/会话隔离/动作↔作用域映射；`PermissionInteractionServiceTests`。
8. Gateway：事件映射；respond/pending 转 Manager（sessionId 校验）；`GatewayPendingPermission` 载荷还原（含 `CreatedAt` 转换）；Presence attach/detach；断连取消；实时 `PermissionMode`。

---

## 12. 阶段（S1 为原子符号切换；**无兼容约束**）

> **S1a 的原子性是硬约束**：新契约与旧契约**同名同命名空间**（`IPermissionChannel`/`PermissionRequest`），并存必然编译冲突。故采用「先删旧、同提交内建新并迁移全部调用点」，构建绿以 S1a 结束为验收点（与 decoupling Phase 2 同性质）。无兼容约束下**不保留任何垫片或适配层**。

| 阶段 | 内容 | 验收 |
|------|------|------|
| **S1a 符号切换** | 删除旧契约与旧栈（§4.1）；新增契约与**全部新类型骨架**（`AuthorizeAsync` 最小实现=按规则/开关直通、Manager 最小实现=直通等待；**并落 `EffectivePermissionPolicy`/`PermissionGrantStore`/`PermissionPresenceStore`/`IPermissionAuthorizerFactory`/`ExecutionContextPermissionAuthorizer`/新 `DenyAllPermissionChannel` 骨架**，否则 `PermissionService` ctor 无法解析）；**全调用点类型迁移**（ExecutionJobService/ToolManager/AgentExecutor/ACP/Gateway/WebUI/DI/测试可编译）；`ChatOptions.PermissionChannel`、`ToolContext.PermissionChannel`、`ToolBase.RequestPermissionAsync`、`IWorkspaceWhitelist` 删除 | `dotnet build Seeing.Agent.slnx` 绿 |
| **S1b 引擎** | `EffectivePermissionPolicy` + `PermissionKindMapper` + `PermissionGrantStore` + 完整决策链（含边界/记忆/开关/白名单写入） | `PermissionAuthorizationTests`/`PermissionGrantStoreTests`/`EffectivePermissionPolicyTests` 绿 |
| **S1c 在途** | `PermissionRequestManager`（并发/超时/重评估/幂等/事件）+ `PermissionPresenceStore` | `PermissionRequestManagerTests`/`PermissionPresenceStoreTests` 绿 |
| **S1d 宿主接线** | ACP（Factory + `RequireInteraction`）、Gateway（根容器具体类型、事件映射、respond/pending、断连清理、删 AsyncLocal）、WebUI（`EventStreamHandler` 新事件、Presence 记账、三态 `SetAutoApproveAsync`） | 全量构建绿；既有测试重写后绿 |
| **S1e 内联卡片** | `PermissionCardAggregator` + 卡片组件 + `PermissionInteractionService`；删除弹窗栈与 `IPermissionEventSink` 残留 | 手工验收 S1/S2/S3；WebUI 测试绿 |
| **S1f 收尾** | seam 移除；文档同步（`02 §5`/`03 §3`/`04 §1`/`06`/README 扫描规则）；release notes（破坏性清单、行为差异「无 Presence → 立即 Deny」、双询问现状） | 扫描无命中；Invariants + 全量测试绿 |

---

## 13. 修订记录（v3 → v4）

### 实施期修订（W1–W5 反馈，v4.1 → v4.2）

| 来源 | 问题/反馈 | 处置 |
|------|-----------|------|
| W1 | spec §4.4 未给 `PermissionResolvedEvent` 字段类型 | 定为枚举（`PermissionEffect`/`PermissionGrantScope`/`PermissionResolvedBy`），与 `PermissionResolution` 对齐（协议层映射按枚举适配） |
| W2-A | `EffectivePermissionPolicy.Resolve` 的 `Deny` 语义歧义（§5.3 vs §5.2） | §5.3 重写：返回 `Allow`（自动批准）/ `Ask`（强制交互）/ `null`（无判定），**不返回 Deny**；重评估仅对 `Allow` 收敛 |
| W2-A | `PermissionResolution` 无 `Allow()/Deny()` 工厂 | 允许实现期内联构造；不强制补齐（YAGNI） |
| W2-B | 能力门同时填 `Arguments` 与 `Metadata["arguments"]` | 接受（展示与策略/记忆分工，spec §4.2 已声明） |
| W2-B/C | ToolManager 无授权器时跳过资源门、能力门无授权器时 Deny | 接受（fail-closed 在能力门；与 spec §6 一致） |
| W2-C | `GatewayOptions.PermissionTimeoutSeconds` 无消费者 | §7.2 增「超时统一」：等待超时统一由 Manager（300s 默认）；该字段**废弃并忽略**，记入 release notes |
| W2-C | 协议字段 `PermissionId` 与事件 `RequestId` 的关系 | 协议层字段名保留（`GatewayEventData.PermissionId`），由 `RequestId` 填充（§4.4） |
| W2-D | 旧内联展示组件与新卡片重复 | §7.1 明确删除 `PermissionMessageComponent`/`PermissionBlockRenderer`/`PermissionRequestViewModel`，由 W5 取代 |
| W2 补漏 | `AddWorkspacePathTool`（Tools.FileSystem）未纳入任何工作流 | 已迁移为 `IPermissionGrantStore.AddSessionDirectory`（独占补丁） |
| 既有问题 | `TaskToolTests.TaskTool_ShouldNotReferenceExecutionJobService` 源码路径解析陈旧（`tests/src/...` 缺一层），非本次引入 | 记入 W6 可选清理 |
| W3c | §5.1 步骤 3b 未声明资源类 kind 与 Agent 规则 `Allow` 的关系（实现按「仅非资源类短路」落地） | §5.1 步骤 3b 补「且 kind 非资源类（filesystem/shell/network）」并加依据说明（见该节注释） |
| W3c | §5.1 步骤 5 未声明 `RequireInteraction=true` 亦跳过宿主通道自动批准 | §5.1 步骤 5 补「`RequireInteraction=true` 时同样跳过」 |
| W4 | §5.2 重评估误述「转 Deny→拒绝」（`Resolve` 不返回 Deny）；溢出与 `Dispose` 未声明发布结果事件 | §5.2 改为「仅 `Allow` 收敛为放行；`Ask`/`null` 保持」；`BeginAsync` 溢出与 `IDisposable` 补「发布 `PermissionResolvedEvent`」 |
| W5 | Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`（单 `_sessionId`） | 记为**已知边界**（见下方「剩余风险」；多会话同屏需按父会话分 key，属后续演进） |
| W6 | §5.2「Core-only 宿主解析 Manager 会失败」与实现不符（已有 `NullExecutionEventPublisher` 兜底） | §5.2 前置依赖改为「Core-only 由 no-op 发布器兜底，解析不失败」；代码 remarks 同步修正 |

### 放行验证修订（v4 → v4.1）

| 来源 | 问题 | 处置 |
|------|------|------|
| [阻断] | Gateway 仅注册具体类型 → 不进 `IEnumerable<IPermissionChannel>`，`TryAutoApprove` 被 Core 兜底 DenyAll 遮蔽，`auto_approve` 失效 | §7.2/§5.5 明确**具体类型 + 接口映射**双注册（`AddSingleton<IPermissionChannel>(sp => sp.GetRequiredService<GatewayPermissionChannel>())`） |
| 非阻断 | `IWorkspaceWhitelist` 消费者漏列 `ToolOutputLimiterDecorator.cs:28/35/41/80`、`ServiceCollectionExtensions.cs:516` | §10.3 补入 |
| 非阻断 | `ConditionLogic` 被 `PermissionRuleEntry.cs:233/239/245` 引用，不可删 | §4.1 改为：仅 `FileOperation` 删除，`ConditionLogic` 保留 |
| 非阻断 | 后缀表未纳入 `Authorizer`/`Mapper` | §2.2 增补四类后缀（`Aggregator`/`Factory`/`Authorizer`/`Mapper`） |
| 非阻断 | `PathSafety`/`PermissionIntegrity` 实为 internal，先例引用不实 | §3.2 更正为「必须 public（被 public ctor 注入）」 |
| 非阻断 | `PermissionRequestEvent.PermissionId` → `RequestId` 改名未声明 | §4.4 点明改名及消费点 |
| 非阻断 | S1a 未含新类型骨架，`PermissionService` ctor 无法解析 | §12 S1a 明确骨架清单 |

### v3 → v4

| 来源 | 问题 | 处置 |
|------|------|------|
| **用户指令** | 「**不需要兼容，可以完全重构**」 | 删除全部兼容妥协：`ToolBase.RequestPermissionAsync`（零调用）**删除**、`ToolContext.PermissionChannel` **删除**（工具零权限）、`ChatOptions.PermissionChannel` **删除**、`IWorkspaceWhitelist` **删除**（并入 GrantStore 目录面）、`IPermissionService` 4 个无生产调用重载 **删除**；同步 supersede `2026-08-03 §Exclusions 4` 与 decoupling §6.2/§6.3/§12.3（§2.2） |
| 评审3-1（阻断） | `internal EffectivePermissionPolicy` 注入 public `PermissionService` → CS0051 | §3.2/§5.3 改 **public** |
| 评审3-2（阻断） | §7.2 按 `IPermissionChannel` 接口解析会同进程取到 WebUI 通道（且其为 Scoped，根解析抛） | §7.2 改**具体类型** `GatewayPermissionChannel` 解析；§5.5 WebUI 通道改 **AddSingleton** |
| 评审3-3（阻断） | Presence attach 挂载点与组件拓扑不符（切会话不重跑 OnInitialized；Summary/Conference 复用 SessionWindow） | §7.1 以**活动会话**为准，切换时 Detach 旧/Attach 新，显式排除 Summary/Conference/TaskCardAggregator/后台订阅；推荐 `ActiveSessionTracker` |
| 评审3-4（阻断） | `AllowedScopes` 默认未按 kind 区分，`SessionDirectory` 无法呈现 | §4.2 注明由执行级授权器按 kind 注入；§7.1 动作映射表声明 `SessionFile → Session` |
| 评审3 事实 | `AcpPermissionContext` 字段为 `SeeingSessionId` 而非 `SessionId`；`GatewayPendingPermission.CreatedAt` 为 `DateTime`；7a 需 `IsRestricting` 前置门；tests 不存在 `ChatOptions{PermissionChannel}` 构造点 | §7.4/§7.2/§5.1 更正 |
| 评审3 非阻断 | `PermissionKindMapper` 未入清单；Manager 依赖 Hosting 注册的 `IExecutionEventPublisher`；§10.3 遗漏 `Program.cs:109-111`/`EventStreamHandler:59/190`/`DefaultExecutionContext:4/50`/`ExecutionJobService:1220`/Gateway 具体类型；`MessageEventTypeStringTests:19`；`filesystem.external` 遗留串；S1 过大 | §10.1/§5.2/§10.3/§10.4/§5.1(7c)/§12（拆 S1a–S1f） |
| 评审2 收敛项 | `Factory` 后缀归属 | §2.2 增补 |
| 评审1/2 已闭项 | N1 阶段同名冲突、N2 ChatOptions、N3 Presence、G1 白名单、B1 双询问声明、B2 破环、B3 ACP 工厂、B4 载荷 | 已落实，见对应章节 |

**剩余风险（已声明）**：① 行为差异「无 Presence → 立即 `Deny(NoChannel)`」比现状「等 5 分钟超时」更快失败；② 多通道 `TryAutoApprove` 为进程级语义（Gateway `auto_approve` 影响同进程 WebUI 会话）；③ 能力门 Ask 时双询问为现状等价行为，不消除；④ 跨会话持久化授权、审批历史、CLI 控制台通道、Gateway `PermissionMode` 在途切换为非目标；⑤ Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`（单 `_sessionId`），多会话同屏需按父会话分 key（W5 已知边界，本期容忍）。
