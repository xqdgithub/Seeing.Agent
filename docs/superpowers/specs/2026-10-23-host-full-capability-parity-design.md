# 宿主默认能力集统一为 full（设计）

日期：2026-10-23
状态：已实施

## 实施结果（2026-10-23）

- 新增 `samples/Seeing.Agent.HostComposition`；4 个宿主改为单行 `AddSeeingFullCapabilities`。
- Gateway.Server 补 `AddSessionHosting()` + `AddExecutionEngine()`；冒烟日志
  `进程级结算完成：boot=*, scenario=full, enabled=32, warnings=0`。
- 删除 `Embed.Demo` / `Gateway.Console.Demo` / `Gateway.WeCom.Demo`。
- 5 个 `*HostShape.CarriableModuleTypes` 统一引用 `BuiltInCapabilitySets.Full`。
- 新增 `tests/apps/Seeing.Agent.HostComposition.Tests`（7 个用例全绿）；
  `Seeing.Agent.Tests`（BuiltInScenarios 6）、`Tui.Tests`（606）、`Cli.Tests`（84）、
  `Hosting.Tests`（48）、`WebUI.Tests`（309）、`Invariants.Tests`（24）全绿；
  `dotnet build Seeing.Agent.slnx` 0 error。

## 1. 背景与问题

审查发现 5 个可运行宿主（WebUI / Tui / Cli-Headless / Gateway.Server / Embed.Demo）的
默认启动模块集不一致：只有 WebUI 恰好等于 `BuiltInCapabilitySets.Full`（32 个模块），
Tui 缺 5 个、Cli 缺 11 个、Gateway.Server 缺 12 个；Gateway.Server 还缺少
`AddSessionHosting()` / `AddExecutionEngine()`，导致 `GatewayHost.StartAsync` 在
`GetRequiredService<ISessionRuntime>()` 处失败。

根因：每个 sample 自己手写 `AddSeeingModule<...>`，没有单一注册点。

## 2. 目标 / 非目标

### 目标

1. 保留的 4 个宿主（WebUI / Tui / Cli-Headless / Gateway.Server）默认启动集完全一致，
   等于 `BuiltInCapabilitySets.Full`（当前 32 个模块 id）。
2. 建立单一注册点，新增模块时不再需要逐个宿主补行。
3. 删除以下示例项目：
   - `samples/Seeing.Agent.Embed.Demo`
   - `samples/Seeing.Gateway.Console.Demo`
   - `samples/Seeing.Gateway.WeCom.Demo`
4. 修复 Gateway.Server 缺少会话运行时的问题。
5. 修正 `HostShape.CarriableModuleTypes`，使其与真实模块 id 一致。

### 非目标

- 不修改 `BuiltInCapabilitySets.Full` 的内容（新增模块仍须登记，由
  `BuiltInScenariosTests.Full_Should_Enable_All_Implemented_Module_Ids` 守护）。
- 不删除 `src/hosting/Seeing.Agent.Hosting.Embed` 包：它是库契约（外部嵌入方仍可使用），
  只是仓库内不再提供 demo 宿主；其 `HostDefaultBoot = "minimal"` 保持不变
  （嵌入方仍可通过 `seeing.json` 的 `Boot` 收窄）。
- 不删除 `samples/Seeing.Gateway.ChannelHost`：它是 WebUI 的 `ReferenceOutputAssembly=false`
  依赖（外部通道宿主），与本次三个 demo 性质不同。
- 不改变各 HostShape 的权限通道语义（Web=EventStream / Tui=TuiPermissionChannel /
  Headless=DenyAll / Gateway=GatewayPermissionChannel）。
- 不重构历史 `docs/superpowers/specs|plans` 中已归档的 demo 提及。

## 3. 方案

### 3.1 新建共享组合根（samples 层）

新增类库 `samples/Seeing.Agent.HostComposition`，提供：

```csharp
public static IServiceCollection AddSeeingFullCapabilities(
    this IServiceCollection services,
    IConfigSectionRegistry registry,
    IConfiguration configuration,
    bool includeGatewayServer = false)
```

