# QuestionTool 与交互基础设施抽取设计

**日期**：2026-09-20
**分支**：feature/modular-architecture
**状态**：设计定稿 v2（子 Agent 架构审查 8 项 Issues 已修订；待复审签字）
**依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`05-extension-guide.md`
- `docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md`（权限子系统 · 同构蓝本）
- `docs/architecture/08-permission-authorization-release-notes.md`（在途 Manager + 呈现端 + 内联卡片）
- 代码基线：`Core/Questions/Question.cs`、`Core/Permission/PermissionRequestManager.cs`、`Core/Permission/PermissionPresentationStore.cs`、`WebUI/Services/PermissionInbox*.cs`、`WebUI/Services/WebUiPermissionPresenter.cs`、`WebUI/Components/Messaging/ToolPermissionSlot.razor`、`Gateway/Permission/GatewaySubscriptionPresenter.cs`

---

## 1. 问题与动机

### 1.1 需求

模型需要"向用户结构化提问并等待作答"：一次可含多个问题，题型含单选、多选、文本输入，支持"其他"自由输入、默认值预选、必填校验。目的是让用户点选而非每次打字。

### 1.2 现状缺口

| # | 事实 | 证据 |
|---|------|------|
| G1 | 内置 Agent 已把 `question` 列入白名单，但工具不存在 | `Agents.BuiltIn/BuiltInAgents.cs:176,190` |
| G2 | 数据模型已存在但是孤儿（全仓零引用），且位于 Core（能力包不可引用） | `Core/Questions/Question.cs:1-115` |
| G3 | 无在途问题管理器、无事件、无 UI | 全仓 `QuestionRequest\|QuestionResult` 零命中 |
| G4 | 权限子系统已趟通"在途 Manager + 呈现端 + 收件箱投影 + 内联 CallId 槽位"整条路径 | `PermissionRequestManager.cs`、`PermissionInbox*.cs`、`ToolPermissionSlot.razor` |

### 1.3 冗余风险（本设计的核心约束）

Question 与 Permission 在机制上高度同构：**在途请求管理、可呈现性（Presence）注册、单例投影→Scoped 视图→组件订阅、内联 CallId 槽位、请求去抖**。若照搬一份将产生两套"等待用户"语义与维护负担。本设计**先抽取领域中立的基础设施，再让权限与 Question 共用**；抽取范围以"确定复用、低风险"为限，不做过度抽象。

---

## 2. 目标与非目标

### 2.1 目标

1. 抽取三项通用交互基础设施：可呈现性注册表、泛型在途请求管理器、内联槽位扩展点。
2. 将权限子系统迁移到通用基础设施上，保持行为等价（回归通过）。
3. 迁移并扩展 `Question` 数据模型到 Abstractions。
4. 实现 `question` 工具（独立能力包 `Seeing.Agent.Tools.Question`）。
5. 实现 Core 的 `QuestionRequestManager`。
6. 实现 WebUI 内联问答卡片（表单、必填、其他、默认值、取消）。

### 2.2 非目标

- **不改造消息卡片的折叠机制**（`@if` 卸载 vs CSS 过渡、思考卡/shell 卡闪屏）——属独立优化项，另立 spec。
- **不为普通 Tool 新建专用展示卡**（edit/read/grep 等）——分析结论已给出，另议。
- 不复用权限的授权决策链、权限枚举、fail-closed 语义、空集确认期（领域专属）。
- 不改动 Gateway/CLI 的权限协议；Question 在无 UI 宿主走降级。
- **不做"已答态回溯 UI"**：问答决议后卡片从槽位消失（与权限一致），答案经工具结果回流；UI 回看已答属后续增强。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| 在途请求 | 已发起、尚未被决议（回答/取消/超时）的交互请求 |
| 呈现端（Presenter） | 宿主侧声明"哪些会话可在本上下文呈现 UI"的对象 |
| 可呈现性（Presence） | `CanSurface(sessionId)`：是否存在能展示该会话交互的宿主 |
| 收件箱（Inbox） | UI 只读投影，从在途权威源派生卡片模型 |
| 内联槽位（Slot） | 挂在 ToolCall 块之后、按 `CallId` 关联交互卡片的组件 |

---

## 4. 复用策略（抽什么、不抽什么）

