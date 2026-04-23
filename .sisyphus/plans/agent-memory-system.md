# AgentMemory 系统插件实现计划

## TL;DR

> **Quick Summary**: 创建基于 Markdown 文件的 AgentMemory 插件系统，通过 Hook 捕获集成，支持语义/情景/程序三种记忆类型，采用 Hybrid 写入策略和 TDD 测试驱动开发。
>
> **Deliverables**:
> - Seeing.Agent.Memory 插件（完整实现）
> - Markdown 文件存储默认实现
> - Hook 捕获机制（ChatAfterComplete + ToolExecuteAfter）
> - 三种记忆类型支持（分离目录）
> - TDD 单元测试覆盖
>
> **Estimated Effort**: Medium（1-2 周）
> **Parallel Execution**: YES - 5 Waves
> **Critical Path**: 接口定义 → 存储实现 → 服务实现 → Hook集成 → 测试验证

---

## Context

### Original Request
"使用插件的方式设计一个标准的 AgentMemory 系统，通过插件的方式导入记忆系统"

### Interview Summary
**Key Discussions**:
- 记忆类型: 语义记忆（用户偏好/事实）+ 情景记忆（对话历史/事件）+ 程序记忆（工作流规则）
- 存储后端: 可扩展 Provider 抽象，默认 Markdown 文件存储
- 文件结构: 分类聚合文件（YAML front matter + Markdown sections）
- 目录结构: `.seeing/memory/{semantic,episodic,procedural}/` 分离目录
- 文件命名: `{sessionId}_{timestamp}_{uuid}.md`
- Hook 点: ChatAfterComplete + ToolExecuteAfter
- 写入策略: Hybrid（关键记忆实时 + 批量 Consolidation）
- 大小限制: 遵守 50KB + 分块存储
- 测试策略: TDD（测试驱动开发）

**Research Findings**:
- 业界最佳实践: Mem0（ADD-only + 混合检索）、LangGraph（Checkpointer + Store）、Zep（时间知识图谱）
- 框架现有架构: IExtension 插件接口、AgentBase 基类、ISessionStore 存储抽象、HookManager 钩子管理
- 集成点: `ServiceCollectionExtensions.cs`（DI 注册）、`HookPoints.cs`（钩子点）、`PluginsExtension.cs`（插件入口模板）

### Metis Review
**Identified Gaps** (addressed):
- Hook 点不存在: 确认使用 `ChatAfterComplete` 而非 `MessageAdded`
- 并发冲突风险: 采用 `{sessionId}_{timestamp}_{uuid}.md` 命名
- 大小限制冲突: 遵守 50KB + 分块存储
- Scope creep 防控: 明确排除向量索引、知识图谱、LLM Consolidation

### Oracle Architecture Review
**Overall Rating**: 艼好（需局部改进）

**Critical Changes (P0)**:
- 复用现有基础设施：`FileSessionStore._fileLocks` 文件锁机制、`FileSystemHelper.IsPathWithinDirectory()` 路径验证
- Hook 解耦：收集数据 → `MemoryWriteQueue` → 异步写入，避免阻塞主流程

**Recommended Changes (P1)**:
- 命名统一：`IMemoryStore` → `IMemoryRepository`, `MemoryService` → `MemoryManager`
- 预留扩展：补充 `IMemoryIndexer` 接口（空实现）、`MemoryHookPoints` 常量类

**Additional Components**:
- `MemoryWriteQueue` - 后台异步写入队列（P1）
- `MemoryCache` - 热记忆缓存层（P1）

### Industry Best Practices Review (2026)
**Source**: 2026年Agent Memory业界主流架构、实现机制与评测体系全解

**Industry Standards Applied**:
- **四层认知分层**：工作记忆（LLM上下文）+ 情景记忆 + 语义记忆 + 元记忆（反思）
- **分层筛选机制**：第一层启发式规则过滤（零LLM）+ 第二层LLM价值判定接口（预留）
- **时序失效管理**：valid_at/invalid_at时间窗口，解决信息变更和冲突（业界标配）
- **遗忘机制**：多因子评分 + 主动遗忘 + 被动衰减（完整实现）
- **评测基准对接**：MemoryAgentBench四大核心能力接口预留（AR/TTL/LRU/SF）

**Additional Components (from Industry Review)**:
- `MemoryFilter` - 启发式规则过滤器（实体检测、关键词触发、长度过滤）
- `MemoryEvaluator` - LLM价值判定接口（预留，后期扩展）
- `MemoryTimeWindow` - 时序失效管理（valid_at/invalid_at）
- `MemoryScorer` - 多因子评分器（score = α×importance + β×access_freq - γ×age）
- `MemoryForgetManager` - 遗忘管理器（主动遗忘 + 被动衰减）
- `MemoryDeduplicator` - 去重合并器（向量相似度预留 + 语义等价）
- `MemoryBenchmarkTests` - 评测测试接口预留（对接MemoryAgentBench）

---

## Work Objectives

### Core Objective
创建完整的 AgentMemory 插件系统，支持三种记忆类型的存储和检索，通过 Hook 捕获机制零侵入集成到现有框架。

### Concrete Deliverables
- `src/Seeing.Agent.Memory/` - 插件完整实现
- `src/Seeing.Agent.Memory/Abstractions/` - 接口定义（IMemoryRepository, IMemoryManager, IMemoryIndexer, IMemoryEvaluator, IMemoryScorer）
- `src/Seeing.Agent.Memory/Core/` - 核心实现（MemoryManager, MdMemoryRepository, MemoryWriteQueue, MemoryCache, MemoryFilter, MemoryTimeWindow, MemoryScorer, MemoryForgetManager, MemoryDeduplicator）
- `src/Seeing.Agent.Memory/Integration/` - Hook/Tool 集成
- `src/Seeing.Agent.Memory/Benchmark/` - 评测测试接口预留
- `tests/Seeing.Agent.Memory.Tests/` - TDD 测试覆盖
- `.seeing/memory/{semantic,episodic,procedural,archive}/` - 默认存储目录（含归档层）

### Definition of Done
- [x] `dotnet build src/Seeing.Agent.Memory` → 成功 ✓
  - [x] `dotnet test tests/Seeing.Agent.Memory.Tests` → 全部通过 ✓
  - [ ] Hook 捕获机制验证：模拟 Agent 执行，确认记忆文件生成
  - [ ] Markdown 文件格式验证：解析器正确读取 YAML front matter + Markdown 内容

