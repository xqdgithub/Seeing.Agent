# QuestionTool 与交互基础设施 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development`（或 `dispatching-parallel-agents`）执行。本计划按**并行子 Agent + 关键位置审查 + 快速模式**编排：同波次任务文件所有权互斥，可并行分派；仅在标注「🔍 关键审查」的任务后设审查门。
>
> **执行者须知**：每个任务给出精确文件、实现契约、验证命令。快速模式 = 不做逐步红绿重构仪式，但**每个任务必须构建通过并跑通验证命令**后才提交。禁止留 TODO/占位。
>
> **开工前置**：确认目标分支（spec 声明 `feature/modular-architecture`；若当前为 `master`，行号一致但请先切分支）。同项目并发的构建可能竞争 `obj/`，波次内构建请串行执行 `dotnet build`。
>
> **Spec:** `docs/superpowers/specs/2026-09-20-question-tool-and-interaction-infrastructure-design.md`（v3，已签字）

**Goal:** 抽取可复用的用户交互基础设施（可呈现性注册 / 泛型在途请求管理器 / 内联槽位扩展点），将权限子系统迁移其上，并新增 `question` 工具与 WebUI 内联问答卡片。

**Architecture:** 契约放 `Abstractions`（`Interactions`/`Questions`），实现在 `Core`；能力包 `Tools.Question` 只依赖 Abstractions；WebUI 复用权限的收件箱→视图→内联槽位模式。权限与 Question **各持一个 `SurfaceRegistry` 实例**（避免 Gateway 误判）。

**Tech Stack:** .NET 10 / C# / Blazor Server / xUnit（VSTest 直跑程序集）/ 中央包管理。

---

## 依赖图与并行波次

```
Wave 1（4 并行，互斥文件，均不依赖本波次其他任务）
  W1-A Registry 基础设施
  W1-B 泛型在途管理器基类（🔍）
  W1-C 内联槽位扩展点
  W1-D Question 数据契约（仅 model/event/常量）
        │
Wave 2（2 并行）
  W2-A 权限子系统迁移 ← W1-A,W1-B,W1-C          🔍
  W2-B QuestionRequestManager + 接口 + DI ← W1-A,W1-B,W1-D
        │
Wave 3（2 并行）
  W3-A 能力包 Tools.Question + 场景登记 ← W1-D,W2-B
  W3-B1 WebUI 问答组件/服务/模型 ← W1-A,W1-C,W1-D,W2-B
        │
Wave 4（接线 + 回归，可并行）
  W3-B2 WebUI 接线 ← W3-A,W3-B1,W2-A
  W4-A 权限回归
  W4-B 基础设施/Question 单测
        │
Wave 5（主控）
  W4-C 端到端手测 + 终审
```

## 文件所有权矩阵（避免并行冲突）

| 文件/区 | 归属任务 | 备注 |
|---------|----------|------|
| `Abstractions/Interactions/**` | W1-A / W1-B | 各自新文件 |
| `Core/Interactions/**` | W1-A / W1-B | 各自新文件 |
| `Abstractions/Questions/**`、`Abstractions/Events/QuestionEvents.cs`、`Events/MessageEventTypes.cs` | W1-D | 独占 |
| `WebUI/Rendering/MessageRenderPipeline.cs`、`Rendering/RenderingServiceExtensions.cs` | W1-C | 独占 |
| `Core/Permission/**`、`Abstractions/Permissions/**`、`src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`、`src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs`、`WebUI/Services/WebUiSurfacePresenter.cs` | W2-A | 独占 |
| `Core/Questions/QuestionRequestManager.cs`、`Core/Questions/QuestionServiceExtensions.cs`、`Abstractions/Questions/IQuestionRequestManager.cs`、`Core/Extensions/ServiceCollectionExtensions.cs`（加 1 行） | W2-B | 独占 |
| `Seeing.Agent.slnx`、`Core/CapabilitySets/BuiltInCapabilitySets.cs`、`Core/Scenarios/BuiltInScenarios.cs`、`src/capabilities/Seeing.Agent.Tools.Question/**`、CLI/Gateway.Server 接线、`tests/capabilities/Seeing.Agent.Tools.Question.Tests/**` | W3-A | 独占 |
| `WebUI/Models/QuestionCardModel.cs`、`WebUI/Services/Question*.cs`、`WebUI/Components/Messaging/Question*.razor`、`WebUI/Rendering/Providers/QuestionToolCallSlotProvider.cs` | W3-B1 | 独占 |
| `WebUI/Program.cs`、`WebUI/Seeing.Agent.WebUI.csproj`、`WebUI/Components/AppSidebar.razor` | W2-A（presenter 行）→ W3-B2（接线行） | **跨波次串行**，不并发 |
| `tests/spine/Seeing.Agent.Tests/Permission/**` | W4-A | 权限回归 |
| `tests/spine/Seeing.Agent.Tests/{Interactions,Questions}/**`、`tests/apps/Seeing.Agent.WebUI.Tests/Questions/**` | W4-B | 基础设施/Question 单测 |