| 机制 | 决策 | 抽取物 | 落点 |
|------|------|--------|------|
| 可呈现性注册 | **抽（强推荐）** | `ISurfaceProvider` + `ISurfaceRegistry` + 实现 `SurfaceRegistry` | Abstractions 新命名空间 `Interactions`；Core 实现 |
| 在途请求管理 | **抽（推荐）** | `IPendingRequestManager<TRequest,TResponse>` + `PendingRequestManager<TRequest,TResponse>` 基类 | Abstractions 接口；Core 实现（权限与 Question 的 Manager 均在 Core，不新增程序集） |
| 内联槽位 | **抽（推荐）** | `IToolCallSlotProvider` + 管线遍历渲染 | WebUI |
| circuit 去抖/稳定快照 presenter | **抽（推荐）** | 通用 `WebUiSurfacePresenter`（可同时注册到权限与 Question 两个 registry） | WebUI |
| 单例投影 → Scoped 视图 | **不抽基类** | 各自实现（Question 投影简单，泛化收益低） | WebUI |
| 卡片 UI | **不抽** | 各自实现 | WebUI |
| 授权决策链 / 权限枚举 / fail-closed / 空集确认期 | **不抽** | 领域专属 | — |

**不新增程序集**：泛型基类与两个具体 Manager 都放 Core。能力包 `Tools.Question` 只依赖 `IQuestionRequestManager` 抽象，不接触基类实现，符合"能力包只依赖 Abstractions + 原语"。

**按领域分离 registry 实例（关键）**：权限与 Question **不共用同一个可呈现性实例**，否则 Gateway 的 `GatewaySubscriptionPresenter`（`GatewayOrchestratorV2.cs:79-82`，固定声明会话可呈现）会让 `CanSurface` 对 Question 误判为 true，导致无问答 UI 的 Gateway 阻塞 10 分钟。做法：通用 `SurfaceRegistry` 实现可实例化多次，DI 分别注册两个**语义子接口**实例：

```csharp
public interface IPermissionSurfaceRegistry : ISurfaceRegistry { }   // 替代 IPermissionPresentationStore
public interface IQuestionSurfaceRegistry : ISurfaceRegistry { }     // Question 专用
```

- 权限侧：WebUI 与 Gateway 的 presenter 注册到 `IPermissionSurfaceRegistry`。
- Question 侧：**仅**具备问答 UI 的宿主（WebUI）注册到 `IQuestionSurfaceRegistry`；Gateway/CLI 不注册 → `CanSurface=false` → 降级。
- `WebUiSurfacePresenter` 同时实现 `ISurfaceProvider` 并注册到两个 registry（一次实现、两处服务）。

---

## 5. 通用交互基础设施设计

命名空间：`Seeing.Agent.Abstractions.Interactions`（契约）；实现位于 Core。

### 5.1 可呈现性注册

```csharp
// Abstractions/Interactions/ISurfaceProvider.cs
public interface ISurfaceProvider
{
    /// <summary>当前可呈现的会话集合快照（须为已确认的稳定快照）。</summary>
    IReadOnlyCollection<string> SurfaceSessionIds { get; }

    /// <summary>稳定态变化后触发。</summary>
    event Action? SurfacedChanged;
}

// Abstractions/Interactions/ISurfaceRegistry.cs
public interface ISurfaceRegistry
{
    void Register(ISurfaceProvider provider);
    void Unregister(ISurfaceProvider provider);
    bool CanSurface(string sessionId);
    event Action? ProviderUnregistered;  // 身份移除；用于在途收敛复核
    event Action? Changed;               // 集合/内容变化；仅 UI/诊断
}
```

- Core 实现 `SurfaceRegistry`（可实例化多次，`SurfaceRegistry : IPermissionSurfaceRegistry, IQuestionSurfaceRegistry`），语义与现 `PermissionPresentationStore` 一致（线性遍历、读取异常隔离、`Unregister` 先 `ProviderUnregistered` 后 `Changed`）。
- 权限迁移见 §7；`IPermissionSurfaceRegistry` / `IQuestionSurfaceRegistry` 为两个独立实例（§4）。

### 5.2 泛型在途请求管理器