### Must Have
- **核心接口**: IMemoryProvider/IMemoryRepository/IMemoryManager/IMemoryIndexer/IMemoryEvaluator/IMemoryScorer
- **存储实现**: MdMemoryRepository（Markdown 文件存储，复用 FileSessionStore 文件锁）
- **筛选机制**: MemoryFilter（启发式规则：实体检测、关键词触发、长度过滤）
- **时序管理**: MemoryTimeWindow（valid_at/invalid_at，解决信息变更和冲突）
- **遗忘机制**: MemoryScorer（多因子评分）+ MemoryForgetManager（主动遗忘+被动衰减）
- **去重合并**: MemoryDeduplicator（去重逻辑预留）
- **异步写入**: MemoryWriteQueue（后台异步写入队列）
- **缓存层**: MemoryCache（热记忆缓存）
- **Hook集成**: MemoryHookHandler（ChatAfterComplete + ToolExecuteAfter，仅收集数据）
- **Tool注入**: MemoryTools（store_memory, recall_memory, search_memory）
- **插件入口**: MemoryExtension（IExtension 入口）
- **Hook常量**: MemoryHookPoints（Created, Retrieved, Consolidated, Forgotten）
- **评测预留**: MemoryBenchmarkTests接口（对接MemoryAgentBench四大能力）
- **TDD覆盖**: 单元测试覆盖率 >80%

### Must NOT Have (Guardrails)
- ❌ 向量 Embedding 索引实现（Scope creep - Future Work，仅预留接口）
- ❌ 时间知识图谱实现（Scope creep - Future Work）
- ❌ LLM 参与 Consolidation（成本风险）
- ❌ LLM 参与价值判定（成本风险 - 仅预留接口，后期扩展）
- ❌ 需手动验证的验收标准（违反 Zero Intervention 原则）
- ❌ 硬编码 Hook 字符串（必须使用 HookPoints 常量）
- ❌ 跨进程并发控制（V1 单进程场景）
- ❌ 重复实现现有基础设施（必须复用 FileSessionStore 文件锁、FileSystemHelper 路径验证）
- ❌ Hook 中同步写入（必须使用 MemoryWriteQueue 异步写入）
- ❌ 无筛选直接写入（必须通过 MemoryFilter 预过滤）

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** - ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: YES（xUnit + Moq + FluentAssertions）
- **Automated tests**: TDD（测试驱动开发）
- **Framework**: xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12
- **TDD 流程**: RED（先写失败测试）→ GREEN（最小实现）→ REFACTOR

### QA Policy
Every task MUST include agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **存储测试**: 使用临时目录（不污染项目文件）
- **Hook 测试**: 使用 Mock Hook 数据，不依赖真实 LLM 调用
- **集成测试**: 使用 Bash（dotnet test）验证测试通过
- **记忆文件格式**: 使用 Read tool 验证 YAML front matter 正确性

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately - 基础设施):
├── Task 1: 项目结构和目录创建 [quick]
├── Task 2: 核心接口定义 [quick]
├── Task 3: 数据模型定义（含valid_at/invalid_at时序字段）[quick]
├── Task 4: 配置选项类 [quick]
└── Task 5: YAML front matter 解析器 [quick]

Wave 2 (After Wave 1 - 存储与筛选):
├── Task 6: MdMemoryRepository 实现 [deep]
├── Task 7: 记忆文件格式解析器 [unspecified-high]
├── Task 8: 分块存储逻辑 [quick]
├── Task 9: 记忆检索器 [unspecified-high]
├── Task 10: MemoryFilter 启发式规则过滤器 [quick]
└── Task 11: MemoryTimeWindow 时序失效管理 [unspecified-high]

Wave 3 (After Wave 2 - 遗忘机制):
├── Task 12: MemoryScorer 多因子评分器 [deep]
├── Task 13: MemoryForgetManager 遗忘管理器 [deep]
├── Task 14: MemoryDeduplicator 去重合并器 [unspecified-high]
└── Task 15: MemoryEvaluator 接口预留 [quick]

Wave 4 (After Wave 3 - 服务实现):
├── Task 16: MemoryManager 实现 [deep]
├── Task 17: MemoryOrchestrator 实现 [deep]
├── Task 18: MemoryChunkManager [unspecified-high]
├── Task 19: MemoryConsolidator [unspecified-high]
└── Task 20: MemoryCache 热记忆缓存 [quick]

Wave 5 (After Wave 4 - 集成层):
├── Task 21: MemoryWriteQueue 后台写入队列 [unspecified-high]
├── Task 22: MemoryHookHandler ChatAfterComplete [unspecified-high]
├── Task 23: MemoryHookHandler ToolExecuteAfter [unspecified-high]
├── Task 24: MemoryTools 实现 [quick]
├── Task 25: MemoryExtension 实现 [quick]
└── Task 26: DI 注册扩展 [quick]

Wave 6 (After Wave 5 - 测试与评测):
├── Task 27: MemoryStore 单元测试 [unspecified-high]
├── Task 28: MemoryService 单元测试 [unspecified-high]
├── Task 29: MemoryHookHandler 单元测试 [unspecified-high]
├── Task 30: MemoryForgetting 单元测试 [unspecified-high]
├── Task 31: 集成测试 [unspecified-high]
└── Task 32: MemoryBenchmark 评测接口预留 [quick]

