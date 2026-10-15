# 改名 Core + Host Shape + Scenario + 生命周期（Phase 6-7）

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** 主库改名 `Seeing.Agent.Core`；落地 Host Shape（Web/Headless/Gateway/Embed）+ 内置 Scenario（minimal/code/work/research/full）；实现进程级结算引擎 + 模块 Activate/Deactivate 生命周期 + HostedService 门控；`SessionData` 加 `Scenario` 字段；`ExecutionJobService` 会话级结算 + schema 唯一计算点 + `SchemaSnapshotEvent`；存量 providers/会话迁移。

**Architecture:** Phase 6 改名 + Host Shape + Scenario 数据 + 结算引擎。Phase 7 生命周期门控 + 会话级结算 + schema 唯一计算。完成后 spec §5 全部落地。

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions。

## Global Constraints
- 改名前类型已搬空（Phase 2-3 完成），禁止「改名却仍装工具」。
- 内置 Scenario 在 Core 以纯字符串 id 数组表达，Core 不引用能力包类型。
- `InitializeSeeingAsync` 在 `Host.StartAsync` 之前显式调用。
- 进程级热重载 Deactivate 推迟到在途执行结束（默认）；会话级切换只对下一次 Submit 生效。
- schema 唯一计算点在 `ExecutionJobService`，executor 读 `context.ToolSchemas`。
- 存量 `providers.json` PascalCase 归一化为小写；`SessionData.Scenario` 缺失为 null 回退进程级。
- 测试用 VSTest 直接运行已构建程序集。

## Execution Policy (user override)
- Prefer parallel waves; review only at Wave/Phase checkpoints (no per-task review).
- Order may be adjusted; mark superseded tasks SKIPPED with reason.
- Task 5 (sample startup): **SKIPPED** — already landed in Phase 5 (`AddSeeingCore` + `InitializeSeeingAsync` in WebUI/Gateway.Server). Re-verify after Host Shape (Task 4) if samples need Host Shape package refs.

### Suggested waves
- Phase 6 W1 (serial): Task 1 rename
- Phase 6 W2 (parallel): Task 2 Scenario + Task 3 Settlement + Task 12 providers migrate（独立）
- Phase 6 W3 (serial/parallel-safe): Task 4 Host Shape → touch samples only after W2 settles
- Phase 6 checkpoint → Phase 7
- Phase 7 W1 (parallel): Task 8 SessionData.Scenario + Task 6 Lifecycle（T6 依赖 T3）
- Phase 7 W2 (parallel after W1): Task 7 HostedService门控 + Task 9 会话级结算 + Task 10 schema + Task 11 reload + Task 13 seams
- Phase 7 W3: Task 14 全量回归 + Phase checkpoint review

---

## Phase 6: 改名 Core + Host Shape + Scenario + 结算

### Task 1: 主库改名 Seeing.Agent.Core
Rename `src/Seeing.Agent/` → `src/Seeing.Agent.Core/`；Update csproj/slnx/命名空间
- [ ] 改目录名，`Seeing.Agent.csproj` → `Seeing.Agent.Core.csproj`，AssemblyName/RootNamespace 改 `Seeing.Agent.Core`
- [ ] slnx 改项目引用名
- [ ] Grep 全库 `using Seeing.Agent;` 改 `using Seeing.Agent.Core;`（能力包不引用主库，仅 sample/Hosting/Core 内部）
- [ ] 全量构建 Expected 成功
- [ ] Commit `refactor: rename Seeing.Agent to Seeing.Agent.Core`

### Task 2: 内置 Scenario 数据
Create `src/Seeing.Agent.Core/Scenarios/BuiltInScenarios.cs`; Create `src/Seeing.Agent.Core/Scenarios/ScenarioDefinition.cs`
- [ ] `ScenarioDefinition` record（`Name`/`Modules:IReadOnlyList<string>`/`DefaultAgent`/`Seams`/`ToolsDisabled`）
- [ ] `BuiltInScenarios` 静态返回 minimal/code/work/research/full（**纯字符串 id 数组**，Core 不引用能力包类型）
  - minimal: `[io.local, agents.builtin, llm.openai, basic]`, agent=general
  - code: minimal+`[filesystem,shell,git,subagent]`, agent=build
  - work: minimal+`[filesystem,web,memory,scheduler]`, agent=general
  - research: minimal+`[web,memory]`, agent=explore
  - full: 全部已引用模块, agent=build
- [ ] 测试：`BuiltInScenarios.Code.Modules` 含 `git`；Core 程序集无 `Tools.Git` 类型引用（反射扫）
- [ ] Commit `feat(scenarios): add built-in scenario definitions as string id arrays`

