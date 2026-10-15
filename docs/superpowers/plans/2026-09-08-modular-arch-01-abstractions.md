# Abstractions 契约补全（Phase 1）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]` checkboxes.

**Goal:** 在 `Seeing.Agent.Abstractions` 补全模块化全部新契约（执行世界、模块、配置节注册表、提示词分节、执行提交、门面执行器、UI 贡献、Schema 快照事件），并把 `ProviderConfig.Type` 从枚举改 string。不搬实现，只为后续阶段提供编译期落点。

**Architecture:** 只动 Abstractions 与主库对枚举/搬入类型的引用点。完成后主库仍可编译运行，新接口尚无实现。

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions。

## Global Constraints
- 依赖只向下：Abstractions 仅依赖 `Seeing.Session`、`Seeing.ConfigSchema`、BCL。
- 命名：接口前缀 `I`，结果类后缀 `Result`，异步 `Async` 后缀。Hook 点用 `HookPoints.*` 常量。
- 不保留兼容入口；`ProviderType` 枚举删除，存量迁移逻辑在 Phase 5。
- 测试用 VSTest 直接运行已构建程序集：`dotnet build tests/Seeing.Agent.Tests && dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Xxx"`
- `ISeeingModule.ConfigureServices` 需要 `IServiceCollection`：Abstractions 可加 `Microsoft.Extensions.DependencyInjection.Abstractions` 包引用（契约层允许）。

## Execution Policy（本会话）
- **并行优先**：同一 Wave 内可并行派多个 implementer（文件无冲突时）。
- **审查策略**：不做逐步 task review；仅在 **Wave 结束 / Phase 结束** 做关键点审查。
- **提交**：每个 Task 仍独立 commit；冲突时由 controller 合并。

### Parallel Waves
| Wave | Tasks | 审查 |
|------|-------|------|
| W1 纯新建契约（可并行） | T1, T2, T3, T6, T8, T9 | Wave 结束：构建+契约测试 |
| W2 搬迁/改枚举（串行） | T4 → T5 → T7 | Wave 结束：全量构建 |
| W3 收尾 | T10 | Phase 1 终审 |

---

## File Structure

| 文件 | 动作 |
|------|------|
| `Abstractions/Execution/IFileSystem.cs` | 新建 |
| `Abstractions/Execution/ISubprocess.cs`（含 `ISubprocessFactory`） | 新建 |
| `Abstractions/Execution/SubprocessSpec.cs` | 新建 |
| `Abstractions/Execution/IExecutionWorld.cs` | 新建 |
| `Abstractions/Modules/{ISeeingModule,IModuleCatalog,ModuleDescriptor}.cs` | 新建 |
| `Abstractions/Configuration/{IConfigSectionRegistry,ConfigSectionMeta}.cs` | 新建 |
| `Abstractions/Prompts/{IPromptSectionContributor,PromptSectionNames}.cs` | 新建 |
| `Abstractions/Prompts/PromptContext.cs` | 从主库搬入 |
| `Abstractions/Execution/IExecutionSubmitter.cs` | 新建 |
| `Abstractions/Execution/IExecutionStatusProvider.cs` | 从主库搬入 |
| `Abstractions/Agents/IAgentExecutorImplementation.cs` | 新建 |
| `Abstractions/Llm/ProviderConfig.cs` | `Type` 改 string |
| `Abstractions/Llm/ILlmClient.cs` | `SupportedTypes` 改 `IReadOnlySet<string>` + `SupportsType` |
| `Abstractions/Events/SchemaSnapshotEvent.cs` | 新建 |
| `Abstractions/Ui/{IUiContribution,IUiContributionRegistry,UiContributionTypes}.cs` | 新建 |
| `src/Seeing.Agent/Execution/IExecutionStatusProvider.cs` | 删除（搬走） |

---

### Task 1: 执行世界 seam 契约

**Files:** Create `Abstractions/Execution/{IFileSystem,ISubprocess,SubprocessSpec,IExecutionWorld}.cs`; Test `tests/Seeing.Agent.Tests/Execution/ExecutionWorldContractsTests.cs`

**Produces:** `IFileSystem`, `ISubprocess`, `ISubprocessFactory`, `SubprocessSpec`, `IExecutionWorld`

- [ ] **Step 1: 写契约**

