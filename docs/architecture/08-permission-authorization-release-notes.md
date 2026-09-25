# 08 权限授权子系统 Release Notes

**日期：** 2026-09-17（增量：2026-09-25 安全收紧与 MCP 授权门，见 §7）
**分支：** `feature/permission-authorization-refactor`
**设计规格：** 原设计规格文件已失落（历史断链）；行为契约以本 release notes 为准。
**合规记录：** [`06-compliance-audit.md` §权限授权子系统复扫](06-compliance-audit.md)  
**后续整改：** [`11 全项目整改 Release Notes`](11-remediation-release-notes.md)（2026-09-25）

---

## 1. 概要

审批从「通道 + 阻塞等待 + 侧信道 + 弹窗」重构为：

> **统一授权引擎 + 在途管理器（`IPermissionRequestManager`）+ 会话事件流 + 内联卡片投影**

- 单一真相源：在途状态 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresentationStore`（2026-09-19 收敛重构后）；生效策略 → `EffectivePermissionPolicy`（实时解析）。
- 无兼容约束（用户明确「不需要兼容」）：旧契约与旧栈**全部删除**，不保留垫片/适配层。

---

## 2. 破坏性变更清单

### 2.1 契约与成员删除 / 改名

| 类型 / 成员 | 位置 | 处置 | 替代 |
|-------------|------|------|------|
| `ChatOptions.PermissionChannel` | `Abstractions/Models/ChatOptions.cs` | **删除** | 宿主通道经 DI 解析；可交互性经 `IPermissionPresenceStore` |
| `ToolContext.PermissionChannel` | `Abstractions/Tools/ITool.cs` | **删除** | 工具零权限代码；资源级检查经 `IToolPermissionPolicy` 映射到 `ToolManager` |
| `IExecutionContext.PermissionChannel`（含 `DefaultExecutionContext` 对应实现） | `Abstractions/Components/IExecutionContext.cs` | **删除** | `IPermissionAuthorizer`（执行级授权入口） |
| `AgentContext.PermissionChannel` | `Abstractions/Agents/AgentContext.cs` | 改名 | `AgentContext.PermissionAuthorizer` |
| `ToolBase.RequestPermissionAsync` | `Tools.Support/ToolBase.cs` | **删除**（零调用） | 资源门集中在 `ToolManager` |
| `IWorkspaceWhitelist` / `SessionWorkspaceWhitelist` | `Abstractions/Permissions/`、Core | **删除** | `IPermissionGrantStore` 目录面（`AddSessionDirectory` / `ContainsSessionPath`） |
| 旧 `IPermissionChannel` / `PermissionChannelResult` / `PermissionChannelAction` / 旧 `PermissionRequest` / `PermissionRequiredException` | `Abstractions/Permissions/` | **删除**（`IPermissionChannel` 为同名新契约） | 新 `IPermissionChannel`（`TryAutoApprove`/`PresentAsync`/`DismissAsync`）、`PermissionRequest`（新）、`PermissionResolution`、`PermissionTicket` |
| `IPermissionMemory` / `SessionPermissionMemory` / `PermissionMemoryEntry` | Core | **删除** | `IPermissionGrantStore` 记忆面 |
| `IPermissionCache` / `PermissionCache` / `PermissionCacheKey` | Core | **删除** | 实时策略（`EffectivePermissionPolicy`），无结果缓存 |
| `PermissionMiddleware` | `Core/Middlewares/` | **删除** | 授权引擎 + `PermissionService` |
| `SerializingPermissionChannel` / `DynamicPermissionChannel` / `DefaultPermissionChannel`（含 `Instance`/`AutoApproveInstance`） | Core | **删除** | `PermissionRequestManager`（**无串行闸门**） |
| `IPermissionEventSink` | `Hosting.Web/Permissions/` | **删除** | `IExecutionEventPublisher` 事件流 + UI 投影 |
| `BlazorPermissionChannel`（旧形态） | `Hosting.Web/Permissions/` | **删除** | `EventStreamPermissionChannel` |
| `PermissionRunContext` + `SetRunContext` | `Seeing.Agent.Gateway` | **删除**（AsyncLocal 断裂链路） | 事件流 + `PermissionRequestManager` |
| `MessageEventType.PermissionResponse` | `Abstractions/Events/MessageEventTypes.cs` | **改名** | `MessageEventType.PermissionResolved`（值 `permission.resolved`） |
| `PermissionRequestEvent.PermissionId` | `Abstractions/Events/MessageEventTypes.cs` | **改名** | `PermissionRequestEvent.RequestId`（协议层 `GatewayEventData.PermissionId` 字段名保留，由 `RequestId` 填充） |
| `IPermissionService` 的 `EvaluateAsync` / `EvaluateAgentAsync` / `EvaluateFileAsync` / `EvaluateMcpToolAsync` | `Abstractions/Permissions/IPermissionService.cs` | **删除**（无生产调用） | 统一 `AuthorizeAsync` + 保留 `EvaluateToolAsync` / `EvaluateSkillAsync` |
| `FileOperation` | `Abstractions/Permissions/` | **删除** | 仅被已删的 `EvaluateFileAsync` 使用；`ConditionLogic` **保留** |
| 旧 UI 权限展示栈：`PermissionHost.razor` / `PermissionModal.razor` / `PermissionMessageComponent.razor` / `PermissionBlockRenderer.cs` / `PermissionRequestViewModel.cs` | `samples/Seeing.Agent.WebUI` | **删除** | 工具卡片内联 `PermissionCard` + `PermissionCardAggregator` 投影 |

### 2.2 新增契约（供迁移对照）

- Abstractions：`IPermissionAuthorizer`（+`IPermissionAuthorizerFactory`）、`IPermissionRequestManager`、`IPermissionGrantStore`、`IPermissionPresenceStore`、`IPermissionChannel`（新）、`PermissionRequest`、`PermissionResolution`、`PermissionGrant`、`PermissionTicket`、`PermissionResolvedEvent`、`IExecutionEventPublisher`（由 Core 上提）。
- Core：`PermissionRequestManager`、`PermissionGrantStore`、`PermissionPresenceStore`、`EffectivePermissionPolicy`、`PermissionKindMapper`、`ExecutionContextPermissionAuthorizer`、`DefaultPermissionAuthorizerFactory`、新 `DenyAllPermissionChannel`、`NullExecutionEventPublisher`（Core-only 兜底）。
- Hosting.Web：`EventStreamPermissionChannel`（Singleton）。
- WebUI：`PermissionCardAggregator` / `PermissionCardModel` / `PermissionCard.razor` / `PermissionInteractionService` / `ActiveSessionTracker`。

### 2.3 全局权限收件箱重构（2026-09-19）

设计规格：[`2026-09-19-global-permission-inbox-design.md`](../superpowers/specs/2026-09-19-global-permission-inbox-design.md)（v3.1）。

| 类型 / 成员 | 位置 | 处置 | 替代 |
|-------------|------|------|------|
| `IPermissionPresenceStore` / `PermissionPresenceStore` | `Abstractions/Permissions/`、Core | **删除** | `IPermissionPresenter`（宿主声明的会话集合快照）+ `IPermissionPresentationStore`（登记表 + `CanSurface` + `PresenterUnregistered`/`Changed`） |
| `PermissionCardAggregator` | WebUI（sample） | **删除** | `PermissionInbox` / `PermissionInboxView`（唯一权威仍在 `IPermissionRequestManager`） |
| `ActiveSessionTracker` | WebUI（sample） | **删除** | 呈现端 `WebUiPermissionPresenter`（circuit 维度） |
| `IPermissionPresenceStore.CanPresent` 决策点 | `PermissionService` 步骤 6 | **改名/改语义** | `IPermissionPresentationStore.CanSurface(sessionId)` |

**新增契约（供迁移对照）：**

- Abstractions：`IPermissionPresenter`（`SurfaceSessionIds` + `SurfacedChanged`）、`IPermissionPresentationStore`（`Register`/`Unregister`/`CanSurface`/`PresenterUnregistered`/`Changed`；`PresenterUnregistered` 为收敛信号，`Changed` 仅 UI 通知）。
- `IPermissionRequestManager`：新增 `GetAllPending()` 与 `PendingChanged`（在途变更尽力通知；`BeginAsync`/`TryResolve`/`WaitAsync` 超时取消/`Dispose` 触发）。
- `ToolContext.PermissionAuthorizer`（`IPermissionAuthorizer?`，执行级 override 贯通，注入点在 `ToolManager.ExecuteAsync`）。
- 收敛策略：**仅呈现端注销（`PresenterUnregistered`）时收敛**（宽限 1s 复核）；注册与 `SurfaceSessionIds` 内容收缩仅触发 `Changed`，不收敛，由 300s 超时兜底（已知边界）。

---

## 3. 行为差异（同输入不同结果）

1. **无交互 Presence → 立即 `Deny(NoChannel)`**：旧行为是等待宿主通道 5 分钟超时；新行为在 `CanPresent=false` 时立即拒绝（Headless/Embed/Core-only 宿主即刻失败，不再悬挂）。
2. **资源类 kind 不套 Agent 规则 Allow**：`filesystem.*` / `shell.*` / `network.*` 等资源类 kind 的 Agent 规则 **Allow 不短路**（仅 Deny 生效）；工作区内由边界预检放行，越界必须走审批。依据：旧 `EvaluateFileAsync`/`EvaluateMcpToolAsync`/`EvaluateAgentAsync` 无生产调用者，资源门（`ToolManager`）从不套用 Agent 规则。
3. **`RequireInteraction=true` 短路宿主自动批准**：步骤 4 的生效开关与步骤 5 的宿主通道 `TryAutoApprove` 均被跳过，直接进入询问（ACP「后端工具调用需确认」不受 Gateway `auto_approve` 影响）。
4. **超时统一 300s**：等待超时统一由 `PermissionRequestManager` 负责（默认 5 分钟；`PermissionRequest.TimeoutSeconds` 可覆盖）。
5. **资源门仅应用 Deny 规则**：与第 2 点同源——资源门不放行 Allow，只拦截 Deny 与触发审批。
6. **在途上限 32**：溢出立即 `Deny(NoChannel,"队列已满")`，并补发 `PermissionResolvedEvent`（使拒绝可被 UI/Gateway 观测）。
7. **多通道自动批准为进程级语义**：同进程 WebUI 与 Gateway 并存时，Gateway `auto_approve` 对 WebUI 会话同样生效（按来源隔离属后续演进）。
8. **子代理实时跟随父会话三态（2026-09-19 修订）**：子会话不再冻结父会话 `AutoApprove` 到执行级 `ChatOptions.AutoApprove`；`EffectivePermissionPolicy` 对 `Kind=SubAgent` 且自身 `FollowGlobal` 的会话沿父链实时上溯。父会话执行中切换「默认/自动/确认」即时作用于子代理（含在途请求；`PermissionRequestManager` 在会话 `Updated` 时全量重评估）。父链索引经 `ISessionGroupManager.TryGetParent`（内存快照）。

---

## 4. 废弃 / 忽略

| 配置 | 处置 |
|------|------|
| `GatewayOptions.PermissionTimeoutSeconds` | **废弃并忽略**（字段保留以避免配置破坏）；超时改由 `PermissionRequestManager` 统一（见 3.4） |
| `PermissionService` 5 分钟结果缓存与清理定时器 | **删除**；策略实时解析，热重载即时生效 |
| `SettlementEngine.ExclusiveSeams` 的 `"permissionChannel"` | **移除**（已核实无模块提供该 seam） |

---

## 5. 已知边界

| 边界 | 说明 |
|------|------|
| ~~Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`~~ | **已关闭**（2026-09-19：`PermissionCardAggregator` 删除，`PermissionInboxView` 按 `registry.Windows` 会话集合投影，多会话同屏按会话分组） |
| 能力门 Ask 时双询问 | 能力门（`AgentExecutor`）与资源门（`ToolManager`）在能力门解析为 Ask 时各询问一次；现状等价，不合并/不消除（非目标） |
| 多通道 `TryAutoApprove` 进程级 | 见 3.7 |
| 非目标 | 跨会话持久化授权、审批历史、CLI 控制台通道、Gateway `PermissionMode` 在途切换 |