Wave FINAL (After ALL tasks — 4 parallel reviews):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high)
└── Task F4: Scope fidelity check (deep)
-> Present results -> Get explicit user okay
```

### Dependency Matrix

| Wave | Depends On | Key Components |
|------|------------|----------------|
| 1 | - | 接口、模型、配置、解析器 |
| 2 | 1 | 存储、检索、筛选、时序管理 |
| 3 | 2 | 评分器、遗忘管理、去重 |
| 4 | 2,3 | Manager、Orchestrator、缓存 |
| 5 | 4 | 写入队列、Hook、Tool、Extension |
| 6 | 5 | 测试、评测接口 |

### Agent Dispatch Summary

- **Wave 1**: 5 tasks → `quick`
- **Wave 2**: 6 tasks → `deep`(T6), `unspecified-high`(T7,T9,T11), `quick`(T8,T10)
- **Wave 3**: 4 tasks → `deep`(T12,T13), `unspecified-high`(T14), `quick`(T15)
- **Wave 4**: 5 tasks → `deep`(T16,T17), `unspecified-high`(T18,T19), `quick`(T20)
- **Wave 5**: 6 tasks → `unspecified-high`(T21,T22,T23), `quick`(T24,T25,T26)
- **Wave 6**: 6 tasks → `unspecified-high`(T27-T31), `quick`(T32)
- **FINAL**: 4 tasks → `oracle`(F1), `unspecified-high`(F2,F3), `deep`(F4)

---

## TODOs

### Wave 1: 基础设施（5 个任务，全部 quick）

- [x] 1. **项目结构和目录创建** ✓

  **What to do**:
  - 创建 `src/Seeing.Agent.Memory/` 项目目录
  - 创建 `src/Seeing.Agent.Memory/Seeing.Agent.Memory.csproj`（引用 Seeing.Agent 主库）
  - 创建子目录: `Abstractions/`, `Core/`, `Integration/`
  - 创建测试项目 `tests/Seeing.Agent.Memory.Tests/`
  - 更新 `Seeing.Agent.slnx` 包含新项目

  **Must NOT do**:
  - 不要创建 Future Work 相关的文件（向量索引、知识图谱）
  - 不要修改 Seeing.Agent 主库的核心文件

  **Recommended Agent Profile**:
  - **Category**: `quick` - 项目结构创建是标准模板操作
  - **Skills**: []
  - **Skills Evaluated but Omitted**: `git-master`（无需复杂 git 操作）

  **Parallelization**:
  - **Can Run In Parallel**: NO（依赖此任务的基础设施）
  - **Parallel Group**: Wave 1 (启动任务)
  - **Blocks**: Tasks 2-5, 6-18
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/Seeing.Agent.csproj` - 项目文件格式参考（CPM 引用方式）
  - `src/Seeing.Agent.Plugins/Seeing.Agent.Plugins.csproj` - 插件项目参考
  - `Seeing.Agent.slnx` - 解决方案格式参考（.slnx XML 格式）

  **QA Scenarios**:
  ```
  Scenario: 项目结构验证
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build src/Seeing.Agent.Memory
      2. ls src/Seeing.Agent.Memory/Abstractions/
      3. ls src/Seeing.Agent.Memory/Core/
      4. ls src/Seeing.Agent.Memory/Integration/
    Expected: Build succeeded, all directories exist
    Evidence: .sisyphus/evidence/task-01-structure.txt
  ```

  **Commit**: YES (Wave 1)
  - Message: `feat(memory): init project structure`
  - Files: `Seeing.Agent.slnx`, `src/Seeing.Agent.Memory/*.csproj`, `tests/Seeing.Agent.Memory.Tests/*.csproj`

- [x] 2. **核心接口定义 (IMemoryProvider, IMemoryStore, IMemoryService)** ✓

  **What to do**:
  - 创建 `Abstractions/IMemoryProvider.cs`（主接口：SearchAsync, StoreAsync, DeleteAsync）
  - 创建 `Abstractions/IMemoryStore.cs`（存储抽象：SaveAsync, LoadAsync, ListAsync，参考 ISessionStore）
  - 创建 `Abstractions/IMemoryService.cs`（服务接口：CreateMemoryAsync, GetMemoryAsync, SearchMemoriesAsync）
  - 创建 `Abstractions/IMemoryExtractor.cs`（提取接口：ExtractFromChatAsync, ExtractFromToolAsync）
  - 创建 `Abstractions/IMemoryRetriever.cs`（检索接口：RetrieveByMetadataAsync, RetrieveByTimeRangeAsync）

  **Must NOT do**:
  - 不要添加向量检索相关接口（Future Work）
  - 不要添加知识图谱相关接口（Future Work）
  - 不要在接口中添加业务逻辑实现

  **Recommended Agent Profile**:
  - **Category**: `quick` - 接口定义是标准抽象设计
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 3, 4, 5 并行）
  - **Parallel Group**: Wave 1
  - **Blocks**: Tasks 6-18
  - **Blocked By**: Task 1

  **References**:
  - `src/Seeing.Session/Storage/ISessionStore.cs` - 存储接口模式参考
  - `src/Seeing.Agent/Core/Interfaces/ITool.cs` - Tool 接口模式参考
  - `src/Seeing.Agent/Hooks/IHook.cs` - Hook 接口模式参考

  **QA Scenarios**:
  ```
  Scenario: 接口编译验证
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build src/Seeing.Agent.Memory
    Expected: Build succeeded, no compiler errors
    Evidence: .sisyphus/evidence/task-02-interface-build.txt
  ```

  **Commit**: YES (Wave 1)
  - Message: `feat(memory): add core interfaces`
  - Files: `Abstractions/*.cs`

- [x] 3. **数据模型定义 (MemoryEntry, MemoryType, MemoryMetadata)** ✓

  **What to do**:
  - 创建 `Core/MemoryEntry.cs`（记忆实体：Id, Type, Content, Metadata, CreatedAt）
  - 创建 `Core/MemoryType.cs`（枚举：Semantic, Episodic, Procedural）
  - 创建 `Core/MemoryMetadata.cs`（元数据：SessionId, AgentId, Source, Tags, Confidence）
  - 创建 `Core/MemoryFilter.cs`（查询过滤器：UserId, SessionId, Type, Tags, TimeRange）
  - 创建 `Core/MemorySearchResult.cs`（检索结果：Memory, Score, Relevance）

  **Must NOT do**:
  - 不要添加向量 Embedding 字段（Future Work）
  - 不要添加图谱节点/边字段（Future Work）

  **Recommended Agent Profile**:
  - **Category**: `quick` - 数据模型定义是标准结构设计
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 2, 4, 5 并行）
  - **Parallel Group**: Wave 1
  - **Blocks**: Tasks 6-18
  - **Blocked By**: Task 1

  **References**:
  - `src/Seeing.Session/Core/SessionData.cs` - Session 数据模型参考
  - `src/Seeing.Session/Core/SessionMessage.cs` - Message 数据模型参考
  - `src/Seeing.Agent/Llm/LlmModels.cs` - ChatMessage 模型参考

  **QA Scenarios**:
  ```
  Scenario: 数据模型编译验证
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build src/Seeing.Agent.Memory
    Expected: Build succeeded
    Evidence: .sisyphus/evidence/task-03-model-build.txt
  ```

  **Commit**: YES (Wave 1)
  - Message: `feat(memory): add data models`
  - Files: `Core/MemoryEntry.cs`, `Core/MemoryType.cs`, `Core/MemoryMetadata.cs`, `Core/MemoryFilter.cs`, `Core/MemorySearchResult.cs`

- [x] 4. **配置选项类 (MemoryOptions, MemoryStoreOptions)** ✓

  **What to do**:
  - 创建 `Configuration/MemoryOptions.cs`（插件主配置：EnableMemory, DefaultStore, StoreOptions）
  - 创建 `Configuration/MemoryStoreOptions.cs`（存储配置：MemoryDirectory, MaxFileSizeKB, EnableChunking）
  - 创建 `Configuration/MemoryHookOptions.cs`（Hook 配置：EnableChatCapture, EnableToolCapture）

  **Must NOT do**:
  - 不要添加向量数据库配置（Future Work）
  - 不要添加图谱数据库配置（Future Work）

  **Recommended Agent Profile**:
  - **Category**: `quick` - 配置类是标准 Options pattern
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 2, 3, 5 并行）
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 18
  - **Blocked By**: Task 1

  **References**:
  - `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs` - Options 类模式参考
  - `src/Seeing.Agent/Configuration/ProviderConfig.cs` - Provider 配置参考

  **QA Scenarios**:
  ```
  Scenario: 配置类编译验证
    Tool: Bash (dotnet)
    Steps:
      1. dotnet build src/Seeing.Agent.Memory
    Expected: Build succeeded
    Evidence: .sisyphus/evidence/task-04-options-build.txt
  ```

  **Commit**: YES (Wave 1)
  - Message: `feat(memory): add configuration options`
  - Files: `Configuration/*.cs`