```csharp
// Abstractions/Interactions/RequestTicket.cs
public readonly record struct RequestTicket(string RequestId, string SessionId);

// Abstractions/Interactions/PendingFallbackReason.cs
public enum PendingFallbackReason { Timeout, Cancelled, Disposed, Unavailable }

// Abstractions/Interactions/IPendingRequestManager.cs
public interface IPendingRequestManager<TRequest, TResponse> : IDisposable
    where TRequest : class
    where TResponse : class
{
    Task<RequestTicket> BeginAsync(TRequest request, CancellationToken ct = default);
    Task<TResponse> WaitAsync(RequestTicket ticket, CancellationToken ct = default);
    bool TryResolve(string requestId, TResponse response, string? expectedSessionId = null);
    IReadOnlyList<TRequest> GetPending(string sessionId);
    IReadOnlyList<TRequest> GetAllPending();
    int PendingCount { get; }
    event Action? PendingChanged;
}
```

Core 抽象基类 `PendingRequestManager<TRequest, TResponse>`（**明确成败所需契约**，回应审查 Issue 1）：

```csharp
public abstract class PendingRequestManager<TRequest, TResponse>
    : IPendingRequestManager<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    protected virtual TimeSpan Timeout => TimeSpan.FromMinutes(5);

    // —— 请求访问器（基类无法从无共同契约的 TRequest 读取，必须由子类提供）——
    protected abstract string GetRequestId(TRequest request);
    protected abstract TRequest AssignRequestId(TRequest request, string id);
    protected abstract string GetSessionId(TRequest request);

    // —— 兜底响应工厂 ——
    /// <summary>超时/取消/释放/呈现端消失时（能拿到 TRequest）。</summary>
    protected abstract TResponse CreateFallback(TRequest request, PendingFallbackReason reason);
    /// <summary>ticket 未命中在途字典（不存在或已完成）时（拿不到 TRequest）。</summary>
    protected abstract TResponse CreateMissingResponse(RequestTicket ticket);

    // —— 领域钩子 ——
    /// <summary>登记前校验；返回非 null 表示立即完成（如权限溢出 Deny），基类负责登记、完成 TCS、调用 OnResolved、发 PendingChanged。</summary>
    protected virtual TResponse? ValidateOnBegin(TRequest request) => null;
    /// <summary>登记成功后（发布请求事件）。即时完成分支不调用。</summary>
    protected virtual void OnRequestBegan(TRequest request) { }
    /// <summary>决议完成后（发布决议事件 / 广播 Dismiss / 重评估通知）。Dismiss 须幂等（即时完成分支可能对未呈现请求广播）。</summary>
    protected virtual void OnResolved(TRequest request, TResponse response) { }
    /// <summary>释放前子类清理钩子（退订事件、释放 Timer）。</summary>
    protected virtual void OnDisposing() { }
}
```

基类机制（与现状权限对齐）：
- 字典 `ConcurrentDictionary<string, PendingEntry>`，`PendingEntry(TRequest, TaskCompletionSource<TResponse>)`，TCS 带 `RunContinuationsAsynchronously`。
- `BeginAsync`：`GetRequestId` 为空则分配 `Guid` 并经 `AssignRequestId` 回写；`ValidateOnBegin` 非 null → 登记 + 完成 TCS + `PendingChanged` + `OnResolved`（**不调** `OnRequestBegan`）；否则登记 + `PendingChanged` + `OnRequestBegan`（顺序对齐权限现状）。
- `WaitAsync`：ticket 未命中在途字典 → 返回 `CreateMissingResponse(ticket)`（对齐现状 `Deny(NoChannel,"请求不存在或已完成")`）；命中则 `Task.WhenAny(completion, Task.Delay(Timeout))`；超时 → `TryResolve(id, CreateFallback(req, Timeout))`；`ct.Register` → `CreateFallback(req, Cancelled)`；退出 compare-and-remove。
- `TryResolve`：`TrySetResult` 幂等 + `expectedSessionId` 归属校验；成功 → `OnResolved` + `PendingChanged`。
- `PendingChanged`：尽力通知、订阅者异常隔离。
- `Dispose`：先调 `OnDisposing()`，再对所有在途 `TryResolve(CreateFallback(req, Disposed))`（单条异常隔离），`finally` 清空 + `PendingChanged`。
- **呈现不在基类**：权限的 `PresentAsync` 仍由 `PermissionService` 在 `BeginAsync` 与 `WaitAsync` 之间调用（现状 `PermissionService.cs:241-243,314-327`）；Question 无 channel 呈现，靠 `PendingChanged` 投影驱动。基类**不提供** `BeforeWaitAsync`（避免重复呈现）。
- **事件发布不内置**：权限/Question 分别在子类的 `OnRequestBegan`/`OnResolved` 中发布各自事件，基类不耦合 `IExecutionEventPublisher`。

