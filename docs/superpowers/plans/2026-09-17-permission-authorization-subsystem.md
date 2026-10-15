# 权限授权子系统重构 · 实施计划（并行优先）

> **For agentic workers:** REQUIRED SUB-SKILL: 使用 `superpowers:subagent-driven-development` 执行本计划；本仓库无 worktree 依赖，可直接在当前分支工作。步骤用 `- [ ]` 跟踪。
> **设计依据（唯一真相源）**：`docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md`（v4.1）。每个子 Agent 必须**先读该 spec**，本计划只给任务切分、文件归属、精确改动与验证命令。

**Goal:** 用绿毯式重构替换权限审批链路（引擎 + 在途管理器 + 单一存储 + 会话事件流 + 内联卡片），消灭「切模式不生效 / 并行审批挂死」类缺陷，同时保持两层权限模型与越界审批扩权语义。

**Architecture:** `PermissionService.AuthorizeAsync`（决策链）+ `PermissionRequestManager`（在途唯一权威 + 策略重评估 + 事件唯一发布点）+ `IPermissionGrantStore`（记忆/白名单单一存储）+ `IPermissionPresenceStore`（可交互性）+ `IPermissionChannel`（宿主通道）+ WebUI 内联卡片投影。审批请求经 `IExecutionEventPublisher`（上提 Abstractions）推送，决策经 Manager 请求-响应回传。

**Tech Stack:** .NET 10 / C#、xUnit + Moq + FluentAssertions、Blazor Server（WebUI）、AntDesign Blazor、`dotnet build`/`dotnet vstest`（MTP 不可用）。

---

## 0. 执行模型：并行波次 + 最小审查

### 0.1 波次与并行度

| 波次 | 内容 | 并行度 | 产出 |
|------|------|--------|------|
| **W0 准备** | 基线构建/测试、确认工作分支与 spec 版本 | 串行（1 次命令） | 基线绿 |
| **W1 契约冻结** | Abstractions 新契约 + 事件 + 删旧契约/旧类型 | **1 Agent**（唯一写者，快速完成） | 契约签名冻结（不要求构建绿） |
| **W2 调用点迁移（大并行）** | 4 个**文件互斥**工作流并行迁移 | **4 Agent 并行** | 全量构建绿 |
| **R1 检查点** | 构建 + Invariants + 受影响测试 | 主控执行 | 放行 W3 |
| **W3 引擎与管理器** | `AuthorizeAsync` 决策链 / `RequestManager`+`PresenceStore` | **2 Agent 并行**（文件互斥） | 新单测绿 |
| **R2 检查点** | 新单测 + 全量构建 | 主控执行 | 放行 W4 |
| **W4 宿主接线** | Gateway 侧 / ACP+WebUI 事件与 Presence | **2 Agent 并行** | 全量构建 + 宿主测试绿 |
| **R3 检查点** | 全量构建 + Gateway/WebUI 测试 | 主控执行 | 放行 W5 |
| **W5 内联卡片** | 投影聚合器 + 卡片组件 + 交互服务 | **1–2 Agent** | UI 手工验收 |
| **R4 检查点（唯一人工验收）** | S1/S2/S3 三症状手工验证 | 主控+用户 | 放行 W6 |
| **W6 收尾** | seam 移除、文档同步、扫描、release notes | **1 Agent** | 最终全量测试绿 |
| **R5 检查点** | 全量测试 + 防回归扫描 | 主控执行 | 完成 |

**审查只在 R1–R5 执行**；W1–W6 内部不设逐任务评审（子 Agent 自带自检：改完即跑该工作流的验证命令）。

### 0.2 文件归属矩阵（防并行写冲突）

> 规则：**同一波次内，一个文件只能有一个 Agent 写**。跨波次传递用「上层已完成」保证。