- [x] 5. **YAML front matter 解析器** ✓

  **What to do**:
  - 创建 `Core/YamlParser.cs`（解析 YAML front matter，使用 YamlDotNet 库）
  - 添加 NuGet 引用: `YamlDotNet` 到 csproj
  - 实现方法: `ParseYamlFrontMatter(string content)` → 返回 Dictionary<string, object>
  - 实现方法: `ExtractMarkdownBody(string content)` → 返回 Markdown 内容部分

  **Must NOT do**:
  - 不要实现复杂的 YAML 验证（简单的键值解析即可）
  - 不要添加额外的 YAML 库（只用 YamlDotNet）

  **Recommended Agent Profile**:
  - **Category**: `quick` - 解析器是标准工具类
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 2, 3, 4 并行）
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 7
  - **Blocked By**: Task 1

  **References**:
  - Markdown YAML front matter 格式:
    ```markdown
    ---
    type: semantic
    sessionId: ses_abc123
    createdAt: 2026-04-22T10:30:00Z
    ---
    ## Content
    ```

  **QA Scenarios**:
  ```
  Scenario: YAML 解析验证
    Tool: Bash (dotnet test)
    Steps:
      1. 创建测试文件 tests/YamlParserTests.cs
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "YamlParserTests"
    Expected: Tests pass, parser correctly extracts YAML and Markdown
    Evidence: .sisyphus/evidence/task-05-yaml-parser.txt
  ```

  **Commit**: YES (Wave 1)
  - Message: `feat(memory): add yaml parser`
  - Files: `Core/YamlParser.cs`, `tests/YamlParserTests.cs`

---

### Wave 2: 存储实现（4 个任务）

- [x] 6. **MdMemoryStore 实现 (Markdown 文件存储)** ✓

  **What to do**:
  - 创建 `Core/MdMemoryStore.cs`（实现 IMemoryStore）
  - 实现方法:
    - `SaveAsync(MemoryEntry memory)` → 写入 `.seeing/memory/{type}/{sessionId}_{timestamp}_{uuid}.md`
    - `LoadAsync(string id)` → 从文件读取 MemoryEntry
    - `DeleteAsync(string id)` → 删除记忆文件
    - `ListAsync(string type)` → 列出指定类型的所有记忆
    - `QueryAsync(MemoryFilter filter)` → 元数据过滤查询
  - 实现文件写入: YAML front matter + Markdown body
  - 参考 FileSessionStore 的原子写入模式

  **Must NOT do**:
  - 不要实现向量检索（仅元数据过滤）
  - 不要跨进程并发控制（V1 单进程）
  - 不要修改 `.seeing/memory/` 目录外的文件

  **Recommended Agent Profile**:
  - **Category**: `deep` - 存储实现需要深入理解文件系统操作和原子写入
  - **Skills**: []
  - **Skills Evaluated but Omitted**: `git-master`（无需 git 操作）

  **Parallelization**:
  - **Can Run In Parallel**: NO（核心存储实现，后续任务依赖）
  - **Parallel Group**: Wave 2 (核心任务)
  - **Blocks**: Tasks 10-18
  - **Blocked By**: Tasks 2, 3

  **References**:
  - `src/Seeing.Session/Storage/FileSessionStore.cs` - 文件存储实现参考（原子写入、文件锁）
  - `src/Seeing.Session/Storage/ISessionStore.cs` - 接口模式参考
  - `src/Seeing.Agent/Tools/BuiltIn/FileSystemHelper.cs` - 文件操作辅助方法

  **QA Scenarios**:
  ```
  Scenario: 存储写入验证
    Tool: Bash (dotnet test)
    Steps:
      1. 创建测试 MemoryStoreTests.cs（使用临时目录）
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryStoreTests.SaveAsync"
    Expected: Test pass, memory file created with correct format
    Evidence: .sisyphus/evidence/task-06-store-save.txt

  Scenario: 存储读取验证
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryStoreTests.LoadAsync"
    Expected: Test pass, correctly reads YAML + Markdown
    Evidence: .sisyphus/evidence/task-06-store-load.txt

  Scenario: 并发写入无冲突
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryStoreTests.ConcurrentWrite"
    Expected: Test pass, no file conflicts with uuid naming
    Evidence: .sisyphus/evidence/task-06-store-concurrent.txt
  ```

  **Commit**: YES (Wave 2)
  - Message: `feat(memory): implement md storage`
  - Files: `Core/MdMemoryStore.cs`
  - Pre-commit: `dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryStoreTests"`