**权限实现映射**：`CreateFallback` → `Timeout`=Deny(Timeout)、`Cancelled`=Deny(Cancellation)、`Disposed`=Deny(Cancellation,"管理器已释放")、`Unavailable`=Deny(NoChannel,"呈现端已移除")；`CreateMissingResponse(ticket)` → `Deny(NoChannel,"请求不存在或已完成")`；`ValidateOnBegin` → 在途 ≥32 返回 `Deny(NoChannel,"队列已满")`（基类负责登记与补发终态事件）。即时完成分支不调用 `OnRequestBegan`（不发请求事件），仅 `OnResolved` 补发决议事件；`Dismiss` 广播须幂等（对未呈现请求无害）。

### 5.3 内联槽位扩展点

```csharp
// WebUI/Rendering/IToolCallSlotProvider.cs
public interface IToolCallSlotProvider
{
    int Order { get; }
    /// <summary>为指定工具调用返回槽位组件类型；无则返回 null。</summary>
    Type? GetSlotComponentType(string callId);
}
```

- 生命周期：**Scoped**（与管线一致），由 `MessageRenderPipeline` 注入 `IEnumerable<IToolCallSlotProvider>`，按 `Order` 遍历、动态 `OpenComponent` 并注入 `CallId` 参数。
- 实现：`PermissionToolCallSlotProvider`（返回 `ToolPermissionSlot`）、`QuestionToolCallSlotProvider`（**无条件**返回 `ToolQuestionSlot`，由槽位按 `CallId` 自过滤 pending；与权限槽位同构）。
- 替代 `MessageRenderPipeline.cs:244-250` 的硬编码。

---

## 6. Question 领域契约

命名空间：`Seeing.Agent.Abstractions.Questions`。**迁移** `Core/Questions/Question.cs` 到此处并删除原文件（含原命名空间）。

### 6.1 数据模型（扩展 + 序列化映射）

```csharp
public enum QuestionKind { Single, Multiple, Text }

public class QuestionOption
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class Question
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("header")] public string Header { get; set; } = "";          // ≤30 字符
    [JsonPropertyName("question")] public string QuestionText { get; set; } = "";
    [JsonPropertyName("options")] public List<QuestionOption> Options { get; set; } = new();
    [JsonPropertyName("kind")] public QuestionKind Kind { get; set; } = QuestionKind.Single;
    [JsonPropertyName("custom")] public bool AllowCustom { get; set; } = true;      // "其他"
    [JsonPropertyName("required")] public bool Required { get; set; } = true;       // 必填
    [JsonPropertyName("defaultSelectedLabels")] public List<string> DefaultSelectedLabels { get; set; } = new();
    [JsonPropertyName("defaultCustomAnswer")] public string? DefaultCustomAnswer { get; set; }
}
```

> 旧 `AllowMultiple` 移除（无外部消费者，无兼容约束）；`true` 等价 `Kind=Multiple`。

```csharp
public class QuestionRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("questions")] public List<Question> Questions { get; set; } = new();
    [JsonPropertyName("tool")] public ToolReference? Tool { get; set; }             // { MessageId, CallId }
}

public class ToolReference
{
    [JsonPropertyName("messageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
}

public class QuestionAnswer
{
    [JsonPropertyName("questionId")] public string QuestionId { get; set; } = "";
    [JsonPropertyName("selectedLabels")] public List<string> SelectedLabels { get; set; } = new();
    [JsonPropertyName("customAnswer")] public string? CustomAnswer { get; set; }
}

public enum QuestionResultStatus { Completed, Cancelled, Timeout, Unavailable }

public class QuestionResult
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("status")] public QuestionResultStatus Status { get; set; }
    [JsonPropertyName("answers")] public List<QuestionAnswer> Answers { get; set; } = new();
}
```

### 6.2 在途管理器契约

