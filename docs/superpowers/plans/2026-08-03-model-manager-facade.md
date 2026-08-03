# ModelManager 门面重构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以 `IModelManager` 作为模型域唯一对外门面，会话/执行路径只传 modelRef；删除 `SelectedModelProvider`；抽干分散的默认模型解析。

**Architecture:** `ModelManager` 实现 `IModelManager`（并过渡期实现 `IModelConfigManager`）。解析/Seed/Apply 集中在门面内；`AgentSelectionResolver` 只保留 Agent/Mode；App/Gateway/WebUI 会话路径不再注入 `IProviderManager`、不再调用 `ModelRef`。

**Tech Stack:** .NET 10.0, C#, xUnit 2.9, Moq 4.20, FluentAssertions 6.12

## Global Constraints

- 跨层只传 **modelRef** 字符串（`provider/model` 或裸 id）
- 会话/执行/Gateway/Tools：**禁止** 注入 `IProviderManager`、禁止直接调用 `ModelRef`
- 管理页可继续用 `IProviderManager`
- **删除** `SelectedModelProvider`，不做历史兼容
- Seeing.Session **不** 依赖 Seeing.Agent
- Native 优先级：`request > session > Agent.Model > DefaultModel`
- ACP：`request > session`，不回退 Native DefaultModel
- 工作区已有临时修补（ChatOrchestrator 注入 ProviderManager 等）须在本计划中**收束为门面调用**，不得保留分散实现

**Spec:** [docs/superpowers/specs/2026-08-03-model-manager-facade-design.md](../specs/2026-08-03-model-manager-facade-design.md)

---

## File Structure

**Create:**
- `src/Seeing.Agent/Llm/IModelManager.cs` — 对外门面
- `src/Seeing.Agent/Llm/ModelManager.cs` — 实现（配置 + Resolve/Seed/Apply）；可内嵌/委托现有 `ModelConfigManager` 逻辑
- `tests/Seeing.Agent.Tests/Llm/ModelManagerTests.cs` — 门面单测

**Modify (core):**
- `src/Seeing.Agent/Llm/IModelConfigManager.cs` — Obsolete 或由 `IModelManager` 继承
- `src/Seeing.Agent/Llm/ModelConfigManager.cs` — 逻辑并入 `ModelManager` 或作内部协作类
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` — DI 注册
- `src/Seeing.Agent/Core/AgentSelectionResolver.cs` — 删除模型方法
- `src/Seeing.Agent/Core/AcpExecutionOverrides.cs` — 改用 `IModelManager.ResolveAcpModel`
- `src/Seeing.Agent/Core/AgentRuntimeManager.cs` — 有效模型委托门面
- `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` — 去掉 Provider 拆分

**Modify (session):**
- `src/Seeing.Session/Core/SessionData.cs` — 删除 `SelectedModelProvider`
- `src/Seeing.Session/Core/ISessionManager.cs` — `SetModelAsync` 单参数
- `src/Seeing.Session/Management/SessionManager.cs` / `SessionForker.cs` — 同步删除

**Modify (app/gateway/ui):**
- `src/Seeing.Agent.App/ChatOrchestrator.cs`
- `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- `src/Seeing.Agent.Gateway/Core/GatewaySessionResolver.cs` / `GatewaySessionService.cs` / `Hosting/GatewayHost.cs`
- `samples/Seeing.Agent.WebUI/Pages/Session.razor` / `State/SessionState.cs`
- `src/Seeing.Agent.TokenBudget/Hooks/BudgetModelLimitHandler.cs`

**Modify (tests):** 所有引用 `SelectedModelProvider` / 旧 Resolver 模型 API 的测试

---

### Task 1: 定义 `IModelManager` 并写失败测试

**Files:**
- Create: `src/Seeing.Agent/Llm/IModelManager.cs`
- Create: `tests/Seeing.Agent.Tests/Llm/ModelManagerTests.cs`
- Modify: `src/Seeing.Agent/Llm/IModelConfigManager.cs`（可选：`IModelManager : IModelConfigManager`）

**Interfaces:**
- Produces: `IModelManager` with methods below

- [ ] **Step 1: 创建接口**