## 验证命令（统一）

```bash
# 构建
dotnet build Seeing.Agent.slnx
# Spine 测试（AGENTS.md：MTP 不可用，用 VSTest 直跑程序集）
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<Filter>"
# 能力包测试
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Question.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Question.Tests.dll
```

---

## Wave 1 — 基础设施（4 任务并行）

### Task W1-A：可呈现性注册基础设施

**Files（全部 Create）**
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/ISurfaceProvider.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/ISurfaceRegistry.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/IPermissionSurfaceRegistry.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/IQuestionSurfaceRegistry.cs`
- `src/spine/Seeing.Agent.Core/Core/Interactions/SurfaceRegistry.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Interactions/SurfaceRegistryTests.cs`

**实现契约**
```csharp
public interface ISurfaceProvider {
    IReadOnlyCollection<string> SurfaceSessionIds { get; }
    event Action? SurfacedChanged;
}
public interface ISurfaceRegistry {
    void Register(ISurfaceProvider provider);
    void Unregister(ISurfaceProvider provider);
    bool CanSurface(string sessionId);
    event Action? ProviderUnregistered;
    event Action? Changed;
}
public interface IPermissionSurfaceRegistry : ISurfaceRegistry { }
public interface IQuestionSurfaceRegistry : ISurfaceRegistry { }
```
`SurfaceRegistry : IPermissionSurfaceRegistry, IQuestionSurfaceRegistry`（Core）：
- `ConcurrentDictionary<ISurfaceProvider, byte>`；`Register` 幂等 + 订阅 `SurfacedChanged` + 触发 `Changed`。
- `Unregister` 幂等；先 `ProviderUnregistered` 再 `Changed`。
- `CanSurface`：空 id → false；遍历读 `SurfaceSessionIds`，异常 catch+warn+continue，任一包含即 true。
- 参考语义：`Core/Permission/PermissionPresentationStore.cs:16-111`。

**验证**
- 测试：Register 幂等、Unregister 事件顺序、CanSurface 命中/空 id/异常隔离。
- `dotnet build Seeing.Agent.slnx`。

- [ ] 实现 + 测试 → 构建 + `--TestCaseFilter:"FullyQualifiedName~SurfaceRegistryTests"` → 提交 `feat(interactions): add surface registry infrastructure`

### Task W1-B：泛型在途请求管理器基类 🔍 关键审查

**Files（全部 Create）**
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/RequestTicket.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/PendingFallbackReason.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Interactions/IPendingRequestManager.cs`
- `src/spine/Seeing.Agent.Core/Core/Interactions/PendingRequestManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Interactions/PendingRequestManagerTests.cs`

