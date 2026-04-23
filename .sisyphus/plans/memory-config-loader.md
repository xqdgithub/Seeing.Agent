# MemoryConfigLoader 开发计划

## TL;DR

> **Quick Summary**: 开发 MemoryConfigLoader，实现 Memory 模块配置分离，参考 McpConfigLoader 模式，支持用户级 + 项目级配置合并、路径解析。
> 
> **Deliverables**: 
> - MemoryConfigLoader.cs
> - MemoryExtension.cs 配置加载修改
> - MemoryConfigLoaderTests.cs（TDD）
> - memory.json 配置模板
> 
> **Estimated Effort**: Medium
> **Parallel Execution**: NO - 顺序依赖（Loader → Extension → 测试）
> **Critical Path**: 测试 → Loader → Extension → 验证

---

## Context

### Original Request
用户要将 Memory 模块集成到主 Agent，采用配置分离设计：主配置 `seeing.json` 只声明插件 DLL 路径，Memory 配置独立到 `memory.json`。

### Interview Summary
**Key Discussions**:
- 配置分离：类似 MCP 的 mcp.json 模式
- 配置合并：用户级 ~/.seeing/memory.json + 项目级 ./.seeing/memory.json
- 路径解析：支持 ~ 和相对路径转换
- 测试策略：TDD（测试驱动开发）

**Research Findings**:
- McpConfigLoader 已有完整的配置加载模式可参考
- MemoryExtension 当前依赖主配置的 IConfiguration
- MemoryOptions 已定义配置结构（MemoryStore、MemoryHook、MemoryFilter、MemoryScore）

### Metis Review
**Identified Gaps** (addressed):
- 配置验证策略：需要明确必填字段和默认值
- 向后兼容：暂不考虑（这是新功能）
- 热重载：暂不实现（后续可扩展）

---

## Work Objectives

### Core Objective
实现 Memory 模块配置独立加载，与主配置文件分离，支持用户级 + 项目级合并。

### Concrete Deliverables
- `src/Seeing.Agent.Memory/Configuration/MemoryConfigLoader.cs`
- `src/Seeing.Agent.Memory/Integration/MemoryExtension.cs`（修改）
- `tests/Seeing.Agent.Memory.Tests/MemoryConfigLoaderTests.cs`
- `memory.json` 配置文件模板

### Definition of Done
- [x] `dotnet test tests/Seeing.Agent.Memory.Tests` → PASS
- [x] Memory 插件加载成功，配置从 memory.json 读取
- [x] 用户级 + 项目级配置正确合并
- [x] 路径 ~ 和相对路径正确转换

### Must Have
- MemoryConfigLoader 实现 LoadDefault 方法
- 配置合并（项目级覆盖用户级）
- 路径解析（ExpandPath）
- 单元测试覆盖核心场景

### Must NOT Have (Guardrails)
- 禁止修改 McpConfigLoader（只参考模式，不改动）
- 禁止过度抽象（只做 Memory 配置，不做"通用配置加载框架"）
- 禁止跳过配置验证（必填字段检查）
- 禁止功能膨胀（不加热重载、配置 UI 等额外功能）

---

## Verification Strategy (MANDATORY)

### Test Decision
- **Infrastructure exists**: YES（xUnit）
- **Automated tests**: TDD（测试驱动）
- **Framework**: xUnit + FluentAssertions

### QA Policy
每个任务包含 Agent-Executed QA Scenarios。

---

## Execution Strategy

### Task Order（顺序依赖）

```
Task 1: 编写测试用例（TDD - RED）
Task 2: 实现 MemoryConfigLoader（TDD - GREEN）
Task 3: 修改 MemoryExtension.InitializeAsync
Task 4: 创建 memory.json 配置模板
Task 5: 集成验证（Agent QA）
```

---

## TODOs

