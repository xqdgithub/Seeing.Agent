# WebUI 模块化 + 不变量测试（Phase 8-9）

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** WebUI 走「模块贡献 + Scenario 过滤」：`IUiContributionRegistry` 渲染侧栏/设置/路由，自定义 `ModuleRouter` 替换默认 `<Router>`，`SessionToolbar` 加场景徽标+切换器，未启用页友好提示。补齐 spec §8 全部不变量测试。

**Architecture:** Phase 8 WebUI 模块化。Phase 9 不变量测试。完成后 spec §7.5 + §8 全部落地。

**Tech Stack:** .NET 10, Blazor Server, AntDesign 2.0, xUnit, FluentAssertions, bUnit（若已用）。

## Global Constraints
- 页面可见性只读 `IModuleCatalog`+Scenario，不自行检查「有没有 X 模块」。
- 侧栏只跟进程级 Scenario；会话级 Scenario 只改会话壳（`SessionToolbar` 徽标/默认 Agent/插槽）。
- `*Page.razor` 删 `@page`，改暴露 `RenderFragment`/组件类型由模块 `IUiContribution.Nav` 携带。
- `ModuleRouter` 路由表来自 `IUiContributionRegistry.Routes`（运行期可变）。
- 测试用 VSTest 直接运行已构建程序集。

## Execution Policy (user override)
- Prefer parallel waves; review only at Phase checkpoints (no per-task review).
- Phase 9 Core/API invariant tests (Tasks 8–15) may run in parallel with Phase 8 UI work (different trees).
- Task 16 UI invariants after Phase 8 surfaces exist; Task 17 last.
- Suggested waves:
  - W1 parallel: P8-T1 registry + P9 T8–T13 + P9 T14–T15
  - W2 after T1: P8-T2+T3 (ModuleRouter + remove @page) as one agent
  - W3 parallel: P8-T4 Sidebar + T5 Settings + T6 Toolbar + T7 SlotHost
  - W4: P9-T16 UI invariants + P9-T17 full regression

---

## Phase 8: WebUI 模块化

### Task 1: IUiContributionRegistry 实现 + 模块贡献登记
Create `samples/Seeing.Agent.WebUI/Services/UiContributionRegistry.cs`; 各能力模块实现 `IUiContribution`
- [ ] `UiContributionRegistry:IUiContributionRegistry`，持 `NavItems`/`SettingsCards`/`Slots`/`MessageRenderers`/`Routes`（可变集合，注册时合并）
- [ ] DI 注册为 Singleton
- [ ] 各能力模块（Memory/Scheduler/Mcp/Acp/Gateway/Tools/Skills）实现 `IUiContribution`，`Contribute()` 返回 Nav/SettingsCard/Slot DTO
- [ ] `Module.ActivateAsync` 时注册 `IUiContribution` 到 registry；`DeactivateAsync` 撤销
- [ ] 测试：模块 Activate 后 registry 含其 Nav；Deactivate 后消失
- [ ] Commit `feat(ui): IUiContributionRegistry + module contributions`

### Task 2: 自定义 ModuleRouter
Create `samples/Seeing.Agent.WebUI/Components/ModuleRouter.razor`; Modify `App.razor`
- [ ] `ModuleRouter` 路由表来源 = `IUiContributionRegistry.Routes`（运行期可变），不再靠 `AppAssembly` 反射扫 `@page`
- [ ] 未命中或命中但模块未启用 → 渲染 `NotEnabledFragment`（友好「当前场景未启用 X」+ 切换场景入口）
- [ ] 路由变更触发 `ModuleRouter` 刷新（`StateHasChanged`）
- [ ] `App.razor` 默认 `<Router>` 改为 `<ModuleRouter>`
- [ ] 测试：未启用模块直链 → `NotEnabledFragment`；启用后可访问
- [ ] Commit `feat(ui): custom ModuleRouter replaces default Router`