### Task 3: 结算引擎（进程级）
Create `src/Seeing.Agent.Core/Modules/SettlementEngine.cs`; Create `src/Seeing.Agent.Core/Modules/ModuleCatalog.cs`
- [ ] `ModuleCatalog:IModuleCatalog`，持 `available`（宿主登记目录）+ `enabled`+`unhealthy`
- [ ] `SettlementEngine.SettleAsync`：`available=宿主目录`；`scenario=seeing.json.scenario ?? Host 默认`；`base=scenario.modules ∩ available`；`enabled=(用户 modules.enabled ?? base) ∩ available − 用户 modules.disabled`
- [ ] 未知 id 告警忽略；`DependsOn` 未启用 → 拒绝启动
- [ ] Scenario 引用未引用模块 id → 该 id 不在 available，告警忽略，不拒启
- [ ] 测试：目录外 id 不启用；硬依赖缺失拒启；未引用 id 告警忽略
- [ ] Commit `feat(settlement): process-level settlement engine`

### Task 4: Host Shape 包
Create `src/Seeing.Agent.Hosting.Web/`+`src/Seeing.Agent.Hosting.Headless/`+`src/Seeing.Agent.Hosting.Gateway/`
- [ ] 各 Host Shape 包**不 ProjectReference 能力包**，只做 UI 表面+宿主 HostedService+声明可承载模块类型
- [ ] `Hosting.Web`：Blazor 布局壳+Circuit+`BlazorPermissionChannel`（从 WebUI sample 抽公共部分）
- [ ] `Hosting.Headless`：无 UI 的执行宿主
- [ ] `Hosting.Gateway`：Gateway 服务端宿主
- [ ] 能力包由 sample 自己引用
- [ ] 测试：Host Shape 包不引用能力包（`dotnet list` 验证）
- [ ] Commit `feat(hosting): add Host Shape packages (Web/Headless/Gateway)`

### Task 5: 改 sample 启动顺序 — SKIPPED
~~Modify `samples/Seeing.Agent.WebUI/Program.cs` + `samples/Seeing.Gateway.Server/Program.cs`~~
- **SKIPPED (2026-09-08):** Phase 5 already shipped `AddSeeingCore(registry)` + `InitializeSeeingAsync` before host start. Re-check after Task 4 if Host Shape package refs need wiring.
- [x] 新模板已在 P5-T7
- [x] `InitializeSeeingAsync` 已在 Host.Start 前
- [x] 删 `AddSeeingAgent()`

---

## Phase 7: 生命周期 + 会话级结算 + schema 唯一计算

### Task 6: 模块 Activate/Deactivate 生命周期
Create `src/Seeing.Agent.Core/Modules/ModuleLifecycleManager.cs`
- [ ] `ActivateAsync`：调各启用模块 `ISeeingModule.ActivateAsync`（注册工具/连外部/贡献提示词）
- [ ] `DeactivateAsync`：对称卸工具/停连接/撤 Handler
- [ ] `InitializeSeeingAsync`：工作区→结算→Activate
- [ ] 测试：Deactivate 后工具与分节消失；再 Activate 恢复
- [ ] Commit `feat(lifecycle): module Activate/Deactivate lifecycle`

### Task 7: HostedService 门控
Modify 各模块 HostedService（Acp/Memory/Scheduler 的 3 个）
- [ ] 每个 `StartAsync` 第一行查 `IModuleCatalog.IsEnabled(moduleId)`，未启用 return
- [ ] 长循环 `BackgroundService.ExecuteAsync` 改双条件 `while (!stoppingToken.IsCancellationRequested && moduleEnabled)`
- [ ] `Deactivate` 翻 `moduleEnabled=false`+写哨兵消息唤醒 Channel 读取
- [ ] 连接类资源（`SqliteConnection`/`AcpConnectionManager`）不进 DI singleton，由模块 Activate/Deactivate 自管；DI 只注册工厂
- [ ] 测试：未启用模块 HostedService 空转结束；Deactivate 后长循环退出
- [ ] Commit `feat(hosted): IsEnabled gating + connection lifecycle self-managed`

### Task 8: SessionData.Scenario + 存量会话兼容
Modify `src/Seeing.Session/Core/SessionData.cs` + `src/Seeing.Session/Management/SessionManager.cs`
- [ ] `SessionData` 加 `string? Scenario` + `SessionScenarioOverride`（可选会话级模块/工具裁剪）
- [ ] 反序列化缺失 `Scenario` 为 null（**不报错、不强制写值**），结算时 null 回退进程级
- [ ] `SessionManager.Create(string? partitionId, string? selectedAgent, string? scenario=null)`，scenario null=进程级
- [ ] `CreateChildAsync` 加 `string? scenario` 参数
- [ ] `TaskTool` 创建子会话时显式传 `parent.Scenario`
- [ ] 测试：存量会话文件无 Scenario 反序列化为 null；null 回退进程级
- [ ] Commit `feat(session): add SessionData.Scenario with backward compat`