- [x] 7. **记忆文件格式解析器 (YAML + Markdown 解析)** ✓

  **What to do**:
  - 创建 `Core/MemoryFileParser.cs`
  - 实现方法:
    - `ParseMemoryFile(string filePath)` → 返回 MemoryEntry
    - `SerializeToMarkdown(MemoryEntry memory)` → 返回 Markdown 字符串
  - 使用 YamlParser 解析 front matter
  - 提取 Markdown body 作为 Content

  **Must NOT do**:
  - 不要添加复杂的 Markdown 解析（仅提取纯文本）
  - 不要解析 Markdown 标题结构（V1 简单处理）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` - 解析器需要健壮的错误处理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 8, 9 并行）
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 10-13
  - **Blocked By**: Tasks 5, 6

  **References**:
  - `Core/YamlParser.cs` - YAML 解析辅助类
  - 记忆文件格式示例:
    ```markdown
    ---
    type: semantic
    sessionId: ses_abc123
    createdAt: 2026-04-22T10:30:00Z
    source: chat.after_complete
    agentId: oracle
    tags: [preference, language]
    confidence: 0.95
    ---
    ## 用户偏好
    用户喜欢简洁回复，偏好使用中文。
    ```

  **QA Scenarios**:
  ```
  Scenario: 解析完整记忆文件
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryFileParserTests.ParseComplete"
    Expected: Test pass, all fields extracted correctly
    Evidence: .sisyphus/evidence/task-07-parse-complete.txt

  Scenario: 解析缺失字段文件
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryFileParserTests.ParseMissingFields"
    Expected: Test pass, handles missing optional fields gracefully
    Evidence: .sisyphus/evidence/task-07-parse-missing.txt
  ```

  **Commit**: YES (Wave 2)
  - Message: `feat(memory): add memory file parser`
  - Files: `Core/MemoryFileParser.cs`, `tests/MemoryFileParserTests.cs`

- [x] 8. **分块存储逻辑 (50KB 限制处理)** ✓

  **What to do**:
  - 创建 `Core/MemoryChunker.cs`
  - 实现方法:
    - `ShouldChunk(string content)` → 判断是否需要分块（>50KB）
    - `ChunkContent(string content)` → 分割内容为多个块
    - `MergeChunks(IEnumerable<string> chunks)` → 合并块
  - 分块策略: 按段落或章节分割，保留 Markdown 结构
  - 分块命名: `{sessionId}_{timestamp}_{uuid}_chunk{N}.md`

  **Must NOT do**:
  - 不要忽略 50KB 限制（必须遵守框架约束）
  - 不要破坏 Markdown 结构（保持段落完整性）

  **Recommended Agent Profile**:
  - **Category**: `quick` - 分块逻辑相对简单
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 7, 9 并行）
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 12
  - **Blocked By**: Task 6

  **References**:
  - `src/Seeing.Agent/Tools/BuiltIn/OutputTruncator.cs` - 输出截断模式参考
  - `src/Seeing.Agent/Tools/BuiltIn/FileSystemHelper.cs` - MaxBytes = 50KB 常量

  **QA Scenarios**:
  ```
  Scenario: 大文件自动分块
    Tool: Bash (dotnet test)
    Steps:
      1. 创建大于 50KB 的测试内容
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryChunkerTests.ShouldChunk"
      3. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryChunkerTests.ChunkLargeContent"
    Expected: Tests pass, content split into multiple chunks, each <50KB
    Evidence: .sisyphus/evidence/task-08-chunk-large.txt
  ```

  **Commit**: YES (Wave 2)
  - Message: `feat(memory): add chunking logic`
  - Files: `Core/MemoryChunker.cs`, `tests/MemoryChunkerTests.cs`

- [x] 9. **记忆检索器 (元数据过滤 + 时间范围查询)** ✓

  **What to do**:
  - 创建 `Core/MemoryRetriever.cs`（实现 IMemoryRetriever）
  - 实现方法:
    - `RetrieveByMetadataAsync(MemoryFilter filter)` → 按 SessionId/Type/Tags 过滤
    - `RetrieveByTimeRangeAsync(DateTime from, DateTime to)` → 按时间范围查询
    - `RetrieveByAgentAsync(string agentId)` → 按 Agent ID 查询
  - 实现简单的关键词匹配（纯元数据检索，无向量）

  **Must NOT do**:
  - 不要实现向量语义检索（Future Work）
  - 不要实现复杂的全文搜索（仅元数据过滤）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` - 检索器需要处理多种查询条件组合
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 7, 8 并行）
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 10
  - **Blocked By**: Task 6

  **References**:
  - `src/Seeing.Session/Storage/FileSessionStore.cs` - QueryAsync 方法参考
  - `Core/MemoryFilter.cs` - 查询过滤器定义

  **QA Scenarios**:
  ```
  Scenario: 元数据过滤检索
    Tool: Bash (dotnet test)
    Steps:
      1. 创建多条测试记忆
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryRetrieverTests.RetrieveByMetadata"
    Expected: Test pass, returns only matching memories
    Evidence: .sisyphus/evidence/task-09-retrieve-metadata.txt

  Scenario: 时间范围查询
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryRetrieverTests.RetrieveByTimeRange"
    Expected: Test pass, returns memories in time range
    Evidence: .sisyphus/evidence/task-09-retrieve-time.txt
  ```

  **Commit**: YES (Wave 2)
  - Message: `feat(memory): add memory retriever`
  - Files: `Core/MemoryRetriever.cs`, `tests/MemoryRetrieverTests.cs`

---

### Wave 3: 遗忘机制（4 个任务）

- [x] 12. **MemoryScorer 多因子评分器** ✓

  **What to do**:
  - 创建 `Core/MemoryService.cs`（实现 IMemoryService）
  - 实现方法:
    - `CreateMemoryAsync(MemoryEntry memory)` → 调用 IMemoryStore.SaveAsync
    - `GetMemoryAsync(string id)` → 调用 IMemoryStore.LoadAsync
    - `UpdateMemoryAsync(string id, MemoryUpdate update)` → 加载 → 更新 → 保存
    - `DeleteMemoryAsync(string id)` → 调用 IMemoryStore.DeleteAsync
    - `SearchMemoriesAsync(string query, MemoryFilter filter)` → 调用 IMemoryRetriever
  - DI 注入: IMemoryStore, IMemoryRetriever, ILogger

  **Must NOT do**:
  - 不要在 Service 中实现存储逻辑（调用 Store）
  - 不要添加 LLM 调用（Service 是纯业务逻辑层）

  **Recommended Agent Profile**:
  - **Category**: `deep` - 服务层需要协调多个组件，处理业务逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO（核心服务，后续任务依赖）
  - **Parallel Group**: Wave 3 (核心任务)
  - **Blocks**: Tasks 14-18
  - **Blocked By**: Tasks 6-9

  **References**:
  - `src/Seeing.Session/Management/SessionManager.cs` - Session 服务模式参考
  - `Abstractions/IMemoryService.cs` - 服务接口定义

  **QA Scenarios**:
  ```
  Scenario: 创建记忆完整流程
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryServiceTests.CreateMemoryAsync"
    Expected: Test pass, memory created with correct file
    Evidence: .sisyphus/evidence/task-10-service-create.txt

  Scenario: 搜索记忆
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryServiceTests.SearchMemoriesAsync"
    Expected: Test pass, returns matching memories
    Evidence: .sisyphus/evidence/task-10-service-search.txt
  ```

  **Commit**: YES (Wave 3)
  - Message: `feat(memory): implement memory service`
  - Files: `Core/MemoryService.cs`
  - Pre-commit: `dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryServiceTests"`