**实现契约**（严格对齐 spec §5.2）
```csharp
public readonly record struct RequestTicket(string RequestId, string SessionId);
public enum PendingFallbackReason { Timeout, Cancelled, Disposed, Unavailable }

public interface IPendingRequestManager<TRequest, TResponse> : IDisposable where TRequest : class {
    Task<RequestTicket> BeginAsync(TRequest request, CancellationToken ct = default);
    Task<TResponse> WaitAsync(RequestTicket ticket, CancellationToken ct = default);
    bool TryResolve(string requestId, TResponse response, string? expectedSessionId = null);
    IReadOnlyList<TRequest> GetPending(string sessionId);
    IReadOnlyList<TRequest> GetAllPending();
    int PendingCount { get; }
    event Action? PendingChanged;
}

public abstract class PendingRequestManager<TRequest, TResponse> : IPendingRequestManager<TRequest, TResponse>
    where TRequest : class {
    protected virtual TimeSpan Timeout => TimeSpan.FromMinutes(5);
    protected abstract string GetRequestId(TRequest request);
    protected abstract TRequest AssignRequestId(TRequest request, string id);
    protected abstract string GetSessionId(TRequest request);
    protected abstract TResponse CreateFallback(TRequest request, PendingFallbackReason reason);
    protected abstract TResponse CreateMissingResponse(RequestTicket ticket);
    protected virtual TResponse? ValidateOnBegin(TRequest request) => null;
    protected virtual void OnRequestBegan(TRequest request) { }
    protected virtual void OnResolved(TRequest request, TResponse response) { }
}
```
行为：
- `PendingEntry(TRequest, TaskCompletionSource<TResponse>)`，TCS 带 `RunContinuationsAsynchronously`。
- `BeginAsync`：id 空则生成并 `AssignRequestId`；`ValidateOnBegin` 非 null → 登记 + `TrySetResult` + `OnResolved` + `PendingChanged`（**不** `OnRequestBegan`）；否则登记 + `OnRequestBegan` + `PendingChanged`；返回 `RequestTicket`。
- `WaitAsync`：未命中 → `CreateMissingResponse(ticket)`；命中 → `WhenAny(completion, Delay(Timeout))`；超时 → `TryResolve(id, CreateFallback(req, Timeout))`；`ct.Register` → `CreateFallback(req, Cancelled)`；结束 `TryRemove(KVP)`。
- `TryResolve`：`expectedSessionId` 校验；`TrySetResult` 幂等；成功 → `OnResolved` + `PendingChanged`。
- `Dispose`：对在途 `TryResolve(CreateFallback(req, Disposed))` 后清空。
- `PendingChanged` 订阅者异常隔离。

**验证**（用 `sealed class TestRequest { string Id; string SessionId; }` + `PendingRequestManager<TestRequest,int>`）
- Begin 返回 ticket、`PendingCount==1`；Wait 在 TryResolve 后返回。
- TryResolve 幂等（第二次 false）；`expectedSessionId` 不符 false。
- 超时（`Timeout=50ms`）→ `CreateFallback(Timeout)`；取消 → `Cancelled`。
- 未命中 ticket → `CreateMissingResponse`。
- `ValidateOnBegin` 非 null → 立即完成、`OnRequestBegan` 未调用、`OnResolved` 调用。
- Dispose → 在途以 `Disposed` 完成。

- [ ] 实现 + 测试 → 构建 + `--TestCaseFilter:"FullyQualifiedName~PendingRequestManagerTests"` → 提交 `feat(interactions): add generic pending request manager`
- [ ] **🔍 审查**：契约完备（访问器/工厂/钩子）、与 spec §5.2 一致、无 `BeforeWaitAsync`。通过后放行 Wave 2。

### Task W1-C：内联槽位扩展点

**Files**
- Create: `samples/Seeing.Agent.WebUI/Rendering/IToolCallSlotProvider.cs`
- Create: `samples/Seeing.Agent.WebUI/Rendering/Providers/PermissionToolCallSlotProvider.cs`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/MessageRenderPipeline.cs:244-250`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/RenderingServiceExtensions.cs`（注册 provider，Scoped）