`IFileSystem`：`ReadAllText`/`ReadAllTextAsync`/`ReadLinesAsync(IAsyncEnumerable<string>)`/`WriteAllText`/`WriteAllTextAsync`/`Exists`/`Delete`/`EnumerateFiles(dir,pattern,recursive)`/`EnumerateDirectories(...)`/`GetFullPath`。

`ISubprocess : IDisposable`：`Id`/`StandardOutput`/`StandardError`/`HasExited`/`WaitForExitAsync`/`Kill(entireTree=true)`。`ISubprocessFactory`：`ISubprocess Start(SubprocessSpec spec)`。

`SubprocessSpec`：`required string FileName`、`Arguments`、`WorkingDirectory`、`IReadOnlyDictionary<string,string?> Environment`、`RedirectStandardOutput/Error`、`Encoding`(UTF8)、`Timeout?`、`CancellationToken`。

`IExecutionWorld`：`string Cwd`、`IFileSystem FileSystem`、`ISubprocessFactory Subprocess`。

- [ ] **Step 2: 写契约测试** — 断言各接口成员存在；`IExecutionWorld.FileSystem` 类型为 `IFileSystem`，`Subprocess` 类型为 `ISubprocessFactory`；`new SubprocessSpec { FileName="git" }` 默认值。
- [ ] **Step 3: 运行测试** — Expected PASS
- [ ] **Step 4: Commit** — `feat(abstractions): add execution world seam`

---

### Task 2: 模块契约与目录

**Files:** Create `Abstractions/Modules/{ISeeingModule,IModuleCatalog,ModuleDescriptor}.cs`; Test `tests/Seeing.Agent.Tests/Modules/ModuleContractsTests.cs`

**Produces:** `ISeeingModule`（`Id`/`ProvidedTools`/`ProvidedSeams`/`DependsOn`/`ConfigureServices`/`ActivateAsync`/`DeactivateAsync`）、`IModuleCatalog`（`Available`/`Enabled`/`Unhealthy`/`IsEnabled(id)`/`IsAvailable(id)`）、`ModuleDescriptor` record

- [ ] **Step 1: 写契约** — `ModuleDescriptor` 为 record `(Id, ProvidedTools, ProvidedSeams, DependsOn)`。`ISeeingModule` 含生命周期三方法。`IModuleCatalog` 只读视图 + 两个查询。
- [ ] **Step 2: 写契约测试** — 断言 `ISeeingModule` 有 `ConfigureServices`/`ActivateAsync`/`DeactivateAsync`/`Id`/`DependsOn`；`IModuleCatalog.Available` 类型为 `IReadOnlyCollection<ModuleDescriptor>`。
- [ ] **Step 3: 运行测试** — Expected PASS
- [ ] **Step 4: Commit** — `feat(abstractions): add ISeeingModule + IModuleCatalog`

---

### Task 3: 配置节注册表

**Files:** Create `Abstractions/Configuration/{IConfigSectionRegistry,ConfigSectionMeta}.cs`; Test `tests/Seeing.Agent.Tests/Configuration/ConfigSectionRegistryContractsTests.cs`

**Produces:** `IConfigSectionRegistry`（`Register(meta)`/`Sections`/`TryGet`）、`ConfigSectionMeta` record（`Key`/`FileName`/`Scope`/`SectionType`/`DefaultValue?`）、`ConfigScope` enum（`Both`/`UserOnly`/`ProjectOnly`）

- [ ] **Step 1: 写契约** — 如上。
- [ ] **Step 2: 写契约测试** — 断言 record 字段、接口方法。
- [ ] **Step 3: 运行测试** — Expected PASS
- [ ] **Step 4: Commit** — `feat(abstractions): add IConfigSectionRegistry`

---

### Task 4: 提示词分节贡献 + 搬 PromptContext

**Files:** Create `Abstractions/Prompts/{IPromptSectionContributor,PromptSectionNames}.cs`; Move `src/Seeing.Agent/Core/Prompts/PromptContext.cs` → `Abstractions/Prompts/PromptContext.cs`; Test `tests/Seeing.Agent.Tests/Prompts/PromptSectionContractsTests.cs`

**Produces:** `IPromptSectionContributor`（`SectionName`/`Order`/`BuildAsync(PromptContext, ct)`）、`PromptSectionNames`（`Tools`/`Skills`/`Agents`/`Environment` 常量）

> `PromptContext` 当前在主库。`IPromptSectionContributor.BuildAsync` 需它，故必须先搬到 Abstractions，否则依赖反向。