```csharp
// src/Seeing.Agent/Llm/IModelManager.cs
using Seeing.Session.Core;

namespace Seeing.Agent.Llm;

/// <summary>模型域对外唯一门面：目录、默认模型、解析、会话读写。</summary>
public interface IModelManager : IModelConfigManager
{
    string? ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName);
    string? ResolveAcpModel(string? requestModelRef, string? sessionModelRef);
    string GetSessionModelRef(SessionData session);
    bool ApplyModelToSession(SessionData session, string? modelRef);
    bool SeedSessionModel(SessionData session, string agentName);
}
```

- [ ] **Step 2: 写失败测试（先测 Resolve / Seed / Apply）**

```csharp
// tests/Seeing.Agent.Tests/Llm/ModelManagerTests.cs
public class ModelManagerTests
{
    [Fact]
    public void ResolveNativeModel_PrefersRequestOverSessionAgentAndDefault() { /* ... */ }

    [Fact]
    public void ResolveNativeModel_PrefersAgentModelOverDefault() { /* ... */ }

    [Fact]
    public void ResolveNativeModel_FallsBackToDefaultModel() { /* ... */ }

    [Fact]
    public void ResolveAcpModel_DoesNotFallBackToDefaultModel() { /* ... */ }

    [Fact]
    public void ApplyModelToSession_WritesSelectedModelOnly() { /* ... */ }

    [Fact]
    public void SeedSessionModel_Native_WritesResolvedDefault() { /* ... */ }

    [Fact]
    public void SeedSessionModel_Acp_DoesNotWriteDefaultModel() { /* ... */ }
}
```

断言要点：
- Native：`request > session > agent.Model.ToString() > options.DefaultModel`
- ACP Resolve：有 DefaultModel 时仍返回 null（无 request/session）
- Apply：只改 `SelectedModel`，空白清空
- Seed ACP：`SelectedModel` 保持空

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelManagerTests"
```

Expected: 编译失败或 FAIL（尚无实现）

- [ ] **Step 4: Commit**

```bash
git add -f src/Seeing.Agent/Llm/IModelManager.cs tests/Seeing.Agent.Tests/Llm/ModelManagerTests.cs
git commit -m "test: add IModelManager facade failing tests"
```

---

### Task 2: 实现 `ModelManager`

**Files:**
- Create: `src/Seeing.Agent/Llm/ModelManager.cs`
- Modify: `src/Seeing.Agent/Llm/ModelConfigManager.cs`（合并逻辑或改为内部基类/委托）
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `UnifiedConfigManager`, `IAgentRegistry`, `IOptionsMonitor<SeeingAgentOptions>`（或现有 Config 依赖）
- Produces: working `ModelManager` registered as `IModelManager` + `IModelConfigManager`

- [ ] **Step 1: 实现 Resolve / Apply / Seed**

```csharp
public sealed class ModelManager : IModelManager // + 现有 IModelConfigManager 成员
{
    public string? ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName)
    {
        if (!string.IsNullOrEmpty(requestModelRef)) return requestModelRef;
        if (!string.IsNullOrEmpty(sessionModelRef)) return sessionModelRef;
        var agent = _agentRegistry.GetAgentAsync(agentName).GetAwaiter().GetResult();
        if (agent?.Model != null && !string.IsNullOrEmpty(agent.Model.ModelId))
            return agent.Model.ToString();
        return GetDefaultModel();
    }

    public string? ResolveAcpModel(string? requestModelRef, string? sessionModelRef)
    {
        if (!string.IsNullOrEmpty(requestModelRef)) return requestModelRef;
        if (!string.IsNullOrEmpty(sessionModelRef)) return sessionModelRef;
        return null;
    }

    public string GetSessionModelRef(SessionData session) =>
        session.SelectedModel ?? string.Empty;

    public bool ApplyModelToSession(SessionData session, string? modelRef)
    {
        var trimmed = modelRef?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            if (string.IsNullOrEmpty(session.SelectedModel)) return false;
            session.SelectedModel = string.Empty;
            session.UpdatedAt = DateTime.Now;
            return true;
        }

        var catalog = GetModel(trimmed);
        var normalized = catalog is null ? trimmed : /* 目录键优先 */ FindCatalogKey(catalog, trimmed) ?? trimmed;
        if (session.SelectedModel == normalized) return false;
        session.SelectedModel = normalized;
        session.UpdatedAt = DateTime.Now;
        return true;
    }

    public bool SeedSessionModel(SessionData session, string agentName)
    {
        if (!string.IsNullOrEmpty(session.SelectedModel)) return false;
        var agent = _agentRegistry.GetAgentAsync(agentName).GetAwaiter().GetResult();
        if (agent?.Runtime == AgentRuntime.AcpPassthrough) return false;
        var model = ResolveNativeModel(null, null, agentName);
        return ApplyModelToSession(session, model);
    }
}
```

说明：目录键查找复用现有 `GetModel` / cache key；`FindCatalogKey` 用已知匹配键，禁止猜 provider。

- [ ] **Step 2: DI**

```csharp
// RegisterLlmServices
services.AddSingleton<ModelManager>();
services.AddSingleton<IModelManager>(sp => sp.GetRequiredService<ModelManager>());
services.AddSingleton<IModelConfigManager>(sp => sp.GetRequiredService<ModelManager>());
// 删除单独的 ModelConfigManager 注册，或让 ModelManager 包装旧类
```

- [ ] **Step 3: 跑通 ModelManagerTests**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelManagerTests"
```