**实现契约**
```csharp
public interface IToolCallSlotProvider {
    int Order { get; }
    Type? GetSlotComponentType(string callId);
}
```
- `PermissionToolCallSlotProvider`：`Order=100`，返回 `typeof(ToolPermissionSlot)`（无条件，槽位自过滤）。
- 管线：注入 `IEnumerable<IToolCallSlotProvider>`；ToolCall 块后按 `Order` 遍历，非 null 则 `OpenComponent(type)` 并注入 `CallId` 参数（`nameof(ToolPermissionSlot.CallId)`）；替代硬编码。
- **本任务只做权限 provider**；Question provider 由 W3-B1 新增（不改本文件）。

**验证**
- `dotnet build Seeing.Agent.slnx`；WebUI 权限卡片仍内联（W4-C 手测）。

- [ ] 实现 + 构建 → 提交 `refactor(webui): add tool call slot provider extension point`

### Task W1-D：Question 数据契约（仅模型/事件/常量）

**Files**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Questions/Question.cs`（`QuestionOption`、`Question`、`ToolReference`、`QuestionKind`）
- Create: `src/abstractions/Seeing.Agent.Abstractions/Questions/QuestionRequest.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Questions/QuestionAnswer.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Questions/QuestionResult.cs`（含 `QuestionResultStatus`）
- Create: `src/abstractions/Seeing.Agent.Abstractions/Events/QuestionEvents.cs`（`QuestionRequestEvent`、`QuestionResolvedEvent`）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`（加 `QuestionRequest`、`QuestionResolved`）
- Delete: `src/spine/Seeing.Agent.Core/Core/Questions/Question.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Questions/QuestionModelTests.cs`

> **注意**：本任务**不含** `IQuestionRequestManager`（它引用 W1-B 的 `IPendingRequestManager`，与 W1-B 同波次并行会编译失败）——该接口移至 W2-B。

**实现契约**
- 按 spec §6.1 全字段 + 全 `[JsonPropertyName]`；`QuestionKind{Single,Multiple,Text}`、`QuestionResultStatus{Completed,Cancelled,Timeout,Unavailable}`。
- `MessageEventType.QuestionRequest/QuestionResolved` 常量（对照 `MessageEventTypes.cs:37,40`）。

**验证**
- `QuestionModelTests`：序列化字段名（`question`/`custom`/`defaultSelectedLabels`/`kind`）往返。
- `grep QuestionRequest` 指向新命名空间；`dotnet build Seeing.Agent.slnx` 通过（确认删除孤儿无断裂）。

- [ ] 实现 + 测试 → 构建 + `--TestCaseFilter:"FullyQualifiedName~QuestionModelTests"` → 提交 `feat(questions): migrate question data contracts to abstractions`

---

## Wave 2 — 迁移与 Core 实现（2 任务并行）

### Task W2-A：权限子系统迁移 🔍 关键审查

**Files**
- Delete: `Core/Permission/PermissionPresentationStore.cs`、`Abstractions/Permissions/IPermissionPresentationStore.cs`、`IPermissionPresenter.cs`、`PermissionTicket.cs`
- Modify: `Abstractions/Permissions/IPermissionRequestManager.cs`（返回/接收 `RequestTicket`；继承 `IPendingRequestManager<PermissionRequest,PermissionResolution>`；**保留** 6 参 `TryResolve`）
- Modify: `Core/Permission/PermissionRequestManager.cs`（继承 `PendingRequestManager<PermissionRequest,PermissionResolution>`，实现访问器/`CreateFallback`/`CreateMissingResponse`/`ValidateOnBegin`/`OnResolved`；保留溢出/重评估/收敛/Dismiss）
- Modify: `Core/Permission/PermissionService.cs:23,34,235`
- Modify: `Core/Extensions/PermissionServiceExtensions.cs`（注册 `IPermissionSurfaceRegistry`→`SurfaceRegistry` 实例、Manager）
- Modify: `src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs`、`src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs:79-82`
- Rename+Modify: `WebUI/Services/WebUiPermissionPresenter.cs` → `WebUiSurfacePresenter.cs`（实现 `ISurfaceProvider`，注册到两个 registry）
- Modify: `WebUI/Program.cs:106`、`WebUI/Components/AppSidebar.razor:20`
- Modify 测试：`FakePermissionPresentationStore`（`WebUiPermissionPresenterTests.cs:122`、`GatewayOrchestratorPresentationTests.cs:151`）、`FakePermissionRequestManager.cs:34,37`、`PermissionRequestManagerTests.cs:167,581,610`、`PermissionAuthorizationTests.cs:457,693,694`、`PermissionServiceTests.cs:108,130`、`PermissionPresentationStoreTests.cs`

