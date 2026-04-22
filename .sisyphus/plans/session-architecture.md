# Session管理架构重构：独立NuGet包设计

## TL;DR

> **Quick Summary**: 创建独立的 Seeing.Session NuGet 包，实现完整的Session生命周期管理（创建/恢复/暂停/销毁/持久化/查询），支持Multi-Agent分区模式，提供可插拔持久化接口，迁移现有SessionManager到新包。
> 
> **Deliverables**:
> - Seeing.Session NuGet包（独立项目）
> - 核心抽象：ISession、ISessionStore、ISessionFactory、ISessionHook、IExecutionState
> - 默认实现：InMemorySessionStore、FileSessionStore（JSON）
> - Multi-Agent分区支持（逻辑分区，PartitionId字段）
> - Agent序列化策略（AgentId + AgentSnapshot组合）
> - 迁移现有SessionManager、SessionCompressor到新包
> - WebUI适配层（JsonPersistenceService → ISessionStore）
> 
> **Estimated Effort**: Large（架构重构 + 新包创建 + 迁移）
> **Parallel Execution**: YES - 3 Waves（接口定义 → 实现 → 迁移适配）
> **Critical Path**: 接口定义 → SessionData模型 → FileSessionStore → 迁移SessionManager → WebUI适配

---

## Context

### Original Request
用户要求将Session管理从Agent核心库中解耦，作为独立NuGet包实现：
- Session管理核心功能统一实现，不应由WebUI单独实现
- Session管理与Agent循环解耦
- 可适配多种智能体
- 功能完善可复用

### Interview Summary
**Key Discussions**:
- **生命周期操作**: 创建、恢复、暂停、销毁、持久化、查询（全部需要）
- **持久化策略**: 可扩展，初步实现内存+文件存储（JSON）
- **Multi-Agent支持**: 分区模式，全局状态 + Agent独立分区
- **隔离需求**: 不需要多租户隔离（单租户场景）
- **测试策略**: TDD测试驱动

**Metis Critical Questions**:
- **IAgent序列化**: 存 AgentId + AgentSnapshot 组合（恢复时优先匹配ID）
- **Hook集成**: 定义Session专用Hook接口（ISessionHook轻量级）
- **分区实现**: 逻辑分区（SessionData.PartitionId字段筛选）
- **主包过渡**: Agent包直接依赖Seeing.Session（不兼容升级）
- **执行状态**: 抽象到Session包接口（IExecutionState）