| 工作流 | 独占文件/目录 |
|--------|--------------|
| W1（契约） | `src/abstractions/Seeing.Agent.Abstractions/**`（Permissions/、Events/、Models/ChatOptions.cs、Tools/ITool.cs、Components/IExecutionContext.cs、Agents/AgentContext.cs） |
| W2-A（Core 引擎骨架） | `src/spine/Seeing.Agent.Core/Core/Permission/**`、`Core/Modules/SettlementEngine.cs`、`Extensions/ServiceCollectionExtensions.cs`（权限相关行）、`Extensions/PermissionServiceExtensions.cs`、`Middlewares/PermissionMiddleware.cs`（删） |
| W2-B（Hosting/工具链） | `src/hosting/Seeing.Agent.Hosting/**`、`src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`、`Tools/BuiltIn/DefaultToolPermissionPolicy.cs`（如涉）、`Core/AgentExecutor.cs`、`Core/Decorators/ToolTimeoutDecorator.cs`、`Core/Models/DefaultExecutionContext.cs` |
| W2-C（Gateway/协议/客户端） | `src/gateway/**` |
| W2-D（ACP + WebUI + Headless/Embed） | `src/capabilities/Seeing.Agent.Acp/**`、`src/hosting/Seeing.Agent.Hosting.Web/**`、`src/hosting/Seeing.Agent.Hosting.Headless/**`、`src/hosting/Seeing.Agent.Hosting.Embed/**`、`samples/Seeing.Agent.WebUI/**` |
| W3-E（引擎） | `Core/Permission/PermissionService.cs`、`EffectivePermissionPolicy.cs`、`PermissionGrantStore.cs`、`PermissionKindMapper.cs` |
| W3-F（管理器） | `Core/Permission/PermissionRequestManager.cs`、`PermissionPresenceStore.cs`、`ExecutionContextPermissionAuthorizer.cs`、`DefaultPermissionAuthorizerFactory.cs` |
| W4-G（Gateway 接线） | `src/gateway/**` |
| W4-H（ACP/WebUI 接线） | `src/capabilities/Seeing.Agent.Acp/**`、`samples/Seeing.Agent.WebUI/**` |
| W5-I（UI） | `samples/Seeing.Agent.WebUI/**` |
| W6-J（收尾） | `docs/**`、`src/spine/.../Modules/SettlementEngine.cs`（seam 若 W2-A 未做） |

> 冲突提示：`src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs` **只由 W2-A 写**；W3-E/F 如需注册新服务，统一扩展现有的 `PermissionServiceExtensions.AddPermissionService()`（该方法体由 W3-E 在其波次内独占修改，此时 W2-A 已结束，无并发）。

### 0.2.1 计划粒度说明（重要）

本计划为**大规模同签名替换重构**（40+ 文件、同名同命名空间类型必须原子切换），因此：

- **W1/W3/W5 给完整代码或完整测试用例**（可直接照做或按 TDD 先失败后实现）。
- **W2/W4 是机械迁移**：给出**独占文件清单 + 精确改动点（file:line）+ 验证命令**；完整契约代码**只在 spec §4 维护一处**（避免计划与 spec 双份漂移）。子 Agent 必须**先读 spec** 再动手，遇契约歧义**回报主控**，不得自行改 `src/abstractions/**`。
- 这是有意的 DRY 取舍，不是占位符：每个 W2/W4 步骤都有确定的文件、确定的改法与确定的验证命令。

### 0.3 提交策略

- 每个检查点（R1–R5）通过后**由主控询问用户**是否提交；未获授权不提交（仓库规范）。
- 子 Agent 不执行 `git commit`。

### 0.4 测试命令（本环境必用）

```powershell
# 构建
dotnet build Seeing.Agent.slnx

# 测试（MTP 在本环境发现不到测试，一律对已构建程序集用 vstest）
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
# 过滤：--TestCaseFilter:"FullyQualifiedName~PermissionAuthorization"
```

---

## W0. 准备（串行，~5 分钟）

- [ ] **Step 0.1 确认基线与 spec**

```powershell
git status --short
dotnet build Seeing.Agent.slnx
```
Expected: build 成功（0 error）。记录当前 `git rev-parse --short HEAD`。

- [ ] **Step 0.2 冻结 spec 摘要**：把 `docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md` §4（契约）粘贴到主控上下文，供 W1 使用。

---

## W1. 契约冻结（1 Agent，独占 Abstractions）

**Agent 指令**（派发时必须原样传递）：先读 spec §4.1–§4.4，然后执行以下步骤。**只许改 `src/abstractions/**`**。

- [ ] **Step 1.1 新建 `Permissions/IPermissionChannel.cs`（新形态，替换旧内容）**

```csharp
namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>宿主审批通道：呈现与宿主级自动批准策略。实现必须幂等。</summary>
public interface IPermissionChannel
{
    /// <summary>宿主级自动批准策略（如 Gateway auto_approve）；null=不短路。</summary>
    PermissionEffect? TryAutoApprove(PermissionRequest request);

    ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default);

    ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default);
}
```

