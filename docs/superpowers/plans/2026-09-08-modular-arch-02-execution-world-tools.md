# 执行世界与能力包抽出（Phase 2-3）

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** 抽 `Seeing.IO.Local`，工具经 `IFileSystem`/`ISubprocess`；抽出 Tools.*/Mcp/Llm.*/Skills/Agents.BuiltIn，删主库 MCP SDK 引用，`ProviderManager` 聚合多工厂。

**Architecture:** Phase 2 抽 IO.Local + 接口化工具（工具暂留主库目录）。Phase 3 能力包迁出。完成后主库 csproj 无 `ModelContextProtocol.Core`。

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions。

## Execution Policy（本会话）
- 并行优先；仅 Wave/Phase 结束审查
- Phase 3 抽出：**每包独立 commit**；同时最多 2 个包并行，且约定：
  - Agent A 只改自己包目录 + 删主库对应工具文件
  - `ServiceCollectionExtensions.RegisterCoreServices` 的删注册由 **controller 在 Wave 合并时统一改**，或串行最后一步
  - 避免两 agent 同时编辑同一 csproj/slnx（后派的先读最新）

### Parallel Waves (Phase 3)
| Wave | Tasks |
|------|-------|
| W1 | T6 FileSystem（先立样板） |
| W2 | T7 Shell + T8 Web（并行） |
| W3 | T9 Git + T10 Basic（并行） |
| W4 | T11 Skills → T12 Mcp → T13 Llm → T14 Agents.BuiltIn（串行，引用链重） |
| W5 | T15 全量回归 |

---

## Phase 2: 执行世界与工具接口化

### Task 1: 新建 Seeing.IO.Local
Create `src/Seeing.IO.Local/{csproj,LocalFileSystem,LocalSubprocessFactory,LocalExecutionWorld,LocalExecutionWorldModule}.cs`; Test `tests/Seeing.IO.Local.Tests/`
- [ ] `dotnet new classlib -n Seeing.IO.Local -o src/Seeing.IO.Local -f net10.0`，引用 Abstractions，加入 slnx
- [ ] `LocalFileSystem:IFileSystem` 用 `File.*`/`Directory.*` 实现
- [ ] `LocalSubprocessFactory:ISubprocessFactory`，`Kill(entireTree)` 用 `Process.Kill(entireProcessTree:true)`
- [ ] `LocalExecutionWorld:IExecutionWorld`（Cwd+FS+Subprocess）
- [ ] `LocalExecutionWorldModule:ISeeingModule`（id=`io.local`，`ProvidedSeams=["executionWorld"]`，注册 `IExecutionWorld` Singleton）
- [ ] 测试：临时目录 FS 读写；`Start("cmd","/c echo hi")` 拿输出；`Module.Id`
- [ ] Commit `feat(io): add Seeing.IO.Local execution world`

### Task 2: ReadTool 经 IFileSystem
Modify `ReadTool.cs`; Test `tests/Seeing.Agent.Tests/Tools/ReadToolFileSystemTests.cs`
- [ ] 构造器加 `IFileSystem`，`Path.*`/`File.*` 改 `_fileSystem.*`
- [ ] 测试用内存假 FS 测不触 OS 返回内容
- [ ] Commit `refactor(tools): ReadTool uses IFileSystem`

### Task 3: BashTool 经 ISubprocess + IShellService
Create `IShellService.cs`+`DefaultShellService.cs`; Modify `BashTool.cs`; Test `tests/Seeing.Agent.Tests/Tools/BashToolSubprocessTests.cs`
- [ ] `IShellService`：`SelectShell()`+`PrepareCommand(shell,command)`（UTF-8 注入防护）
- [ ] BashTool 注入 `IExecutionWorld`+`IShellService`，删 `IWorkspaceProvider` cwd 用途；`shell.env` Hook 在 Start 前触发塞 `SubprocessSpec.Environment`；危险命令拦截留 BashTool；workdir fallback `_world.Cwd`
- [ ] 执行：`PrepareCommand`→`SubprocessSpec`→`_world.Subprocess.Start`→读输出→`WaitForExitAsync`
- [ ] 测试用假 factory 测不触 OS；`SubprocessSpec.WorkingDirectory`==`_world.Cwd` fallback
- [ ] Commit `refactor(tools): BashTool uses ISubprocess + IShellService`

### Task 4: GitService 经 ISubprocess
Modify `GitService.cs`; Test `tests/Seeing.Agent.Tests/Git/GitServiceSubprocessTests.cs`
- [ ] 注入 `IExecutionWorld`，`WorkingDirectory` fallback `_world.Cwd`，`Process.Start` 改 `_world.Subprocess.Start`
- [ ] 测试用假 factory 测 `StatusAsync` 不触 OS
- [ ] Commit `refactor(git): GitService uses ISubprocess`