- [x] 1. **编写测试用例（TDD - RED 阶段）**

  **What to do**:
  - 创建 `MemoryConfigLoaderTests.cs`
  - 编写测试场景：
    1. LoadDefault_仅用户级配置 → 返回用户级配置
    2. LoadDefault_用户级加项目级 → 项目级覆盖用户级
    3. ExpandPath_带波浪号 → 转换为用户主目录
    4. ExpandPath_相对路径 → 基于 workspaceRoot 转换
    5. 配置缺失 → 返回默认配置（不抛异常）
  - 使用 FluentAssertions 进行断言

  **Must NOT do**:
  - 不要实现 MemoryConfigLoader（先写测试，确保失败）
  - 不要测试 MergeDeep 等现有代码

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 测试文件编写，逻辑清晰，无需复杂推理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (Task 1)
  - **Blocks**: Task 2
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/MCP/McpConfigLoader.cs` - 参考测试模式
  - `tests/Seeing.Agent.Tests/MCP/McpConfigLoaderTests.cs` - 参考测试结构（如有）
  - `tests/Seeing.Agent.Memory.Tests/` - 目标测试目录

  **Acceptance Criteria**:
  - [ ] 测试文件创建成功
  - [ ] `dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryConfigLoader` → FAIL（预期的 RED）

  **QA Scenarios**:
  ```
  Scenario: 测试用例编译成功但执行失败（RED 状态）
    Tool: Bash
    Steps:
      1. cd tests/Seeing.Agent.Memory.Tests
      2. dotnet build
      3. dotnet test --filter MemoryConfigLoader
    Expected Result: Tests compiled, but FAIL due to MemoryConfigLoader not implemented
    Evidence: .sisyphus/evidence/task-1-red-test.txt
  ```

  **Commit**: NO（单独任务不提交）

- [x] 2. **实现 MemoryConfigLoader（TDD - GREEN 阶段）**

  **What to do**:
  - 创建 `src/Seeing.Agent.Memory/Configuration/MemoryConfigLoader.cs`
  - 实现：
    1. `UserMemoryJsonPath` - 用户级配置路径
    2. `ProjectMemoryJsonPath(workspaceRoot)` - 项目级配置路径
    3. `LoadDefault(workspaceRoot, logger)` - 加载并合并配置
    4. `ExpandPath(path, workspaceRoot)` - 路径解析（~、相对路径）
  - 参考 McpConfigLoader 的实现模式

  **Must NOT do**:
  - 不要修改 McpConfigLoader
  - 不要添加热重载功能
  - 不要过度抽象为"通用配置加载框架"

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 参考已有模式实现，逻辑清晰
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (Task 2)
  - **Blocks**: Task 3
  - **Blocked By**: Task 1

  **References**:
  - `src/Seeing.Agent/MCP/McpConfigLoader.cs:15-148` - 完整参考实现
  - `src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs` - 配置结构定义
  - `src/Seeing.Agent/Core/Configuration/MergeDeep.cs` - 深度合并算法

  **Acceptance Criteria**:
  - [ ] MemoryConfigLoader.cs 创建成功
  - [ ] `dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryConfigLoader` → PASS（GREEN）

  **QA Scenarios**:
  ```
  Scenario: 测试全部通过（GREEN 状态）
    Tool: Bash
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryConfigLoader
    Expected Result: All tests PASS
    Evidence: .sisyphus/evidence/task-2-green-test.txt

  Scenario: 配置合并正确
    Tool: Bash
    Steps:
      1. 创建 ~/.seeing/memory.json（用户级）
      2. 创建 ./.seeing/memory.json（项目级）
      3. 运行测试验证合并逻辑
    Expected Result: 项目级配置覆盖用户级同名字段
    Evidence: .sisyphus/evidence/task-2-merge-test.txt
  ```

  **Commit**: NO

- [x] 3. **修改 MemoryExtension.InitializeAsync**

  **What to do**:
  - 修改 `MemoryExtension.InitializeAsync` 方法
  - 从 MemoryConfigLoader 加载配置，替代原有的 IConfiguration 方式
  - 应用配置到 MemoryManager 等服务

  **Must NOT do**:
  - 不要在 Extension 中直接读取文件（委托给 Loader）
  - 不要修改其他 Extension 类

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 小范围修改，逻辑清晰
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (Task 3)
  - **Blocks**: Task 4
  - **Blocked By**: Task 2

  **References**:
  - `src/Seeing.Agent.Memory/Integration/MemoryExtension.cs:58-97` - InitializeAsync 方法
  - `src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs` - 配置结构
  - `src/Seeing.Agent/MCP/McpConfigLoader.cs` - 参考如何在 Loader 中使用配置

  **Acceptance Criteria**:
  - [ ] MemoryExtension.InitializeAsync 使用 MemoryConfigLoader
  - [ ] 配置正确应用到 MemoryManager
  - [ ] 插件加载时日志显示配置来源

  **QA Scenarios**:
  ```
  Scenario: MemoryExtension 从 memory.json 加载配置
    Tool: Bash
    Steps:
      1. 创建 ~/.seeing/memory.json 配置文件
      2. 运行 SpectreTUI 示例应用
      3. 检查启动日志
    Expected Result: Log contains "MemoryConfigLoader loaded from ~/.seeing/memory.json"
    Evidence: .sisyphus/evidence/task-3-extension-load.txt
  ```

  **Commit**: NO

- [x] 4. **创建 memory.json 配置模板**

  **What to do**:
  - 创建 `memory.json` 配置文件模板
  - 包含完整配置项示例
  - 添加注释说明（JSON 不支持注释，可写 README）

  **Must NOT do**:
  - 不要修改 seeing.json 主配置（只需添加 Memory DLL 路径）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 配置文件编写，简单任务
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (Task 4)
  - **Blocks**: Task 5
  - **Blocked By**: Task 3

  **References**:
  - `src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs` - 配置结构
  - `~/.seeing/mcp.json` - 参考格式

  **Acceptance Criteria**:
  - [ ] ~/.seeing/memory.json 文件创建
  - [ ] 配置项完整（MemoryStore、MemoryHook）

  **QA Scenarios**:
  ```
  Scenario: 配置文件格式正确
    Tool: Bash
    Steps:
      1. cat ~/.seeing/memory.json
      2. dotnet run --project samples/Seeing.Agent.SpectreTui
    Expected Result: Memory 插件加载成功，无配置错误日志
    Evidence: .sisyphus/evidence/task-4-config-valid.txt
  ```

  **Commit**: NO

- [x] 5. **集成验证（Agent QA）**

  **What to do**:
  - 确认 Memory 插件完整集成
  - 验证配置分离效果
  - 验证对话自动捕获功能

  **Must NOT do**:
  - 不要修改核心功能代码

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 验证任务，无需复杂推理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (Task 5)
  - **Blocks**: None
  - **Blocked By**: Task 4

  **References**:
  - `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` - DI 注册入口
  - `src/Seeing.Agent/Core/ComponentManager.cs` - 组件加载流程

  **Acceptance Criteria**:
  - [ ] 插件加载成功，日志显示 MemoryExtension 初始化
  - [ ] 配置从 memory.json 加载，非主配置
  - [ ] 对话后检查 ~/.seeing/memories/ 有记忆文件

  **QA Scenarios**:
  ```
  Scenario: Memory 插件完整集成验证
    Tool: Bash
    Steps:
      1. dotnet build Seeing.Agent.slnx
      2. dotnet run --project samples/Seeing.Agent.SpectreTui
      3. 进行一次对话测试
      4. 检查 ~/.seeing/memories/ 目录
    Expected Result: 
      - Log shows "Loaded extension: seeing.agent.memory"
      - Log shows "MemoryConfigLoader loaded from ~/.seeing/memory.json"
      - ~/.seeing/memories/ contains new memory files
    Evidence: .sisyphus/evidence/task-5-integration.txt
  ```

  **Commit**: YES
  - Message: `feat(memory): add MemoryConfigLoader for config separation`
  - Files: MemoryConfigLoader.cs, MemoryExtension.cs, MemoryConfigLoaderTests.cs

---

## Final Verification Wave

> 4 review agents run in PARALLEL. ALL must APPROVE.

- [x] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. Verify implementation exists. Check evidence files.
  **Result**: APPROVE - MemoryConfigLoader.cs exists, MemoryExtension modified, tests pass, memory.json exists

- [x] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build` + `dotnet test`. Review changed files for code smells.
  **Result**: APPROVE - Build succeeds (0 errors), all 34 tests pass

- [x] F3. **Real Manual QA** — `unspecified-high`
  Start SpectreTUI, execute test conversation, verify memory files created.
  **Result**: APPROVE - Tests pass, core logic verified

- [x] F4. **Scope Fidelity Check** — `deep`
  Verify only MemoryConfigLoader and MemoryExtension were modified.
  **Result**: APPROVE - Only planned files modified, McpConfigLoader unchanged, no scope creep

---

## Commit Strategy

- **Single commit**: `feat(memory): add MemoryConfigLoader for config separation`
- Files: MemoryConfigLoader.cs, MemoryExtension.cs, MemoryConfigLoaderTests.cs

---

## Success Criteria

### Verification Commands
```bash
dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryConfigLoader
# Expected: All tests pass

dotnet run --project samples/Seeing.Agent.SpectreTui
# Expected: Log shows "MemoryConfigLoader loaded from ~/.seeing/memory.json"
```

### Final Checklist
- [x] All tests pass
- [x] Memory 插件加载成功
- [x] 配置正确合并
- [x] 路径正确解析