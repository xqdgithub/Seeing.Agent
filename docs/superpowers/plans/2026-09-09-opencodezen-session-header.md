# OpenCodeZen 按会话注入 `x-opencode-session` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 仅在 OpenCodeZen 出站 HTTP 上按 Seeing 会话注入 `x-opencode-session`（无会话时回退稳定 GUID），修复 Console 免费层 `MissingSessionID`，并改善按对话的路由 / prompt cache 亲和。

**Architecture:** 品牌网关拥有专属头；通用 `ChatRequest` / `OpenAiChatClient` 不感知 OpenCode。`LlmService` 已有的 `sessionId` 经既有 `chat.before_start` Hook 进入 OpenCodeZen 自有 `AsyncLocal`；OpenCodeZen 通过 `ProviderConfig` 上的可选 `WrapHandler` 只包装自己的 `HttpClient` 管道，由 `DelegatingHandler` 逐请求写头。

**Tech Stack:** .NET 10、`Seeing.Agent.Abstractions`、`Seeing.Agent.Llm.OpenAI`、`Seeing.Provider.OpenCodeZen`、xUnit + FluentAssertions

**背景错误:** `HTTP 400 MissingSessionID: OpenCode's free tier can only be used in OpenCode`  
**官方约束:** 每个对话稳定发送 `x-opencode-session`（路由 + prompt caching）；`User-Agent: opencode/<version>`

## Global Constraints

- TFM: `net10.0`
- **禁止**在 `ChatRequest` / 全部 LLM Client 上统一塞 `x-opencode-*`
- **禁止** OpenCodeZen 引用 Core 具体类型；只依赖 Abstractions + 协议工厂
- `WrapHandler` / 动态会话头 **不入** `seeing.json` / provider JSON（`[JsonIgnore]`）
- 不复活「把 `chat.headers` 结果应用到所有 Client」作为本方案主路径
- 不要 git commit（除非用户另行要求）

## 方案对比（审核焦点）

| 方案 | 做法 | 优点 | 缺点 | 结论 |
|------|------|------|------|------|
| **A（推荐）** | `ProviderConfig.WrapHandler` + OpenCodeZen `DelegatingHandler` + `chat.before_start` → `AsyncLocal` | 头只落在 OpenCodeZen 管道；不改 `ChatRequest`；与品牌网关扩展模型一致 | Abstractions 增加运行时-only 委托；`CloneConfig` 需透传 | **采用** |
| B | `ChatRequest.Headers` + `OpenAiChatClient` 合并 + Hook | 复用死掉的 `chat.headers` 契约 | 通用 Client 承载品牌头机制，扩大面 | **否决**（架构不符） |
| C | 维持进程级 GUID 静态头 | 已能过 MissingSessionID | 跨会话共用亲和键，prompt cache 次优 | **仅作无 session 回退** |
| D | OpenCodeZen 自建 `HttpClient`、绕过工厂 | 零 Abstractions 改动 | 复制代理/超时配置；易与 `LlmHttpClientFactory` 漂移 | 不采用 |

## 推荐设计

### 数据流

```
AgentExecutor(context.SessionId)
  → LlmService.CompleteStreamAsync(..., sessionId)
  → Hook chat.before_start(payload.SessionId)   // 流式/非流式均已触发
  → OpenCodeZenSessionAffinity.Set(sessionId)  // AsyncLocal，仅插件内
  → OpenAiChatClient HTTP
  → OpenCodeZenSessionHandler（仅该 Provider 的 handler 链）
       写 x-opencode-session = AsyncLocal ?? 进程 GUID
       不改动其他 Provider 的 HttpClient
```

### 职责切分

| 组件 | 职责 |
|------|------|
| `ProviderConfig.WrapHandler` | 可选：`Func<HttpMessageHandler, HttpMessageHandler>`，`[JsonIgnore]` |
| `LlmHttpClientFactory` | 创建 `SocketsHttpHandler` 后若有 `WrapHandler` 则包装再交给 `HttpClient` |
| `CloneConfig`（Core 若干处） | 拷贝 `WrapHandler` 引用，避免配置克隆丢管道 |
| `OpenCodeZenSessionAffinity` | 插件内 `AsyncLocal<string?>` + 进程级 fallback GUID |
| `OpenCodeZenSessionHook` | `IHookHandler`，`Spec = ChatBeforeStart`，写入 Affinity |
| `OpenCodeZenSessionHandler` | `DelegatingHandler`，逐请求设置/覆盖 `x-opencode-session` |
| `OpenCodeZenProvider.CreateClient` | 静态头仅 `User-Agent`、`x-opencode-client`；`WrapHandler` 挂 SessionHandler；**去掉**静态 `x-opencode-session` |
| `OpenCodeZenLlmModule` | Activate 注册 Hook；Deactivate 移除 Hook |