```csharp
public interface IQuestionRequestManager
    : IPendingRequestManager<QuestionRequest, QuestionResult>
{
}
```

### 6.3 事件

- `Abstractions.Events`：`QuestionRequestEvent`、`QuestionResolvedEvent`（载荷：RequestId/SessionId/CallId/问题摘要/状态）。
- `MessageEventType` 增加 `QuestionRequest`、`QuestionResolved`。
- UI 收件箱不订阅事件流（与权限一致，订阅 `PendingChanged`）；事件供 Gateway 远端观测与时间线刷新。

---

## 7. 权限子系统重构

### 7.1 改造点与迁移触点清单（**直接重命名，不保留别名**）

| 项 | 现状 → 目标 | 触点 |
|----|-------------|------|
| 呈现端注册表 | `IPermissionPresentationStore` → `IPermissionSurfaceRegistry : ISurfaceRegistry` | 接口定义；实装 `PermissionPresentationStore` → `SurfaceRegistry`；DI `PermissionServiceExtensions.cs:30` |
| 呈现端契约 | `IPermissionPresenter` → 删除，统一 `ISurfaceProvider` | `WebUiPermissionPresenter.cs`、`GatewaySubscriptionPresenter.cs`、`GatewayOrchestratorV2.cs:79-82`；测试 `WebUiPermissionPresenterTests.cs:122`、`GatewayOrchestratorPresentationTests.cs:151` |
| 在途管理器 | `PermissionRequestManager` → 继承 `PendingRequestManager<PermissionRequest, PermissionResolution>` | 领域逻辑（溢出/重评估/收敛/Dismiss/事件）保留在子类 |
| 接口重载 | `IPermissionRequestManager` **保留** 6 参 `TryResolve(effect,scope,resolvedBy,reason,expectedSessionId)` | 内部构造 `PermissionResolution` 后委派基类泛型 `TryResolve`；消费点 `PermissionInteractionService.cs:61` 与测试不变 |
| 票据类型 | `PermissionTicket` → 删除，统一 `RequestTicket` | 接口 `IPermissionRequestManager.cs:6,8`；`FakePermissionRequestManager.cs:34,37`；`PermissionRequestManagerTests.cs:167`；`PermissionAuthorizationTests.cs:457,693,694` |
| 内联槽位 | 管线硬编码 → `PermissionToolCallSlotProvider` | `MessageRenderPipeline.cs:244-250` |
| WebUI presenter | `WebUiPermissionPresenter` → `WebUiSurfacePresenter`，注册到 `IPermissionSurfaceRegistry` + `IQuestionSurfaceRegistry` | `Program.cs:106` |

其余机械重命名触点（编译期兜底，规划时计入工作量）：`AppSidebar.razor:20`（`@inject WebUiPermissionPresenter`，Scoped 拉活）、`PermissionService.cs:23,34,235`、`PermissionRequestManager.cs:22,42,55,76,117,217,311`；测试 `PermissionServiceTests.cs:108,130`、`PermissionRequestManagerTests.cs:581,610`、`PermissionPresentationStoreTests.cs`（整文件）。

### 7.2 行为等价性保证（回归基线）

1. 在途上限 32 溢出 → 立即 `Deny(NoChannel,"队列已满")` + 补发终态事件。
2. 默认超时 5 分钟；超时 → `Deny(Timeout)`。
3. 取消 → `Deny(Cancellation)`（不抛 `OperationCanceledException`）。
4. `TryResolve` 幂等（仅首个成功发布事件/广播 Dismiss）与 `expectedSessionId` 归属校验。
5. `ProviderUnregistered` → 1s 收敛仅对 `!CanSurface` 在途请求 `Deny(NoChannel,"呈现端已移除")`；`Register`/集合收缩不收敛。
6. 重评估：会话三态 `Updated` 与 `AutoApproveAll` 变化时对在途按策略放行。
7. 无呈现端 → 授权链步骤 6 立即 `Deny(NoChannel,"无交互通道")`（**Question 不照搬**）。
8. 事件发布与 `Dismiss` 广播时机不变（溢出即时完成路径新增的 Dismiss 为幂等补发，无副作用）；`PresentAsync` 仍由 `PermissionService` 调用。

### 7.3 迁移顺序