- [ ] **Step 1.2 新建 `Permissions/PermissionRequest.cs`、`PermissionResolution.cs`、`IPermissionAuthorizer.cs`、`IPermissionAuthorizerFactory.cs`**

（代码逐字照抄 spec §4.2；`SessionAutoApprove` 来自 `Seeing.Session`，加 `using Seeing.Session.Core;`）

- [ ] **Step 1.3 新建 `Permissions/IPermissionRequestManager.cs`、`IPermissionGrantStore.cs`、`IPermissionPresenceStore.cs`、`PermissionGrant.cs`、`PermissionTicket.cs`**

（逐字照抄 spec §4.3）

- [ ] **Step 1.4 精简 `Permissions/IPermissionService.cs`**：删除 `EvaluateAsync`/`EvaluateAgentAsync`/`EvaluateFileAsync`/`EvaluateMcpToolAsync`，保留 `EvaluateToolAsync`/`EvaluateSkillAsync`/`GetPolicyAsync`/`MergePolicies`/`InvalidateCache`/`LogAuditAsync`，新增 `AuthorizeAsync`（spec §4.2）。

- [ ] **Step 1.5 删除旧类型**：`Permissions/IPermissionChannel.cs` 旧内容（旧接口 + 旧 `PermissionRequest` 类 + `PermissionRequiredException`）、`Permissions/PermissionChannelResult.cs`、`Permissions/IWorkspaceWhitelist.cs`。

- [ ] **Step 1.6 事件**：`Events/MessageEventTypes.cs`
  - `MessageEventType.PermissionResponse` → `PermissionResolved`（常量值 `"permission.resolved"`）
  - `PermissionRequestEvent`：`PermissionId` → `RequestId`，新增 `CallId/LoopId/AllowedScopes/Arguments`
  - 新增 `record PermissionResolvedEvent : IMessageEvent`（spec §4.4）
  - 旧 `PermissionResponseEvent` 删除
- [ ] **Step 1.7 迁移 `IExecutionEventPublisher`**：`Core/Execution/IExecutionEventPublisher.cs` → `Abstractions/Events/IExecutionEventPublisher.cs`（含 `using Seeing.Agent.Abstractions.Events;` 去除；命名空间改 `Seeing.Agent.Abstractions.Events`）。**删除 Core 侧原文件**（由 W2-A 确认无残留 using）。
- [ ] **Step 1.8 字段清理**：`Models/ChatOptions.cs` 删除 `PermissionChannel` 属性与相关注释；`Tools/ITool.cs` 删除 `ToolContext.PermissionChannel`（:27-29）；`Components/IExecutionContext.cs` 删除 `PermissionChannel`（:34）；`Agents/AgentContext.cs` 的 `PermissionChannel` → `PermissionAuthorizer`（类型 `IPermissionAuthorizer?`）。
- [ ] **Step 1.9 自检**：`dotnet build src/abstractions/Seeing.Agent.Abstractions` 必须通过（Abstractions 内部自洽）。
- [ ] **Step 1.10 回报**：列出「最终契约签名清单」+ 编译结果。**不提交。**

---

## W2. 调用点迁移（4 Agent 并行，文件互斥）

> 目标：全量构建绿（S1a）。所有 Agent 先读 spec §4/§5/§6/§7/§10.3。**禁止改 `src/abstractions/**`**（W1 已冻结）；如需契约调整，回报主控。

### W2-A：Core 引擎骨架 + seam + Core DI（Agent A）

**Files:** `Core/Permission/**`、`Core/Modules/SettlementEngine.cs`、`Core/Extensions/ServiceCollectionExtensions.cs`、`Core/Extensions/PermissionServiceExtensions.cs`、`Middlewares/PermissionMiddleware.cs`

