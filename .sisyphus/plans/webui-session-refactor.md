# WebUI Session 管理重构计划

## TL;DR

> **Quick Summary**: 重构 Session 核心包删除双模式设计，统一使用 SessionData；重构 WebUI 接入层删除数据冗余，设计标准接入框架。
> 
> **Deliverables**:
> - Session 核心包：删除 SessionEntry 双模式，新增 ISessionEventPublisher/ISessionLifecycle 接口
> - WebUI 接入层：删除三层缓存，新增 SessionProvider 统一管理，简化页面调用
> - 测试：TDD 策略，所有新增接口先编写测试
> 
> **Estimated Effort**: Large（涉及 Session 核心包 + WebUI 接入层 + 测试重写）
> **Parallel Execution**: YES - 4 Waves（18 个实现任务 + 4 个验证任务）
> **Critical Path**: Task 1 → Task 2 → Task 6 → Task 13 → Task 15 → F1-F4

---

## Context

### Original Request
用户要求设计标准的 WebUI 接入框架，当前 WebUI 自己管理了 session，不合理。应该使用 Session 核心包统一管理以及与 Agent 核心交互，直接重构，不要兼容。

### Interview Summary

**关键讨论决策**:

| 决策项 | 用户选择 | 说明 |
|--------|----------|------|
| 重构范围 | 同时重构 Session 核心包 | 删除 SessionEntry 双模式，统一 SessionData |
| 推送机制 | Blazor 状态同步 | 利用 StateHasChanged 自动同步，无需 SignalR |
| 框架位置 | 核心功能扩展到 Session，其他在 WebUI | Session 包扩展接口，WebUI 实现接入逻辑 |
| 多用户模式 | 单用户 | 无需用户隔离，无需 PartitionId 机制 |
| 持久化策略 | 文件存储（现有） | 使用 FileSessionStore（~/.seeing/sessions/*.json） |
| 事件订阅模式 | IObservable | 转换为可订阅接口，支持多消费者 |
| 测试策略 | TDD | 先编写测试，再实现 |
| 线程安全 | 不需要 | Blazor Server 单线程执行，SessionData 保持普通 List/Dictionary |
| AgentExecutorAdapter | 迁移到 SessionProvider | 事件转换逻辑统一在 SessionProvider 处理 |
| SessionViewModel | 保留轻量 ViewModel | 侧边栏列表使用轻量 ViewModel，减少序列化开销 |

**研究发现**:
- SessionManager 同时维护 `_sessionDataCache` 和 `_sessions` 双模式，内存翻倍
- WebUI 三层缓存：SessionState → AppState.Sessions → EventStreamHandler.MessageCache
- MainLayout.razor 有 16 处 SessionManager 调用，保存逻辑分散
- AgentExecutor 返回事件流，通过 AgentExecutorAdapter 转换
- TaskTool.cs 使用 `using SessionData = SessionEntry` 别名，需迁移
- SessionManagerTests.cs 30+ 测试用例针对 SessionEntry，需重写

### Metis Review

**识别的 Gaps**（已处理）:
- **Thread Safety 需求**: 用户确认不需要线程安全（Blazor Server 单线程）
- **AgentExecutorAdapter 处理**: 用户确认迁移到 SessionProvider
- **SessionViewModel**: 用户确认保留轻量 ViewModel

**Guardrails Applied**:
- 不扩展重构范围到 AgentExecutor.cs 或 FileSessionStore.cs
- 不添加 SignalR、数据库存储、PartitionId 业务逻辑
- 不创建复杂的事件总线（仅使用最简单的 IObservable）
- 不添加"防御性"冗余代码（用户明确说"直接重构，不要兼容"）

---

## Work Objectives

### Core Objective
设计标准的 WebUI 接入框架，通过重构 Session 核心包（删除双模式）和 WebUI 接入层（删除数据冗余），实现 Session 统一管理和 Agent 核心正确交互。

### Concrete Deliverables
1. `src/Seeing.Session/Core/SessionEntry.cs` - **删除**
2. `src/Seeing.Session/Core/ISessionEventPublisher.cs` - **新增接口**
3. `src/Seeing.Session/Core/ISessionLifecycle.cs` - **新增接口**
4. `src/Seeing.Session/Management/SessionManager.cs` - **清理双模式代码**
5. `samples/Seeing.Agent.WebUI/State/AppState.cs` - **删除 Sessions 缓存**
6. `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` - **删除 MessageCache**
7. `samples/Seeing.Agent.WebUI/Services/SessionProvider.cs` - **新增（替代 JsonPersistenceService）**
8. `samples/Seeing.Agent.WebUI/Models/SessionViewModel.cs` - **保留轻量版本**
9. `tests/Seeing.Session.Tests/SessionManagerTests.cs` - **重写**
10. 所有页面（MainLayout、Session、Index）调用简化

### Definition of Done
- [ ] SessionEntry 文件删除，构建零错误
- [ ] SessionManager 仅使用 SessionData（无 `_sessions` 字典）
- [ ] AppState.Sessions 属性删除
- [ ] EventStreamHandler.MessageCache 字段删除
- [ ] SessionProvider 实现并替代 JsonPersistenceService
- [ ] MainLayout SessionManager 调用 ≤ 5 处（当前 16 处）
- [ ] 所有测试通过（Session + WebUI）
- [ ] WebUI 功能回归（Agent 执行正常）

### Must Have
- Session 核心包双模式彻底删除
- WebUI 数据冗余清理完成
- SessionProvider 统一管理 Session + Agent 事件流
- TDD 测试覆盖所有新增接口

### Must NOT Have (Guardrails)
- 不修改 AgentExecutor.cs
- 不修改 FileSessionStore.cs
- 不添加 SignalR
- 不添加 PartitionId 用户隔离逻辑
- 不添加复杂的事件总线
- 不添加"防御性"冗余代码
- 不"顺便重构"其他代码

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** - ALL verification is agent-executed. No exceptions.
> Acceptance criteria requiring "user manually tests/confirms" are FORBIDDEN.

### Test Decision
- **Infrastructure exists**: YES（xUnit + Moq + FluentAssertions）
- **Automated tests**: TDD（先编写测试，再实现）
- **Framework**: xUnit
- **If TDD**: 每个新增接口先编写测试，再实现，遵循 RED → GREEN → REFACTOR

### QA Policy
Every task MUST include agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **C#/.NET**: Use Bash (dotnet test, dotnet build)
- **文件验证**: Use Grep（检查文件是否存在/删除）
- **代码验证**: Use Grep（检查代码是否清理）

---

## Execution Strategy

### Parallel Execution Waves

> Maximize throughput by grouping independent tasks into parallel waves.
> Each wave completes before the next begins.
> Target: 5-8 tasks per wave. Fewer than 3 per wave (except final) = under-splitting.

```
Wave 1 (Start Immediately - Session 核心包清理):
├── Task 1: SessionData 消息操作测试（TDD） [quick]
├── Task 2: 删除 SessionEntry 类及相关代码 [quick]
├── Task 3: 清理 SessionManager 双模式 [unspecified-high]
├── Task 4: TaskTool 别名迁移 [quick]
├── Task 5: 重写 SessionManagerTests.cs [unspecified-high]
└── Task 6: ISessionEventPublisher 接口定义测试（TDD） [quick]

Wave 2 (After Wave 1 - 新接口实现):
├── Task 7: SessionEventPublisher 实现 [unspecified-high]
├── Task 8: ISessionLifecycle 接口定义测试（TDD） [quick]
├── Task 9: SessionLifecycle 实现 [unspecified-high]
├── Task 10: DI 注册更新（Session 核心包） [quick]
└── Task 11: Session 核心包构建验证 [quick]

Wave 3 (After Wave 2 - WebUI 接入层重构):
├── Task 12: 删除 AppState.Sessions 缓存 [quick]
├── Task 13: 删除 EventStreamHandler.MessageCache [quick]
├── Task 14: 优化 SessionViewModel（轻量版本） [quick]
├── Task 15: 新增 SessionProvider [deep]
├── Task 16: 迁移 AgentExecutorAdapter 事件转换逻辑 [unspecified-high]
└── Task 17: WebUI DI 注册更新 [quick]

Wave 4 (After Wave 3 - 页面调用简化):
├── Task 18: 简化 MainLayout.razor 调用 [unspecified-high]
├── Task 19: 简化 Session.razor 调用 [quick]
├── Task 20: 简化 Index.razor 调用 [quick]
└── Task 21: 删除废弃文件（JsonPersistenceService、AgentExecutorAdapter） [quick]

Wave FINAL (After ALL tasks — 4 parallel reviews):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high)
└── Task F4: Scope fidelity check (deep)
-> Present results -> Get explicit user okay

Critical Path: Task 1 → Task 2 → Task 3 → Task 6 → Task 7 → Task 15 → Task 18 → F1-F4
Parallel Speedup: ~60% faster than sequential
Max Concurrent: 6 (Wave 1)
```

### Dependency Matrix (abbreviated)

- **1-6**: - (可并行开始) - 7-11, 12-17, 18-21
- **7**: 6 - 9-11, 15-17
- **9**: 8 - 10-11, 15-17
- **11**: 7, 9 - 12-17
- **12-14**: 11 - 15-17
- **15-16**: 14, 11 - 17-21
- **18-21**: 15, 16 - F1-F4
- **F1-F4**: ALL - (等待用户确认)

---

## TODOs

---

### Wave 1: Session 核心包清理（数据模型）

- [ ] 1. **SessionData 消息操作测试（TDD）**

  **What to do**:
  - 编写 SessionData 消息操作的单元测试
  - 测试场景：AddMessage、GetMessages、ClearMessages、Clone
  - 使用 xUnit + FluentAssertions
  - 测试文件：`tests/Seeing.Session.Tests/SessionDataTests.cs`
  - 验证 SessionData 可完全替代 SessionEntry 的消息操作

  **Must NOT do**:
  - 不添加额外的防御性检查（用户明确说"直接重构，不要兼容"）
  - 不创建新的抽象类或接口

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 编写单元测试是标准任务，不需要深度推理或创造性
  - **Skills**: []
    - 无需特定技能，标准 xUnit 测试模式
  - **Skills Evaluated but Omitted**: N/A

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2, 3, 4, 5, 6)
  - **Blocks**: Task 2（需要确认 SessionData 功能完整）
  - **Blocked By**: None（可立即开始）

  **References**:
  - `src/Seeing.Session/Core/SessionData.cs:AddMessage()` - 现有消息添加方法
  - `src/Seeing.Session/Core/SessionEntry.cs:AddMessage()` - 对比参考（线程安全版本）
  - `tests/Seeing.Session.Tests/SessionManagerTests.cs` - 现有测试模式参考

  **Acceptance Criteria**:
  - [ ] 测试文件创建：`tests/Seeing.Session.Tests/SessionDataTests.cs`
  - [ ] 测试覆盖：AddMessage、GetMessages、ClearMessages、Clone
  - [ ] `dotnet test tests/Seeing.Session.Tests --filter SessionDataTests` → PASS（≥4 tests）

  **QA Scenarios**:

  ```
  Scenario: SessionData AddMessage 正常工作
    Tool: Bash
    Preconditions: 测试文件已创建
    Steps:
      1. dotnet test tests/Seeing.Session.Tests --filter "AddMessage" --verbosity normal
    Expected Result: 测试 PASS，输出显示 "Passed: 1"
    Failure Indicators: 测试 FAIL 或编译错误
    Evidence: .sisyphus/evidence/task-01-sessiondata-addmessage.txt

  Scenario: SessionData Clone 深拷贝验证
    Tool: Bash
    Preconditions: Clone 测试已编写
    Steps:
      1. 创建 SessionData 并添加消息
      2. Clone 后修改原对象
      3. 验证克隆不受影响
    Expected Result: Clone 测试 PASS
    Failure Indicators: Clone 后修改原对象影响克隆
    Evidence: .sisyphus/evidence/task-01-sessiondata-clone.txt
  ```

  **Commit**: NO（Wave 1 完成后统一提交）

---

- [ ] 2. **删除 SessionEntry 类及相关代码**

  **What to do**:
  - 删除 `src/Seeing.Session/Core/SessionEntry.cs` 文件
  - 搜索所有引用 SessionEntry 的代码并清理
  - 使用 `lsp_find_references(SessionEntry)` 确认影响范围
  - 删除 SessionManager 中的 `_sessions` 字典及相关代码
  - 删除 SessionManager 中所有 `CreateSessionAsync`（旧 API）相关代码

  **Must NOT do**:
  - 不扩展删除范围到其他文件（如 AgentExecutor.cs、FileSessionStore.cs）
  - 不"顺便重构"其他代码
  - 不添加向后兼容层

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 删除文件和清理引用是标准任务，风险可控
  - **Skills**: []
    - 无需特定技能
  - **Skills Evaluated but Omitted**: N/A

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3, 4, 5, 6)
  - **Blocks**: Task 3（需要 SessionEntry 删除后才能清理 SessionManager）
  - **Blocked By**: Task 1（确认 SessionData 功能完整）

  **References**:
  - `src/Seeing.Session/Core/SessionEntry.cs` - 待删除文件
  - `src/Seeing.Session/Management/SessionManager.cs:_sessions` - 待删除字段
  - `src/Seeing.Session/Management/SessionManager.cs:CreateSessionAsync()` - 待删除方法

  **Acceptance Criteria**:
  - [ ] `src/Seeing.Session/Core/SessionEntry.cs` 文件删除
  - [ ] `grep -r "SessionEntry" src/Seeing.Session/` → 仅剩 TaskTool 别名（Task 4 处理）
  - [ ] SessionManager 中 `_sessions` 字典删除
  - [ ] `dotnet build src/Seeing.Session` → PASS（0 errors）

  **QA Scenarios**:

  ```
  Scenario: SessionEntry 文件删除验证
    Tool: Bash
    Preconditions: 文件已删除
    Steps:
      1. test -f src/Seeing.Session/Core/SessionEntry.cs && echo "FAIL" || echo "PASS"
    Expected Result: PASS: File deleted
    Failure Indicators: FAIL: File still exists
    Evidence: .sisyphus/evidence/task-02-sessionentry-deleted.txt

  Scenario: SessionEntry 引用清理验证
    Tool: Bash
    Preconditions: 引用已清理
    Steps:
      1. grep -r "SessionEntry" src/Seeing.Session/ --include="*.cs" | wc -l
    Expected Result: ≤ 1（仅剩 TaskTool 别名）
    Failure Indicators: > 1 引用残留
    Evidence: .sisyphus/evidence/task-02-sessionentry-refs.txt

  Scenario: Session 核心包构建零错误
    Tool: Bash
    Preconditions: 删除完成
    Steps:
      1. dotnet build src/Seeing.Session --no-restore 2>&1 | grep -E "error|Error" || echo "PASS"
    Expected Result: PASS: 0 errors
    Failure Indicators: 编译错误输出
    Evidence: .sisyphus/evidence/task-02-session-build.txt
  ```

  **Commit**: NO（Wave 1 完成后统一提交）

---

- [ ] 3. **清理 SessionManager 双模式**

  **What to do**:
  - 清理 SessionManager 中的 `_sessions` 字典（旧架构）
  - 保留 `_sessionDataCache` 字典（新架构）
  - 删除所有使用 `_sessions` 的方法：
    - `CreateSessionAsync()`（旧 API）
    - `GetActiveSessions()`
    - `CompactAsync()`（旧版本）
  - 统一所有 API 使用 SessionData
  - 确保方法签名一致：
    - `Create()` → 返回 SessionData
    - `Get(id)` → 返回 SessionData?
    - `Delete(id)` → 返回 bool
    - `List()` → 返回 IReadOnlyList<SessionData>

  **Must NOT do**:
  - 不添加向后兼容方法（用户明确说"直接重构，不要兼容"）
  - 不修改 FileSessionStore.cs（已使用 SessionData）
  - 不添加额外的 null 检查或防御性代码

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要仔细理解 SessionManager 双模式，清理逻辑复杂
  - **Skills**: []
    - 无需特定技能
  - **Skills Evaluated but Omitted**: N/A

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 4, 5, 6)
  - **Blocks**: Task 5（需要 SessionManager 清理后才能重写测试）
  - **Blocked By**: Task 2（需要 SessionEntry 删除）

  **References**:
  - `src/Seeing.Session/Management/SessionManager.cs:_sessions` - 待删除字段
  - `src/Seeing.Session/Management/SessionManager.cs:_sessionDataCache` - 保留字段
  - `src/Seeing.Session/Core/ISessionManager.cs` - 接口定义参考

  **Acceptance Criteria**:
  - [ ] `_sessions` 字典删除
  - [ ] 所有旧 API（CreateSessionAsync、GetActiveSessions）删除
  - [ ] `grep "_sessions" src/Seeing.Session/Management/SessionManager.cs` → 无匹配
  - [ ] `dotnet build src/Seeing.Session` → PASS（0 errors）

  **QA Scenarios**:

  ```
  Scenario: _sessions 字典删除验证
    Tool: Bash
    Preconditions: SessionManager 已清理
    Steps:
      1. grep "_sessions" src/Seeing.Session/Management/SessionManager.cs || echo "PASS"
    Expected Result: PASS: _sessions deleted
    Failure Indicators: 找到 _sessions 引用
    Evidence: .sisyphus/evidence/task-03-sessions-dict.txt

  Scenario: SessionManager API 统一验证
    Tool: Bash
    Preconditions: API 已统一
    Steps:
      1. grep -E "Create\(|Get\(|Delete\(|List\(" src/Seeing.Session/Management/SessionManager.cs | head -5
    Expected Result: 所有方法返回 SessionData 相关类型
    Failure Indicators: 发现 SessionEntry 返回类型
    Evidence: .sisyphus/evidence/task-03-api-unified.txt
  ```

  **Commit**: NO（Wave 1 完成后统一提交）

---

- [ ] 4. **TaskTool 别名迁移**

  **What to do**:
  - 修改 `src/Seeing.Agent/Tools/Builtin/TaskTool.cs` 中的 using 别名
  - 当前：`using SessionData = Seeing.Session.Core.SessionEntry;`
  - 改为：直接使用 `Seeing.Session.Core.SessionData`（无需别名）
  - 更新 TaskTool 中所有使用 SessionEntry 的代码
  - 验证 TaskTool 功能正常

  **Must NOT do**:
  - 不添加向后兼容逻辑
  - 不修改 TaskTool 的核心功能

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 简单的别名修改和类型替换
  - **Skills**: []
    - 无需特定技能
  - **Skills Evaluated but Omitted**: N/A

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 3, 5, 6)
  - **Blocks**: None（独立任务）
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs:using SessionData = SessionEntry` - 待修改别名
  - `src/Seeing.Session/Core/SessionData.cs` - 新类型参考

  **Acceptance Criteria**:
  - [ ] TaskTool.cs 中 SessionEntry 别名删除
  - [ ] TaskTool 直接使用 `Seeing.Session.Core.SessionData`
  - [ ] `dotnet build src/Seeing.Agent` → PASS（0 errors）

  **QA Scenarios**:

  ```
  Scenario: TaskTool 别名删除验证
    Tool: Bash
    Preconditions: 别名已删除
    Steps:
      1. grep "using SessionData = SessionEntry" src/Seeing.Agent/Tools/Builtin/TaskTool.cs && echo "FAIL" || echo "PASS"
    Expected Result: PASS: Alias deleted
    Failure Indicators: 别名仍存在
    Evidence: .sisyphus/evidence/task-04-tasktool-alias.txt

  Scenario: TaskTool SessionData 使用验证
    Tool: Bash
    Preconditions: 代码已更新
    Steps:
      1. grep "Seeing.Session.Core.SessionData" src/Seeing.Agent/Tools/Builtin/TaskTool.cs || echo "FAIL"
    Expected Result: 找到 SessionData 引用
    Failure Indicators: 未找到正确引用
    Evidence: .sisyphus/evidence/task-04-tasktool-ref.txt
  ```

  **Commit**: NO（Wave 1 完成后统一提交）

---

- [ ] 5. **重写 SessionManagerTests.cs**

  **What to do**:
  - 重写 `tests/Seeing.Session.Tests/SessionManagerTests.cs` 中所有测试用例
  - 当前 30+ 测试用例针对 SessionEntry，需全部迁移到 SessionData
  - 测试场景：Create/Get/Delete/List、SaveAsync/LoadAsync、上下文管理、Hook 触发
  - 使用 xUnit + Moq + FluentAssertions

  **Must NOT do**:
  - 不添加额外的测试场景（仅迁移现有测试）
  - 不测试向后兼容性（已删除）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需理解原有测试意图并迁移，工作量较大
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1-4, 6)
  - **Blocks**: Wave 2（需要测试通过）
  - **Blocked By**: Task 3（需要 SessionManager 清理）

  **References**:
  - `tests/Seeing.Session.Tests/SessionManagerTests.cs` - 待重写文件
  - `src/Seeing.Session/Management/SessionManager.cs` - 实现参考

  **Acceptance Criteria**:
  - [ ] 所有测试用例重写完成
  - [ ] `dotnet test tests/Seeing.Session.Tests` → PASS（≥30 tests）

  **QA Scenarios**:
  ```
  Scenario: SessionManagerTests 全部通过
    Tool: Bash
    Steps: dotnet test tests/Seeing.Session.Tests --verbosity normal
    Expected Result: Passed: ≥30, Failed: 0
    Evidence: .sisyphus/evidence/task-05-tests-pass.txt
  ```

  **Commit**: NO

---

- [ ] 6. **ISessionEventPublisher 接口定义测试（TDD）**

  **What to do**:
  - 定义 ISessionEventPublisher 接口
  - 定义 SessionEvent 模型（SessionId、Type、Data、Message）
  - 编写接口测试（Events 属性、Publish 方法、多订阅者）
  - 文件：`src/Seeing.Session/Core/ISessionEventPublisher.cs`、`SessionEvent.cs`
  - 测试：`tests/Seeing.Session.Tests/SessionEventPublisherTests.cs`

  **Must NOT do**:
  - 不设计复杂事件总线（仅最简单 IObservable）
  - 不添加事件持久化、分区

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 7（实现）
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Session/Core/SessionData.cs` - SessionEvent.Data 类型
  - `src/Seeing.Session/Core/SessionMessage.cs` - SessionEvent.Message 类型

  **Acceptance Criteria**:
  - [ ] 接口和模型文件创建
  - [ ] 测试文件创建并通过

  **QA Scenarios**:
  ```
  Scenario: 接口文件存在
    Tool: Bash
    Steps: test -f src/Seeing.Session/Core/ISessionEventPublisher.cs && echo "PASS" || echo "FAIL"
    Expected Result: PASS
    Evidence: .sisyphus/evidence/task-06-interface.txt
  ```

  **Commit**: NO

---

### Wave 2: 新接口实现

- [ ] 7. **SessionEventPublisher 实现**

  **What to do**:
  - 实现 ISessionEventPublisher 接口
  - 使用 Subject<SessionEvent> 作为最简单实现
  - 文件：`src/Seeing.Session/Core/SessionEventPublisher.cs`
  - 验证测试通过

  **Must NOT do**:
  - 不添加复杂逻辑（仅 Subject.AsObservable）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 8-11)
  - **Blocked By**: Task 6（接口定义）

  **Acceptance Criteria**:
  - [ ] 实现文件创建
  - [ ] 测试通过

  **Commit**: NO

---

- [ ] 8. **ISessionLifecycle 接口定义测试（TDD）**

  **What to do**:
  - 定义 ISessionLifecycle 接口
  - 方法：BeginSessionAsync、EndSessionAsync、CloneSessionAsync、SwitchSessionAsync
  - 编写接口测试
  - 文件：`src/Seeing.Session/Core/ISessionLifecycle.cs`
  - 测试：`tests/Seeing.Session.Tests/SessionLifecycleTests.cs`

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 9（实现）

  **Acceptance Criteria**:
  - [ ] 接口文件创建
  - [ ] 测试通过

  **Commit**: NO

---

- [ ] 9. **SessionLifecycle 实现**

  **What to do**:
  - 实现 ISessionLifecycle 接口
  - 封装 SessionManager 的生命周期方法
  - 集成 SessionEventPublisher 事件触发
  - 文件：`src/Seeing.Session/Core/SessionLifecycle.cs`

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocked By**: Task 8（接口定义）

  **Acceptance Criteria**:
  - [ ] 实现文件创建
  - [ ] 测试通过

  **Commit**: NO

---

- [ ] 10. **Session 核心包 DI 注册更新**

  **What to do**:
  - 更新 `src/Seeing.Session/Extensions/ServiceCollectionExtensions.cs`
  - 注册 ISessionEventPublisher、SessionEventPublisher
  - 注册 ISessionLifecycle、SessionLifecycle
  - 保持现有 SessionManager、ISessionStore 注册

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocked By**: Task 7, 9（实现完成）

  **Acceptance Criteria**:
  - [ ] DI 注册更新
  - [ ] 构建通过

  **Commit**: NO

---

- [ ] 11. **Session 核心包构建验证**

  **What to do**:
  - 运行完整构建和测试
  - 验证所有新增接口工作正常
  - 验证 SessionEntry 相关代码已清理

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocked By**: Task 10（DI 注册）

  **Acceptance Criteria**:
  - [ ] `dotnet build src/Seeing.Session` → PASS
  - [ ] `dotnet test tests/Seeing.Session.Tests` → PASS

  **QA Scenarios**:
  ```
  Scenario: Session 核心包构建零错误
    Tool: Bash
    Steps: dotnet build src/Seeing.Session --no-restore
    Expected Result: 0 errors
    Evidence: .sisyphus/evidence/task-11-build.txt
  ```

  **Commit**: YES（Wave 2 完成）
  - Message: `feat(session): add ISessionEventPublisher and ISessionLifecycle`
  - Files: `src/Seeing.Session/Core/ISessionEventPublisher.cs`, `SessionEvent.cs`, `SessionEventPublisher.cs`, `ISessionLifecycle.cs`, `SessionLifecycle.cs`
  - Pre-commit: `dotnet test tests/Seeing.Session.Tests`

---

### Wave 3: WebUI 接入层重构

- [ ] 12. **删除 AppState.Sessions 缓存**

  **What to do**:
  - 删除 `samples/Seeing.Agent.WebUI/State/AppState.cs` 中的 `Sessions` 属性
  - UI 列表直接从 SessionProvider 获取
  - 保留其他状态（CurrentSessionId、SelectedAgent 等）

  **Must NOT do**:
  - 不删除 AppState 文件（保留其他功能）
  - 不添加向后兼容

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 13-17)
  - **Blocked By**: Wave 2

  **References**:
  - `samples/Seeing.Agent.WebUI/State/AppState.cs:Sessions` - 待删除属性

  **Acceptance Criteria**:
  - [ ] Sessions 属性删除
  - [ ] grep 验证无匹配

  **QA Scenarios**:
  ```
  Scenario: Sessions 属性删除
    Tool: Bash
    Steps: grep "public List<SessionViewModel> Sessions" samples/Seeing.Agent.WebUI/State/AppState.cs && echo "FAIL" || echo "PASS"
    Expected Result: PASS
    Evidence: .sisyphus/evidence/task-12-sessions-del.txt
  ```

  **Commit**: NO

---

- [ ] 13. **删除 EventStreamHandler.MessageCache**

  **What to do**:
  - 删除 `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` 中的 `MessageCache` 字段
  - 消息直接操作 SessionData.Messages
  - 保留事件处理逻辑

  **Must NOT do**:
  - 不删除 EventStreamHandler 文件（保留事件处理）
  - 不改变事件流处理逻辑

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocked By**: Wave 2

  **References**:
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:MessageCache` - 待删除字段

  **Acceptance Criteria**:
  - [ ] MessageCache 字段删除
  - [ ] grep 验证无匹配

  **Commit**: NO

---

- [ ] 14. **优化 SessionViewModel（轻量版本）**

  **What to do**:
  - 简化 `samples/Seeing.Agent.WebUI/Models/SessionViewModel.cs`
  - 仅保留侧边栏列表需要的字段（Id、Title、CreatedAt、UpdatedAt）
  - 从 SessionData 提取创建，不缓存

  **Must NOT do**:
  - 不删除 SessionViewModel（保留用于列表渲染）
  - 不添加额外字段

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocked By**: Wave 2

  **Acceptance Criteria**:
  - [ ] SessionViewModel 简化完成
  - [ ] 仅保留 Id、Title、CreatedAt、UpdatedAt

  **Commit**: NO

---

- [ ] 15. **新增 SessionProvider**

  **What to do**:
  - 创建 `samples/Seeing.Agent.WebUI/Services/SessionProvider.cs`
  - 封装 SessionManager + SessionEventPublisher + ISessionLifecycle
  - 提供统一的 Session 管理接口：
    - CreateSessionAsync、LoadSessionAsync、SaveCurrentSessionAsync
    - DeleteSessionAsync、GetSessionList
  - 集成 AgentExecutor 事件流订阅（迁移 AgentExecutorAdapter 转换逻辑）
  - 触发 Blazor StateHasChanged（通过 ISessionEventPublisher）

  **Must NOT do**:
  - 不创建复杂的事件总线
  - 不添加防御性代码

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 需要理解 Agent 事件流、Session 管理和 Blazor 状态同步
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 16-21（页面调用）
  - **Blocked By**: Wave 2

  **References**:
  - `samples/Seeing.Agent.WebUI/Services/JsonPersistenceService.cs` - 待替代
  - `samples/Seeing.Agent.WebUI/Services/AgentExecutorAdapter.cs` - 待迁移事件转换逻辑
  - `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` - Core 事件类型
  - `src/Seeing.Session/Core/ISessionEventPublisher.cs` - 事件发布接口

  **Acceptance Criteria**:
  - [ ] SessionProvider 文件创建
  - [ ] 封装 SessionManager + EventPublisher + Lifecycle
  - [ ] Agent 事件流订阅集成
  - [ ] WebUI 构建通过

  **QA Scenarios**:
  ```
  Scenario: SessionProvider 文件存在
    Tool: Bash
    Steps: test -f samples/Seeing.Agent.WebUI/Services/SessionProvider.cs && echo "PASS" || echo "FAIL"
    Expected Result: PASS
    Evidence: .sisyphus/evidence/task-15-provider.txt

  Scenario: WebUI 构建零错误
    Tool: Bash
    Steps: dotnet build samples/Seeing.Agent.WebUI --no-restore
    Expected Result: 0 errors
    Evidence: .sisyphus/evidence/task-15-webui-build.txt
  ```

  **Commit**: NO

---

- [ ] 16. **迁移 AgentExecutorAdapter 事件转换逻辑**

  **What to do**:
  - 将 AgentExecutorAdapter 的事件转换逻辑迁移到 SessionProvider
  - Core.IMessageEvent → WebUI 事件处理
  - 删除 AgentExecutorAdapter 文件（迁移完成后）

  **Must NOT do**:
  - 不改变事件转换逻辑（仅迁移）
  - 不修改 AgentExecutor.cs

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocked By**: Task 15（SessionProvider 创建）

  **References**:
  - `samples/Seeing.Agent.WebUI/Services/AgentExecutorAdapter.cs` - 待迁移文件
  - `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` - Core 事件类型

  **Acceptance Criteria**:
  - [ ] AgentExecutorAdapter 文件删除
  - [ ] SessionProvider 包含事件转换逻辑
  - [ ] WebUI 构建通过

  **Commit**: NO

---

- [ ] 17. **WebUI DI 注册更新**

  **What to do**:
  - 更新 `samples/Seeing.Agent.WebUI/Program.cs`
  - 注册 SessionProvider（替代 JsonPersistenceService）
  - 注册 ISessionEventPublisher（使用 Session 核心包实现）
  - 删除 JsonPersistenceService 注册

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3
  - **Blocked By**: Task 15, 16

  **References**:
  - `samples/Seeing.Agent.WebUI/Program.cs` - DI 注册入口
  - `samples/Seeing.Agent.WebUI/Services/JsonPersistenceService.cs` - 待删除注册

  **Acceptance Criteria**:
  - [ ] SessionProvider 注册
  - [ ] JsonPersistenceService 注册删除
  - [ ] WebUI 构建通过

  **Commit**: YES（Wave 3 完成）
  - Message: `refactor(webui): remove data redundancy, add SessionProvider`
  - Files: `samples/Seeing.Agent.WebUI/Services/SessionProvider.cs`, `Program.cs`, `State/AppState.cs`, `Services/EventStreamHandler.cs`
  - Pre-commit: `dotnet build samples/Seeing.Agent.WebUI`

---

### Wave 4: 页面调用简化

- [ ] 18. **简化 MainLayout.razor SessionManager 调用**

  **What to do**:
  - 简化 `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`
  - 当前 16 处 SessionManager 调用 → 简化为 ≤ 5 处
  - 使用 SessionProvider 替代直接 SessionManager 调用
  - 删除分散的 SaveAsync 调用

  **Must NOT do**:
  - 不改变页面功能
  - 不添加新的 UI 元素

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4 (with Tasks 19-21)
  - **Blocked By**: Task 15（SessionProvider）

  **References**:
  - `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor` - 待简化文件
  - `samples/Seeing.Agent.WebUI/Services/SessionProvider.cs` - 新服务

  **Acceptance Criteria**:
  - [ ] SessionManager 调用 ≤ 5 处
  - [ ] 页面功能正常

  **QA Scenarios**:
  ```
  Scenario: MainLayout SessionManager 调用统计
    Tool: Bash
    Steps: grep -c "SessionManager" samples/Seeing.Agent.WebUI/Shared/MainLayout.razor
    Expected Result: ≤ 5
    Evidence: .sisyphus/evidence/task-18-mainlayout.txt
  ```

  **Commit**: NO

---

- [ ] 19. **简化 Session.razor 调用**

  **What to do**:
  - 简化 `samples/Seeing.Agent.WebUI/Pages/Session.razor`
  - 当前 5 处 SessionManager 调用 → 简化为 ≤ 2 处
  - 使用 SessionProvider 替代

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4
  - **Blocked By**: Task 15

  **Acceptance Criteria**:
  - [ ] SessionManager 调用 ≤ 2 处
  - [ ] 页面功能正常

  **Commit**: NO

---

- [ ] 20. **简化 Index.razor 调用**

  **What to do**:
  - 简化 `samples/Seeing.Agent.WebUI/Pages/Index.razor`
  - 使用 SessionProvider 替代 JsonPersistenceService

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4
  - **Blocked By**: Task 15

  **Acceptance Criteria**:
  - [ ] JsonPersistenceService 调用删除
  - [ ] 使用 SessionProvider

  **Commit**: NO

---

- [ ] 21. **删除废弃文件**

  **What to do**:
  - 删除 `samples/Seeing.Agent.WebUI/Services/JsonPersistenceService.cs`
  - 删除 `samples/Seeing.Agent.WebUI/Services/AgentExecutorAdapter.cs`（如果还没删除）
  - 验证无引用残留

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4
  - **Blocked By**: Task 18-20（页面调用完成）

  **Acceptance Criteria**:
  - [ ] 废弃文件删除
  - [ ] grep 验证无引用

  **QA Scenarios**:
  ```
  Scenario: 废弃文件删除验证
    Tool: Bash
    Steps: test -f samples/Seeing.Agent.WebUI/Services/JsonPersistenceService.cs && echo "FAIL" || echo "PASS"
    Expected Result: PASS
    Evidence: .sisyphus/evidence/task-21-files-del.txt
  ```

  **Commit**: YES（Wave 4 完成）
  - Message: `refactor(webui): simplify page calls to SessionProvider`
  - Files: `MainLayout.razor`, `Session.razor`, `Index.razor`
  - Pre-commit: `dotnet build samples/Seeing.Agent.WebUI`

---

## Final Verification Wave (MANDATORY)

> 4 review agents run in PARALLEL. ALL must APPROVE.
> Present consolidated results to user and get explicit "okay" before completing.
> Do NOT auto-proceed after verification. Wait for user's explicit approval.

---

- [ ] F1. **Plan Compliance Audit** — `oracle`

  **What to do**:
  - Read plan end-to-end, check all "Must Have" implemented (files exist, methods work)
  - Check all "Must NOT Have" absent (grep forbidden patterns)
  - Verify evidence files exist (.sisyphus/evidence/*.txt)
  - Compare deliverables against plan TODOs

  **Must Have Check**:
  - `src/Seeing.Session/Core/ISessionEventPublisher.cs` exists
  - `src/Seeing.Session/Core/SessionEvent.cs` exists
  - `src/Seeing.Session/Core/ISessionLifecycle.cs` exists
  - `samples/Seeing.Agent.WebUI/Services/SessionProvider.cs` exists
  - SessionEntry.cs deleted

  **Must NOT Have Check**:
  - No SignalR in WebUI
  - No PartitionId business logic (only as field)
  - No AgentExecutor.cs modifications
  - No FileSessionStore.cs modifications
  - No backward compatibility code

  **QA Scenarios**:
  ```
  Scenario: Must Have files exist
    Tool: Bash
    Steps:
      1. ls src/Seeing.Session/Core/ISessionEventPublisher.cs src/Seeing.Session/Core/SessionEvent.cs src/Seeing.Session/Core/ISessionLifecycle.cs samples/Seeing.Agent.WebUI/Services/SessionProvider.cs 2>&1 | grep -c "cannot access" || echo "PASS"
    Expected Result: PASS: All files exist
    Evidence: .sisyphus/evidence/f1-must-have.txt

  Scenario: Must NOT Have patterns absent
    Tool: Bash
    Steps:
      1. grep -r "SignalR" samples/Seeing.Agent.WebUI/ && echo "FAIL" || echo "PASS"
      2. test -f src/Seeing.Session/Core/SessionEntry.cs && echo "FAIL" || echo "PASS"
    Expected Result: PASS: No forbidden patterns
    Evidence: .sisyphus/evidence/f1-must-not-have.txt
  ```

  **Output**: `Must Have [5/5] | Must NOT Have [5/5] | VERDICT: APPROVE`

---

- [ ] F2. **Code Quality Review** — `unspecified-high`

  **What to do**:
  - Run `dotnet build` + `dotnet test`
  - Check AI slop: `as any`/`@ts-ignore`, empty catches, unused imports
  - Check generic names: data/result/item/temp
  - Check excessive comments

  **QA Scenarios**:
  ```
  Scenario: Build zero errors
    Tool: Bash
    Steps:
      1. dotnet build Seeing.Agent.slnx --no-restore 2>&1 | grep -E "error|Error" || echo "PASS"
    Expected Result: PASS: 0 errors
    Evidence: .sisyphus/evidence/f2-build.txt

  Scenario: All tests pass
    Tool: Bash
    Steps:
      1. dotnet test tests/ --verbosity normal 2>&1 | grep "Failed: 0" || echo "FAIL"
    Expected Result: Failed: 0
    Evidence: .sisyphus/evidence/f2-tests.txt

  Scenario: No AI slop patterns
    Tool: Grep
    Steps:
      1. grep -r "as any" src/ samples/ --include="*.cs" && echo "FAIL" || echo "PASS"
      2. grep -r "// TODO" src/ samples/ --include="*.cs" | wc -l
    Expected Result: PASS: No AI slop, TODOs ≤ 3
    Evidence: .sisyphus/evidence/f2-slop.txt
  ```

  **Output**: `Build [PASS] | Tests [≥50 pass/0 fail] | Files [clean] | VERDICT: APPROVE`

---

- [ ] F3. **Real Manual QA** — `unspecified-high`

  **What to do**:
  - Start WebUI, verify session creation/switch/delete
  - Verify Agent execution (send message, receive response)
  - Verify sidebar list refresh
  - Verify streaming response display
  - Save evidence to .sisyphus/evidence/final-qa/

  **QA Scenarios**:
  ```
  Scenario: WebUI startup
    Tool: Bash
    Steps:
      1. dotnet run --project samples/Seeing.Agent.WebUI --urls=http://localhost:5000 &
      2. sleep 5
      3. curl -s http://localhost:5000/ | head -20
    Expected Result: HTML content returned
    Evidence: .sisyphus/evidence/f3-webui-start.txt

  Scenario: Session creation and Agent execution
    Tool: Bash
    Steps:
      1. Create new session in WebUI
      2. Send "hello" message
      3. Verify response received
    Expected Result: Agent responds with valid message
    Evidence: .sisyphus/evidence/f3-agent-exec.txt
  ```

  **Output**: `Scenarios [2/2 pass] | Integration [PASS] | VERDICT: APPROVE`

---

- [ ] F4. **Scope Fidelity Check** — `deep`

  **What to do**:
  - For each task: read "What to do", compare with git diff
  - Verify 1:1 mapping (everything planned was built, nothing unplanned was built)
  - Check "Must NOT do" compliance per task
  - Detect cross-task contamination (Task N touching Task M's files)
  - Flag unaccounted changes

  **QA Scenarios**:
  ```
  Scenario: No Scope Creep
    Tool: Bash
    Steps:
      1. git diff --stat HEAD~10 | grep -E "AgentExecutor|FileSessionStore" && echo "FAIL" || echo "PASS"
    Expected Result: PASS: No forbidden file modifications
    Evidence: .sisyphus/evidence/f4-scope.txt

  Scenario: Task-diff 1:1 mapping
    Tool: Bash
    Steps:
      1. git log --oneline HEAD~10..HEAD
      2. Compare with plan Wave 1-4 commit messages
    Expected Result: All commit messages match plan structure
    Evidence: .sisyphus/evidence/f4-commit.txt
  ```

  **Output**: `Tasks [21/21 compliant] | Contamination [CLEAN] | Unaccounted [CLEAN] | VERDICT: APPROVE`

---

## Commit Strategy

按 Wave 分批提交：
- **Wave 1 完成**: `refactor(session): delete SessionEntry dual-mode, unify SessionData`
- **Wave 2 完成**: `feat(session): add ISessionEventPublisher and ISessionLifecycle interfaces`
- **Wave 3 完成**: `refactor(webui): remove data redundancy, add SessionProvider`
- **Wave 4 完成**: `refactor(webui): simplify page calls to SessionProvider`
- **Final**: `chore: cleanup deprecated files, update DI registration`

---

## Success Criteria

### Verification Commands
```bash
# Session 核心包构建验证
dotnet build src/Seeing.Session --no-restore 2>&1 | grep -E "error|Error" || echo "PASS: 0 errors"

# SessionEntry 文件删除验证
test -f src/Seeing.Session/Core/SessionEntry.cs && echo "FAIL: File exists" || echo "PASS: File deleted"

# WebUI 构建验证
dotnet build samples/Seeing.Agent.WebUI --no-restore 2>&1 | grep -E "error|Error" || echo "PASS: 0 errors"

# AppState.Sessions 删除验证
grep -r "public List<SessionViewModel> Sessions" samples/Seeing.Agent.WebUI/State/ && echo "FAIL" || echo "PASS"

# EventStreamHandler.MessageCache 删除验证
grep -r "MessageCache" samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs && echo "FAIL" || echo "PASS"

# MainLayout SessionManager 调用统计
grep -c "SessionManager" samples/Seeing.Agent.WebUI/Shared/MainLayout.razor

# 完整测试通过
dotnet test tests/Seeing.Session.Tests tests/Seeing.Agent.Tests --verbosity normal

# Agent 执行功能回归
dotnet run --project samples/Seeing.Agent.Sample -- "hello"
```

### Final Checklist
- [ ] SessionEntry 文件删除
- [ ] SessionManager 仅使用 SessionData
- [ ] AppState.Sessions 属性删除
- [ ] EventStreamHandler.MessageCache 字段删除
- [ ] SessionProvider 实现并替代 JsonPersistenceService
- [ ] MainLayout SessionManager 调用 ≤ 5 处
- [ ] 所有测试通过
- [ ] WebUI 功能回归（Agent 执行正常）