### Task 5: 删 IWorkspaceProvider.WorkspaceRoot
Modify `WorkspaceProvider.cs`; Grep 全库引用点
- [ ] Grep `WorkspaceRoot` 找全部引用
- [ ] 工具改注入 `IExecutionWorld` 读 `_world.Cwd`；`ToolManager` 保留 `IWorkspaceProvider`（只用于 tool-state.json）
- [ ] 删接口与实现的 `WorkspaceRoot`
- [ ] 全量构建 Expected 成功
- [ ] Commit `refactor: remove IWorkspaceProvider.WorkspaceRoot, use IExecutionWorld.Cwd`

---

## Phase 3: 能力包迁出主库

> 模式：建项目→移文件→改命名空间→主库删注册→加 `ISeeingModule`。每包独立可测。
> 顺序：Tools.FileSystem→Shell→Web→Git→Basic→Skills→Mcp→Llm.OpenAI/Anthropic→Agents.BuiltIn

### Task 6: 抽出 Seeing.Agent.Tools.FileSystem
Create `src/Seeing.Agent.Tools.FileSystem/`; Move `src/Seeing.Agent/Tools/BuiltIn/FileSystem/*`; Create `FileSystemModule.cs`
- [ ] 建项目引用 Abstractions，加入 slnx
- [ ] 移文件，命名空间改 `Seeing.Agent.Tools.FileSystem`
- [ ] `FileSystemModule:ISeeingModule`（id=`filesystem`，`ProvidedTools=[read,write,edit,glob,grep,delete,workspace]`）；`ConfigureServices` 注册 `ITool`；`Activate` 时 `IToolManager.RegisterTool`，`Deactivate` remove
- [ ] 主库 `RegisterCoreServices` 删 ReadTool 等注册
- [ ] 测试：`Module.Id`、`ProvidedTools` 含 read；Core+本模块后 `IToolManager` 含 read
- [ ] Commit `feat(tools): extract Seeing.Agent.Tools.FileSystem`

### Task 7: 抽出 Seeing.Agent.Tools.Shell
Create `src/Seeing.Agent.Tools.Shell/`; Move `src/Seeing.Agent/Tools/BuiltIn/Shell/*`（含 BashTool/IShellService）；Create `ShellModule.cs`（id=`shell`，tools=`[bash]`）
- [ ] 同 Task 6 模式。`ShellModule` 注册 `BashTool`+`IShellService`。`ShellOptions` 建在 Tools.Shell（Phase 5 完成拆节）
- [ ] Commit `feat(tools): extract Seeing.Agent.Tools.Shell`

### Task 8: 抽出 Seeing.Agent.Tools.Web
Create `src/Seeing.Agent.Tools.Web/`; Move `src/Seeing.Agent/Tools/BuiltIn/Web/*`; Create `WebModule.cs`（id=`web`，tools=`[webfetch,websearch,code-search]`）
- [ ] 同 Task 6 模式。Commit `feat(tools): extract Seeing.Agent.Tools.Web`

### Task 9: 抽出 Seeing.Agent.Tools.Git
Create `src/Seeing.Agent.Tools.Git/`; Move `src/Seeing.Agent/Git/*`; Create `GitModule.cs`（id=`git`）
- [ ] 同 Task 6 模式。GitService 已 `ISubprocess`。Commit `feat(tools): extract Seeing.Agent.Tools.Git`

### Task 10: 抽出 Seeing.Agent.Tools.Basic
Create `src/Seeing.Agent.Tools.Basic/`; Move `current_time` 工具；Create `BasicModule.cs`（id=`basic`，tools=`[current_time]`）
- [ ] 同 Task 6 模式。Commit `feat(tools): extract Seeing.Agent.Tools.Basic`

### Task 11: 抽出 Seeing.Agent.Skills
Create `src/Seeing.Agent.Skills/`; Move `src/Seeing.Agent/Skills/*`; Create `SkillsModule.cs`（id=`skills`，`ProvidedSeams=[skills]`）
- [ ] 建项目移文件
- [ ] `SkillManager` 实现 `IPromptSectionContributor`（`SectionName=skills`，`BuildAsync` 返回技能列表，`Order` 中间值）
- [ ] 测试：`Module.Id`；`SkillManager` 可赋值给 `IPromptSectionContributor`
- [ ] Commit `feat(skills): extract Seeing.Agent.Skills + implement IPromptSectionContributor`