- [ ] **Step 1: 搬 `PromptContext`** — 读原文件全部成员，整体移到 `Abstractions/Prompts/PromptContext.cs`，命名空间改 `Seeing.Agent.Abstractions.Prompts`。用 Grep 找全部引用点逐一改 using。
- [ ] **Step 2: 写 `PromptSectionNames`（4 个常量）与 `IPromptSectionContributor`（`SectionName`/`Order:int`/`BuildAsync` 返回 `Task<string?>`）**
- [ ] **Step 3: 写契约测试** — 断言接口成员；`PromptSectionNames.Tools == "tools"`。
- [ ] **Step 4: 全量构建** — `dotnet build Seeing.Agent.slnx`，Expected 成功（验证 PromptContext 搬迁无遗漏）。
- [ ] **Step 5: Commit** — `feat(abstractions): add IPromptSectionContributor + move PromptContext`

---

### Task 5: 执行提交口与状态查询口

**Files:** Create `Abstractions/Execution/IExecutionSubmitter.cs`; Move `src/Seeing.Agent/Execution/IExecutionStatusProvider.cs` → `Abstractions/Execution/`; Test `tests/Seeing.Agent.Tests/Execution/ExecutionAbstractionsContractsTests.cs`

**Produces:** `IExecutionSubmitter`（`SubmitAsync`/`CancelAsync(executionId)`/`CancelBySessionAsync(sessionId)`/`WaitForExecutionAsync(executionId)`）、`IExecutionStatusProvider`（`GetOverview`/`GetExecution`）

- [ ] **Step 1: 搬 `IExecutionStatusProvider`** — 移文件，命名空间改 `Seeing.Agent.Abstractions.Execution`。主库 `ExecutionJobService : IExecutionStatusProvider` 改 using。
- [ ] **Step 2: 写 `IExecutionSubmitter`** — 方法签名如 Produces。`ExecutionSubmitResult`/`ChatInput`/`ChatOptions` 用 Grep 定位；若在主库则一并搬到 Abstractions。
- [ ] **Step 3: 写契约测试** — 断言两接口成员齐全。
- [ ] **Step 4: 全量构建** — Expected 成功。
- [ ] **Step 5: Commit** — `feat(abstractions): add IExecutionSubmitter + move IExecutionStatusProvider`

---

### Task 6: 门面执行器实现口

**Files:** Create `Abstractions/Agents/IAgentExecutorImplementation.cs`; Test `tests/Seeing.Agent.Tests/Agents/ExecutorContractsTests.cs`

**Produces:** `IAgentExecutorImplementation`（`AgentRuntime SupportedRuntime` + `ExecuteAsync` 与 `IAgentExecutor` 同签名）

- [ ] **Step 1: 写契约** — 核对 `IAgentExecutor.ExecuteAsync` 现有签名，让 `IAgentExecutorImplementation` 与之一致，仅多 `SupportedRuntime`。
- [ ] **Step 2: 写契约测试** — 断言 `SupportedRuntime` + `ExecuteAsync`。
- [ ] **Step 3: 运行测试** — Expected PASS
- [ ] **Step 4: Commit** — `feat(abstractions): add IAgentExecutorImplementation`

---

### Task 7: ProviderConfig.Type 改 string + ILlmClientFactory 扩展

**Files:** Modify `Abstractions/Llm/ProviderConfig.cs`, `Abstractions/Llm/ILlmClient.cs`; Test `tests/Seeing.Agent.Tests/Llm/ProviderConfigStringTests.cs`

**Produces:** `ProviderConfig.Type: string`、`ILlmClientFactory.SupportedTypes: IReadOnlySet<string>` + `SupportsType(string)`（大小写不敏感）

- [ ] **Step 1: 改 `ProviderConfig`** — 删 `ProviderType` 枚举（或保留为内部常量字符串集合，不作路由键）。`Type` 改 `string`。`[JsonStringEnumConverter]` 移除。
- [ ] **Step 2: 改 `ILlmClientFactory`** — 加 `IReadOnlySet<string> SupportedTypes` + `bool SupportsType(string type)`（实现做 `OrdinalIgnoreCase` 比较）。
- [ ] **Step 3: 修主库引用点** — Grep `ProviderType.` 全部引用（`PredefinedModels`、`ConfiguredLlmProvider`、`ProviderManager` 等），改为字符串常量。`Enum.TryParse<ProviderType>` 改为字符串比较。本阶段只让编译通过，迁移归一化逻辑在 Phase 5。
- [ ] **Step 4: 写测试** — 断言 `ProviderConfig.Type` 为 string；`SupportsType("OpenAI")` 与 `SupportsType("openai")` 等价（测试用假工厂实现 `SupportsType` 做 OrdinalIgnoreCase）。
- [ ] **Step 5: 全量构建 + 运行测试** — Expected 成功。
- [ ] **Step 6: Commit** — `feat(abstractions): change ProviderConfig.Type to string + ILlmClientFactory.SupportsType`

