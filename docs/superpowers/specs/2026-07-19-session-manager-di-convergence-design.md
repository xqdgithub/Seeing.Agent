# SessionManager DI / 消费侧收敛 — 设计方案

**日期:** 2026-07-19  
**状态:** 已实施（方案 B）  
**选定方案:** B（DI 收口 + 消费侧统一依赖 `ISessionManager`）  
**约束:** 不改 Session 持久化格式与公开 API 语义；默认不 git commit（除非用户要求）

---

## 1. 目标

保证进程内 **只有一个 Session 管理实例**，对外统一通过 `ISessionManager` 消费，彻底消除：

- 「`ISessionManager` 有 Store、具体类型 `SessionManager` 无 Store」双实例分裂
- 日志 `未配置 SessionStore，无法保存: …` 在已配置存储时仍出现

### 非目标

- 不把 `SessionManager` 标为 `internal`（留给未来大版本 / 方案 C）
- 不改 `ISessionManager` 方法签名与行为语义
- 不改会话文件格式、路径约定（仍为 `{ProjectSeeing}/sessions`）
- 不在本变更中修正 AGENTS.md 里「SessionManager = Scoped」的文档过时问题（可另开文档任务）

---

## 2. 背景与根因

### 现象

同一次执行中交替出现：

```
info: 保存会话: qq_group_...
warn: 未配置 SessionStore，无法保存: qq_group_...
```

### 根因

1. `AddSeeingAgent` 曾用无参 `AddSingleton<SessionManager>()`，DI 构造时 `ISessionStore` 未注入 → `_store == null`
2. WebUI 再调 `AddSessionManager()`，只覆盖 **`ISessionManager`** 为一个带 `FileSessionStore` 的新实例
3. 消费侧混用：
   - `ExecutionJobService` 等 → `ISessionManager`（有 Store）→ Info 保存成功
   - `EventStreamHandler` / Gateway 等 → 具体 `SessionManager`（无 Store）→ Warning

`ISessionManager` 已覆盖 `SessionManager` 全部公开能力，依赖具体类型无额外收益。

---

## 3. 目标架构

```
AddSeeingAgent（主入口）
        │
        ▼
  ISessionStore  →  FileSessionStore({ProjectSeeing}/sessions)
        │
        ▼
  SessionManager（唯一实例，构造时注入 store）
        │
        ▼
  ISessionManager  →  同一实例（factory: GetRequiredService<SessionManager>()）

AddSessionManager（可选补充入口）
  - 若已有 ISessionManager → no-op
  - 否则按同一规则注册 Store + 实现 + 接口
```

### 不变量

| ID | 规则 |
|----|------|
| I1 | 进程内 `GetRequiredService<SessionManager>()` 与 `GetRequiredService<ISessionManager>()` **必须是同一引用** |
| I2 | 业务代码（`src/`、`samples/`）构造函数 / `GetRequiredService` **禁止**依赖具体类型 `SessionManager` |
| I3 | `new SessionManager(...)` **仅允许**出现在：DI 注册 factory、单元测试 |
| I4 | `SaveAsync` 在默认 Host 配置下不得因 `_store == null` 打 Warning（Store 已注册） |

---

## 4. 变更清单

### 4.1 DI 注册

| 文件 | 变更 |
|------|------|
| `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | 保持/固化：注册 `ISessionStore` + 带 store 的 `SessionManager` + `ISessionManager` 映射同一实例（此前 hotfix 已落地，本任务做一致性核对与注释） |
| `src/Seeing.Session/SessionServiceExtensions.cs` | 保持/固化：已有 `ISessionManager` 则 no-op；补充 XML 备注说明与 `AddSeeingAgent` 的关系 |
| `samples/Seeing.Agent.WebUI/Program.cs` | 去掉或保留为注释说明的 `AddSessionManager()`（推荐删除调用，避免误导；行为上 no-op 亦可） |

其他 Host（Gateway Server 等）只要走 `AddSeeingAgent` 即自动带 Store，无需再强制 `AddSessionManager`。

### 4.2 消费侧 → `ISessionManager`

| 文件 | 现状 | 目标 |
|------|------|------|
| `src/Seeing.Session/Core/SessionLifecycle.cs` | `SessionManager` | `ISessionManager` |
| `src/Seeing.Session/Management/SessionForker.cs` | `SessionManager` | `ISessionManager` |
| `src/Seeing.Session/Management/SessionReverter.cs` | `SessionManager` | `ISessionManager` |
| `src/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs` | `GetRequiredService<SessionManager>` | `ISessionManager` |
| `src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs` | `SessionManager` | `ISessionManager` |
| `src/Seeing.Agent.Gateway/Core/GatewaySessionService.cs` | `SessionManager` | `ISessionManager` |
| `src/Seeing.Agent.Gateway/Core/GatewaySessionResolver.cs` | `SessionManager` | `ISessionManager` |
| `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` | `SessionManager` | `ISessionManager` |

已使用 `ISessionManager` 的模块（App、Agent Tools、Acp、TokenBudget、Scheduler 等）**不改逻辑**。

测试项目中的 `new SessionManager(...)` / Fake 实现 **保留**。

### 4.3 测试与验收

| 项 | 说明 |
|----|------|
| 单元测试（建议） | 在 `Seeing.Session.Tests` 或 Agent 扩展测试中：模拟 `ServiceCollection` 调用注册后，断言 `ReferenceEquals(sm, ism)` 且 store 非空路径下 `SaveAsync` 不依赖「未配置」分支 |
| 编译 | `Seeing.Agent`、`Seeing.Session`、`Seeing.Agent.Gateway`、WebUI 相关项目通过 |
| 运行时 | 一轮 QQ/WebUI 执行后日志无 `未配置 SessionStore` |

可选（非必须）：在 `AGENTS.md` WHERE TO LOOK / CONVENTIONS 增加一行「会话管理对外依赖 `ISessionManager`」。

---

## 5. 实施顺序

1. **核对 DI**（已部分完成）→ 确认双解析同一实例  
2. **Session 包内** Lifecycle / Forker / Reverter  
3. **Gateway** 四处消费点  
4. **WebUI** EventStreamHandler + Program 清理  
5. **测试 + 手工验收**

每步可独立编译验证；无协议/存储迁移，无需分 PR 拆数据兼容。

---

## 6. 风险与回滚

| 风险 | 缓解 |
|------|------|
| 某处仍注入具体类型，编译漏网 | 全仓 grep `SessionManager`（排除定义与测试）作为验收门禁 |
| 测试依赖具体类型构造函数参数 | 保持测试直接 `new`，不强制走 DI |
| 误改 Store 路径导致旧会话「丢失」 | 路径仍用 `ProjectSeeingDirectory/sessions`，不改目录 |

回滚：按文件 revert 即可，无数据迁移。

---

## 7. 成功标准

- [x] 业务代码无具体类型 `SessionManager` 注入 / 解析
- [x] DI 下 `SessionManager` 与 `ISessionManager` 为同一实例（`SessionServiceExtensionsTests`）
- [ ] 默认配置下不再出现 `未配置 SessionStore，无法保存`（需重启 WebUI 手工确认）
- [x] `Seeing.Session.Tests` DI 测试 + GatewaySession 测试通过

---

## 8. 后续（明确不做）

- 方案 C：`SessionManager` internal + 仅测 InternalsVisibleTo  
- 生命周期 Singleton → Scoped 的评估与迁移  
- Hook/Store 可插拔策略的进一步抽象