**等价性基线（必须逐条保留·spec §7.2）**
1. ≥32 溢出 → `Deny(NoChannel,"队列已满")` + 补发终态事件（`ValidateOnBegin`）。
2. 默认 5min 超时 → `Deny(Timeout)`。
3. 取消 → `Deny(Cancellation)`（不抛）。
4. `TryResolve` 幂等 + 归属校验；仅首成功发事件/广播 Dismiss。
5. `ProviderUnregistered` → 1s 收敛仅 `!CanSurface` → `Deny(NoChannel,"呈现端已移除")`。
6. 重评估（三态 `Updated` / `AutoApproveAll`）。
7. 无呈现端授权链步骤 6 → `Deny(NoChannel,"无交互通道")`（不改）。
8. 事件/`Dismiss` 时机不变；`PresentAsync` 仍由 `PermissionService` 调用；溢出即时完成新增 Dismiss 幂等。

**验证**
- `dotnet build Seeing.Agent.slnx`；`--TestCaseFilter:"FullyQualifiedName~Permission"` 全绿；手验溢出/超时/取消/收敛四条。

- [ ] 实现迁移 → 构建 → 权限测试全绿 → 提交 `refactor(permission): migrate to shared interaction infrastructure`
- [ ] **🔍 审查**：8 条基线等价、双 registry 不共用、无残留旧类型。通过后放行 Wave 3。

### Task W2-B：QuestionRequestManager + 接口 + DI