- [ ] 2A.1 删除：`SerializingPermissionChannel.cs`、`DynamicPermissionChannel.cs`、`DefaultPermissionChannel.cs`、`IPermissionMemory.cs`、`SessionPermissionMemory.cs`、`SessionWorkspaceWhitelist.cs`、`IPermissionCache.cs`、`PermissionCache.cs`、`PermissionCacheKey.cs`、`Middlewares/PermissionMiddleware.cs`。
- [ ] 2A.2 新建骨架（签名完整、逻辑最小可编译）：
  - `PermissionGrantStore.cs`（实现 spec §4.3：记忆 + `AddSessionDirectory/ContainsSessionPath`，`TrimEnd` + `PathSafety.IsPathWithinDirectory`）
  - `PermissionPresenceStore.cs`（ConcurrentDictionary 计数，`Attach/Detach/CanPresent/ClearAll`）
  - `EffectivePermissionPolicy.cs`（public sealed；`Resolve` 按 spec §5.3，读 `ISessionManager.Get` + `IOptionsMonitor`）
  - `PermissionKindMapper.cs`（映射表：`tool.execute→Tool`、`filesystem.*→File`、`shell.*→Shell`、`network.*→Network`，含 `filesystem.external` 容错）
  - `PermissionRequestManager.cs`、`ExecutionContextPermissionAuthorizer.cs`、`DefaultPermissionAuthorizerFactory.cs`、`DenyAllPermissionChannel.cs`（骨架，W3-F/W3-E 补齐逻辑）
  - `PermissionService.AuthorizeAsync` 最小实现：`Allow`（直通）——W3-E 替换为完整决策链
- [ ] 2A.3 `PermissionService.cs`：删除 4 个被删重载（含 `FileOperation` 使用）；保留其余；删 5 分钟缓存字段与清理定时器、`IPermissionCache` 依赖。
- [ ] 2A.4 `WorkspacePathGate.cs`/`WorkspaceBoundaryLifecycle.cs`：依赖改 `IPermissionGrantStore` 目录面。
- [ ] 2A.5 `SettlementEngine.cs:24-25`：`ExclusiveSeams` 去掉 `"permissionChannel"`。
- [ ] 2A.6 DI：`PermissionServiceExtensions.AddPermissionService` 内注册全部新服务（Singleton）+ `TryAddSingleton<IPermissionChannel, DenyAllPermissionChannel>()`；删除 `ServiceCollectionExtensions.cs:574-595` 旧通道工厂与 `:212`（Middleware）、`:570`（`IWorkspaceWhitelist` 注册）；确认 `:516` 装饰器工厂改 `IPermissionGrantStore`。
- [ ] 2A.7 自检：`dotnet build src/spine/Seeing.Agent.Core`（预计仍红——W2-B/C/D 未完成，属正常；只需确认本工作流文件无错）。

### W2-B：Hosting + 工具链（Agent B）

**Files:** `Hosting/**`、`Core/Tools/ToolManager.cs`、`Core/AgentExecutor.cs`、`Core/Decorators/**`、`Core/Models/DefaultExecutionContext.cs`

- [ ] 2B.1 `ExecutionJobService.cs`：删 `ResolvePermissionChannel`（:773-775/892-945）与 `:1220` 透传；改为构造 `ExecutionContextPermissionAuthorizer`（经 `IPermissionAuthorizerFactory.Create(sessionId, record.Options?.AutoApprove)`）并写入 `ChatExecutionContext.PermissionAuthorizer`。
- [ ] 2B.2 `ChatExecutionContext.cs:40/117`：`IPermissionChannel? PermissionChannel` → `IPermissionAuthorizer? PermissionAuthorizer`。
- [ ] 2B.3 `IToolManager.cs:51` + `ToolManager.cs:480/545-569`：参数/资源门改 `IPermissionAuthorizer?` 并调 `AuthorizeAsync`（从 `PermissionResourceCheck` 构造 `PermissionRequest`，`callId=toolCall.Id`，`AllowedScopes` 由授权器填充）。
- [ ] 2B.4 `AgentExecutor.cs`：`EvaluatePermissionAsync` 的 Ask 分支（:937-954）改 `context.PermissionAuthorizer.AuthorizeAsync(kind:"tool.execute", resource:toolName, callId:tc.Id)`；删 `:154-155` 的 `DefaultPermissionChannel.Instance` 兜底；透传签名中的 `IPermissionChannel` → `IPermissionAuthorizer`（:481/627/652/722/746/791/802/827/930）。
- [ ] 2B.5 `ToolBase.cs:51-70`：**删除** `RequestPermissionAsync`；`ToolTimeoutDecorator.cs:74` 删除通道拷贝；`DefaultExecutionContext.cs:4/18/42/50/55/69/71` 删除权限字段/别名。
- [ ] 2B.5b `Core/Decorators/ToolOutputLimiterDecorator.cs:28/35/41/80`：`IWorkspaceWhitelist` → `IPermissionGrantStore` 目录面；同步重写 `tests/spine/Seeing.Agent.Tests/Decorators/ToolOutputLimiterDecoratorTests.cs`（构造参数）。
- [ ] 2B.6 `IExecutionEventPublisher` 上提后的 using 修正：`ExecutionJobService.cs`、`ExecutionEventPublisher.cs`（Core 实现保留）、`HostingServiceCollectionExtensions.cs:83`。
- [ ] 2B.7 自检：`dotnet build src/hosting/Seeing.Agent.Hosting`（预计仍红于 Gateway/WebUI，属正常）。