先抽 `ISurfaceRegistry` 并让权限切换到独立实例（行为不变）→ 抽泛型基类并让权限 Manager 继承（逐条回归 §7.2）→ 抽槽位扩展点 → 接入 Question。

---

## 8. QuestionTool

程序集：`src/capabilities/Seeing.Agent.Tools.Question/`（模块 `QuestionToolsModule`，工具 `QuestionTool : BuiltInToolBase`）。

### 8.1 工具定义

- **Id**：`question`（与内置 Agent 白名单 `BuiltInAgents.cs:176,190` 一致）。
- **Description**：向用户提出一个或多个结构化问题并等待作答；说明支持单选/多选/文本、"其他"、必填。
- **ParametersSchema**：`questions[]`（`id`/`header`/`question`/`options[{label,description}]`/`kind{enumsq single,multiple,text}`/`custom`/`required`/`defaultSelectedLabels`/`defaultCustomAnswer`），`required:[id,header,question]`，顶层 `required:[questions]`、`minItems:1`。
- 入口校验（失败即 `Failure`）：问题数 ≤ 10；每题选项 ≤ 20；`header` ≤ 30 字符；文本题 `options` 必须为空；`id` 唯一。

### 8.2 执行流程

```
解析 arguments → 构造 QuestionRequest(SessionId=context.SessionId, Tool={MessageId,CallId})
→ 若 IQuestionRequestManager 缺失 → Failure("问题交互通道未接入")
→ 若 !IQuestionSurfaceRegistry.CanSurface(SessionId) → Failure("当前环境不支持交互提问，请改用文本提问")
→ ticket = BeginAsync(request, ct) → result = WaitAsync(ticket, ct)
→ 按 Status 格式化输出：Completed（结构化作答）/Cancelled/Timeout/Unavailable
→ Success/Failure 返回
```

- **不 fail-closed**：无 UI 直接降级返回，让模型自行继续。
- 取消：观察 `context.CancellationToken`，不写父会话事件（工具取消契约）。

### 8.3 宿主接线（含 CapabilitySet/Scenario 登记 · 回应审查 Issue 3）

1. `.slnx` 在 `/src/capabilities/` 登记项目。
2. 宿主 csproj 加 `ProjectReference`；入口在 `AddSeeingCore` **前**加 `AddSeeingModule<QuestionToolsModule>(registry)`（WebUI/CLI/Gateway.Server/Embed.Demo）。
3. **模块 id `question.tools`**，`ProvidedTools = ["question"]`。
4. **能力集/场景登记**：把 `question.tools` 纳入相关 `BuiltInCapabilitySets`（Code/Work/Research 等，`src/.../BuiltInCapabilitySets.cs`）的 `Modules`，并同步 `BuiltInScenarios`；同步硬编码测试列表（`BuiltInScenariosTests.cs`）。否则会话 `settledToolIds` 不含 `question`，schema 不生效。
5. 工具 schema 由既有链路计算（`ToolManager` → `ExecutionJobService`），不在工具内重复。

---

## 9. Core：QuestionRequestManager

- `QuestionRequestManager : PendingRequestManager<QuestionRequest, QuestionResult>`，单例，注册到 `IQuestionRequestManager`。
- `Timeout`：默认 10 分钟（问答思考长于权限），可配置。
- 访问器：`GetRequestId=Id`、`AssignRequestId` 回写 `Id`、`GetSessionId=SessionId`。
- `CreateFallback`：`Timeout`→`Timeout`、`Cancelled`→`Cancelled`、`Disposed`→`Unavailable`（管理器释放视为通道不可用）、`Unavailable`→`Unavailable`；`CreateMissingResponse(ticket)` → `Unavailable`。
- `OnRequestBegan`：发布 `QuestionRequestEvent`；`OnResolved`：发布 `QuestionResolvedEvent`。
- 监听 `IQuestionSurfaceRegistry.ProviderUnregistered` → 1s 收敛，对 `!CanSurface` 在途请求以 `Unavailable` 完成（**非 Deny**）。
- 无呈现端不在 Manager 内 fail-closed：由工具侧先 `CanSurface` 判定（§8.2）。

---

## 10. WebUI 呈现

### 10.1 呈现端（复用实现、分离注册）