### Task 3: *Page.razor 删 @page 改 RenderFragment
Modify 全部 `samples/Seeing.Agent.WebUI/Pages/*Page.razor`（28 个 Page）
- [ ] 删 `@page "/xxx"` 指令
- [ ] 各 Page 改暴露 `RenderFragment`/组件类型，由所属模块 `IUiContribution.Nav` 携带（`ComponentType` 字段）
- [ ] `ModuleRouter` 按 route → `NavContribution.ComponentType` 渲染
- [ ] 测试：各 Page 经 registry 路由可达；直链未启用 → 友好提示
- [ ] Commit `refactor(ui): remove @page, pages rendered via contribution registry`

### Task 4: AppSidebar 从注册表渲染
Modify `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`
- [ ] 删硬编码 `<MenuItem NavigateTo="/cron-jobs">` 等
- [ ] 从 `IUiContributionRegistry.NavItems` 渲染，按 `Requires`（模块 id）+ `Scenarios` 过滤可见性
- [ ] 可见性读 `IModuleCatalog.IsEnabled`+进程级 Scenario
- [ ] 测试：`AppSidebar` 无硬编码 `MenuItem`（反射/源码扫描断言）；切进程场景侧栏变
- [ ] Commit `feat(ui): AppSidebar renders from IUiContributionRegistry`

### Task 5: Settings 从注册表渲染
Modify `samples/Seeing.Agent.WebUI/Pages/Settings.razor`
- [ ] 删硬编码设置卡片挂载
- [ ] 从 `IUiContributionRegistry.SettingsCards` 渲染，按 `Requires` 过滤
- [ ] 测试：Settings 页卡片来自 registry；未启用模块卡片不出现
- [ ] Commit `feat(ui): Settings renders from IUiContributionRegistry`

### Task 6: SessionToolbar 场景徽标 + 切换器
Modify `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`
- [ ] 显示当前会话场景徽标（读 `session.Scenario` ?? 进程级）
- [ ] 场景切换器：切 `session.Scenario`，只对下一次 Submit 生效
- [ ] 默认 Agent 取 `sessionScenario.defaultAgent`
- [ ] 会话内工具栏/输入区插槽按会话场景过滤
- [ ] 测试：切会话场景侧栏不变；`SessionToolbar` 徽标变；默认 Agent 变
- [ ] Commit `feat(ui): SessionToolbar scenario badge + switcher`

### Task 7: SlotHost 升级
Modify `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor`
- [ ] 插槽贡献由 `IUiContribution` 登记
- [ ] 可见性读 `IModuleCatalog`+会话 Scenario
- [ ] `LeftActions`/`RightActions`/`session_prompt`/`message_toolbar` 保留
- [ ] 测试：会话级场景排除某模块 → 该会话内插槽不出现；进程级页面仍可访问
- [ ] Commit `feat(ui): SlotHost contributions via IUiContribution`

---

## Phase 9: 不变量测试补齐

### Task 8: Core 零工具不变量
Test `tests/Seeing.Agent.Tests/Invariants/CoreZeroToolsTests.cs`
- [ ] 只 `AddSeeingCore()` 时 `IToolManager` 为空，schema 为空
- [ ] 删「AddSeeingAgent 后必有 ReadTool」类测试（Grep 删）
- [ ] Commit `test(invariants): Core zero tools`

### Task 9: 可见即可请求不变量
Test `tests/Seeing.Agent.Tests/Invariants/VisibilityTests.cs`
- [ ] 任意步进：schema 工具 id == 层 1∩层 2 集合；Ask 可在其中；Deny/未启用/禁用不得进 schema
- [ ] Commit `test(invariants): visible means requestable`

### Task 10: 执行世界成对不变量
Test `tests/Seeing.Agent.Tests/Invariants/ExecutionWorldTests.cs`
- [ ] 解析到的 `IFileSystem` 与 `ISubprocess` 属于同一 `IExecutionWorld`
- [ ] 工具程序集无对 `File.WriteAllText`/`Process.Start` 的生产路径（反射扫，替身除外）
- [ ] Commit `test(invariants): execution world paired`

### Task 11: 生命周期 + 替换不变量
Test `tests/Seeing.Agent.Tests/Invariants/LifecycleTests.cs`
- [ ] Deactivate 后工具与分节消失；再 Activate 恢复
- [ ] 硬依赖 A 依赖 B 只启用 A → 拒启
- [ ] 同 id 未 Replace → 拒启；Replace 后旧类型不可解析
- [ ] Commit `test(invariants): lifecycle + replace`