### W2-C：Gateway 全链路（Agent C）

**Files:** `src/gateway/**`

- [ ] 2C.1 `GatewayPermissionChannel.cs`：实现新 `IPermissionChannel`（`TryAutoApprove` 读 `IOptions<GatewayOptions>`；`PresentAsync`/`DismissAsync` no-op）；删除 `_pending` 表与 `RequestAsync`/`GetPendingPermissions`/`Respond` 旧实现（保留方法名但转 `manager`）。
- [ ] 2C.2 `GatewayServiceCollectionExtensions.cs`：注册**具体类型 + 接口映射**（spec §7.2）。
- [ ] 2C.3 `GatewayHost.cs:64/91/105`：删 `new`，改 `_rootServices.GetRequiredService<GatewayPermissionChannel>()`；`GatewayServer.cs:38` 传 `IOptions<GatewayOptions>`。
- [ ] 2C.4 `GatewayOrchestratorV2.cs`：`BuildChatOptions` 补 `PermissionChannel = _permissionChannel`；删 `SetRunContext`；`SubscribeExecutionEventsAsync` 加 `PresenceStore.Attach/Detach`。
- [ ] 2C.5 `IGatewayEventSink.cs`：删 `PermissionRunContext`。
- [ ] 2C.6 `ChatEventMapper`（`Core/GatewayEventMapper.cs`）+ `Seeing.Gateway/Mapping/GatewayEventMapper.cs`：Request 事件字段改名（`RequestId`）、Resolved 事件映射。
- [ ] 2C.7 `GatewayEndpoints.cs:98-121`、`GatewayWebSocketHandler.cs`：pending/respond 转 `IPermissionRequestManager`（`TryResolve(..., expectedSessionId)`）。
- [ ] 2C.8 `GatewayConnectionManager.cs`：连接断开 → 会话取消（`TryResolve(..., Deny(Cancellation))`）。
- [ ] 2C.9 客户端/协议：`Seeing.Gateway.Client/*`、`GatewayWsFrame.cs:154` 字段同步（`PermissionId↔RequestId`）。
- [ ] 2C.10 自检：`dotnet build src/gateway/Seeing.Agent.Gateway`。

### W2-D：ACP + WebUI + Headless/Embed（Agent D）

**Files:** `capabilities/Seeing.Agent.Acp/**`、`Hosting.Web/**`、`Hosting.Headless/**`、`Hosting.Embed/**`、`samples/Seeing.Agent.WebUI/**`

- [ ] 2D.1 ACP：`AcpPermissionBridge.cs` 改注入 `IPermissionAuthorizerFactory`（`AcpPermissionContext.SeeingSessionId`）+ `RequireInteraction=true`；删 `ResolveDefaultChannel`；`AcpPermissionContext.cs:18` 删通道字段；`AcpSessionRunner.cs:80` 删传参；`AcpTool.cs:91-107/206-214` 改从 `context.Services` 取 Factory。
- [ ] 2D.2 `BlazorPermissionChannel` → 新 `EventStreamPermissionChannel`（Hosting.Web，Singleton；`PresentAsync`/`DismissAsync` no-op，`TryAutoApprove=null`）；删 `IPermissionEventSink.cs`；`WebHostingServiceCollectionExtensions.cs:47-56` 改注册（**AddSingleton**）。
- [ ] 2D.3 Headless/Embed：注册新 `DenyAllPermissionChannel`（试 `TryAddSingleton<IPermissionChannel>`）。
- [ ] 2D.4 WebUI：`Program.cs:109-111` 删 `IPermissionEventSink` 注册；`SessionWindow.razor:34/1058` 删 channel inject/赋值、`:1060` 不再复制三态、`:1402-1410` 改 `SessionManager.SetAutoApproveAsync`；`MainLayout.razor:52` 删 `<PermissionHost/>`；`SecurityPage.razor:11/81` 的 `PendingCount` 改读 `IPermissionRequestManager`；`EventStreamHandler.cs` 改新事件 + 实现 `IDisposable`；`SessionWindowTimelineSync.cs:110-111` 改事件类型。**临时**：删除 `PermissionHost.razor`/`PermissionModal.razor` 及其 VM（W5 以内联卡片取代）。
- [ ] 2D.5 `primitives`：`ISessionManager` + `SessionManager.SetAutoApproveAsync`（对齐 `SetTitleAsync:586-621`）。
- [ ] 2D.6 自检：`dotnet build samples/Seeing.Agent.WebUI`。