**Files**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Questions/IQuestionRequestManager.cs`（`IQuestionRequestManager : IPendingRequestManager<QuestionRequest, QuestionResult> { }`）
- Create: `src/spine/Seeing.Agent.Core/Core/Questions/QuestionRequestManager.cs`
- Create: `src/spine/Seeing.Agent.Core/Core/Questions/QuestionServiceExtensions.cs`（`AddSeeingQuestions(this IServiceCollection)`：注册 `IQuestionSurfaceRegistry`→新 `SurfaceRegistry` 实例、`IQuestionRequestManager`→`QuestionRequestManager`）
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（`AddSeeingCore` 内加 `services.AddSeeingQuestions();`）
- Test: `tests/spine/Seeing.Agent.Tests/Questions/QuestionRequestManagerTests.cs`

**实现契约**
- `QuestionRequestManager : PendingRequestManager<QuestionRequest, QuestionResult>`，Singleton；`Timeout=10min`。
- 访问器：`GetRequestId=Id`、`AssignRequestId` 回写 `Id`、`GetSessionId=SessionId`。
- `CreateFallback`：`Timeout→Timeout`、`Cancelled→Cancelled`、`Disposed→Unavailable`、`Unavailable→Unavailable`；`CreateMissingResponse(ticket)` → `Unavailable`。
- `OnRequestBegan`/`OnResolved` 发 `QuestionRequestEvent`/`QuestionResolvedEvent`（`IExecutionEventPublisher` 可空）。
- 订阅 `IQuestionSurfaceRegistry.ProviderUnregistered` → 1s 去抖收敛：对 `!CanSurface(sessionId)` 在途以 `Unavailable` 完成；`Register`/收缩不收敛。

**验证**
- `QuestionRequestManagerTests`：resolve、超时/取消状态、收敛 `Unavailable`、事件发布（mock publisher）。
- 构建 + `--TestCaseFilter:"FullyQualifiedName~QuestionRequestManagerTests"`。

- [ ] 实现 + 测试 → 构建 + 测试通过 → 提交 `feat(questions): add QuestionRequestManager, interface and DI`

---

## Wave 3 — 能力包与 WebUI 组件（2 任务并行）

### Task W3-A：能力包 Tools.Question + 场景登记（轻审查）

**Files**
- Create: `src/capabilities/Seeing.Agent.Tools.Question/Seeing.Agent.Tools.Question.csproj`（模板 `Tools.Session.csproj`）
- Create: `.../QuestionToolsModule.cs`（`Id="question.tools"`，`ProvidedTools=["question"]`）
- Create: `.../QuestionTool.cs`（`: BuiltInToolBase`，`Id="question"`）
- Modify: `Seeing.Agent.slnx`（`/src/capabilities/` 加项目；`/tests/capabilities/` 加测试项目）
- Modify: CLI `samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`、`samples/Seeing.Gateway.Server/Program.cs`（`AddSeeingModule<QuestionToolsModule>` + 各自 csproj `ProjectReference`）
- Modify: `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs`（相关集合 `Modules` 加 `question.tools`）、`src/spine/Seeing.Agent.Core/Scenarios/BuiltInScenarios.cs`、`BuiltInScenariosTests.cs`
- Create: `tests/capabilities/Seeing.Agent.Tools.Question.Tests/`（csproj + `QuestionToolTests.cs`）

**实现契约**
- `QuestionTool.ParametersSchema` 按 spec §8.1；入口校验（问题数 ≤10、选项 ≤20、`header` ≤30、文本题无 options、id 唯一）。
- `ExecuteAsync`：取 `IQuestionRequestManager` + `IQuestionSurfaceRegistry`；缺失 → `Failure`；`!CanSurface(sessionId)` → `Failure("当前环境不支持交互提问，请改用文本提问")`；否则 `BeginAsync`→`WaitAsync`→按 `Status` 格式化；观察 `context.CancellationToken`；答案 JSON 转义 + 定界。
- **WebUI 的接线不在此任务**（归 W3-B2）。

**验证**
- `QuestionToolTests`：schema 有效；超限 `Failure`；`CanSurface=false` 降级；`Completed` 格式；取消传播。
- 构建 + `dotnet vstest tests/capabilities/Seeing.Agent.Tools.Question.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Question.Tests.dll`。

- [ ] 实现 + 测试 → 构建 + 测试通过 → 提交 `feat(question): add question tool capability package`
- [ ] 轻审查：schema/降级/能力集登记完整性。

### Task W3-B1：WebUI 问答组件与投影（不含接线）

**Files**
- Create: `WebUI/Models/QuestionCardModel.cs`
- Create: `WebUI/Services/QuestionInbox.cs`（Singleton）
- Create: `WebUI/Services/QuestionInboxView.cs`（Scoped，50ms 单飞去抖）
- Create: `WebUI/Services/QuestionInteractionService.cs`（Scoped）
- Create: `WebUI/Components/Messaging/QuestionCard.razor`
- Create: `WebUI/Components/Messaging/ToolQuestionSlot.razor`
- Create: `WebUI/Rendering/Providers/QuestionToolCallSlotProvider.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Questions/QuestionInboxTests.cs`（投影过滤/去重；该测试项目已引用 WebUI）

**实现契约**
- 仿权限：`QuestionInbox` 订阅 `IQuestionRequestManager.PendingChanged` → `GetAllPending()` → `QuestionCardModel`，`Changed` 按 RequestId 去重；`QuestionInboxView` 作用域=`SessionWindowRegistry.Windows`，`GetByCallId`。
- `QuestionCard.razor`：`Single`→RadioGroup、`Multiple`→CheckboxGroup、`Text`→TextArea；`AllowCustom`→"其他"输入；默认值预选；`Required` 未答禁用提交；提交→`TryResolve(Completed)`、取消→`TryResolve(Cancelled)`。
- `ToolQuestionSlot.razor`：`[Parameter] string? CallId`；过滤 pending；决议后空渲染。
- `QuestionToolCallSlotProvider`：`Order=110`，返回 `typeof(ToolQuestionSlot)`。

**验证**
- `dotnet build Seeing.Agent.slnx`；`dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~QuestionInbox"`。