---

### Task 8: Schema 快照事件

**Files:** Create `Abstractions/Events/SchemaSnapshotEvent.cs`; Test `tests/Seeing.Agent.Tests/Events/SchemaSnapshotEventTests.cs`

**Produces:** `SchemaSnapshotEvent : IMessageEvent`（`ToolIds: IReadOnlyList<string>` + `SectionIds: IReadOnlyList<string>` + `ExecutionId` + `SessionId`）

- [ ] **Step 1: 写契约** — 实现 `IMessageEvent`（核对 `IMessageEvent` 现有成员：`Type`/`SessionId` 等），加 `ToolIds`/`SectionIds`/`ExecutionId`。`MessageEventTypes` 加常量。
- [ ] **Step 2: 写测试** — 断言 `SchemaSnapshotEvent` 实现 `IMessageEvent`；字段可赋值。
- [ ] **Step 3: 运行测试** — Expected PASS
- [ ] **Step 4: Commit** — `feat(abstractions): add SchemaSnapshotEvent`

---

### Task 9: UI 贡献契约

**Files:** Create `Abstractions/Ui/{IUiContribution,IUiContributionRegistry,UiContributionTypes}.cs`; Test `tests/Seeing.Agent.Tests/Ui/UiContributionContractsTests.cs`

**Produces:** `IUiContribution`（`ModuleId`/`Contribute()` 返回贡献列表）、`IUiContributionRegistry`（`Register`/`NavItems`/`SettingsCards`/`Slots`/`MessageRenderers`/`Routes`）、`NavContribution`/`SettingsCardContribution`/`SlotContribution`/`MessageRendererContribution` DTO

- [ ] **Step 1: 写 DTO** — `NavContribution(Route, Title, Icon, Requires: IReadOnlyList<string>, Scenarios?: IReadOnlyList<string>)`；`SettingsCardContribution(Route, Title, RenderFragment factory, Requires)`；`SlotContribution(Name, RenderFragment factory, Requires, Scenarios?)`；`MessageRendererContribution(MessageType, Renderer)`。注：Abstractions 不引用 Blazor，`RenderFragment` 用 `Func<object?>` 或 `Type ComponentType` 表达，WebUI 侧再转 `RenderFragment`。
- [ ] **Step 2: 写 `IUiContribution`** — `string ModuleId` + `IReadOnlyList<object> Contribute()`（返回上述 DTO 实例）。
- [ ] **Step 3: 写 `IUiContributionRegistry`** — 各类型只读集合 + `Routes`（`IReadOnlyDictionary<string, NavContribution>`）+ `Register(IUiContribution)`。
- [ ] **Step 4: 写契约测试** — 断言各 DTO 可构造；接口成员齐全。
- [ ] **Step 5: 运行测试** — Expected PASS
- [ ] **Step 6: Commit** — `feat(abstractions): add IUiContribution contracts`

---

### Task 10: 全量回归 + 收尾

- [ ] **Step 1: 全量构建** — `dotnet build Seeing.Agent.slnx`，Expected 成功。
- [ ] **Step 2: 全量测试** — `dotnet build tests/Seeing.Agent.Tests && dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`，Expected 全绿（旧测试不受影响，新契约测试通过）。
- [ ] **Step 3: Commit** — `chore(abstractions): phase 1 contracts complete`

---

## Self-Review

- **Spec coverage:** spec §9.1 列出的全部 Abstractions 新接口（执行世界、模块、配置节注册表、提示词分节、执行提交、状态查询、门面执行器、UI 贡献、Schema 快照、ProviderConfig.Type 改 string）均有对应 Task。
- **Placeholder:** 无 TBD/TODO；每个 Task 给出确切文件路径与契约成员清单。
- **Type consistency:** `IExecutionSubmitter`/`IExecutionStatusProvider`/`IAgentExecutorImplementation` 命名跨 Task 一致。