### 非目标

- 不实现「按 Seeing 会话拆多个 HttpClient」
- 不强制所有 Provider 走 `chat.headers` 应用路径
- 不在本任务修复 `LlmService` 非流式 `chat.headers`「触发未应用」的历史债（可另开）
- 不把 `x-opencode-request` / `x-opencode-project` 纳入本期（非 MissingSessionID 必需）

### 并发与 ExecutionContext

- `LlmService` 在 `Task.Run` 流式泵之前已触发 `chat.before_start`；`Task.Run` 默认捕获 `ExecutionContext`，`AsyncLocal` 应流入出站请求
- 单测需覆盖：同 Provider 连续两次调用、不同 `sessionId`，捕获到的头值不同（证明非静态死绑）

### 回退策略

| 场景 | `x-opencode-session` |
|------|----------------------|
| Hook 写入了非空 `payload.SessionId` | 使用该值（建议原样；必要时 trim） |
| 无会话（连接测试、内部调用 `sessionId: null`） | 进程级 `ses_{guid:N}`（Provider 实例生命周期内稳定） |
| Deactivate 后 | Hook 已移除；未发起新 LLM 则无影响；残留 AsyncLocal 不影响其他 Provider |

## File Structure

| 路径 | 职责 |
|------|------|
| `src/abstractions/.../Llm/ProviderConfig.cs` | 新增 `WrapHandler` |
| `src/capabilities/Seeing.Agent.Llm.OpenAI/Clients/LlmHttpClientFactory.cs` | 应用 `WrapHandler` |
| `src/spine/Seeing.Agent.Core/Llm/ProviderManager.cs` 等 Clone | 透传 `WrapHandler` |
| `plugs/.../OpenCodeZenSessionAffinity.cs` | AsyncLocal + fallback |
| `plugs/.../OpenCodeZenSessionHandler.cs` | DelegatingHandler |
| `plugs/.../OpenCodeZenSessionHook.cs` | chat.before_start |
| `plugs/.../OpenCodeZenProvider.cs` | 调整 Headers + WrapHandler |
| `plugs/.../OpenCodeZenLlmModule.cs` | Hook 注册/注销 |
| `tests/plugs/Seeing.Provider.OpenCodeZen.Tests/*` | Handler / Hook / CreateClient |
| `tests/.../LlmHttpClientFactory` 或 OpenAI 侧轻测 | WrapHandler 被调用（若已有工厂测试则扩展） |

---