### R1 检查点（主控）

- [ ] R1.1 `dotnet build Seeing.Agent.slnx` → **0 error**
- [ ] R1.2 受影响测试重写（由 W2-B/W2-D Agent 各自负责，主控验证）：
  `dotnet vstest tests/spine/Seeing.Agent.Tests/...dll --TestCaseFilter:"FullyQualifiedName~ExecutionJobService|FullyQualifiedName~ToolInvoker|FullyQualifiedName~Permission"`
- [ ] R1.3 `dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/...dll` → 绿
- [ ] R1.4 派 1 个「轻量审查 Agent」核对：契约与 spec §4 一致、无旧类型残留（`rg "SerializingPermissionChannel|PermissionChannelResult|IPermissionEventSink|IPermissionMemory|IWorkspaceWhitelist"`）、依赖方向无违规（`rg "Seeing\.Agent\.Core\.csproj" src -g "*.csproj"` 仅 hosting）。
- [ ] R1.5 询问用户是否提交。

---

## W3. 引擎与管理器（2 Agent 并行）

### W3-E：授权引擎（Agent E）

**Files（独占）:** `Core/Permission/PermissionService.cs`、`EffectivePermissionPolicy.cs`、`PermissionGrantStore.cs`、`PermissionKindMapper.cs`、`tests/spine/Seeing.Agent.Tests/Permission/PermissionAuthorizationTests.cs`、`PermissionGrantStoreTests.cs`、`EffectivePermissionPolicyTests.cs`

- [ ] E1 写失败测试（TDD）：`PermissionAuthorizationTests` 覆盖 spec §11.1 全项（决策链顺序、越界审批放行后 `WorkspacePathGate` 放行含 Once、无 SessionId+Restrict→Deny、记忆短路、规则+Mapper、Override/会话/全局实时值、`RequireInteraction`、无 Presence→Deny(NoChannel)、`TryAutoApprove` 落点、`AllowedScopes` 按 kind）。
- [ ] E2 实现 `AuthorizeAsync` 完整决策链（spec §5.1 步骤 0–8）。
- [ ] E3 实现 `EffectivePermissionPolicy.Resolve`（spec §5.3）。
- [ ] E4 实现 `PermissionGrantStore` 记忆 + 目录面（spec §5.4）+ `PermissionKindMapper`。
- [ ] E5 规则编译快照（spec §8）：按「配置版本（`ConfigChanged` 递增）+ Agent 策略版本」缓存**解析结果**（非判定结果），`InvalidateCache` 清除该快照。若实现成本明显超过收益，可先落「不缓存」并在 spec §8 标注为后续优化（YAGNI），但**不得**保留旧的 5 分钟判定结果缓存。
- [ ] E6 验证：

```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionAuthorization|FullyQualifiedName~PermissionGrantStore|FullyQualifiedName~EffectivePermissionPolicy"
```

### W3-F：在途管理器与可交互性（Agent F）

**Files（独占）:** `Core/Permission/PermissionRequestManager.cs`、`PermissionPresenceStore.cs`、`ExecutionContextPermissionAuthorizer.cs`、`DefaultPermissionAuthorizerFactory.cs`、`DenyAllPermissionChannel.cs`、`tests/spine/Seeing.Agent.Tests/Permission/PermissionRequestManagerTests.cs`、`PermissionPresenceStoreTests.cs`