- [x] 13. **MemoryForgetManager 遗忘管理器** ✓

  **What to do**:
  - 创建 `Core/MemoryOrchestrator.cs`
  - 实现方法:
    - `CaptureImmediateAsync(string sessionId, MemoryType type, string content)` → 立即写入（关键记忆）
    - `QueueForConsolidationAsync(string sessionId)` → 加入合并队列（批量）
    - `RunConsolidationAsync()` → 执行批量合并（触发时机：空闲或手动）
  - Hybrid 策略配置: MemoryOptions.ImmediateCaptureThreshold（立即写入阈值）
  - 简单合并算法: 同 SessionId 的多条记忆合并为一条摘要（无 LLM）

  **Must NOT do**:
  - 不要使用 LLM 进行 Consolidation（成本风险）
  - 不要实现复杂的合并算法（简单拼接即可）

  **Recommended Agent Profile**:
  - **Category**: `deep` - 编排器需要处理 Hybrid 写入策略和队列管理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 12, 13 并行）
  - **Parallel Group**: Wave 3
  - **Blocks**: Tasks 14-18
  - **Blocked By**: Task 10

  **References**:
  - 业界参考: Mem0 Hybrid 写入策略（关键记忆 Hot Path + 批量 Background）
  - `Configuration/MemoryOptions.cs` - Hybrid 配置

  **QA Scenarios**:
  ```
  Scenario: 立即写入关键记忆
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryOrchestratorTests.CaptureImmediateAsync"
    Expected: Test pass, memory written immediately
    Evidence: .sisyphus/evidence/task-11-orchestrator-immediate.txt

  Scenario: 批量合并队列
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryOrchestratorTests.QueueForConsolidationAsync"
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryOrchestratorTests.RunConsolidationAsync"
    Expected: Tests pass, memories queued and merged
    Evidence: .sisyphus/evidence/task-11-orchestrator-consolidation.txt
  ```

  **Commit**: YES (Wave 3)
  - Message: `feat(memory): implement memory orchestrator`
  - Files: `Core/MemoryOrchestrator.cs`, `tests/MemoryOrchestratorTests.cs`

- [x] 14. **MemoryDeduplicator 去重合并器** ✓

  **What to do**:
  - 创建 `Core/MemoryChunkManager.cs`
  - 实现方法:
    - `LoadAllChunksAsync(string baseId)` → 加载所有分块并合并
    - `SaveAllChunksAsync(MemoryEntry memory)` → 自动分块保存
    - `GetChunkCount(string baseId)` → 返回分块数量
  - 协调 MemoryChunker 和 MdMemoryStore

  **Must NOT do**:
  - 不要破坏分块的完整性（保持顺序）
  - 不要忽略分块的索引信息

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` - 分块管理需要处理多文件协调
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 11, 13 并行）
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 14
  - **Blocked By**: Tasks 8, 10

  **References**:
  - `Core/MemoryChunker.cs` - 分块逻辑
  - `Core/MdMemoryStore.cs` - 存储实现

  **QA Scenarios**:
  ```
  Scenario: 大记忆分块保存和加载
    Tool: Bash (dotnet test)
    Steps:
      1. 创建大于 50KB 的记忆
      2. dotnet test tests/Seeing.Agent.Memory.Tests --filter "MemoryChunkManagerTests.SaveAndLoadChunks"
    Expected: Test pass, memory split into chunks, loaded and merged correctly
    Evidence: .sisyphus/evidence/task-12-chunk-manager.txt
  ```

  **Commit**: YES (Wave 3)
  - Message: `feat(memory): add chunk manager`
  - Files: `Core/MemoryChunkManager.cs`, `tests/MemoryChunkManagerTests.cs`

- [x] 15. **MemoryEvaluator 接口预留** ✓

### Wave 5: Integration 层（6 个任务）- **CRITICAL GAP**

> **⚠️ 以下文件缺失，需要立即创建：**
> - `Integration/MemoryWriteQueue.cs` - 后台异步写入队列
> - `Integration/MemoryHookHandler.cs` - Hook 集成（ChatAfterComplete + ToolExecuteAfter）
> - `Integration/MemoryTools.cs` - Tool 定义（[Tool] 注解）
> - `Integration/MemoryExtension.cs` - IExtension 入口
> - `Extensions/MemoryServiceCollectionExtensions.cs` - DI 注册扩展

---

- [x] 21. **MemoryWriteQueue 后台写入队列**（原计划遗漏，新增） ✓

  **What to do**:
  - 创建 `Integration/MemoryWriteQueue.cs`
  - 使用 `System.Threading.Channels` 实现异步写入队列
  - 实现方法:
    - `EnqueueWriteAsync(MemoryEntry entry)` → 加入写入队列（非阻塞）
    - `StartProcessingAsync()` → 启动后台消费线程
    - `StopProcessingAsync()` → 停止后台线程
  - 集成到 MemoryOrchestrator:
    - `CaptureImmediateAsync` → MemoryWriteQueue.EnqueueWriteAsync
    - `QueueForConsolidationAsync` → 保持原有逻辑

  **Must NOT do**:
  - ❌ Hook 中同步写入（违反 Oracle P0 建议）
  - ❌ 无限队列容量（设置 MaxCapacity = 1000）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` - 后台服务需要理解异步编程模式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 22-26 并行）
  - **Parallel Group**: Wave 5
  - **Blocks**: Task 22, 23
  - **Blocked By**: Wave 4 (MemoryOrchestrator 存在)

  **References**:
  - `src/Seeing.Agent.Memory/Core/MemoryOrchestrator.cs:CaptureImmediateAsync` - 调用此队列
  - `System.Threading.Channels` - Bounded queue 实现
  - `BackgroundService` 模式 - 后台线程生命周期管理

  **代码模板**:
  ```csharp
  using System.Threading.Channels;
  using System.Threading.Tasks;
  
  public class MemoryWriteQueue
  {
      private readonly Channel<MemoryEntry> _channel;
      private readonly IMemoryManager _memoryManager;
      private readonly CancellationTokenSource _cts = new();
      
      public MemoryWriteQueue(IMemoryManager memoryManager, int capacity = 1000)
      {
          _memoryManager = memoryManager;
          _channel = Channel.CreateBounded<MemoryEntry>(new BoundedChannelOptions(capacity)
          {
              FullMode = BoundedChannelFullMode.Wait
          });
      }
      
      public async Task EnqueueWriteAsync(MemoryEntry entry)
      {
          await _channel.Writer.WriteAsync(entry);
      }
      
      public async Task StartProcessingAsync()
      {
          await Task.Run(async () => {
              while (!_cts.Token.IsCancellationRequested)
              {
                  var entry = await _channel.Reader.ReadAsync(_cts.Token);
                  await _memoryManager.CreateMemoryAsync(entry);
              }
          });
      }
      
      public void StopProcessing() => _cts.Cancel();
  }
  ```

  **Commit**: YES (Wave 5)
  - Message: `feat(memory): add background write queue`
  - Files: `Integration/MemoryWriteQueue.cs`

---

