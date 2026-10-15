# Hosting 抽出与 Config/Options 解耦（Phase 4-5）

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** 抽出 `Seeing.Agent.Hosting`（Execution+Task+Orchestrator+命令发现），切断 App→ACP；`AgentExecutorRouter` 替换 `ReplaceExecutionRouter`；`TaskTool`/`TaskStatusTool` 解耦服务定位器；`SeeingAgentOptions` 拆节；删 `IExtension`/PluginLoader/`AddSeeingAgent`/`PrimaryTools` 硬编码；`UnifiedConfigManager` 消费 `IConfigSectionRegistry`，`LoadAsync` 推迟到 `InitializeSeeingAsync`；`AgentManager` 两阶段注册。

**Architecture:** Phase 4 抽 Hosting 并解耦执行环。Phase 5 拆 Options + Config 注册表 + Agent 两阶段。完成后主库无神对象 Options、无文件插件扫描、无硬编码工具集。

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions。

## Global Constraints
- 依赖只向下：Hosting 引用 Core+Abstractions，禁止引用 ACP。
- `TaskTool`/`TaskStatusTool` 只依赖 `IExecutionSubmitter`+`IExecutionStatusProvider`+`IExecutionEventPublisher`（均 Abstractions），不用 `context.Services`。
- `AcpAgentExecutor` 改 implementation 后删 `_inner` 与透传分支。
- Hook 点用 `HookPoints.*` 常量。
- 测试用 VSTest 直接运行已构建程序集。

---

## Phase 4: Hosting 抽出与执行解耦

### Task 1: 抽出 Seeing.Agent.Hosting
Create `src/Seeing.Agent.Hosting/`; Move `src/Seeing.Agent.App/*`（ChatOrchestrator/ChatExecutionQueue/ChatRunTracker）+ `src/Seeing.Agent/Execution/ExecutionJobService.cs` + `src/Seeing.Agent/Tools/BuiltIn/Task/{TaskTool,TaskStatusTool}.cs` + `todo_write` 工具；Create `HostingServiceCollectionExtensions`
- [ ] 建项目引用 Core+Abstractions，加入 slnx。**禁止引用 ACP**
- [ ] 移文件，命名空间改 `Seeing.Agent.Hosting`
- [ ] `HostingServiceCollectionExtensions`：`AddChatOrchestrator`+`AddExecutionEngine`（从 App 层搬来）
- [ ] 删 `src/Seeing.Agent.App/`（并入 Hosting）。slnx 删 App 项目
- [ ] 全量构建 Expected 成功
- [ ] Commit `feat(hosting): extract Seeing.Agent.Hosting from App`

### Task 2: AgentExecutorRouter 替换 ReplaceExecutionRouter
Create `src/Seeing.Agent/Core/AgentExecutorRouter.cs`; Modify `NativeAgentExecutor` 实现 `IAgentExecutorImplementation`; Modify `src/Seeing.Agent.Acp/Execution/AcpAgentExecutor.cs`; Delete `AcpServiceCollectionExtensions.ReplaceExecutionRouter`
- [ ] `AgentExecutorRouter:IAgentExecutor`，构造 `IEnumerable<IAgentExecutorImplementation>`，按 `AgentRuntime` 分派
- [ ] `NativeAgentExecutor` 实现 `IAgentExecutorImplementation`（`SupportedRuntime=Native`）
- [ ] `AcpAgentExecutor` 改 `IAgentExecutorImplementation`（`SupportedRuntime=AcpPassthrough`）；**删 `_inner NativeAgentExecutor` 构造依赖与 `Runtime != AcpPassthrough` 透传分支**
- [ ] Core 注册 `AgentExecutorRouter` 为 `IAgentExecutor`；Acp 模块注册 `AcpAgentExecutor` 为 `IAgentExecutorImplementation`（追加非 Replace）
- [ ] 删 `AcpServiceCollectionExtensions.ReplaceExecutionRouter`
- [ ] 测试：容器只有一个 `IAgentExecutor`（router）；`IAgentExecutorImplementation` 多个按 Runtime 分派；无 Replace
- [ ] Commit `feat(executor): AgentExecutorRouter replaces ReplaceExecutionRouter`