- [ ] 实现 + 测试 → 构建 + 测试通过 → 提交 `feat(webui): add question components and inbox projection`

---

## Wave 4 — 接线与回归（可并行）

### Task W3-B2：WebUI 接线

**Files**
- Modify: `WebUI/Seeing.Agent.WebUI.csproj`（`ProjectReference` → `Tools.Question`）
- Modify: `WebUI/Program.cs`（`AddSeeingModule<QuestionToolsModule>`；注册 `QuestionInbox`/`QuestionInboxView`/`QuestionInteractionService`/`IToolCallSlotProvider, QuestionToolCallSlotProvider`）
- Modify: `WebUI/Components/AppSidebar.razor`（`@inject QuestionInboxView` 拉活）

> **不要修改** `WebUI/Rendering/RenderingServiceExtensions.cs`（W1-C 所有）；provider 注册写在 `Program.cs`。

**验证**
- `dotnet build Seeing.Agent.slnx`；`dotnet run --project samples/Seeing.Agent.WebUI` 启动无异常。

- [ ] 实现 → 构建 + 启动通过 → 提交 `feat(webui): wire question module and services`

### Task W4-A：权限回归
- [ ] `dotnet vstest ...Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Permission"` 全绿；对照 §7.2 八条。修复后提交 `test(permission): regression after infrastructure migration`。

### Task W4-B：基础设施/Question 单测补全
- [ ] Spine：`dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SurfaceRegistry|FullyQualifiedName~PendingRequestManager|FullyQualifiedName~Question"` 全绿。
- [ ] WebUI 投影：`dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~QuestionInbox"` 全绿。提交 `test: interaction infrastructure and question suites`。

---

## Wave 5 — 端到端（主控）

### Task W4-C：端到端手测 + 终审
- [ ] 启动 WebUI，选 plan agent 触发 `question`：验证内联卡片、单选/多选/文本、"其他"、默认预选、必填拦截、提交/取消、超时降级；CLI 触发验证降级文案。记录结果并终审 spec 覆盖。

---

## 审查检查点汇总（仅关键位置）

| 检查点 | 时机 | 审查重点 |
|--------|------|----------|
| 🔍 W1-B | Wave 1 后、放行 Wave 2 前 | 泛型基类契约完备、无 `BeforeWaitAsync` |
| 🔍 W2-A | Wave 2 后、放行 Wave 3 前 | §7.2 八条等价、双 registry 不共用、无残留旧类型 |
| 轻审查 W3-A | Wave 3 内 | schema/降级/能力集登记 |
| 终审 W4-C | 全部完成后 | 端到端 + spec 覆盖 |

---

## Self-Review（作者自检）

- **Spec 覆盖**：§5.1→W1-A；§5.2→W1-B；§5.3→W1-C；§6→W1-D/W2-B；§7→W2-A；§8/§9→W2-B/W3-A；§10→W3-B1/W3-B2；§11–§13→W3/W4。
- **占位符**：无 TODO/TBD；每任务含精确文件、契约、验证命令与提交信息。
- **类型一致性**：`RequestTicket`/`PendingFallbackReason`/`ISurfaceProvider`/`ISurfaceRegistry`/`IQuestionRequestManager`/`QuestionResultStatus` 全程同名；`SurfaceRegistry` 实现两 marker。
- **并行正确性（已修审查项）**：W1-D 不再引用 W1-B（`IQuestionRequestManager` 移 W2-B）；W3-B2 显式依赖 W3-A；修正 `BuiltInCapabilitySets`（`Core/CapabilitySets/`）、`BuiltInScenarios`（`Core/Scenarios/`）、`GatewayOrchestratorV2`（`Gateway/Core/`）路径；provider 注册落在 W3-B2 自有 `Program.cs`。