Expected: PASS

- [ ] **Step 4: Commit**

```bash
git commit -m "feat: implement IModelManager facade with resolve/seed/apply"
```

---

### Task 3: 删除 `SelectedModelProvider`

**Files:**
- Modify: `src/Seeing.Session/Core/SessionData.cs`
- Modify: `src/Seeing.Session/Core/ISessionManager.cs`
- Modify: `src/Seeing.Session/Management/SessionManager.cs`
- Modify: `src/Seeing.Session/Management/SessionForker.cs`
- Fix compile errors across solution（本任务只求编译通过的最小改动：删除字段赋值/读取）

**Interfaces:**
- Produces: `SetModelAsync(string sessionId, string modelId, CancellationToken ct = default)`
- Produces: `SessionData` without `SelectedModelProvider`

- [ ] **Step 1: 删除属性与 API 参数**

`SessionData`：删除 `SelectedModelProvider` 及 `WithSelection` / `Clone` 中的拷贝。  
`SetModelAsync`：只保留 `modelId`；Hook 的 `modelId` 直接用该字符串。

- [ ] **Step 2: 全库编译，按错误删除引用**

```bash
dotnet build Seeing.Agent.slnx
```

Expected: 0 errors（测试中断言 Provider 的先改成只断言 `SelectedModel`）

- [ ] **Step 3: 跑 Session / Agent 相关测试**

```bash
dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionChild"
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ExecutionJobServiceModelSelection|FullyQualifiedName~ChatOrchestratorCreateSession|FullyQualifiedName~GatewaySession"
```

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: remove SelectedModelProvider; session stores modelRef only"
```

---

### Task 4: 抽干 `AgentSelectionResolver` 与 ACP builder

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentSelectionResolver.cs`
- Modify: `src/Seeing.Agent/Core/AcpExecutionOverrides.cs`
- Modify: `tests/Seeing.Agent.Tests/Core/AgentSelectionResolverTests.cs`
- Modify: `tests/Seeing.Agent.Tests/Gateway/AcpExecutionContextBuilderTests.cs`

**Interfaces:**
- Consumes: `IModelManager.ResolveAcpModel`
- Produces: Resolver without model methods

- [ ] **Step 1: 删除 `ResolveModelId` / `ResolveAcpModelId`**

Resolver 仅保留 `ResolveAgentIdAsync` / `ResolveAcpModeId`。

- [ ] **Step 2: `AcpExecutionContextBuilder.Resolve` 注入/接收 `IModelManager`**

```csharp
public static AcpExecutionOverrides Resolve(
    IModelManager models,
    AgentSelectionResolver resolver,
    string? requestModelId,
    string? requestModeId,
    SessionData session)
{
    var modelId = models.ResolveAcpModel(requestModelId, session.SelectedModel);
    var modeId = resolver.ResolveAcpModeId(requestModeId, session.SelectedAcpMode);
    return new AcpExecutionOverrides(modelId, modeId);
}
```