说明：`subagent` 由宿主 Host Shape 的 `AddSessionHosting()` 提供（Web/Tui/Headless 内置；
Gateway.Server 显式调用），共享组合根本身不重复登记，避免同 Id 模块冲突。

内部注册 `BuiltInCapabilitySets.Full` 对应的全部模块：

- `AddSeeingModule<LocalExecutionWorldModule>`（io.local）
- `AddSeeingModule<AgentsBuiltInModule>`（agents.builtin）
- `AddSeeingModule<OpenAiLlmModule>` / `AnthropicLlmModule`
- `AddSeeingModule<ModelCapabilitiesModule>` / `BuiltinCatalogModule`
- `AddSeeingModule<DeepSeekLlmModule>` / `MiMoLlmModule` / `OpenCodeZenLlmModule` / `SiliconFlowLlmModule`
- `AddSeeingModule<BasicModule>` / `FileSystemModule` / `FileWorkspaceModule` / `EditorToolsModule`
- `AddSeeingModule<ShellModule>` / `GitModule` / `SessionToolsModule` / `ToolDiscoveryModule`
- `AddSeeingModule<QuestionToolsModule>` / `WebModule` / `WorkspaceGitExcludeModule`
- `AddSeeingModule<ChannelMessagingModule>`
- `AddSeeingModule<SkillsModule>` / `McpModule`
- `AddSeeingAcp`（acp）
- `AddSeeingScheduler`（scheduler）
- `AddMemoryServices`（memory）
- `AddSystemOne` + `AddSystemOneTools`（systemone / systemone.tools）
- `AddTokenBudgetIntegration`（非模块，但与 WebUI/TUI 现有行为对齐）
- gateway / comms：
  - `includeGatewayServer=true` → `AddSeeingGatewayServer`（模块 + HostedService + PermissionChannel）
  - `includeGatewayServer=false` → `AddSeeingComms` + 登记 `GatewayModule`（只保证模块 id 一致，不监听端口）

为什么不放在 `src/hosting/`：Hosting 层硬规则禁止引用能力包（AGENTS.md / 04 规范），
共享组合根必须待在 samples 层（sample 本来就可以引用能力包）。

### 3.2 4 个宿主改为调用共享组合根

删除各 Program 中分散的 `AddSeeingModule<...>` 段，替换为一行
`AddSeeingFullCapabilities(registry, builder.Configuration)`。

各宿主保留形态专属接线：

| 宿主 | 保留项 |
|------|--------|
| WebUI | `AddSeeingHostingWeb()`、UI 服务、Blazor/AntDesign、渲染管线、`AddGatewayChannelRegistry` + `AddChannelHostManagement`、`UseTokenBudgetHooks` |
| Tui | `AddSeeingHostingTui(...)`、TUI 渲染/输入服务、`UseTokenBudgetHooks` |
| Cli-Headless | `AddSeeingHostingHeadless()`、`UseTokenBudgetHooks`（新增） |
| Gateway.Server | `AddSeeingHostingGateway()` + **新增 `AddSessionHosting()` + `AddExecutionEngine()`**、`AddGatewayChannelRegistry` + `AddChannelHostManagement`、`UseTokenBudgetHooks`（新增） |

说明：Gateway.Server 不改为 `AddSeeingHostingHeadless()`，以免登记两个
`ProcessSettlementOptions`（Headless 会覆盖 Gateway 的 HostShape 默认）；只补它
缺失的两个 Hosting 扩展。

### 3.3 删除三个示例项目

- 删除目录：
  - `samples/Seeing.Agent.Embed.Demo/`
  - `samples/Seeing.Gateway.Console.Demo/`
  - `samples/Seeing.Gateway.WeCom.Demo/`