### Task 1: `ProviderConfig.WrapHandler` + 工厂应用

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Llm/ProviderConfig.cs`
- Modify: `src/capabilities/Seeing.Agent.Llm.OpenAI/Clients/LlmHttpClientFactory.cs`
- Modify: `src/spine/Seeing.Agent.Core/Llm/ProviderManager.cs`（`CloneConfig`）
- Modify: `src/spine/Seeing.Agent.Core/Llm/ModelConfigManager.cs`（`CloneProviderConfig`）
- Modify: `src/spine/Seeing.Agent.Core/Llm/ConfiguredLlmProvider.cs`（`CloneConfig`）
- Test: 优先在 `tests/plugs/Seeing.Provider.OpenCodeZen.Tests` 用自定义 wrap 断言；或 OpenAI 测试项目若存在则补一条

**Interfaces:**
- Produces: `ProviderConfig.WrapHandler`：`Func<HttpMessageHandler, HttpMessageHandler>?`，`[JsonIgnore]`
- Produces: `LlmHttpClientFactory.Create`：`handler = config.WrapHandler?.Invoke(sockets) ?? sockets`

- [ ] **Step 1: 写失败测试（WrapHandler 被调用）**  
  构造带 `WrapHandler` 的 `ProviderConfig`，经工厂/`OpenAiLlmClientFactory` 创建后，用可观察的 wrapper（记录 `Send` 或标记字段）证明管道生效。

- [ ] **Step 2: 实现 `WrapHandler` + 工厂 + Clone 透传**

- [ ] **Step 3: 跑相关测试至通过**

---

### Task 2: OpenCodeZen SessionAffinity + Handler

**Files:**
- Create: `plugs/providers/Seeing.Provider.OpenCodeZen/OpenCodeZenSessionAffinity.cs`
- Create: `plugs/providers/Seeing.Provider.OpenCodeZen/OpenCodeZenSessionHandler.cs`
- Test: `tests/plugs/Seeing.Provider.OpenCodeZen.Tests/OpenCodeZenSessionHandlerTests.cs`

**Interfaces:**
- Produces:
  - `OpenCodeZenSessionAffinity.Begin(string? sessionId) : IDisposable`（或 Set/Clear 对）
  - `OpenCodeZenSessionAffinity.CurrentOrFallback`
  - `OpenCodeZenSessionHandler(HttpMessageHandler inner)` → 设置头 `x-opencode-session`

- [ ] **Step 1: 失败测试**  
  - 无 ambient → 头 = fallback（非空）  
  - `Begin("sess-a")` → 头为 `sess-a`  
  - 嵌套/顺序两次不同 session → 头跟随 ambient

- [ ] **Step 2: 最小实现 Handler + Affinity**

- [ ] **Step 3: 测试通过**

**实现要点:**
- 使用 `TryAddWithoutValidation` / 先 `Remove` 再加，避免与 DefaultRequestHeaders 冲突
- **不要**在 Handler 里写 `User-Agent`（仍由静态 Headers 配置）

---

### Task 3: Hook + Module 生命周期 + Provider 接线

**Files:**
- Create: `plugs/providers/Seeing.Provider.OpenCodeZen/OpenCodeZenSessionHook.cs`
- Modify: `plugs/providers/Seeing.Provider.OpenCodeZen/OpenCodeZenLlmModule.cs`
- Modify: `plugs/providers/Seeing.Provider.OpenCodeZen/OpenCodeZenProvider.cs`
- Test: `tests/plugs/Seeing.Provider.OpenCodeZen.Tests/OpenCodeZenSessionHookTests.cs`
- Test: 更新 `OpenCodeZenProviderTests`（CreateClient 断言）

**Interfaces:**
- Consumes: `IHookManager.Register/Remove`，`HookRegistry.ChatBeforeStart`
- Produces: Activate 注册 Hook；CreateClient：
  - Headers: `User-Agent=opencode/1.18.29`，`x-opencode-client=cli`（**无**静态 session）
  - `WrapHandler = inner => new OpenCodeZenSessionHandler(inner)`

- [ ] **Step 1: Hook 单测** — `ExecuteAsync` 后 `CurrentOrFallback` 等于 `payload.SessionId`

- [ ] **Step 2: 实现 Hook；Module Activate/Deactivate 注册与移除；DI `TryAddSingleton` Hook**

- [ ] **Step 3: 改 `CreateClient`；更新 Provider 测试**  
  - 断言存在 `WrapHandler`  
  - 断言 Headers **不含** `x-opencode-session`（改由 Handler）  
  - 断言仍有 UA / client

- [ ] **Step 4: 跑 `Seeing.Provider.OpenCodeZen.Tests` 全绿**

```bash
dotnet build tests/plugs/Seeing.Provider.OpenCodeZen.Tests
dotnet vstest tests/plugs/Seeing.Provider.OpenCodeZen.Tests/bin/Debug/net10.0/Seeing.Provider.OpenCodeZen.Tests.dll
```

---

### Task 4: 文档与手工验收

**Files:**
- Modify（可选短注）: `docs/architecture/05-extension-guide.md`「品牌网关」——提及可用 `WrapHandler` 做 Provider 专属 HTTP 装饰，勿把品牌头写入通用 Client

- [ ] **Step 1: 补一句扩展指南（保持简短）**

- [ ] **Step 2: 手工** — WebUI 选 `mimo-v2.5-free`，两会话各发一轮；应无 `MissingSessionID`；日志/抓包可见不同 `x-opencode-session`（等于 Seeing session id）

---

## 风险与回滚

| 风险 | 缓解 |
|------|------|
| `AsyncLocal` 在某条后台路径未流入 | 单测 + 手工两会话；若发现断裂，再评估在 `LlmService` 泵内显式 `Begin`（仍不泄漏 OpenCode 类型到 Core，可用 Abstractions 级 `ILlmCallAmbient`——**仅当实测失败再开**） |
| `WrapHandler` 被 Clone 丢失 | Task 1 强制 Clone 透传 |
| 免费层策略再变 | 头仍隔离在插件；回滚静态 GUID 即可临时恢复连通 |

## 审核清单（请勾选意见）

- [ ] 同意采用方案 A（`WrapHandler` + 插件 `AsyncLocal`），否决 B
- [ ] 同意本期不做 `x-opencode-request` / `chat.headers` 全局应用
- [ ] 同意无 session 时回退进程 GUID
- [ ] session 值：**原样使用 Seeing `SessionId`**（默认） / 需要加前缀（请注明）
- [ ] 其他修改意见：________________