### Task 9: 会话级结算
Modify `src/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`
- [ ] 执行前读 `session.Scenario`+`SessionScenarioOverride` 计算 `sessionEnabled`
- [ ] `sessionScenario = session.Scenario ?? 进程级 scenario`
- [ ] `sessionBase = sessionScenario.modules ∩ 进程级 enabled`
- [ ] `sessionEnabled = (session.modules.enabled ?? sessionBase) ∩ 进程级 enabled − session.tools.disabled − 用户 tools.disabled`
- [ ] 会话级只能收窄进程级已启用，不能新增；取交集
- [ ] 会话级切换只对**下一次 SubmitAsync** 生效，当前轮次用快照 scenario 跑完
- [ ] 测试：会话 A 用 code、B 用 work 同进程并行；A 看不到 memory，B 看不到 git；会话级只收窄不新增
- [ ] Commit `feat(settlement): session-level settlement in ExecutionJobService`

### Task 10: schema 唯一计算点 + SchemaSnapshotEvent
Modify `ExecutionJobService.cs` + `NativeAgentExecutor.cs`; Create `SchemaSnapshotEvent` 已在 Phase 1
- [ ] 新 `IToolManager` API：`GetToolSchemasAsync(IReadOnlyCollection<string> settledToolIds, AgentDefinition agent)` 返回层 3（Agent Allowed/Denied）过滤后 schema
- [ ] `ExecutionJobService` 在调 executor 前算好 schema（层 1∩层 2 已结算 tool id 集 + agent），结果经 `AgentContext.ToolSchemas` 传入 executor
- [ ] `NativeAgentExecutor.BuildRequestAsync` **删自算 `GetToolSchemas(agent)` 调用**，改读 `context.ToolSchemas`
- [ ] `ExecutionJobService` 在调 executor 前发 `SchemaSnapshotEvent`（`ToolIds`+`SectionIds`+`ExecutionId`+`SessionId`）
- [ ] `ChatEventTracker` 写入 `SessionMessage.Metadata["schema_snapshot"]`
- [ ] `SessionForker` 深拷贝 metadata
- [ ] 测试：`SchemaSnapshotEvent` 与 `ChatRequest.Tools` 同源；executor 不自算 schema（反射断言无 `GetToolSchemas(agent)` 调用）
- [ ] Commit `feat(schema): ExecutionJobService single computation point + SchemaSnapshotEvent`

### Task 11: 进程级热重载 + 在途轮次边界
Create `src/Seeing.Agent.Core/Modules/ReloadOrchestrator.cs`
- [ ] 配置文件变更 → 重新结算 → Activate 新增模块 / Deactivate 移除模块
- [ ] 检测有在途执行 → **Deactivate 推迟到在途执行结束**（默认）；强制切换取消在途并报错（`ReloadOrchestrator` 配置决定）
- [ ] 不能启用从未引用的包
- [ ] 测试：在途执行时热重载 Deactivate 推迟；强制切换取消在途
- [ ] Commit `feat(reload): process-level hot reload with in-flight boundary`

### Task 12: 存量 providers.json 迁移
Modify `ProviderConfig` 反序列化 + `ProviderManager` 加载
- [ ] 反序列化时旧 PascalCase 值（`OpenAI`/`Anthropic`）归一化为小写（`openai`/`anthropic`）
- [ ] 读时容错、写时新格式；首次保存自动迁移
- [ ] 测试：读旧 `providers.json`（`"type":"OpenAI"`）归一化不报错；首次保存写小写
- [ ] Commit `feat(llm): migrate legacy providers.json PascalCase to lowercase`

### Task 13: seams 绑定（executionWorld/permissionChannel）
Modify `SettlementEngine`
- [x] `seams.executionWorld` 按 id 找已登记模块，由该模块在 `ConfigureServices` 注册 `IExecutionWorld`
- [x] `seams.permissionChannel` 同理，禁止 Core 写死 `blazor`/`deny-all` switch
- [x] 同一 seam 提供方目录里只能有一个绑定，多个 → 拒启
- [x] 测试：seam 未绑定却有消费方 Activate → 拒启；多绑定 → 拒启
- [x] Commit `feat(seams): bind executionWorld/permissionChannel by module id`

### Task 14: 全量回归
- [ ] 全量构建 Expected 成功
- [ ] 全量测试 Expected 全绿
- [ ] Commit `chore: phase 6-7 core rename + lifecycle complete`

---

## Self-Review
- **Spec coverage:** §9.6（改名 Core+Host Shape+内置 Scenario+改 sample+InitializeSeeingAsync）、§9.7（HostedService 门控+SessionData.Scenario+会话级结算）全覆盖。§5.3 在途边界/schema 唯一计算点、§4.2 seams 绑定、§5.5 存量迁移均有 Task。
- **Placeholder:** 无 TBD；每 Task 给确切路径与改造点。
- **Type consistency:** `ScenarioDefinition`/`SettlementEngine`/`ModuleCatalog`/`ReloadOrchestrator` 命名跨 Task 一致；`SessionData.Scenario` 与 Phase 1 `IExecutionSubmitter` 协调。