### Task 12: 抽出 Seeing.Agent.Mcp（删主库 MCP SDK）
Create `src/Seeing.Agent.Mcp/`; Move `src/Seeing.Agent/MCP/*`; Create `McpModule.cs`（id=`mcp`，`ProvidedSeams=[mcp]`）；Modify `src/Seeing.Agent/Seeing.Agent.csproj` 删 `ModelContextProtocol.Core`
- [ ] 建 `Seeing.Agent.Mcp`，引用 Abstractions + `ModelContextProtocol.Core`
- [ ] 移 `MCP/*`，命名空间改 `Seeing.Agent.Mcp`
- [ ] `McpModule.ConfigureServices` 注册 `McpClientManager` 为 `IMcpManager`
- [ ] 主库 csproj 删 `<PackageReference Include="ModelContextProtocol.Core">`；Grep 主库残留 `using ...MCP` 改/删
- [ ] 主库 `RegisterCoreServices` 删 MCP 注册
- [ ] 测试：`Module.Id`；`dotnet list src/Seeing.Agent package` 无 MCP 包
- [ ] 全量构建 Expected 成功
- [ ] Commit `feat(mcp): extract Seeing.Agent.Mcp, drop MCP SDK from core`

### Task 13: 抽出 Llm.OpenAI/Anthropic + ProviderManager 聚合
Create `src/Seeing.Agent.Llm.OpenAI/`+`src/Seeing.Agent.Llm.Anthropic/`; Move 工厂；Create `OpenAiLlmModule`/`AnthropicLlmModule`（id=`llm.openai`/`llm.anthropic`，`ProvidedSeams=[llm]`）；Modify `ProviderManager.cs`
- [ ] 建 `Llm.OpenAI`（引用 Abstractions+OpenAI SDK），工厂 `SupportedTypes={"openai"}`，`SupportsType` OrdinalIgnoreCase
- [ ] 同建 `Llm.Anthropic`（`SupportedTypes={"anthropic"}`）
- [ ] `ProviderManager` 构造 `ILlmClientFactory single`→`IEnumerable<ILlmClientFactory> factories`；`CreateClient(config)` 按 `config.Type` 取首个 `SupportsType==true`，多工厂同 type 先注册 wins
- [ ] 删主库 `DefaultLlmClientFactory` 的 OpenAI/Anthropic switch
- [ ] 两 Module `ConfigureServices` 注册各自工厂
- [ ] 测试：两工厂并列；关 openai 不影响 anthropic；`SupportsType("OpenAI")`==`("openai")`
- [ ] Commit `feat(llm): extract Llm.OpenAI/Anthropic + ProviderManager aggregates factories`

### Task 14: 抽出 Seeing.Agent.Agents.BuiltIn
Create `src/Seeing.Agent.Agents.BuiltIn/`; Move `src/Seeing.Agent/Core/BuiltInAgents/*`; Create `AgentsBuiltInModule.cs`（id=`agents.builtin`）
- [ ] 建项目引用 Abstractions
- [ ] 移 `BuiltInAgents.cs`，命名空间改 `Seeing.Agent.Agents.BuiltIn`
- [ ] 改 Agent 定义：explore/general 显式 `DeniedTools=["task"]`；plan 用 `AllowedTools` 白名单；build 不设
- [ ] `AgentsBuiltInModule.ActivateAsync` 通过 `IAgentStore.RegisterAsync` 注册（Phase 5 完成 AgentManager 两阶段，本阶段先定义模块）
- [ ] 测试：`Module.Id`；explore `DeniedTools` 含 `task`
- [ ] Commit `feat(agents): extract Seeing.Agent.Agents.BuiltIn`

### Task 15: 全量回归
- [ ] 全量构建 Expected 成功
- [ ] `dotnet list src/Seeing.Agent package` 无 `ModelContextProtocol.Core`
- [ ] 全量测试 Expected 全绿
- [ ] Commit `chore: phase 2-3 extraction complete`

---

## Self-Review
- **Spec coverage:** §9.2（IO.Local+工具接口化+BashTool 三层归属+删 WorkspaceRoot）、§9.3（Tools.*/Mcp/Llm.*/Skills/Agents.BuiltIn 抽出+删工厂 switch+ProviderManager 聚合）全覆盖。
- **Placeholder:** 无 TBD；每 Task 给确切路径与成员清单。
- **Type consistency:** `ISeeingModule.Id` 命名跨 Task 一致（`io.local`/`filesystem`/`shell`/`web`/`git`/`basic`/`skills`/`mcp`/`llm.openai`/`llm.anthropic`/`agents.builtin`）。