- [ ] F1 写失败测试（spec §11.2/§11.5）：Begin/Wait/TryResolve 幂等；取消/超时；策略变更重评估（会话 `Updated`/全局值变化 → `TryResolve(Allow, Policy)`）；回调异常隔离；上限 fail-closed；`GetPending`；`Dispose` 退订；Presence attach/detach/ClearAll。
- [ ] F2 实现 `PermissionRequestManager`（spec §5.2；事件唯一发布点、`WaitAsync` 5 分钟超时、`IDisposable`）。
- [ ] F3 实现 `PermissionPresenceStore`、`ExecutionContextPermissionAuthorizer`（含 `AllowedScopes` 按 kind 填充）、`DefaultPermissionAuthorizerFactory`、`DenyAllPermissionChannel`。
- [ ] F4 验证：

```powershell
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionRequestManager|FullyQualifiedName~PermissionPresenceStore"
```

### R2 检查点（主控）

- [ ] R2.1 `dotnet build Seeing.Agent.slnx` 绿
- [ ] R2.2 上述两组测试 + `dotnet vstest tests/primitives/Seeing.Session.Tests/...dll --TestCaseFilter:"FullyQualifiedName~SetAutoApprove"` 全绿
- [ ] R2.3 派 1 个审查 Agent 复核 `AuthorizeAsync` 与 spec §5.1 **逐条对齐**（含 7a/7b 白名单与记忆语义）
- [ ] R2.4 询问用户是否提交。

---

## W4. 宿主接线（2 Agent 并行）

### W4-G：Gateway 完善（Agent G）

**Files（独占）:** `src/gateway/**`、`tests/gateway/Seeing.Gateway.Tests/**`

- [ ] G1 测试：事件映射（Request/Resolved）、respond/pending 转 Manager（sessionId 校验）、`GatewayPendingPermission` 全字段还原（`CreatedAt` 的 `DateTimeOffset→DateTime`）、Presence attach/detach（外部订阅）、断连取消、实时 `PermissionMode`（新请求）。
- [ ] G2 补齐实现与测试通过。
- [ ] G3 验证：`dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll`

### W4-H：ACP 与 WebUI 接线（Agent H）

**Files（独占）:** `src/capabilities/Seeing.Agent.Acp/**`、`samples/Seeing.Agent.WebUI/Services/**`、`samples/Seeing.Agent.WebUI/Pages/Session.razor`、`tests/apps/Seeing.Agent.WebUI.Tests/**`、`tests/capabilities/Seeing.Agent.Acp.Tests/**`

- [ ] H1 ACP 测试（`RequireInteraction` 触发询问；Factory 回退路径）+ 修 `AcpToolTests.FakeSessionManager`（补 `SetAutoApproveAsync`）。
- [ ] H2 WebUI：新增 `ActiveSessionTracker`（Singleton，refcount + circuit 维度）；`Session.razor` OnInitialized/切换/Dispose 记账；`SessionWindowTimelineSyncTests`/`EventStreamHandlerTests` 重写为新事件。
- [ ] H3 验证：

```powershell
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Acp.Tests/bin/Debug/net10.0/Seeing.Agent.Acp.Tests.dll
```

### R3 检查点（主控）

- [ ] R3.1 全量构建 + 全量测试：

```powershell
dotnet build Seeing.Agent.slnx
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll
```
- [ ] R3.2 询问用户是否提交。

---

## W5. 内联卡片（1 Agent，UI 强耦合）

**Files（独占）:** `samples/Seeing.Agent.WebUI/Services/PermissionCardAggregator.cs`、`Services/PermissionInteractionService.cs`、`Models/PermissionCardModel.cs`、`Components/Messaging/PermissionCard*.razor`、工具卡片渲染接入点、`tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs`

- [ ] I1 写失败测试：CallId 归纳、Resolved 收敛、`GetPending` 对账、忽略陈旧重放、会话隔离、动作↔作用域映射（Once/Session/SessionDirectory）。
- [ ] I2 实现 `PermissionCardAggregator`（Scoped）+ `PermissionCardModel` + `PermissionInteractionService`（调 `manager.TryResolve`）。
- [ ] I3 工具卡片内联渲染：在工具调用卡片渲染分支（`Components/LoopGroupItem.razor` 的 ToolCall 区域，或同目录工具卡片组件）以 `CallId` 从 `PermissionCardAggregator` 取 `PermissionCardModel`，内联渲染「本次允许 / 始终允许 / 允许此目录（仅 filesystem.*）/ 本次拒绝 / 始终拒绝」；**不改 `ToolCallViewModel`**；渲染只读无副作用；按 `RequestId` 使用 `@key`。
- [ ] I4 验证：`dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/...dll --TestCaseFilter:"FullyQualifiedName~PermissionCardAggregator"`