- `WebUiSurfacePresenter : ISurfaceProvider`（由现 `WebUiPermissionPresenter` 泛化）：保留 circuit 去抖（250ms）+ 稳定快照 + 空集确认（1s）；构造时注册到 `IPermissionSurfaceRegistry` 与 `IQuestionSurfaceRegistry` 两个实例。
- Gateway 仅将其 `GatewaySubscriptionPresenter`（改为 `ISurfaceProvider`）注册到 `IPermissionSurfaceRegistry` → Question 判定在 Gateway 恒 false（§4）。

### 10.2 收件箱投影

- `QuestionInbox`（Singleton，IDisposable）：订阅 `IQuestionRequestManager.PendingChanged`，`GetAllPending()` → `QuestionCardModel` 全量重建；`Changed`（按 RequestId 序列去重）。
- `QuestionInboxView`（Scoped，IDisposable）：作用域 = `SessionWindowRegistry.Windows`；50ms 单飞去抖合并；`GetByCallId(callId)`。
- `QuestionCardModel`：只读投影（问题列表、选项、kind、required、默认值、pending 状态）。

### 10.3 内联问答卡片 `QuestionCard.razor`

- 按 `Kind` 渲染：`Single` → RadioGroup；`Multiple` → CheckboxGroup；`Text` → TextArea/Input。
- 非纯文本题且 `AllowCustom` → 末尾"其他"项，选中后显示输入框。
- 默认值预选（`DefaultSelectedLabels`/`DefaultCustomAnswer`）。
- 必填校验：存在 `Required` 未作答 → 禁用提交并高亮。
- 按钮：提交（`Completed`）、取消（`Cancelled`）。
- **决议后卡片从槽位消失**（与权限一致）；不在卡片内持久化已答态。

### 10.4 内联槽位 `ToolQuestionSlot.razor`

- `[Parameter] string? CallId`；订阅 `QuestionInboxView.Changed`；`GetByCallId(CallId)` 过滤 `pending`；无则空渲染；决议后消失。
- 由 `QuestionToolCallSlotProvider` 注册（§5.3）。

### 10.5 交互服务 `QuestionInteractionService`

- `Submit(card, answers)` / `Cancel(card)` → 校验 `RequestId` 非空、`IsPending`、归属会话 → `IQuestionRequestManager.TryResolve(...)`，返回 bool。

### 10.6 DI 与生命周期（照权限）

```csharp
builder.Services.AddSingleton<QuestionInbox>();
builder.Services.AddScoped<QuestionInboxView>();
builder.Services.AddScoped<QuestionInteractionService>();
builder.Services.AddScoped<IToolCallSlotProvider, QuestionToolCallSlotProvider>();
// 两个独立实例（勿把同一实例注册到两个接口）
builder.Services.AddSingleton<IPermissionSurfaceRegistry>(_ => new SurfaceRegistry());
builder.Services.AddSingleton<IQuestionSurfaceRegistry>(_ => new SurfaceRegistry());
// WebUiSurfacePresenter 由现有注册复用（Scoped），同时注册到上述两个实例
```

- Scoped 服务需常驻组件 `@inject` 拉活（如 `AppSidebar.razor`）。
- 复用既有 `SessionWindowRegistry`、`CircuitContext`、`SessionEventStreamRouter`。

---

## 11. 数据流时序

```
LLM tool_call: question
   ▼
QuestionTool.ExecuteAsync
   │ CanSurface? ──否──> Failure("不支持交互提问") → LLM 自行继续
   ▼
IQuestionRequestManager.BeginAsync → QuestionRequestEvent
   │ PendingChanged ──> QuestionInbox(Singleton) → QuestionInboxView(Scoped,50ms)
   ▼
ToolQuestionSlot（CallId 匹配）→ QuestionCard 显现
   │ 用户提交/取消
   ▼
QuestionInteractionService.TryResolve(id, QuestionResult)
   │ TrySetResult（幂等）→ QuestionResolvedEvent → PendingChanged
   ▼
WaitAsync 返回 → QuestionTool 格式化 → 工具结果回填 LLM；卡片从槽位消失
```

---

## 12. 错误处理与边界