### Research Findings
- **WebUI现状**: JsonPersistenceService.cs 实现JSON文件存储（~/.seeing/sessions/*.json），缺少通用接口抽象
- **核心库现状**: SessionManager.cs（446行）内存管理+Hook集成，SessionCompressor.cs滑动窗口压缩
- **缺口**: 缺少持久化接口、克隆方法、元数据结构不统一
- **最佳实践**: LangGraph分区模式（segregated-write+shared-read）、AutoGen序列化模式、Semantic Kernel三级记忆

### Metis Review
**Identified Gaps** (addressed):
- **IAgent序列化障碍**: 改用 AgentId + AgentSnapshot 元数据组合
- **Hook依赖耦合**: 定义轻量级ISessionHook接口，不依赖主包IHookManager
- **分区结构未定义**: 采用逻辑分区，PartitionId字段筛选
- **WebUI执行状态归属**: 抽象为IExecutionState接口
- **向后兼容策略**: 直接依赖新包，不兼容升级（用户明确）

---

## Work Objectives

### Core Objective
创建独立NuGet包 Seeing.Session，提供完整的Session生命周期管理能力，支持Multi-Agent分区模式，提供可插拔持久化接口，迁移现有实现。

### Concrete Deliverables
- `src/Seeing.Session/` - 独立NuGet包项目
- `ISession` - Session核心接口
- `ISessionStore` - 持久化抽象接口
- `ISessionFactory` - Session创建工厂接口
- `ISessionHook` - Session专用Hook接口
- `IExecutionState` - 执行状态管理接口
- `SessionData` - 数据模型（含PartitionId、AgentId+AgentSnapshot）
- `InMemorySessionStore` - 内存存储实现
- `FileSessionStore` - JSON文件存储实现
- 迁移后的 SessionManager、SessionCompressor
- WebUI适配层

### Definition of Done
- [ ] `dotnet build src/Seeing.Session` → PASS
- [ ] `dotnet test tests/Seeing.Session.Tests` → 100% pass（TDD覆盖）
- [ ] WebUI使用新包持久化接口，JSON文件兼容加载
- [ ] Agent核心库引用Seeing.Session包，SessionManager迁移完成

### Must Have
- 独立NuGet包项目结构
- 完整生命周期操作（创建/恢复/暂停/销毁/持久化/查询）
- 可插拔持久化接口（ISessionStore）
- Multi-Agent分区支持（PartitionId字段）
- AgentId + AgentSnapshot序列化策略
- SessionHook集成（轻量级接口）
- 执行状态接口（IExecutionState）
- 迁移现有代码

### Must NOT Have (Guardrails - Metis锁定)
- **NO Database/Redis 存储** - 仅实现 Memory + File（用户明确）
- **NO 全文搜索** - 仅支持 PartitionId/AgentId 过滤
- **NO 物理分区隔离** - 仅逻辑分区（字段筛选）
- **NO 版本迁移机制** - 第一版简单序列化，无schema evolution
- **NO IAgent直接序列化** - 使用 AgentId + AgentSnapshot 替代
- **NO WebUI执行状态硬编码** - 使用 IExecutionState 接口抽象
- **NO 双重维护** - 完全迁移，Agent包不保留Session实现

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** - ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: NO（新包需创建测试项目）
- **Automated tests**: YES (TDD)
- **Framework**: xUnit + FluentAssertions（与主库一致）
- **TDD workflow**: RED（失败测试）→ GREEN（最小实现）→ REFACTOR

### QA Policy
Every task MUST include agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **NuGet包**: `dotnet build` + `dotnet pack` 验证
- **持久化**: 单元测试验证 Save/Load/Clone/Query
- **分区**: 单元测试验证 PartitionId 筛选
- **WebUI适配**: 集成测试验证 JSON 文件兼容

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately - 项目骨架 + 接口定义):
├── Task 1: 创建Seeing.Session项目骨架 [quick]
├── Task 2: 定义ISession核心接口 [quick]
├── Task 3: 定义ISessionStore持久化接口 [quick]
├── Task 4: 定义ISessionFactory创建接口 [quick]
├── Task 5: 定义ISessionHook轻量级接口 [quick]
├── Task 6: 定义IExecutionState执行状态接口 [quick]
└── Task 7: 定义SessionData数据模型（含PartitionId、AgentSnapshot） [quick]

Wave 2 (After Wave 1 - 核心实现，MAX PARALLEL):
├── Task 8: 实现InMemorySessionStore（依赖：ISessionStore） [quick]
├── Task 9: 实现FileSessionStore（JSON，依赖：ISessionStore） [unspecified-high]
├── Task 10: 实现SessionFactory（依赖：ISessionFactory、SessionData） [quick]
├── Task 11: 实现Session克隆/分支功能 [quick]
├── Task 12: 实现Session查询功能（PartitionId筛选） [quick]
├── Task 13: 实现SessionHookManager（依赖：ISessionHook） [unspecified-high]
└── Task 14: 实现ExecutionStateManager（依赖：IExecutionState） [quick]

Wave 3 (After Wave 2 - 迁移 + 适配):
├── Task 15: 迁移SessionManager到新包（依赖：Wave 2） [deep]
├── Task 16: 迁移SessionCompressor到新包（依赖：Wave 2） [quick]
├── Task 17: 更新Agent核心包引用Seeing.Session [quick]
├── Task 18: 实现WebUI适配层（JsonPersistenceService → ISessionStore） [unspecified-high]
├── Task 19: 创建WebUI SessionState → IExecutionState适配 [quick]
└── Task 20: 解决方案集成测试 [deep]

Wave FINAL (After ALL tasks — 4 parallel reviews):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high)
└── Task F4: Scope fidelity check (deep)
-> Present results -> Get explicit user okay
```

### Dependency Matrix (abbreviated)
- **1-7**: - - 8-14, 15-20
- **8**: 3 - 15, 1
- **9**: 3, 7 - 15, 18, 1
- **11**: 2, 7 - 15, 1
- **12**: 2, 7 - 15, 1
- **13**: 5 - 15, 1
- **15**: 8-14 - 17, 20, 2
- **18**: 9 - 20, 3

### Agent Dispatch Summary
- **Wave 1**: **7** - T1-T7 → `quick`
- **Wave 2**: **7** - T8 → `quick`, T9 → `unspecified-high`, T10 → `quick`, T11-T12 → `quick`, T13 → `unspecified-high`, T14 → `quick`
- **Wave 3**: **6** - T15 → `deep`, T16 → `quick`, T17 → `quick`, T18 → `unspecified-high`, T19 → `quick`, T20 → `deep`
- **FINAL**: **4** - F1 → `oracle`, F2 → `unspecified-high`, F3 → `unspecified-high`, F4 → `deep`

---

## TODOs

> Implementation + Test = ONE Task. Never separate.
> EVERY task MUST have: Recommended Agent Profile + Parallelization info + QA Scenarios.

- [x] 1. 创建 Seeing.Session 项目骨架
- [x] 2. 定义 ISession 核心接口
- [x] 3. 定义 ISessionStore 持久化接口
- [x] 4. 定义 ISessionFactory 创建接口
- [x] 5. 定义 ISessionHook 轻量级接口
- [x] 6. 定义 IExecutionState 执行状态接口
- [x] 7. 定义 SessionData 数据模型

  **What to do**:
  - 创建 `src/Seeing.Session/Core/SessionData.cs`
  - 定义核心字段：Id、Title、CreatedAt、UpdatedAt、PartitionId
  - 定义 Agent 信息：AgentId（string）、AgentSnapshot（AgentMetadata DTO）
  - 定义消息列表：Messages（ChatMessage[]）
  - 定义元数据：Metadata（Dictionary<string, object>）
  - 定义状态：Status（SessionStatus 枚举：Active/Paused/Error/Destroyed）
  - 创建 `AgentMetadata` DTO（Name、Model、Configuration）
  - **TDD**: 先创建失败测试 `SessionData_ShouldSerializeToJson`

  **Must NOT do**:
  - 不要包含 IAgent 类型字段（用 AgentId + AgentSnapshot）
  - 不要包含 CancellationTokenSource 字段
  - 不要包含循环引用（Metadata 应限制类型）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 数据模型定义，DTO创建
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: Tasks 9, 11, 12, 15（依赖数据模型）
  - **Blocked By**: Task 1

  **References**:
  - `src/Seeing.Agent/Core/Sessions/SessionData.cs` - 现有模型参考
  - WebUI `SessionPersistenceData` - JSON存储结构参考
  - Draft: `.sisyphus/drafts/session-architecture.md` - AgentId+Snapshot决策

  **Acceptance Criteria**:
  - [ ] `SessionData.cs` 文件存在
  - [ ] `AgentMetadata.cs` DTO 存在
  - [ ] SessionData 可 JSON 序列化（JsonSerializer.Serialize 成功）
  - [ ] `dotnet test --filter "SessionData_ShouldSerialize"` → PASS

  **QA Scenarios**:
  ```
  Scenario: SessionData可序列化为JSON
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionData_ShouldSerializeToJson"
    Expected Result: Test PASSED（序列化输出包含所有字段）
    Evidence: .sisyphus/evidence/task-07-serialize-test.txt

  Scenario: AgentSnapshot包含完整Agent元数据
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~AgentMetadata_ShouldContainRequiredFields"
    Expected Result: Test PASSED（Name、Model、Configuration字段存在）
    Evidence: .sisyphus/evidence/task-07-metadata-test.txt
  ```

  **Commit**: YES (Wave 1 group)

- [x] 8. 实现 InMemorySessionStore

  **What to do**:
  - 创建 `src/Seeing.Session/Storage/InMemorySessionStore.cs`
  - 实现 ISessionStore 接口
  - 使用 ConcurrentDictionary<string, SessionData> 存储
  - 实现 SaveAsync、LoadAsync、DeleteAsync、ListAsync、QueryAsync
  - QueryAsync 支持 PartitionId/AgentId 筛选
  - **TDD**: RED → GREEN → REFACTOR（先写失败测试）

  **Must NOT do**:
  - 不要添加数据库连接逻辑
  - 不要添加文件 I/O（这是内存存储）
  - 不要添加分布式锁（单实例）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 内存存储实现，简单CRUD
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 9-14)
  - **Blocks**: Task 15（可使用内存存储测试）
  - **Blocked By**: Tasks 1, 3, 7（ISessionStore + SessionData）

  **References**:
  - `src/Seeing.Agent/Sessions/SessionManager.cs` - 内存管理模式参考（ConcurrentDictionary）
  - Draft: `.sisyphus/drafts/session-architecture.md` - 逻辑分区决策

  **Acceptance Criteria**:
  - [ ] `InMemorySessionStore.cs` 实现 ISessionStore
  - [ ] `dotnet test --filter "InMemorySessionStore"` → ALL PASS
  - [ ] QueryAsync 支持 PartitionId 筛选

  **QA Scenarios**:
  ```
  Scenario: 保存和加载循环测试
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~InMemorySessionStore_SaveLoadRoundtrip"
    Expected Result: Test PASSED（保存后加载的数据与原始数据一致）
    Evidence: .sisyphus/evidence/task-08-roundtrip.txt

  Scenario: QueryAsync按PartitionId筛选
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~InMemorySessionStore_QueryByPartitionId"
    Expected Result: Test PASSED（只返回指定PartitionId的会话）
    Evidence: .sisyphus/evidence/task-08-query.txt

  Scenario: 并发写入不冲突
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~InMemorySessionStore_ConcurrentWrites"
    Expected Result: Test PASSED（多线程保存不同SessionId成功）
    Evidence: .sisyphus/evidence/task-08-concurrent.txt
  ```

  **Commit**: YES (Wave 2 group)
  - Message: `feat(session): implement InMemorySessionStore`

- [x] 9. 实现 FileSessionStore（JSON文件存储）

  **What to do**:
  - 创建 `src/Seeing.Session/Storage/FileSessionStore.cs`
  - 实现 ISessionStore 接口
  - 使用 JSON 文件存储（兼容 WebUI ~/.seeing/sessions/*.json）
  - 实现文件路径管理（GetFilePath、EnsureDirectoryExists）
  - 实现文件锁机制（防止并发写入冲突）
  - 实现损坏文件处理（加载失败抛出 SessionLoadException）
  - **TDD**: RED → GREEN → REFACTOR

  **Must NOT do**:
  - 不要添加数据库逻辑
  - 不要添加二进制序列化
  - 不要静默吞异常（损坏文件需明确报错）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 文件I/O + 并发锁 + 错误处理，中等复杂度
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 15, 18（依赖实现）
  - **Blocked By**: Tasks 1, 3, 7

  **References**:
  - WebUI `JsonPersistenceService.cs` - JSON文件存储模式参考
  - WebUI `~/.seeing/sessions/` - 文件路径格式
  - Draft: `.sisyphus/drafts/session-architecture.md` - 全量序列化决策

  **Acceptance Criteria**:
  - [ ] `FileSessionStore.cs` 实现 ISessionStore
  - [ ] JSON 文件格式兼容 WebUI 现有文件
  - [ ] `dotnet test --filter "FileSessionStore"` → ALL PASS
  - [ ] 损坏文件抛出 SessionLoadException

  **QA Scenarios**:
  ```
  Scenario: 保存和加载JSON文件
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~FileSessionStore_SaveLoadRoundtrip"
    Expected Result: Test PASSED（JSON文件正确保存和加载）
    Evidence: .sisyphus/evidence/task-09-json-roundtrip.txt

  Scenario: 加载损坏文件抛出异常
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~FileSessionStore_CorruptedFileHandling"
    Expected Result: Test PASSED（抛出SessionLoadException，非静默吞错误）
    Evidence: .sisyphus/evidence/task-09-corrupted.txt

  Scenario: 跨平台路径兼容
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~FileSessionStore_CrossPlatformPath"
    Expected Result: Test PASSED（Windows/Linux路径格式正确）
    Evidence: .sisyphus/evidence/task-09-crossplatform.txt
  ```

  **Commit**: YES (Wave 2 group)
  - Message: `feat(session): implement FileSessionStore with JSON`

- [x] 10. 实现 SessionFactory

  **What to do**:
  - 创建 `src/Seeing.Session/Core/SessionFactory.cs`
  - 实现 ISessionFactory 接口
  - 实现 CreateAsync：生成新 SessionId（Guid格式），初始化默认值
  - 实现 CloneAsync：深拷贝消息，新 SessionId，保留 PartitionId/AgentId
  - 实现 ResumeAsync：从 ISessionStore 加载现有会话
  - **TDD**: RED → GREEN → REFACTOR

  **Must NOT do**:
  - 不要 AgentRegistry 依赖（AgentId仅为字符串）
  - 不要浅拷贝消息（必须深拷贝）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 工厂实现，创建逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 15
  - **Blocked By**: Tasks 1, 4, 7, 8（ISessionFactory + SessionData + Store）

  **References**:
  - WebUI `OnBranchSession()` - 克隆逻辑参考（需改为深拷贝）
  - Draft: `.sisyphus/drafts/session-architecture.md` - 克隆决策

  **Acceptance Criteria**:
  - [ ] `SessionFactory.cs` 实现 ISessionFactory
  - [ ] CloneAsync 深拷贝消息（修改不影响原会话）
  - [ ] `dotnet test --filter "SessionFactory"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 创建新Session
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionFactory_CreateAsync"
    Expected Result: Test PASSED（SessionId为Guid格式，默认值正确）
    Evidence: .sisyphus/evidence/task-10-create.txt

  Scenario: 克隆Session深拷贝消息
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionFactory_CloneDeepCopy"
    Expected Result: Test PASSED（克隆会话修改不影响原会话）
    Evidence: .sisyphus/evidence/task-10-clone.txt
  ```

  **Commit**: YES (Wave 2 group)

- [x] 11. 实现 Session 克隆/分支功能

  **What to do**:
  - 在 SessionFactory 中完善 CloneAsync 实现
  - 深拷贝消息列表（JSON序列化再反序列化）
  - 生成新 SessionId
  - 可选参数：newTitle、newPartitionId
  - **TDD**: 测试覆盖所有克隆场景

  **Must NOT do**:
  - 不要浅拷贝（WebUI当前问题）
  - 不要修改原会话数据

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 克隆功能完善
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 15
  - **Blocked By**: Tasks 2, 7, 10

  **References**:
  - WebUI `Session.razor:OnBranchSession()` - 浅拷贝问题参考
  - Draft: `.sisyphus/drafts/session-architecture.md` - 深拷贝决策

  **Acceptance Criteria**:
  - [ ] 克隆消息列表完全独立
  - [ ] 新 SessionId 符合规范
  - [ ] `dotnet test --filter "SessionClone"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 克隆消息完全独立
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionClone_DeepCopyMessages"
    Expected Result: Test PASSED（克隆会话消息修改不影响原会话）
    Evidence: .sisyphus/evidence/task-11-deepcopy.txt

  Scenario: 克隆生成新SessionId
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionClone_NewSessionId"
    Expected Result: Test PASSED（新SessionId与原SessionId不同）
    Evidence: .sisyphus/evidence/task-11-newid.txt
  ```

  **Commit**: YES (Wave 2 group)

- [x] 12. 实现 Session 查询功能（PartitionId筛选）

  **What to do**:
  - 在 ISessionStore.QueryAsync 中实现筛选逻辑
  - 支持按 PartitionId 筛选
  - 支持按 AgentId 筛选
  - 支持按 Status 筛选
  - 返回 IAsyncEnumerable<SessionData>
  - **TDD**: 测试覆盖所有筛选场景

  **Must NOT do**:
  - 不要实现全文搜索
  - 不要实现复杂查询语法

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 查询功能，简单筛选
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 15
  - **Blocked By**: Tasks 2, 7, 8

  **References**:
  - Draft: `.sisyphus/drafts/session-architecture.md` - 逻辑分区决策

  **Acceptance Criteria**:
  - [ ] QueryAsync 支持 PartitionId/AgentId/Status 筛选
  - [ ] `dotnet test --filter "SessionQuery"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 按PartitionId筛选
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionQuery_FilterByPartitionId"
    Expected Result: Test PASSED（只返回指定PartitionId的会话）
    Evidence: .sisyphus/evidence/task-12-query-partition.txt

  Scenario: 按AgentId筛选
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionQuery_FilterByAgentId"
    Expected Result: Test PASSED（只返回指定AgentId的会话）
    Evidence: .sisyphus/evidence/task-12-query-agent.txt
  ```

  **Commit**: YES (Wave 2 group)

- [x] 13. 实现 SessionHookManager

  **What to do**:
  - 创建 `src/Seeing.Session/Hooks/SessionHookManager.cs`
  - 管理 ISessionHook 注册（AddHook、RemoveHook）
  - 触发 Hook（TriggerAsync）
  - 实现 6个 Hook 点：created、saving、saved、loading、loaded、destroyed
  - **TDD**: RED → GREEN → REFACTOR

  **Must NOT do**:
  - 不要依赖主包 IHookManager
  - 不要阻塞主流程（Hook异步触发）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Hook管理，事件触发机制
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 15
  - **Blocked By**: Tasks 1, 5

  **References**:
  - `src/Seeing.Agent/Hooks/HookManager.cs` - Hook管理模式参考
  - Draft: `.sisyphus/drafts/session-architecture.md` - Session专用Hook决策

  **Acceptance Criteria**:
  - [ ] `SessionHookManager.cs` 实现 Hook 注册和触发
  - [ ] 6个 Hook 点正确触发
  - [ ] `dotnet test --filter "SessionHookManager"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: Hook注册和触发
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionHookManager_TriggerAsync"
    Expected Result: Test PASSED（Hook在正确时机触发）
    Evidence: .sisyphus/evidence/task-13-hook-trigger.txt
  ```

  **Commit**: YES (Wave 2 group)

- [x] 14. 实现 ExecutionStateManager

  **What to do**:
  - 创建 `src/Seeing.Session/Execution/ExecutionStateManager.cs`
  - 实现 IExecutionState 接口
  - 管理 CancellationTokenSource
  - 实现 StartExecutionAsync、PauseExecutionAsync、CancelExecutionAsync
  - **TDD**: RED → GREEN → REFACTOR

  **Must NOT do**:
  - 不要包含 Agent 执行逻辑
  - 不要阻塞 UI 线程

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 执行状态管理，CancellationToken管理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 19
  - **Blocked By**: Tasks 1, 6

  **References**:
  - WebUI `SessionState.cs` - CancellationTokenSource 管理参考
  - Draft: `.sisyphus/drafts/session-architecture.md` - 执行状态决策

  **Acceptance Criteria**:
  - [ ] `ExecutionStateManager.cs` 实现 IExecutionState
  - [ ] CancellationTokenSource 正确管理
  - [ ] `dotnet test --filter "ExecutionStateManager"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 取消执行
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~ExecutionStateManager_CancelExecution"
    Expected Result: Test PASSED（CancellationToken正确取消）
    Evidence: .sisyphus/evidence/task-14-cancel.txt
  ```

  **Commit**: YES (Wave 2 group)

- [x] 15. 迁移 SessionManager 到新包

  **What to do**:
  - 读取 `src/Seeing.Agent/Sessions/SessionManager.cs`（446行）
  - 迁移核心逻辑到 `src/Seeing.Session/SessionManager.cs`
  - 移除 IAgent 强依赖（改用 AgentId + AgentSnapshot）
  - 移除 IHookManager 强依赖（改用 SessionHookManager）
  - 保持内存管理模式（ConcurrentDictionary）
  - 实现 ISession 接口
  - **TDD**: 迁移后所有测试通过

  **Must NOT do**:
  - 不要保留 IAgent 类型属性
  - 不要保留对主包的依赖
  - 不要删除压缩功能（需迁移到Task 16）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 大规模代码迁移，逻辑重构，解耦处理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO（依赖Wave 2全部完成）
  - **Parallel Group**: Sequential（Wave 3第一个）
  - **Blocks**: Tasks 17, 20
  - **Blocked By**: Tasks 8-14

  **References**:
  - `src/Seeing.Agent/Sessions/SessionManager.cs` - 迁移源文件
  - `src/Seeing.Agent/Core/Interfaces/ISessionManager.cs` - 现有接口定义
  - Draft: `.sisyphus/drafts/session-architecture.md` - 解耦决策

  **Acceptance Criteria**:
  - [ ] 新 SessionManager 实现 ISession
  - [ ] 无 IAgent/IHookManager 主包依赖
  - [ ] 内存管理功能完整（Create/Get/Delete）
  - [ ] `dotnet test --filter "SessionManager_Migrated"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 迁移后无主包依赖
    Tool: Bash (grep)
    Steps:
      1. grep -r "IAgent" src/Seeing.Session/SessionManager.cs
      2. grep -r "IHookManager" src/Seeing.Session/SessionManager.cs
    Expected Result: No matches found（无主包依赖）
    Failure Indicators: grep returns matches
    Evidence: .sisyphus/evidence/task-15-no-dependency.txt

  Scenario: 内存管理功能完整
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionManager_Migrated_CreateGetDelete"
    Expected Result: Test PASSED（Create/Get/Delete正常工作）
    Evidence: .sisyphus/evidence/task-15-migrated.txt
  ```

  **Commit**: YES (Wave 3 group)
  - Message: `refactor(session): migrate SessionManager to Seeing.Session`

- [x] 16. 迁移 SessionCompressor 到新包

  **What to do**:
  - 读取 `src/Seeing.Agent/Core/Sessions/SessionCompressor.cs`（88行）
  - 迁移到 `src/Seeing.Session/SessionCompressor.cs`
  - 保持滑动窗口算法
  - 添加压缩触发时机配置
  - **TDD**: 迁移后测试通过

  **Must NOT do**:
  - 不要修改压缩算法逻辑

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 小文件迁移，算法保持不变
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 17-20)
  - **Blocks**: None
  - **Blocked By**: Tasks 8-14

  **References**:
  - `src/Seeing.Agent/Core/Sessions/SessionCompressor.cs` - 迁移源文件

  **Acceptance Criteria**:
  - [ ] `SessionCompressor.cs` 在新包中存在
  - [ ] 滑动窗口算法正确工作
  - [ ] `dotnet test --filter "SessionCompressor"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 压缩功能正常
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SessionCompressor_SlidingWindow"
    Expected Result: Test PASSED（保留系统提示+最后N条消息）
    Evidence: .sisyphus/evidence/task-16-compressor.txt
  ```

  **Commit**: YES (Wave 3 group)

- [x] 17. 更新 Agent 核心包引用 Seeing.Session

  **What to do**:
  - 编辑 `src/Seeing.Agent/Seeing.Agent.csproj`
  - 添加 ProjectReference 或 PackageReference 到 Seeing.Session
  - 删除原 Sessions/ 目录（已迁移）
  - 删除原 Core/Sessions/SessionCompressor.cs（已迁移）
  - 更新 DI 注册（ServiceCollectionExtensions）
  - **TDD**: 主包构建通过

  **Must NOT do**:
  - 不要保留原 SessionManager 文件
  - 不要保留 ISessionManager façade（直接依赖）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 项目引用更新，文件删除
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 20
  - **Blocked By**: Task 15

  **References**:
  - `src/Seeing.Agent/Seeing.Agent.csproj` - 项目文件
  - `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` - DI注册

  **Acceptance Criteria**:
  - [ ] `dotnet build src/Seeing.Agent` → PASS
  - [ ] 原 Sessions/ 目录已删除
  - [ ] DI 注册使用 Seeing.Session 类型

  **QA Scenarios**:
  ```
  Scenario: 主包构建成功
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build src/Seeing.Agent
    Expected Result: Build succeeded with 0 errors
    Failure Indicators: missing Seeing.Session reference
    Evidence: .sisyphus/evidence/task-17-build.txt

  Scenario: 原文件已删除
    Tool: Bash (ls)
    Steps:
      1. ls src/Seeing.Agent/Sessions/
      2. ls src/Seeing.Agent/Core/Sessions/SessionCompressor.cs
    Expected Result: No such file or directory
    Failure Indicators: files still exist
    Evidence: .sisyphus/evidence/task-17-deleted.txt
  ```

  **Commit**: YES (Wave 3 group)
  - Message: `refactor(agent): reference Seeing.Session package`

- [ ] 18. 实现 WebUI 适配层

  **What to do**:
  - 创建 `samples/Seeing.Agent.WebUI/Adapters/SessionStoreAdapter.cs`
  - 适配 WebUI JsonPersistenceService → ISessionStore
  - 保持 JSON 文件格式兼容（~/.seeing/sessions/*.json）
  - 迁移 WebUI 持久化调用到新接口
  - **TDD**: WebUI 加载现有 JSON 文件成功

  **Must NOT do**:
  - 不要修改 JSON 文件格式（保持兼容）
  - 不要删除 JsonPersistenceService（渐进过渡）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: WebUI适配，JSON兼容验证，迁移逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 20
  - **Blocked By**: Tasks 9, 15

  **References**:
  - WebUI `JsonPersistenceService.cs` - 现有实现
  - Draft: `.sisyphus/drafts/session-architecture.md` - WebUI适配决策

  **Acceptance Criteria**:
  - [ ] WebUI 使用 ISessionStore 接口
  - [ ] 现有 JSON 文件可加载
  - [ ] `dotnet test --filter "WebUI_Adapter"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: 加载现有JSON文件
    Tool: Bash (dotnet test)
    Steps:
      1. 创建测试JSON文件 ~/.seeing/sessions/test-session.json
      2. dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~WebUI_Adapter_LoadExistingJson"
    Expected Result: Test PASSED（JSON文件正确加载）
    Evidence: .sisyphus/evidence/task-18-load-json.txt

  Scenario: 保存新Session为JSON
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~WebUI_Adapter_SaveNewSession"
    Expected Result: Test PASSED（新Session保存为JSON格式正确）
    Evidence: .sisyphus/evidence/task-18-save-json.txt
  ```

  **Commit**: YES (Wave 3 group)

- [ ] 19. 创建 WebUI SessionState → IExecutionState 适配

  **What to do**:
  - 编辑 `samples/Seeing.Agent.WebUI/State/SessionState.cs`
  - 实现 IExecutionState 接口
  - 保持现有 CancellationTokenSource 管理
  - 保持现有 IsExecuting 状态
  - **TDD**: SessionState 作为 IExecutionState 使用

  **Must NOT do**:
  - 不要改变 WebUI 现有行为
  - 不要添加额外状态属性

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 接口实现，适配简单
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 20
  - **Blocked By**: Tasks 6, 14

  **References**:
  - WebUI `SessionState.cs` - 现有实现
  - Draft: `.sisyphus/drafts/session-architecture.md` - 执行状态决策

  **Acceptance Criteria**:
  - [ ] SessionState 实现 IExecutionState
  - [ ] WebUI 构建通过
  - [ ] `dotnet test --filter "SessionState_ExecutionState"` → ALL PASS

  **QA Scenarios**:
  ```
  Scenario: SessionState实现IExecutionState
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SessionState_ExecutionState_InterfaceImpl"
    Expected Result: Test PASSED（接口方法正确实现）
    Evidence: .sisyphus/evidence/task-19-execution-state.txt
  ```

  **Commit**: YES (Wave 3 group)

- [ ] 20. 解决方案集成测试

  **What to do**:
  - 运行 `dotnet build Seeing.Agent.slnx` → 全部通过
  - 运行 `dotnet test` → 全部测试通过
  - 运行 WebUI 示例 → 加载现有会话成功
  - 验证 NuGet 包打包 → Seeing.Session.1.0.0.nupkg
  - **TDD**: 集成测试覆盖端到端流程

  **Must NOT do**:
  - 不要跳过任何失败测试

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 集成测试，端到端验证，问题修复
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO（依赖所有任务完成）
  - **Parallel Group**: Sequential（Wave 3最后）
  - **Blocks**: Final Verification Wave
  - **Blocked By**: Tasks 15-19

  **References**:
  - `Seeing.Agent.slnx` - 解决方案文件
  - Draft: `.sisyphus/drafts/session-architecture.md` - 所有决策

  **Acceptance Criteria**:
  - [ ] `dotnet build` → ALL PASS
  - [ ] `dotnet test` → 100% PASS
  - [ ] WebUI 加载现有会话成功
  - [ ] `dotnet pack` → NuGet包生成

  **QA Scenarios**:
  ```
  Scenario: 全部构建成功
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build Seeing.Agent.slnx
    Expected Result: Build succeeded. 0 Error(s)
    Failure Indicators: any build error
    Evidence: .sisyphus/evidence/task-20-build-all.txt

  Scenario: 全部测试通过
    Tool: Bash (dotnet)
    Steps:
      1. dotnet test Seeing.Agent.slnx
    Expected Result: Passed!  - All tests passed
    Failure Indicators: any test failure
    Evidence: .sisyphus/evidence/task-20-test-all.txt

  Scenario: NuGet包生成成功
    Tool: Bash (dotnet)
    Steps:
      1. dotnet pack src/Seeing.Session -c Release
      2. ls src/Seeing.Session/bin/Release/Seeing.Session.1.0.0.nupkg
    Expected Result: NuGet包文件存在
    Failure Indicators: nupkg not found
    Evidence: .sisyphus/evidence/task-20-nupkg.txt

  Scenario: WebUI加载现有会话
    Tool: Bash (dotnet run)
    Steps:
      1. 创建 ~/.seeing/sessions/integration-test.json
      2. dotnet run --project samples/Seeing.Agent.WebUI
    Expected Result: 应用启动，会话列表显示integration-test
    Failure Indicators: session not loaded
    Evidence: .sisyphus/evidence/task-20-webui-run.txt
  ```

  **Commit**: YES (Final integration)
  - Message: `test(session): integration tests complete`

---

## Final Verification Wave (MANDATORY)

> 4 review agents run in PARALLEL. ALL must APPROVE. Present consolidated results to user and get explicit "okay" before completing.

- [ ] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. For each "Must Have": verify implementation exists (read file, check test). For each "Must NOT Have": search codebase for forbidden patterns — reject with file:line if found. Check evidence files exist in .sisyphus/evidence/. Compare deliverables against plan.
  
  **Must Have Check**:
  - [ ] Seeing.Session NuGet包项目存在
  - [ ] ISession/ISessionStore/ISessionFactory/ISessionHook/IExecutionState 接口定义
  - [ ] SessionData数据模型（含PartitionId、AgentId+AgentSnapshot）
  - [ ] InMemorySessionStore/FileSessionStore 实现
  - [ ] SessionHookManager 实现
  - [ ] ExecutionStateManager 实现
  - [ ] SessionManager迁移完成
  - [ ] WebUI适配层
  
  **Must NOT Have Check**:
  - [ ] 无Database/Redis存储代码（grep -r "RedisClient|SqlConnection" src/Seeing.Session）
  - [ ] 无全文搜索代码（grep -r "FullTextSearch|SearchIndex" src/Seeing.Session）
  - [ ] 无物理分区隔离代码（grep -r "PhysicalPartition|IsolatedStore" src/Seeing.Session）
  - [ ] 无版本迁移代码（grep -r "VersionMigration|SchemaEvolution" src/Seeing.Session）
  - [ ] 无IAgent直接引用（grep -r "IAgent" src/Seeing.Session）
  - [ ] 无IHookManager直接引用（grep -r "IHookManager" src/Seeing.Session）
  
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build src/Seeing.Session --no-incremental` + linter (if available) + `dotnet test`. Review all changed files for: `as any`/`@ts-ignore` equivalent (C# `dynamic` abuse), empty catches, console.log in prod, commented-out code, unused imports. Check AI slop: excessive comments, over-abstraction, generic names (data/result/item/temp).
  
  **Quality Checks**:
  - [ ] `dotnet build` → 0 errors, 0 warnings
  - [ ] `dotnet test` → 100% pass
  - [ ] No `dynamic` type abuse
  - [ ] No empty catch blocks
  - [ ] No excessive comments (>30% of code)
  - [ ] No generic variable names (data/result/item)
  - [ ] No commented-out code
  
  Output: `Build [PASS/FAIL] | Tests [N pass/N fail] | Files [N clean/N issues] | VERDICT`

- [ ] F3. **Real Manual QA** — `unspecified-high`
  Start from clean state. Execute EVERY QA scenario from EVERY task — follow exact steps, capture evidence. Test cross-task integration (SessionStore → SessionManager → WebUI). Test edge cases: empty session, corrupted JSON, concurrent writes, missing AgentId. Save to `.sisyphus/evidence/final-qa/`.
  
  **QA Scenarios to Execute**:
  - [ ] 创建新Session → 保存 → 加载 → 数据一致
  - [ ] 克隆Session → 深拷贝验证 → 修改不影响原会话
  - [ ] 按PartitionId查询 → 返回正确筛选结果
  - [ ] 加载损坏JSON文件 → 抛出SessionLoadException
  - [ ] WebUI加载现有JSON文件 → 显示正确
  - [ ] 并发保存不同Session → 无冲突
  
  Output: `Scenarios [N/N pass] | Integration [N/N] | Edge Cases [N tested] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  For each task: read "What to do", read actual diff (git log/diff). Verify 1:1 — everything in spec was built (no missing), nothing beyond spec was built (no creep). Check "Must NOT do" compliance. Detect cross-task contamination: Task N touching Task M's files. Flag unaccounted changes.
  
  **Fidelity Checks**:
  - [ ] Task 1-7: 接口定义按规格实现
  - [ ] Task 8-14: 实现按规格，无超出范围
  - [ ] Task 15: SessionManager迁移完整，无遗漏
  - [ ] Task 18: WebUI适配正确，JSON格式兼容
  - [ ] 无未列出的文件被修改
  - [ ] 无超出规格的额外功能
  
  Output: `Tasks [N/N compliant] | Contamination [CLEAN/N issues] | Unaccounted [CLEAN/N files] | VERDICT`

---

## Commit Strategy

- **Wave 1**: `feat(session): add Seeing.Session project skeleton and interfaces`
  - Files: `src/Seeing.Session/**`, `tests/Seeing.Session.Tests/**`, `*.slnx`
  - Pre-commit: `dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests --filter "Interface"`

- **Wave 2**: `feat(session): implement core Session management features`
  - Files: `src/Seeing.Session/Storage/**`, `src/Seeing.Session/Core/**`, `src/Seeing.Session/Hooks/**`, `src/Seeing.Session/Execution/**`
  - Pre-commit: `dotnet test tests/Seeing.Session.Tests --filter "InMemory|File|Factory|Hook|Execution"`

- **Wave 3**: `refactor(session): migrate to independent package and adapt WebUI`
  - Files: `src/Seeing.Session/SessionManager.cs`, `src/Seeing.Session/SessionCompressor.cs`, `src/Seeing.Agent/**`, `samples/Seeing.Agent.WebUI/**`
  - Pre-commit: `dotnet build Seeing.Agent.slnx && dotnet test`

- **Final**: `test(session): integration tests complete and verify all pass`
  - Pre-commit: `dotnet test Seeing.Agent.slnx && dotnet pack src/Seeing.Session`

---

## Success Criteria

### Verification Commands
```bash
# 构建验证
dotnet build src/Seeing.Session    # Expected: Build succeeded, 0 Error(s)

# 测试验证（TDD - 全部通过）
dotnet test tests/Seeing.Session.Tests  # Expected: Passed! All tests passed

# NuGet打包验证
dotnet pack src/Seeing.Session -c Release  # Expected: Seeing.Session.1.0.0.nupkg created

# WebUI兼容验证
dotnet run --project samples/Seeing.Agent.WebUI  # Expected: App starts, loads existing sessions

# 主包引用验证
dotnet build src/Seeing.Agent  # Expected: Build succeeded（引用Seeing.Session）
```

### Final Checklist
- [ ] All "Must Have" present (9项)
- [ ] All "Must NOT Have" absent (6项)
- [ ] All TDD tests pass (100%)
- [ ] WebUI JSON files load correctly
- [ ] Agent core package references Seeing.Session
- [ ] NuGet package generated successfully
- [ ] Evidence files exist in .sisyphus/evidence/