### R4 检查点（唯一人工验收，主控 + 用户）

- [ ] R4.1 启动 WebUI：`dotnet run --project samples/Seeing.Agent.WebUI`
- [ ] R4.2 **S1**：进入交互式审批会话 → 切三态「自动批准」→ 发新消息（含工具）→ 不再弹审批、工具直接执行。
- [ ] R4.3 **S2**：安全设置打开全局「自动批准所有权限」保存 → 新消息工具直接执行（无需重启）。
- [ ] R4.4 **S3**：构造单轮并行两工具调用 → 卡片内联各自审批；先允许第一个、再允许第二个 → 两者均正常完成，无「闪现关闭/永久等待」。
- [ ] R4.5 刷新页面 → 未决审批卡片按 `GetPending` 重建。
- [ ] R4.6 询问用户是否提交。

---

## W6. 收尾（1 Agent）

**Files（独占）:** `docs/**`、`src/spine/Seeing.Agent.Core/Modules/SettlementEngine.cs`（若 W2-A 未移除 seam）

- [ ] J1 文档同步：`docs/architecture/02 §5`（`EventStreamPermissionChannel`）、`03 §3`（移除 `permissionChannel` seam）、`04 §1`（增补 `Aggregator`/`Factory`/`Authorizer`/`Mapper`）、`06`（复扫记录）、`README.md`（扫描命令增加权限契约分层规则）。
- [ ] J2 release notes（`docs/` 内合适位置或 CHANGELOG）：破坏性清单（`ChatOptions.PermissionChannel`/`ToolContext.PermissionChannel`/`ToolBase.RequestPermissionAsync`/`IWorkspaceWhitelist`/旧 `IPermissionChannel` 等）、行为差异（无 Presence → 立即 `Deny(NoChannel)`）、双询问现状说明。
- [ ] J3 不变量与扫描：

```powershell
rg "Seeing\.Agent\.Core\.csproj" src -g "*.csproj"          # 仅 hosting/*
rg "Seeing\.Agent\.(Core|Hosting)\.csproj" src/gateway -g "*.csproj"   # 空
rg "SerializingPermissionChannel|IPermissionMemory|IWorkspaceWhitelist|IPermissionEventSink" src samples tests   # 空
rg "permissionChannel" src -g "*.cs"                          # 仅无关标识（无 seam）
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
```

### R5 最终检查点（主控）

- [ ] R5.1 `dotnet build Seeing.Agent.slnx` + 全量测试（spine/hosting/gateway/apps/primitives/capabilities）全绿
- [ ] R5.2 三症状手工回归（R4.2–R4.5 复跑一遍）
- [ ] R5.3 询问用户是否提交最终波次。

---

## 附录 A：并行派发模板（主控使用）

```
task(subagent_type="general", description="<工作流名>", prompt="""
你是本仓库重构的实施子 Agent。先读设计 spec：docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md（v4.1），
再读实施计划对应波次：docs/superpowers/plans/2026-09-17-permission-authorization-subsystem.md（<WX 段>）。
文件归属：<独占文件清单>。严禁修改归属外文件与 src/abstractions/**（已冻结）。
按 TDD：先写失败测试 → 跑 → 最小实现 → 跑通过。完成后回报：改动文件清单、验证命令与实际输出、遇到的契约歧义。
""")
```

## 附录 B：风险与回退

| 风险 | 缓解 |
|------|------|
| W2 四 Agent 并行导致跨工作流符号不一致 | 契约在 W1 冻结；R1 的轻量审查核对签名；编译错误即检查点 |
| `ExecutionJobService` 同时被 W2-A/W2-B 触碰 | 文件归属矩阵：`ExecutionJobService.cs` 仅 W2-B |
| S1a 期间构建长期红 | 明确 R1 前不要求绿；子 Agent 各自只验证本工作流可编译范围 |
| 手工验收发现 UI 行为差异 | R4 为唯一人工门；必要时回到 W5（文件归属唯一，不阻塞其它波次） |
| 破坏性变更遗漏文档 | W6-J2 release notes 为强制项；R5 扫描校验 |