### Task 3: TaskTool/TaskStatusTool 解耦服务定位器
Modify `TaskTool.cs`+`TaskStatusTool.cs`; `ExecutionJobService` 实现 `IExecutionSubmitter`+`IExecutionStatusProvider`
- [ ] `ExecutionJobService` 实现 `IExecutionSubmitter`（`SubmitAsync`/`CancelAsync`/`CancelBySessionAsync`/`WaitForExecutionAsync`）+ 已有 `IExecutionStatusProvider`
- [ ] `TaskTool`/`TaskStatusTool` 构造器注入 `IExecutionSubmitter`+`IExecutionStatusProvider`+`IExecutionEventPublisher`，删 `context.Services?.GetService(typeof(ExecutionJobService))`
- [ ] 测试：两工具不解析 `ExecutionJobService` 具体类（反射断言无 `typeof(ExecutionJobService)` 引用）
- [ ] Commit `feat(task): decouple TaskTool from service locator via IExecutionSubmitter`

### Task 4: 切断 App→ACP 斜杠命令
Grep `src/Seeing.Agent.App`（已并入 Hosting）残留 `AcpDynamicSkillCommand`/ACP 引用
- [ ] Grep `AcpDynamicSkillCommand`/`using ...Acp` 在 Hosting
- [ ] 删或改：斜杠命令注册表保持空，ACP 命令由 Acp 模块自己注册（Phase 6 模块化时完成）
- [ ] 全量构建 Expected 成功（Hosting 不引用 ACP）
- [ ] Commit `refactor(hosting): cut App→ACP command dependency`

---

## Phase 5: Options 拆节 + Config 注册表 + Agent 两阶段

### Task 5: SeeingAgentOptions 拆节
Create 各模块 Options 类（`McpOptions`/`SkillsOptions`/`SchedulerOptions`/`GatewayOptions`/`AcpOptions`/`TokenBudgetOptions`/`MemoryOptions`/`ShellOptions`）；Modify `SeeingAgentOptions` 删子系统字段
- [ ] Grep `SeeingAgentOptions` 全部子节字段
- [ ] 各 Options 类迁到对应能力包（McpOptions→Mcp，SchedulerOptions→Scheduler 等），各 `AddOptions<TOptions>().Bind(configSection)`
- [ ] `SeeingAgentOptions` 只留脊柱字段（modules/seams/agents/permission/models/workspace）
- [ ] 测试：Core 程序集不含 Gateway/Acp/TokenBudget Options 类型（反射扫 `Seeing.Agent.Core` 程序集）
- [ ] Commit `refactor(options): split SeeingAgentOptions into per-module options`

### Task 6: UnifiedConfigManager 消费 IConfigSectionRegistry
Modify `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`; Create `ConfigSectionRegistry.cs`（主库，实现 `IConfigSectionRegistry`）
- [ ] `ConfigSectionRegistry:IConfigSectionRegistry`（可变 `Dictionary`，`Register`/`Sections`/`TryGet`）
- [ ] `UnifiedConfigManager.BuildSectionRegistry` 改为只注册脊柱节（modules/seams/agents/permission/models/workspace）
- [ ] `UnifiedConfigManager` 构造接 `IConfigSectionRegistry`，`GetSection<T>` 从注册表取 meta 后从 `_cache` 反序列化，不再 switch 到 `SeeingAgentOptions`
- [ ] 能力包 `ConfigureServices` 通过注入的 `IConfigSectionRegistry` 注册自己的节（mcp/skills/scheduler 等）
- [ ] `LoadAsync` 不在工厂内同步调，推迟到 `InitializeSeeingAsync`
- [ ] 测试：`BuildSectionRegistry` 只含脊柱节；能力包节由模块注册；`LoadAsync` 推迟
- [ ] Commit `refactor(config): UnifiedConfigManager consumes IConfigSectionRegistry`

### Task 7: 公共 API 形状 + InitializeSeeingAsync
Modify `ServiceCollectionExtensions.cs`（AddSeeingCore/AddSeeingModule 签名加 `IConfigSectionRegistry`）；Create `InitializeSeeingAsync`
- [ ] `AddSeeingCore(IConfigSectionRegistry registry)` 与 `AddSeeingModule<T>(IConfigSectionRegistry registry)` 共用签名
- [ ] 宿主新模板：`var registry = new ConfigSectionRegistry(); services.AddSingleton(registry); AddSeeingAcp(registry); ...; AddSeeingCore(registry);`
- [ ] `InitializeSeeingAgentAsync` 改名 `InitializeSeeingAsync`：工作区→结算→Activate，必须在 `Host.StartAsync` 之前显式调用
- [ ] 删 `AddSeeingAgent()` 全开入口
- [ ] 改各 sample `Program.cs` 调用顺序（AddSeeingModule 系列在 AddSeeingCore 之前）
- [ ] 测试：`AddSeeingAgent` 方法不存在（反射断言）；`AddSeeingCore` 签名含 registry 参数
- [ ] Commit `feat(api): AddSeeingCore/AddSeeingModule share IConfigSectionRegistry, rename InitializeSeeingAsync`