`ApplyToSession` 改为 `models.ApplyModelToSession(session, overrides.ModelId)`（若有 mode 另写）。

- [ ] **Step 3: 迁移/删除旧 `AgentSelectionResolverTests` 中的模型用例到 `ModelManagerTests`**

- [ ] **Step 4: 跑测试并 Commit**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelManagerTests|FullyQualifiedName~AcpExecutionContextBuilderTests|FullyQualifiedName~AgentSelectionResolverTests"
git commit -m "refactor: move model resolution from AgentSelectionResolver to IModelManager"
```

---

### Task 5: App 层 — ChatOrchestrator + ExecutionJobService

**Files:**
- Modify: `src/Seeing.Agent.App/ChatOrchestrator.cs`
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- Modify: `tests/Seeing.Agent.Tests/App/ChatOrchestratorCreateSessionTests.cs`
- Modify: `tests/Seeing.Agent.Tests/App/ExecutionJobServiceOutboundBackfillTests.cs`

**Interfaces:**
- Consumes: `IModelManager.SeedSessionModel`, `ApplyModelToSession`, `ResolveNativeModel`, `ResolveAcpModel`, `GetSessionModelRef`
- Produces: 无 `IProviderManager` 依赖的 Orchestrator；无模型分支的 TryBackfill

- [ ] **Step 1: ChatOrchestrator**

构造函数：去掉 `IProviderManager`；注入 `IModelManager`。  
`CreateSessionAsync`：

```csharp
var agentId = await _agentSelectionResolver.ResolveAgentIdAsync(agentId, null, ct);
var session = _sessionManager.Create(selectedAgent: agentId);
// title / workingDirectory ...
_modelManager.SeedSessionModel(session, agentId);
await _sessionManager.SaveAsync(session.Id);
```

删除本地 `ApplyDefaultModelIfNeededAsync` / `TryBackfill` 调用。

- [ ] **Step 2: ExecutionJobService**

Submit：` _modelManager.ApplyModelToSession(session, options?.ModelId)`；mode 单独写 `SelectedAcpMode`。  
删除 `TryBackfillSessionModelSelection` 的模型逻辑（整方法可改为 `TryBackfillSessionMode` 或内联）。  

`BuildExecutionContextAsync`：

```csharp
var agentId = await agentSelectionResolver.ResolveAgentIdAsync(
    record.Options?.AgentId, session.SelectedAgent, CancellationToken.None);
// ...
var sessionRef = modelManager.GetSessionModelRef(session);
string? requestModelId = agentDef.Runtime == AgentRuntime.AcpPassthrough
    ? modelManager.ResolveAcpModel(record.Options?.ModelId, sessionRef)
    : modelManager.ResolveNativeModel(record.Options?.ModelId, sessionRef, agentId);
```

- [ ] **Step 3: 更新测试** — CreateSession 断言 `SelectedModel == "openai/gpt-4o"`（完整 modelRef，不再拆 provider）；删除 Provider 断言。

- [ ] **Step 4: 跑测试并 Commit**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ChatOrchestratorCreateSession|FullyQualifiedName~ExecutionJobService"
git commit -m "refactor: App uses IModelManager for session model seed and resolve"
```

---

### Task 6: Gateway 收敛

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Core/GatewaySessionResolver.cs`
- Modify: `src/Seeing.Agent.Gateway/Core/GatewaySessionService.cs`
- Modify: `src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs`
- Modify: `tests/Seeing.Agent.Tests/Gateway/GatewaySessionServiceTests.cs`

- [ ] **Step 1: 两处只注入 `IModelManager`**

`EnsureSessionAsync` / `ResetAsync`：设 DefaultAgent 后 `_modelManager.SeedSessionModel(session, agentId)`，去掉 ProviderManager 与 TryBackfill。

- [ ] **Step 2: GatewayHost 构造参数改为取 `IModelManager`**

- [ ] **Step 3: 测试断言 `SelectedModel` 为完整 DefaultModel 字符串**

- [ ] **Step 4: 跑测试并 Commit**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~GatewaySession"
git commit -m "refactor: Gateway seeds session model via IModelManager"
```

---

