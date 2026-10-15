# SessionManager DI 收敛 Implementation Plan

> **For agentic workers:** 按任务顺序实施。Steps 用 checkbox 跟踪。用户约束：**不要 git commit**，除非另行要求。

**Goal:** 进程内唯一 Session 实例，业务代码统一依赖 `ISessionManager`。

**Architecture:** `ISessionStore` → `SessionManager`（唯一）→ `ISessionManager` 同引用；消费侧去掉具体类型注入。

**Tech Stack:** .NET 10, Microsoft.Extensions.DependencyInjection, xUnit

**Spec:** `docs/superpowers/specs/2026-07-19-session-manager-di-convergence-design.md`

## Global Constraints

- 不改 `ISessionManager` API / 持久化格式
- 不 commit
- 测试可继续 `new SessionManager(...)`

---

### Task 1: DI 收口核对 + AddSessionManager 对齐 I1

**Files:**
- Modify: `src/Seeing.Session/SessionServiceExtensions.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（注释核对）
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（删除冗余 `AddSessionManager()`）

- [ ] **Step 1:** `AddSessionManager` 在注册时同时注册 `SessionManager` + `ISessionManager`（同一实例），与 `AddSeeingAgent` 一致
- [ ] **Step 2:** WebUI 去掉 `AddSessionManager()` 调用
- [ ] **Step 3:** 编译 Seeing.Session / Seeing.Agent

---

### Task 2: Session 包内改接口

**Files:**
- Modify: `src/Seeing.Session/Core/SessionLifecycle.cs`
- Modify: `src/Seeing.Session/Management/SessionForker.cs`
- Modify: `src/Seeing.Session/Management/SessionReverter.cs`

- [ ] 三处构造函数参数 `SessionManager` → `ISessionManager`
- [ ] 编译 Seeing.Session

---

### Task 3: Gateway 消费侧

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`
- Modify: `src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs`
- Modify: `src/Seeing.Agent.Gateway/Core/GatewaySessionService.cs`
- Modify: `src/Seeing.Agent.Gateway/Core/GatewaySessionResolver.cs`
- Modify: 相关测试若构造函数签名变化

- [ ] 全部改为 `ISessionManager`
- [ ] 编译 Gateway + 跑 Gateway 相关测试

---

### Task 4: WebUI EventStreamHandler

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`

- [ ] 字段/构造函数改为 `ISessionManager`
- [ ] 编译 WebUI

---

### Task 5: 回归测试 + grep 门禁

- [ ] 加 DI 同一性测试（或 Session 测试）
- [ ] `dotnet test tests/Seeing.Session.Tests`
- [ ] 全仓 grep：`src/` + `samples/` 无业务侧具体类型注入
- [ ] 更新 spec 状态为已实施

---