### Task 8: AgentManager 两阶段注册
Modify `src/Seeing.Agent/Configuration/AgentManager.cs`; 删 `ServiceCollectionExtensions.GetBuiltInAgents`/`DiscoverAgentsAsync`
- [ ] `AgentManager` 构造器删 `IEnumerable<AgentDefinition> builtInAgents` 参数，只持 `IAgentStore`，启动时 store 为空
- [ ] 删 `ServiceCollectionExtensions` 的 `GetBuiltInAgents()`+`DiscoverAgentsAsync()` 调用
- [ ] `AgentsBuiltInModule.ActivateAsync`（Phase 2-3 Task 14 已建）通过 `IAgentStore.RegisterAsync` 注册人格
- [ ] 结算引擎保证 `Agents.BuiltIn` 在 `AgentManager.StartAsync` 之前 Activate（Phase 6 结算引擎实现）
- [ ] 测试：`AgentManager` 构造器无 `IEnumerable<AgentDefinition>` 参数（反射断言）
- [ ] Commit `refactor(agents): AgentManager two-phase registration, built-ins via module`

### Task 9: 删 IExtension/PluginLoader/PrimaryTools 硬编码
Delete `IExtension`/`ExtensionManager`/`ExtensionLoader`/`ComponentManager`; Modify `ToolManager.cs`
- [ ] 删 `Abstractions/Extensions/IExtension.cs` 及 7 个拆分接口、`ExtensionManager`、`ExtensionLoader`、`ComponentManager`
- [ ] `ToolManager` 删 `PrimaryTools`/`SubAgentTools`/`SubAgentExcludedTools` 三个 HashSet
- [ ] `GetToolSchemasForModeAsync(mode)` 删；保留 `GetToolSchemasForAgentAsync(agent)` 走层 2（Agent Allowed/Denied）
- [ ] `AgentRegistry` 注册时校验 `Mode==SubAgent` 且 `DeniedTools` 不含 `task` → 自动注入 `task` 到 `DeniedTools` 并告警
- [ ] 删 `seeing.json` Plugins 扫描
- [ ] 测试：`ToolManager` 无三个 HashSet（反射断言）；SubAgent 漏写 `task` 自动注入并告警
- [ ] Commit `refactor: remove IExtension/PluginLoader/PrimaryTools hardcode`

### Task 10: PromptBuilder 解耦 SkillManager + section 注入
Modify `src/Seeing.Agent/Core/Prompts/PromptBuilder.cs`
- [ ] 删 `PromptBuilder` 对 `SkillManager` 具体类的构造依赖，改聚合 `IEnumerable<IPromptSectionContributor>`
- [ ] 删占位符替换（`{{tools}}`/`{{skills}}` 等），改 section 注入：按 contributor `Order` 把 section 文本 append 到固定锚点标题（`## Tools`/`## Skills`）下
- [ ] `BuildToolSection`（吃 `context.Tools`）包装为 Core 内置 contributor（`SectionName=tools`，最低 Order）
- [ ] 内置 Agent prompt 改写含锚点标题（`## Tools`/`## Skills`）
- [ ] 测试：`PromptBuilder` 不引用 `SkillManager` 具体类（反射断言）；section 注入按 Order
- [ ] Commit `refactor(prompts): PromptBuilder decoupled, section injection via IPromptSectionContributor`

### Task 11: 全量回归
- [ ] 全量构建 Expected 成功
- [ ] 全量测试 Expected 全绿
- [ ] Commit `chore: phase 4-5 hosting+config decoupling complete`

---

## Self-Review
- **Spec coverage:** §9.4（Hosting 抽出+切断 App→ACP+AgentExecutorRouter）、§9.5（Options 拆节+删 IExtension/PluginLoader/AddSeeingAgent/PrimaryTools+UnifiedConfigManager 消费注册表+LoadAsync 推迟+AgentManager 两阶段）全覆盖。
- **Placeholder:** 无 TBD；每 Task 给确切路径与改造点。
- **Type consistency:** `IExecutionSubmitter`/`IAgentExecutorImplementation`/`IConfigSectionRegistry` 命名跨 Task 与 Phase 1 一致。