### Task 7: WebUI + TaskTool + TokenBudget

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: `samples/Seeing.Agent.WebUI/State/SessionState.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Hooks/BudgetModelLimitHandler.cs`
- WebUI 中仍用 `IModelConfigManager` 的页面改为 `IModelManager`（或保留因继承而不改）

- [ ] **Step 1: SessionState** — 删除 `SelectedModelProvider` 属性

- [ ] **Step 2: Session.razor**

- 删除 `ResolveCatalogKey` / 本地 `ModelRef.Parse` / ProviderManager（若仅用于模型路径）
- 加载：`EnsureDefault*` 改为只补 Agent；模型依赖创建时 Seed（或调 `ModelManager.SeedSessionModel` 一次）
- 变更模型：`SessionManager.SetModelAsync(id, modelRef)` 或先 `ApplyModelToSession` 再 Save
- 发送：`ChatOptions.ModelId = SessionState.SelectedModel`（已是 modelRef）
- SyncSelector：直接用 `SelectedModel`；空则显示 `ModelManager.GetDefaultModel()`，禁止目录首项冒充已选

- [ ] **Step 3: TaskTool** — child session 写 `SelectedModel = agentInfo.Model?.ToString()`；RequestModelId 用 `session.SelectedModel`

- [ ] **Step 4: BudgetModelLimitHandler** — 只用 `session.SelectedModel`

- [ ] **Step 5: Build WebUI + 相关测试并 Commit**

```bash
dotnet build samples/Seeing.Agent.WebUI
dotnet test tests/Seeing.Agent.Tests tests/Seeing.Agent.Acp.Tests --filter "FullyQualifiedName~Task|FullyQualifiedName~Budget|FullyQualifiedName~Model"
git commit -m "refactor: WebUI and tools use modelRef via IModelManager"
```

---

### Task 8: AgentRuntimeManager 旁路收束 + 清理

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentRuntimeManager.cs`
- Grep 全库：`SelectedModelProvider|ModelRef\.(Parse|Format)|TryBackfillSessionModelSelection|ResolveModelId|ResolveAcpModelId`

- [ ] **Step 1: `GetEffectiveModelIdAsync` 委托 `IModelManager.ResolveNativeModel`**（去掉 lastUsed 旁路，或明确文档化后删除）

- [ ] **Step 2: Grep 清零违规**

```bash
rg "SelectedModelProvider|TryBackfillSessionModelSelection|ResolveAcpModelId" --glob "*.cs" --glob "*.razor"
```

Expected: 无业务代码命中（测试/文档除外）

会话路径：

```bash
rg "IProviderManager" src/Seeing.Agent.App src/Seeing.Agent.Gateway samples/Seeing.Agent.WebUI/Pages/Session.razor
```

Expected: Session.razor / Orchestrator / Gateway 会话类无注入

- [ ] **Step 3: 全量测试**

```bash
dotnet test tests/Seeing.Agent.Tests tests/Seeing.Session.Tests tests/Seeing.Agent.Acp.Tests
```

- [ ] **Step 4: 更新 spec 状态为 Implemented（可选）并 Commit**

```bash
git add -f docs/superpowers/specs/2026-08-03-model-manager-facade-design.md
git commit -m "chore: finish ModelManager facade cleanup"
```

---

## Spec Coverage Checklist

| Spec 要求 | Task |
|-----------|------|
| Manager 原则 / 一域一门面 | Task 1–2 |
| IModelManager Resolve/Seed/Apply | Task 1–2 |
| 删除 SelectedModelProvider | Task 3 |
| AgentSelectionResolver 去模型 | Task 4 |
| ChatOrchestrator / ExecutionJobService | Task 5 |
| Gateway | Task 6 |
| WebUI / TaskTool / TokenBudget | Task 7 |
| AgentRuntimeManager / 禁止旁路清零 | Task 8 |
| 不做历史兼容 | Task 3（直接删字段） |
| 管理页可留 IProviderManager | Task 7（不改 ModelsPage Provider CRUD） |

## Self-Review Notes

- 无 TBD/placeholder 步骤
- `IModelManager : IModelConfigManager` 保证管理页注入类型可平滑切换
- Apply 规范化规则与 spec「禁止猜 provider」一致
- 先前临时修补中的 `IProviderManager` 注入在 Task 5/6 移除