### Task 12: LLM 并列 + 门面执行器不变量
Test `tests/Seeing.Agent.Tests/Invariants/LlmAndExecutorTests.cs`
- [ ] 关 openai 不影响已启用 anthropic
- [ ] 容器只有一个 `IAgentExecutor`（router）；`IAgentExecutorImplementation` 多个按 Runtime 分派；无 Replace
- [ ] `ProviderConfig.Type` 为 string；第三方工厂经 `SupportedTypes` 声明不改 Abstractions
- [ ] Commit `test(invariants): LLM parallel + facade executor`

### Task 13: 配置 + 提示词 + 子执行解耦不变量
Test `tests/Seeing.Agent.Tests/Invariants/ConfigPromptTaskTests.cs`
- [ ] Core 程序集不含 Gateway/Acp Options 类型
- [ ] `UnifiedConfigManager.BuildSectionRegistry` 只含脊柱节；能力包节由模块注册；`LoadAsync` 推迟到 `InitializeSeeingAsync`
- [ ] `PromptBuilder` 不引用 `SkillManager` 具体类；section 注入走锚点标题
- [ ] `TaskTool`/`TaskStatusTool` 只依赖 Abstractions 接口，不解析 `ExecutionJobService` 具体类，不用 `context.Services`
- [ ] Commit `test(invariants): config + prompt + task decoupling`

### Task 14: schema 唯一计算 + 场景不变量
Test `tests/Seeing.Agent.Tests/Invariants/SchemaScenarioTests.cs`
- [ ] `SchemaSnapshotEvent` 与 `ChatRequest.Tools` 同源
- [ ] 模型可见即可重建：同模块集下重放可重建；跨进程模块集变化取交集并告警不拒启
- [ ] 会话级场景收窄：A=code、B=work 同进程并行；A 看不到 memory，B 看不到 git
- [ ] 场景切换：切 `session.Scenario` 后下一次执行 schema 变；进程级 Activate 状态不变
- [ ] 在途轮次边界：进程级热重载 Deactivate 推迟到在途结束；会话级切换只对下一次 Submit 生效
- [ ] Commit `test(invariants): schema unique + scenario`

### Task 15: 存量迁移 + Scenario 纯字符串不变量
Test `tests/Seeing.Agent.Tests/Invariants/MigrationTests.cs`
- [ ] 读旧 `providers.json`（PascalCase `Type`）归一化为小写不报错；首次保存写新格式
- [ ] `SessionData.Scenario` 缺失为 null，回退进程级，不报错不强制写
- [ ] Core 内置 Scenario 是字符串 id 数组，不引用能力包类型；未引用 id 结算时告警忽略
- [ ] Commit `test(invariants): migration + scenario pure string`

### Task 16: UI 不变量
Test `tests/Seeing.Agent.WebUI.Tests/Invariants/UiContributionTests.cs`
- [ ] `AppSidebar` 无硬编码 `MenuItem`
- [ ] 未启用模块页面友好提示
- [ ] 侧栏只跟进程级：切进程场景侧栏变；切会话场景侧栏不变
- [ ] 会话壳跟会话级：切会话场景 `SessionToolbar` 徽标/默认 Agent/插槽变；进程级 Activate 状态不变
- [ ] UI 真相源唯一：页面可见性只读 `IModuleCatalog`+Scenario
- [ ] Commit `test(invariants): UI contribution`

### Task 17: 全量回归
- [ ] 全量构建 Expected 成功
- [ ] 全量测试 Expected 全绿
- [ ] Commit `chore: phase 8-9 webui modularization + invariants complete`

---

## Self-Review
- **Spec coverage:** §9.8（IUiContributionRegistry+AppSidebar/Settings 注册表渲染+SessionToolbar 场景徽标+未启用页友好提示）、§9.9（§8 不变量测试）全覆盖。spec §7.5.3 Blazor 路由机制改造（ModuleRouter+删 @page+NotEnabledFragment）有 Task 2/3。
- **Placeholder:** 无 TBD；每 Task 给确切路径与改造点。
- **Type consistency:** `IUiContributionRegistry`/`ModuleRouter`/`NavContribution` 命名跨 Task 与 Phase 1 一致。