---

## 6. 迁移指引（第三方 / 插件）

- **工具**：移除一切权限代码；如需资源级检查，实现 `IToolPermissionPolicy`（映射到 `ToolManager` 统一入口）。
- **宿主**：不要设置 `ChatOptions.PermissionChannel`；改为注册 `IPermissionChannel`（Host Shape），并向 `IPermissionPresentationStore` 注册 `IPermissionPresenter` 声明可呈现的会话集合。
- **审批请求回传**：使用 `IPermissionRequestManager.TryResolve(requestId, decision, scope, resolvedBy, reason, expectedSessionId)`（幂等）。
- **事件订阅**：监听 `PermissionRequestEvent` / `PermissionResolvedEvent`（`MessageEventType.PermissionResolved`）。

---

## 7. 增量变更（2026-09-25 全项目整改）

来源：批次 1 安全止损、批次 1.4 MCP 授权门；汇总见 [`11 全项目整改 Release Notes`](11-remediation-release-notes.md)。

### 7.1 行为差异（同输入不同结果）

1. **Once 批准不再写入会话白名单**：越界审批放行后，仅当 `resolution.Scope != PermissionGrantScope.Once`（SessionDirectory 及以上）才写入目录白名单。对 `filesystem.write` 选「本次允许」后，同目录再次访问（含 delete）仍会询问。**破坏性收紧**（旧行为：Once 批准即整目录免审）。
2. **`RequireInteraction=true` 绕过工作区白名单**：步骤 1 边界预检的 Allow 分支新增 `!normalized.RequireInteraction` 守卫——即便命中华名单/工作区 gate，`RequireInteraction` 请求仍进入询问（对齐契约；否则后端工具调用可被静默放行）。
3. **MCP 工具 server 粒度审批**：MCP 工具以 `mcp.execute` kind、resource=server 名发起资源审批（`McpToolPermissionGate`）；`PermissionService.IsResourceKind` 将 `mcp.` 前缀纳入资源类，Agent 的 `Allow(Tool,"*")` 不再短路。审批记忆按 **server 粒度**（批准一次=信任该 server 全部工具）。`plan`/`explore` 的 `AllowedTools` 白名单继续兜底。

### 7.2 已知边界（互补）

| 边界 | 说明 |
|------|------|
| MCP server 粒度记忆上限 | 单 server 批准即信任其全部工具（含后续新增工具）；按工具隔离属后续演进 |
| SessionDirectory 批准的 kind 维度 | 批准 `write` 后同目录 `delete` 仍免审（体验/安全折中，维护者知情接受；见 06 接受残留） |

### 7.3 ACP 执行路径 Hook 面差异（明示）

ACP（`AcpPassthroughExecutor`）当前**不触发 chat 级 Hook**（如 `chat.on_error`）——Native 路径的横切能力在 ACP 路径缺位；取消路径已对齐 Native（排空后产出 `LoopCancelledEvent`）。此为**已知差异**，不做行为增强（spec §4 E）。