- 从 `Seeing.Agent.slnx` 移除对应三条项目条目。
- 更新文档/README 中指向这些 sample 的引用：
  - `docs/architecture/01-overview.md`（Sample 清单）
  - `docs/architecture/02-modules.md`（Samples 与插件表）
  - `docs/architecture/05-extension-guide.md` §9（Embed 宿主）
  - `docs/architecture/06-compliance-audit.md`（Embed 行）
  - `docs/architecture/README.md`（目录示例）
  - `docs/architecture/19-tool-context-budgeting-release-notes.md`（全量能力集宿主列表）
  - 根 `README.md`（目录树、Sample 表、启动命令）
  - `samples/Seeing.Gateway.Server/README.md`（客户端启动示例）
  - `src/gateway/Seeing.Gateway.WeCom/README.md`（Demo 链接）
  - `src/gateway/Seeing.Gateway.Client/README.md`（Demo 启动命令）
- 保留：`src/hosting/Seeing.Agent.Hosting.Embed`、`samples/Seeing.Gateway.ChannelHost`。
- 历史 `docs/superpowers/specs|plans` 不改。

### 3.4 HostShape 清理

5 个 `*HostShape.cs` 的 `CarriableModuleTypes` 统一改为：

```csharp
public static readonly IReadOnlyList<string> CarriableModuleTypes = BuiltInCapabilitySets.Full;
```

理由：旧清单使用了不存在的 id（`tools.*`、`ui`、`token-budget`）且漏掉大量真实 id，
虽然当前无运行时读取方，但作为"可承载模块"的自描述必须准确；直接引用 SSOT 可消除漂移。

## 4. 测试与验证

新增 `tests/apps/Seeing.Agent.HostComposition.Tests`：

1. `Composition_PlusSessionHosting_ShouldRegisterExactlyFullModuleIds`
   —— 构建 DI（共享组合根 + `AddSessionHosting`），取出 `IEnumerable<ISeeingModule>.Id`，
   与 `BuiltInCapabilitySets.Full` 全等断言。
2. `Settlement_BootFull_ShouldEnableExactlyFull`
   —— 用 `SettlementEngine` 结算，断言 `Enabled == Full`。
3. `HostSamples_ShouldUseSharedComposition`
   —— 源码扫描 4 个 Program 文件，断言包含 `AddSeeingFullCapabilities`，
   且不再出现分散的 `AddSeeingModule<`（防回潮）。

验证命令：

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~BuiltInScenariosTests"
```

冒烟：分别启动 WebUI / Tui / Gateway.Server（Gateway.Enabled 默认 false，不应起 HTTP），
确认模块结算日志中 enabled 数 = 32、无 DependsOn 缺失。

## 5. 风险与缓解

| 风险 | 缓解 |
|------|------|
| TUI/CLI 新增 gateway/comms/systemone/memory 后启动副作用 | `Gateway.Enabled` 默认 false；Memory/SystemOne 均为 Activate 时惰性连接；冒烟验证 |
| 共享项目扩大 sample 的编译引用面 | samples 层本来即可引用能力包；不引入 Hosting/Core 反向依赖 |
| 删除 3 个 demo 造成文档漂移 | 同 PR 更新架构文档 6 处 + 根 README + 3 个包/样例 README；历史 specs/plans 保留 |
| 未来新增模块忘记加进 Full | 已有 `BuiltInScenariosTests` 守护；共享组合根测试再次守护 |

## 6. 实施文件清单

- 新增：`samples/Seeing.Agent.HostComposition/{csproj, FullCapabilityComposition.cs}`
- 新增：`tests/apps/Seeing.Agent.HostComposition.Tests/**`
- 修改：`samples/Seeing.Agent.WebUI/Program.cs`、`samples/Seeing.Agent.Tui/Program.cs`、
  `samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`、
  `samples/Seeing.Gateway.Server/Program.cs`
- 修改：上述 4 个 csproj（加共享项目引用）
- 修改：5 个 `*HostShape.cs`
- 删除：`samples/Seeing.Agent.Embed.Demo/**`、`samples/Seeing.Gateway.Console.Demo/**`、
  `samples/Seeing.Gateway.WeCom.Demo/**`
- 修改：`Seeing.Agent.slnx`
- 修改：架构文档 6 处 + 根 `README.md` + `samples/Seeing.Gateway.Server/README.md` +
  `src/gateway/Seeing.Gateway.WeCom/README.md` + `src/gateway/Seeing.Gateway.Client/README.md`