| 场景 | 处理 |
|------|------|
| 无 UI 宿主 | `CanSurface=false` → 工具返回"不支持交互提问"，模型继续 |
| 用户取消 | 工具返回"用户取消了作答" |
| 超时（默认 10 分钟） | 工具返回"用户未在限定时间内作答" |
| 等待期间呈现端注销 | `Unavailable` 收敛 |
| 参数非法（超限/文本题带选项/重复 id） | 工具入口 `Failure`，不发起请求 |
| 答案超长 | 提交时截断（单答案 ≤ 2000 字符）并记录 |
| 提示注入 | 回填工具结果时 JSON 转义 + 定界包裹，避免答案被当指令 |
| 并发多题 | 同会话允许多个在途；卡片按 CallId 各自渲染 |
| circuit 断开 | 呈现端注销 → 收敛 `Unavailable` |

---

## 13. 测试策略

- **通用基础设施**：`PendingRequestManager<,>`（Begin/Wait/TryResolve 幂等/超时/取消/Dispose/归属校验/`ValidateOnBegin` 立即完成路径/`CreateMissingResponse` 未知或已完成 ticket）；`SurfaceRegistry`（Register/Unregister/CanSurface/事件顺序）。
- **权限回归**：迁移后逐条验证 §7.2 的 8 项（复用既有权限测试）。
- **Question 契约**：`QuestionRequestManager`（状态码、事件、收敛）。
- **工具**：`QuestionTool`（schema、参数校验、无 UI 降级、结果格式化、取消）。
- **WebUI 投影**：`QuestionInbox`/`QuestionInboxView`（作用域过滤、CallId、去重）。
- 测试项目 `tests/capabilities/Seeing.Agent.Tools.Question.Tests` 登记 `.slnx`。
- 测试命令遵循 AGENTS.md（VSTest 直接跑程序集）。

---

## 14. 分阶段实施（供 writing-plans 细化）

1. **可呈现性抽取**：`ISurfaceProvider`/`ISurfaceRegistry`/`SurfaceRegistry` + `IPermissionSurfaceRegistry`/`IQuestionSurfaceRegistry`；权限切换到独立实例（回归）。
2. **泛型在途管理器**：`PendingRequestManager<,>`；权限 Manager 继承迁移（回归 §7.2）。
3. **内联槽位扩展点**：`IToolCallSlotProvider` + 管线遍历；权限槽位 provider 化。
4. **Question 契约迁移**：`Core/Questions` → `Abstractions/Questions`（含序列化映射、Kind/Required/Default*）；事件与 `MessageEventType`。
5. **Core `QuestionRequestManager`** 与 DI。
6. **能力包 `Tools.Question`**：模块、工具、schema、降级、CapabilitySet/Scenario 登记。
7. **WebUI**：`WebUiSurfacePresenter`（双注册）、`QuestionInbox`/`QuestionInboxView`、`QuestionCard`、`ToolQuestionSlot`、`QuestionInteractionService`、槽位 provider、DI。
8. **测试与回归**：各层单测 + 权限回归 + 端到端手测（plan agent 触发 question）。

---

## 15. 风险与回滚

| 风险 | 缓解 |
|------|------|
| 权限重构引入回归 | 分阶段、§7.2 基线逐条回归；先切 registry 再切 Manager |
| 泛型化行为偏移（溢出/重评估/收敛） | 领域策略留子类；基类只机制；补单测 |
| 共享 presenter 影响权限 fail-closed | registry 实例分离；权限 fail-closed 仍在授权链，未改动 |
| 能力包误引用 Core | 工具只用 `IQuestionRequestManager` 抽象；Review 检查 |
| 泛型抽象一次性风险 | 按 §14 顺序，registry/槽位先行，Manager 泛型化随后并可回滚（保留子类内领域逻辑） |

---

## 16. 依赖与合规

- **依赖方向**：能力包 → Abstractions（+ 原语 + Tools.Support）；Core → Abstractions；WebUI → 全部。无反向依赖。
- **Abstractions 零实现**：仅放接口/DTO/枚举/事件声明。
- **schema 单点**：`question` schema 由 `ToolManager`/`ExecutionJobService` 既有链路计算。
- **后缀即职责**：`ISurfaceRegistry`（Registry）、`IPendingRequestManager`（Manager）、`IQuestionRequestManager`（Manager）、`QuestionInteractionService`（Service）。
- **无 appsettings**：无新配置节；超时如需可经既有配置节扩展。
- **既有约束**：不改 `ToolContext` 加业务方法；不新增 God Manager；不照搬 fail-closed；无兼容垫片。