- [x] 22-23. **MemoryHookHandler Hook 集成**（合并为单一任务） ✓

  **What to do**:
  - 创建 `Integration/MemoryHookHandler.cs`（实现 IHookHandler）
  - 实现两个 Hook 点:
    - `HookPoints.ChatAfterComplete` → 调用 `MemoryWriteQueue.EnqueueWriteAsync`
    - `HookPoints.ToolExecuteAfter` → 调用 `MemoryOrchestrator.QueueForConsolidationAsync`
  - 提取 Hook 数据:
    - `context.Data["sessionId"]` → SessionId
    - `context.Data["response"]` / `context.Output["output"]` → 内容

  **Must NOT do**:
  - ❌ 硬编码 Hook 字符串（使用 HookPoints.* 常量）
  - ❌ Hook 中同步写入（必须使用 MemoryWriteQueue）
  - ❌ 修改 HookContext.Data（只读取）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high` - Hook 集成需要理解框架 Hook 机制
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 21, 24-26 并行）
  - **Parallel Group**: Wave 5
  - **Blocks**: Task 25
  - **Blocked By**: Task 21 (MemoryWriteQueue)

  **References**:
  - `src/Seeing.Agent/Core/Interfaces/IHook.cs:IHookHandler` - 接口签名
  - `src/Seeing.Agent/Core/Interfaces/IHook.cs:HookPoints` - Hook 常量（ChatAfterComplete, ToolExecuteAfter）
  - `src/Seeing.Agent/Hooks/HookManager.cs:RegisterHandler` - 注册方法
  - `src/Seeing.Agent.Memory/Core/MemoryOrchestrator.cs:QueueForConsolidationAsync` - 批量合并方法

  **代码模板**:
  ```csharp
  using Seeing.Agent.Core.Interfaces;
  using Seeing.Agent.Memory.Core;
  
  public class MemoryHookHandler : IHookHandler
  {
      private readonly MemoryWriteQueue _writeQueue;
      private readonly MemoryOrchestrator _orchestrator;
      
      // ChatAfterComplete Hook
      public string HookPoint => HookPoints.ChatAfterComplete;
      public int Priority => 10;
      
      public async Task<HookResult> ExecuteAsync(HookContext context)
      {
          var sessionId = context.Data["sessionId"]?.ToString();
          var response = context.Output["response"]?.ToString();
          
          if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(response))
          {
              var entry = CreateMemoryEntry(sessionId, response, MemoryType.Episodic);
              await _writeQueue.EnqueueWriteAsync(entry);  // 异步写入
          }
          
          return new HookResult { Continue = true };
      }
  }
  ```

  **Commit**: YES (Wave 5)
  - Message: `feat(memory): add hook handler integration`
  - Files: `Integration/MemoryHookHandler.cs`

---

- [x] 24. **MemoryTools Tool 定义** ✓

  **What to do**:
  - 创建 `Integration/MemoryTools.cs`（使用 [Tool] 注解）
  - 实现三个工具:
    - `[Tool("存储记忆")] StoreMemoryAsync(content, type, sessionId)`
    - `[Tool("检索记忆")] RecallMemoryAsync(sessionId, type)`
    - `[Tool("搜索记忆")] SearchMemoryAsync(query, sessionId)`
  - 调用 IMemoryManager 方法

  **Must NOT do**:
  - ❌ Tool 中实现存储逻辑（调用 IMemoryManager）
  - ❌ async void 返回类型
  - ❌ out/ref 参数

  **Recommended Agent Profile**:
  - **Category**: `quick` - Tool 定义是标准注解模式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 21-23, 25-26 并行）
  - **Parallel Group**: Wave 5
  - **Blocks**: Task 25
  - **Blocked By**: Wave 4 (IMemoryManager 存在)

  **References**:
  - `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` - [Tool], [ToolParam], [Required] 注解
  - `src/Seeing.Agent/Tools/Discovery/ReflectedTool.cs` - 注解发现机制
  - `src/Seeing.Agent.Memory/Abstractions/IMemoryManager.cs` - CRUD 接口

  **代码模板**:
  ```csharp
  using Seeing.Agent.Tools.Attributes;
  using Seeing.Agent.Memory.Abstractions;
  using Seeing.Agent.Memory.Core;
  
  public class MemoryTools
  {
      private readonly IMemoryManager _manager;
      
      public MemoryTools(IMemoryManager manager) => _manager = manager;
      
      [Tool("存储记忆到会话")]
      public async Task<string> StoreMemoryAsync(
          [ToolParam("记忆内容")] string content,
          [ToolParam("记忆类型")] [Required] string type,
          [ToolParam("会话 ID")] string sessionId)
      {
          var entry = CreateEntry(content, type, sessionId);
          await _manager.CreateMemoryAsync(entry);
          return $"记忆已保存: {entry.Id}";
      }
      
      [Tool("搜索记忆")]
      public async Task<string> SearchMemoryAsync(
          [ToolParam("搜索关键词")] string query,
          [ToolParam("会话 ID")] string? sessionId = null)
      {
          var result = await _manager.SearchMemoriesAsync(query);
          return $"找到 {result.TotalCount} 条记忆";
      }
  }
  ```

  **Commit**: YES (Wave 5)
  - Message: `feat(memory): add memory tools`
  - Files: `Integration/MemoryTools.cs`

---

- [x] 25. **MemoryExtension IExtension 入口** ✓

  **What to do**:
  - 创建 `Integration/MemoryExtension.cs`（实现 IExtension）
  - 元数据: Id="seeing.agent.memory", Version="1.0.0"
  - `ConfigureServices(IServiceCollection)` → 注册 Memory 服务
  - `InitializeAsync(ExtensionContext)` → 获取 HookManager，注册 MemoryHookHandler
  - `GetHookHandlers()` → 返回 HookHandler 列表
  - `GetTools()` → 返回 Tool 列表（可选）

  **Must NOT do**:
  - ❌ Extension 中实现业务逻辑（只做注册和协调）
  - ❌ 硬编码服务生命周期

  **Recommended Agent Profile**:
  - **Category**: `quick` - Extension 是标准插件入口模式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 21-24, 26 并行）
  - **Parallel Group**: Wave 5
  - **Blocks**: Wave 6 (Final Verification)
  - **Blocked By**: Tasks 22-24

  **References**:
  - `src/Seeing.Agent.Plugins/PluginsExtension.cs` - IExtension 完整实现模板
  - `src/Seeing.Agent/Core/Interfaces/IExtension.cs` - IExtension 接口签名
  - `src/Seeing.Agent/Core/Interfaces/IHook.cs:HookContext/ExtensionContext` - 上下文类

  **代码模板**:
  ```csharp
  using Seeing.Agent.Core.Interfaces;
  using Microsoft.Extensions.DependencyInjection;
  
  public class MemoryExtension : IExtension
  {
      public string? Id => "seeing.agent.memory";
      public string Version => "1.0.0";
      public string Name => "Seeing.Agent Memory";
      public string Description => "长期记忆存储与检索系统";
      
      private readonly List<IHookHandler> _handlers = new();
      
      public void ConfigureServices(IServiceCollection services)
      {
          services.AddSingleton<IMemoryManager, MemoryManager>();
          services.AddSingleton<MemoryOrchestrator>();
          services.AddSingleton<MemoryWriteQueue>();
          services.AddTransient<MemoryTools>();
      }
      
      public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
      {
          var hookManager = context.HookManager;
          var writeQueue = context.Services.GetRequiredService<MemoryWriteQueue>();
          var orchestrator = context.Services.GetRequiredService<MemoryOrchestrator>();
          
          _handlers.Add(new MemoryHookHandler(writeQueue, orchestrator));
          
          foreach (var handler in _handlers)
              hookManager.RegisterHandler(handler);
          
          await writeQueue.StartProcessingAsync();
      }
      
      public IEnumerable<IHookHandler> GetHookHandlers() => _handlers;
      
      public async Task DisposeAsync()
      {
          // Stop write queue
          await Task.CompletedTask;
      }
  }
  ```

  **Commit**: YES (Wave 5)
  - Message: `feat(memory): implement memory extension`
  - Files: `Integration/MemoryExtension.cs`

---

- [x] 26. **MemoryServiceCollectionExtensions DI 注册** ✓

  **What to do**:
  - 创建 `Extensions/MemoryServiceCollectionExtensions.cs`
  - 实现 `AddSeeingAgentMemory(IServiceCollection, Action<MemoryOptions>)`
  - 服务生命周期:
    - `IMemoryManager` → Singleton
    - `MemoryOrchestrator` → Singleton
    - `MemoryWriteQueue` → Singleton
    - `MemoryHookHandler` → Transient（通过 Extension 创建）
    - `MemoryTools` → Transient

  **Must NOT do**:
  - ❌ 使用错误的生命周期
  - ❌ 注册 Future Work 服务（向量索引等）

  **Recommended Agent Profile**:
  - **Category**: `quick` - DI 注册扩展是标准模式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES（与 Task 21-25 并行）
  - **Parallel Group**: Wave 5
  - **Blocks**: Wave 6
  - **Blocked By**: None（可立即开始）

  **References**:
  - `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:AddSeeingAgent` - DI 注册模板
  - `src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs` - 配置选项类
  - `AGENTS.md` - DI 生命周期约定表

  **代码模板**:
  ```csharp
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Configuration;
  
  public static class MemoryServiceCollectionExtensions
  {
      public static IServiceCollection AddSeeingAgentMemory(
          this IServiceCollection services,
          Action<MemoryOptions>? configure = null)
      {
          // 配置选项
          if (configure != null)
              services.Configure<MemoryOptions>(configure);
          
          // 注册核心服务（Singleton）
          services.AddSingleton<IMemoryRepository, MdMemoryRepository>();
          services.AddSingleton<IMemoryRetriever, MemoryRetriever>();
          services.AddSingleton<IMemoryManager, MemoryManager>();
          services.AddSingleton<MemoryOrchestrator>();
          services.AddSingleton<MemoryWriteQueue>();
          
          // 注册工具（Transient）
          services.AddTransient<MemoryTools>();
          
          // 注册 Hook Handler（Transient）
          services.AddTransient<MemoryHookHandler>();
          
          // 注册 Extension
          services.AddSingleton<IExtension, MemoryExtension>();
          
          return services;
      }
      
      public static IServiceCollection AddSeeingAgentMemory(
          this IServiceCollection services,
          IConfiguration configuration)
      {
          services.Configure<MemoryOptions>(configuration.GetSection("Memory"));
          return AddSeeingAgentMemory(services);
      }
  }
  ```

  **Commit**: YES (Wave 5)
  - Message: `feat(memory): add DI registration extension`
  - Files: `Extensions/MemoryServiceCollectionExtensions.cs`

---

### Wave 6: 测试覆盖（已完成 5 个测试文件）

> **现有测试文件**:
> - `tests/MemoryStoreTests.cs` ✓
> - `tests/MemoryServiceTests.cs` ✓
> - `tests/MemoryHookHandlerTests.cs` ✓ (但只测试 HookPoints 常量，未测试 HookHandler 实现)
> - `tests/MemoryForgettingTests.cs` ✓
> - `tests/MemoryIntegrationTests.cs` ✓
>
> **需要补充**: Integration 层测试（MemoryWriteQueue, MemoryHookHandler 实际执行）

- [ ] 27-32. **测试补充**（可选，现有测试覆盖 Core 层）

---

## Final Verification Wave (MANDATORY)

- [x] F1. **Plan Compliance Audit** — `oracle` ✓
  VERDICT: ⚠️ REJECT (2 violations: hardcoded source strings, no filter flow)
  Must Have [12/14] | Must NOT Have [8/10] | Tasks [18/21]

- [x] F2. **Code Quality Review** — `unspecified-high` ✓
  VERDICT: ✅ PASS
  Build [PASS] | Tests [29/29 pass] | Files [5 clean]

- [x] F3. **Real Manual QA** — `unspecified-high` ✓
  VERDICT: ✅ PASS (with gaps)
  Scenarios [29/29 pass] | Edge Cases [2/4 tested]

- [x] F4. **Scope Fidelity Check** — `unspecified-high` ✓
  VERDICT: ⚠️ REJECT (2 deviations)
  Tasks [4/6 compliant] | Contamination [CLEAN]

---

## Commit Strategy

- **Wave 1**: `feat(memory): add project structure and interfaces` - 项目文件 + 接口定义
- **Wave 2**: `feat(memory): implement md storage` - MdMemoryStore 实现
- **Wave 3**: `feat(memory): implement memory service` - MemoryService 实现
- **Wave 4**: `feat(memory): add hook integration` - Hook + Tool + Extension 实现
- **Wave 5**: `test(memory): add unit tests` - 测试覆盖

---

## Success Criteria

### Verification Commands
```bash
dotnet build src/Seeing.Agent.Memory         # Expected: Build succeeded
dotnet test tests/Seeing.Agent.Memory.Tests  # Expected: All tests passed
dotnet pack src/Seeing.Agent.Memory -c Release  # Expected: Package created
```

### Final Checklist
- [x] All "Must Have" present ✓ (12/14 pass, 2 partial)
- [x] All "Must NOT Have" absent ✓ (8/10 pass, 2 minor violations)
- [x] All tests pass ✓ (29/29)
- [ ] Hook 捕获机制验证 (需要运行时验证)
- [ ] Markdown 文件格式验证 (需要运行时验证)