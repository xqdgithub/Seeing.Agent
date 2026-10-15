# Wave 3 — Core 依赖盘点（能力包 / Gateway 族）

**日期：** 2026-09-08  
**范围：** 仍 `ProjectReference` → `Seeing.Agent.Core.csproj` 的能力包（不含 Hosting / Host Shapes；不含 TokenBudget——另 agent 负责）  
**对照：** `docs/superpowers/plans/2026-09-08-modular-arch-compliance-remediation.md`（W3）、`docs/architecture/06-compliance-audit.md`  
**本文件性质：** 只读盘点；**不实现**解耦。

## 摘要

| 包 | Core 引用必要性 | 真实依赖面 | 死/陈旧 using | 预估 |
|----|-----------------|------------|---------------|------|
| Seeing.Agent.Skills | 弱 | `ConfigSectionOptionsMonitor` + `IConfigSectionStore`（Configuration） | 无 | **S** |
| Seeing.Agent.Memory | 中 | Configuration 存储 + `Seeing.Agent.Llm` 接口 | `Core.Models`×3 工具文件（无类型引用） | **M** |
| Seeing.Agent.Scheduler | 中高 | Hooks 具体类、Reminders、AgentSelectionResolver、UnifiedConfigManager、Workspace | 工具 `Core.Models`×6 疑似死 using | **M** |
| Seeing.Agent.Acp | 中高 | Configuration、Hooks 具体类、Commands 发现、Llm、Skills 具体类；大量 `Core.Models` 疑似死 using | 多处 | **M** |
| Seeing.Agent.Gateway | 高 | AgentSelectionResolver、IModelManager、Reminders、Configuration；且另有 **Hosting** 硬依赖 | `Core.Models` 在 SessionService 疑似死 | **L** |
| Seeing.Gateway.QQ | 极弱 | 仅 `IWorkspaceProvider` / `WorkspaceProvider` | — | **S** |
| Seeing.Gateway.WeCom | 极弱 | 同上 + SessionTracker 注入 `IWorkspaceProvider` | — | **S** |

**共享上提（多包共用，建议先落地或并入首个 PR）：**

| Core 具体类型（命名空间） | 建议 Abstractions 端口 | 消费包 |
|---------------------------|---------------------|--------|
| `IConfigSectionStore`、`ConfigChangedEventArgs`（`Seeing.Agent.Configuration`） | `Abstractions.Configuration` | Skills, Memory, Acp, Gateway, Scheduler（间接） |
| `ConfigSectionOptionsMonitor<T>` | 迁到 Abstractions（或 thin shared util） | Skills（直接）；模式同 Acp/Gateway OptionsMonitor |
| `IWorkspaceProvider`（+ 可选保留实现于 Core） | `Abstractions.Configuration`（已有 `ISeeingDirectories`；扩展或并列接口） | QQ, WeCom, Memory, Scheduler, Acp, Gateway |
| `WorkspaceProvider` 具体类 | **不**上提实现；通道包改为只 `TryAdd`/`GetRequired` 接口，由 Host/Core 注册 | QQ, WeCom |
| `HookManager`（`Core.Hooks`） | 已有 `IHookManager` → 改注入接口 | Scheduler, Acp |
| `SystemReminderRenderer`（`Core.Reminders`） | `ISystemReminder` / 静态工具迁 Session 或 Abstractions.Reminders | Scheduler, Gateway |
| `AgentSelectionResolver`（`Seeing.Agent.Core`） | `IAgentSelectionResolver` | Scheduler, Gateway |
| `IModelManager` / `ITextCompletion` / `IProviderEndpointLookup`（`Seeing.Agent.Llm`） | 迁 Abstractions.Llm（或保留接口于 Abstractions、实现留 Core） | Gateway, Memory, Acp/Scheduler（ChatMessage 已在 Abstractions） |
| `UnifiedConfigManager` | 窄化为 `IConfigSectionStore` / `ISchedulerOptionsProvider` 已有路径 | Scheduler |
| `CommandDiscovery` + `Seeing.Agent.Commands.Attributes` | 命令发现已有 `ICommandRegistry`；属性/发现器上提或 Hosting 暴露端口 | Acp（及 Wave 4 Skills 命令） |

> 注：多处 `using Seeing.Agent.Core.Models` **未引用该命名空间内任何类型**（`AgentContext` / `AgentContextKeys` / `ToolContext` 等已在 Abstractions）。删 Core ref 前应先清掉这些死 using，避免误判依赖面。

---

## 1. Seeing.Agent.Skills — **S**

### csproj

- `ProjectReference` → Core + Abstractions + Tools.Support

### Core 具体类型 / 命名空间

| 类型 | 命名空间 | 样例 |
|------|----------|------|
| `ConfigSectionOptionsMonitor<SkillsOptions>` | `Seeing.Agent.Configuration` | `SkillsModule.cs:61-67` |
| `IConfigSectionStore`（经上式解析） | `Seeing.Agent.Configuration` | `SkillsModule.cs:62` |

其余代码已走 Abstractions（`ISkillManager`、`IConfigSectionRegistry`、`IPromptSectionContributor`、`ITool` 等）。`SkillManager` 本身不引用 Core。

### 建议端口

1. 将 `IConfigSectionStore` + `ConfigChangedEventArgs` 上提到 `Seeing.Agent.Abstractions.Configuration`（Core 的 `UnifiedConfigManager` 实现接口）。
2. 将 `ConfigSectionOptionsMonitor<T>` 迁到 Abstractions（多包共用；见 remediation W3-2）。
3. csproj 删 Core ref；保留 Abstractions + Tools.Support。

### 与 Wave 4 的关系

Skills 去 Core **不**自动解除 Hosting→Skills；见文末「Wave 4 备注」。

---

## 2. Seeing.Agent.Memory — **M**

### Core 具体类型 / 命名空间

| 类型 | 命名空间 | 样例 |
|------|----------|------|
| `IConfigSectionStore` | `Seeing.Agent.Configuration` | `MemoryOptionsProvider.cs:16,22`；`ConfigSectionMemoryOptionsStore.cs:15,17`；`IMemoryOptionsStore.cs` 文档引用 |
| `IWorkspaceProvider` | `Seeing.Agent.Configuration` | `MemoryServiceExtensions.cs:227` |
| `ITextCompletion` | `Seeing.Agent.Llm` | `LlmMemoryExtractor.cs:19,24`；`LlmMemoryEvolution.cs:21,29` |
| `IProviderEndpointLookup` | `Seeing.Agent.Llm` | `ProviderEmbeddingService.cs:17,24` |
| （死）`using Seeing.Agent.Core.Models` | — | `MemorySearchTool.cs:6`；`MemoryReadTool.cs:6`；`MemoryWriteTool.cs:7`（工具仅用 `ToolBase`/`ToolContext` ← Support/Abstractions） |

### 建议端口

1. 共享：`IConfigSectionStore` / `ConfigChangedEventArgs` / `IWorkspaceProvider` → Abstractions。
2. `ITextCompletion`、`IProviderEndpointLookup` → `Abstractions.Llm`（实现留 Core）。
3. 删除三处死 `Core.Models` using；确认无其它 Core 类型后删 ProjectReference。

---

## 3. Seeing.Agent.Scheduler — **M**

### Core 具体类型 / 命名空间

| 类型 | 命名空间 | 样例 |
|------|----------|------|
| `AgentSelectionResolver` | `Seeing.Agent.Core` | `AgentJob.cs:28,40`；`HeartbeatJob.cs:29,42` |
| `HookManager`（具体类） | `Seeing.Agent.Core.Hooks` | `AgentJob.cs:32,44`；`HeartbeatJob.cs:34,47` — 已有 `IHookManager` |
| `SystemReminderRenderer` | `Seeing.Agent.Core.Reminders` | `AgentJob.cs:313,367,369`；`HeartbeatJob.cs:176-178,293`；`ScheduleDispatchers.cs:55` |
| `IWorkspaceProvider` | `Seeing.Agent.Configuration` | `AgentJob.cs:29`；`JsonScheduleRepository.cs:21`；`SchedulerServiceCollectionExtensions.cs:172` |
| `UnifiedConfigManager` | `Seeing.Agent.Configuration` | `SchedulerOptionsProvider.cs:23,28`；`SchedulerServiceCollectionExtensions.cs:148` |
| （死）`Core.Models` on Cron* tools | — | `CronListTool.cs:6` 等 6 文件 |
| `Seeing.Agent.Llm`（ChatMessage 等） | 部分类型已在 Abstractions.Llm | Jobs 仍 `using Seeing.Agent.Llm` |

另：csproj 已 ref **Skills**（嵌入 skill 资源）；与去 Core 正交，勿与 Wave 4 混淆。

### 建议端口

1. `IAgentSelectionResolver`（方法签名照搬 `AgentSelectionResolver`）。
2. Jobs/Dispatchers 改 `IHookManager`；删对 `HookManager` 的构造依赖。
3. `ISystemReminderRenderer`（或把 reminder 纯函数迁到 Session / Abstractions，因主要产出 `SessionMessage`）。
4. `SchedulerOptionsProvider` 改为只依赖 `IConfigSectionStore`（或已有 `ISchedulerOptionsProvider` + store），去掉 `UnifiedConfigManager`。
5. 清死 using；删 Core ref。验证仍只通过 Abstractions 的 `IAgentExecutor` / `IExecutionWorld`（勿反向依赖 Hosting）。

---

## 4. Seeing.Agent.Acp — **M**

### Core 具体类型 / 命名空间

| 类型 | 命名空间 | 样例 |
|------|----------|------|
| `HookManager` | `Core.Hooks` | `AcpHookRegistrationHostedService.cs:5,16,22` |
| `IConfigSectionStore` / `ConfigChangedEventArgs` | `Configuration` | `AcpOptionsMonitor.cs:2,11,14,37`；多处 Client/Backend/Transport |
| `IWorkspaceProvider` | `Configuration` | `AcpMcpServerMapper.cs:20,26` |
| `CommandDiscovery` + `[Command]` 属性 | `Seeing.Agent.Commands*` | `AcpServiceCollectionExtensions.cs:23-24,105`；`AcpCommands.cs:3-4` |
| `SkillManager`（具体类，跨包） | `Seeing.Agent.Skills` | `AcpServiceCollectionExtensions.cs:116` — 应用 `ISkillManager` |
| `Seeing.Agent.Llm` | Core 程序集 | `ContentBlockMapper.cs:4`；`AcpPassthroughExecutor.cs:10`；`AcpEventMapper.cs:5`（`ChatMessage` 主体已在 Abstractions） |
| （死/陈旧）`Core.Models` | — | `AcpTool.cs:15`；`AcpMetadataKeys.cs:2`（实际用 `AgentContextKeys` ← Abstractions）；`AcpCommands.cs:5`；`AcpPermissionBridge.cs:8` 等 |

### 建议端口

1. Hook → `IHookManager`。
2. 共享 Configuration / Workspace 端口（同上）。
3. 命令：属性与 `CommandDiscovery` 上提到 Abstractions 或经 Hosting 端口注册；`InitializeAcpCommands` 只碰 `ICommandRegistry`。
4. `GetService<SkillManager>()` → `ISkillManager`（接口已有 `GetAllSkillInfos`）。
5. Llm：确认仅 Abstractions.Llm 类型后删 `Seeing.Agent.Llm` using；否则上提残留接口。
6. 清死 `Core.Models`；删 Core ref。csproj 仍可 ref Skills（能力组合），但类型面应走 Abstractions。

---

## 5. Seeing.Agent.Gateway — **L**（strict）

### Core 具体类型 / 命名空间

| 类型 | 命名空间 | 样例 |
|------|----------|------|
| `AgentSelectionResolver` | `Seeing.Agent.Core` | `GatewayHost.cs:70`；`GatewaySessionResolver.cs:14,18` |
| `IModelManager` | `Seeing.Agent.Llm` | `GatewayHost.cs:73`；`GatewaySessionService.cs:17,23`；`GatewaySessionResolver.cs:15,20` |
| `SystemReminderRenderer` | `Core.Reminders` | `GatewayScheduleDispatcher.cs:2,48` |
| `IConfigSectionStore` / `IWorkspaceProvider` | `Configuration` | `GatewayOptionsMonitor.cs:11`；`ChannelHostConfigStore.cs:22-29`；`ChannelHostManager.cs:16`；`GatewayChannelRegistryExtensions.cs:17` |
| （疑似死）`Core` / `Core.Models` | — | `GatewaySessionService.cs:2-3`（体用 Abstractions Agents + Llm） |

### 额外：Hosting 硬依赖（非 Core，但阻塞「集成包只依赖 Abstractions」）

| 类型 | 样例 |
|------|------|
| `IChatOrchestrator` / `Seeing.Agent.Hosting*` | `GatewayOrchestratorV2.cs:5-7,63,137,200` |
| `Seeing.Agent.Hosting.Events` | `GatewayEventMapper.cs:2` |
| csproj | `Seeing.Agent.Hosting.csproj` ProjectReference |

编排应优先改用已有 Abstractions 端口：`IExecutionSubmitter` / `IExecutionStatusProvider` / 事件契约；Hosting 类型留在 Host Shape / sample 组合层。

### 建议端口

1. `IAgentSelectionResolver`、`IModelManager`（Abstractions）、`ISystemReminder*`、Configuration/Workspace 共享端口。
2. 削薄 Hosting：Orchestrator 只依赖 Abstractions Execution/Events。
3. **可拆 PR：** (a) QQ/WeCom 先去 Core；(b) Gateway 配置/会话端口；(c) Orchestrator 去 Hosting。风险与 remediation W3-6 一致——依赖面大，拆分后回用户确认。

---

## 6. Seeing.Gateway.QQ — **S**

| 类型 | 样例 |
|------|------|
| `IWorkspaceProvider` + `new WorkspaceProvider()` | `QQServiceCollectionExtensions.cs:3,29` |

### 建议端口

- 只依赖 `IWorkspaceProvider`（Abstractions）；**禁止**在通道包 `new` Core 实现。
- Host / Gateway 集成在根 DI 注册 `WorkspaceProvider`；QQ 仅 `TryAddSingleton` 跳过（若已注册）或完全不注册。
- 删 Core ProjectReference；可加 Abstractions（若尚未经 Gateway 传递）。

---

## 7. Seeing.Gateway.WeCom — **S**

| 类型 | 样例 |
|------|------|
| `IWorkspaceProvider` + `new WorkspaceProvider()` | `WeComServiceCollectionExtensions.cs:4,30` |
| `IWorkspaceProvider` 注入 | `WeComSessionTracker.cs:5,17,30`（状态文件路径解析） |

同 QQ：接口上提 + 宿主注册实现；删 Core ref。

---

## 建议 PR 顺序

与 remediation 波次对齐，并按**真实依赖宽度**微调：

| 顺序 | PR | 内容 | 预估 | 依赖 |
|------|-----|------|------|------|
| 0 | **W3-0 Shared ports**（可并入 PR1） | `IConfigSectionStore`、`ConfigChangedEventArgs`、`ConfigSectionOptionsMonitor<T>`、`IWorkspaceProvider` → Abstractions；Core 实现保留 | S–M | — |
| 1 | W3-2 Skills | 删 Core ref；OptionsMonitor 用共享端口 | **S** | 0 |
| 2 | W3-6a QQ + WeCom | 删 Core；Workspace 仅接口 | **S** | 0 |
| 3 | W3-3 Memory | Config + Llm 接口上提；清死 using | **M** | 0 |
| 4 | W3-4 Scheduler | Selection / Hooks / Reminders / 去 UnifiedConfigManager | **M** | 0 |
| 5 | W3-5 Acp | Hooks + Commands 发现端口 + `ISkillManager`；清死 using | **M** | 0, 建议 Skills 已绿 |
| 6 | W3-6b Gateway | Selection / Model / Reminders / Config；评估 Hosting 削薄是否同 PR 或拆 6c | **L** | 0, 2 |
| — | W3-1 TokenBudget | 另 agent | — | — |

**验收（每包）：** `rg "Seeing.Agent.Core.csproj" src/<pkg>` 为空；`dotnet build src/<pkg>`；相关测试绿。

---

## Wave 4 备注 — Hosting → Skills 耦合

**现状（不在本波实现，供 W4 规划）：**

| 耦合点 | 位置 | 问题 |
|--------|------|------|
| csproj ProjectReference Skills | `Seeing.Agent.Hosting.csproj:25` | 宿主强制带 Skills 程序集 |
| `using Seeing.Agent.Skills` | `HostingServiceCollectionExtensions.cs:18` | 能力泄漏进 Hosting |
| 注册具体 `SkillCommands` | `HostingServiceCollectionExtensions.cs:42,115` | 命令应属 Skills 模块 |
| `GetService<SkillManager>()` | `HostingServiceCollectionExtensions.cs:131` | 应用 `ISkillManager`（Abstractions 已有；缺 `GetSkillInfo` 需补端口） |
| `SkillCommands` / `DynamicSkillCommand` / `SkillTemplateProcessor` | `Hosting/Commands/SkillCommands.cs:18-23,314` | 构造注入 `SkillManager` 具体类；文件应迁 `Seeing.Agent.Skills/Commands/` |

**W4 目标（remediation）：** 命令迁 Skills → `ActivateAsync` 经 `ICommandRegistry` 贡献；Hosting 只保留 `ISkillManager`；删 Skills ProjectReference。

**与 W3 交叉：** Skills 去 Core（W3-2）可先做；Hosting 去 Skills（W4）依赖 W1-4 命令扫描 + 接口补全 `GetSkillInfo`。Acp 的 `InitializeAcpCommands` 同样应改 `ISkillManager`，可与 W4 同批或紧随 W3-5。

---

## 风险与观察

1. **命名空间 ≠ 程序集：** `Seeing.Agent.Configuration` / `Seeing.Agent.Llm` / `Seeing.Agent.Commands` 均在 **Core 程序集**内——扫依赖时不能只搜 `Seeing.Agent.Core.*`。
2. **死 using 膨胀盘点：** Memory/Scheduler/Acp/Gateway 多处 `Core.Models` 无实质引用；先清理可缩小 PR。
3. **Gateway strict：** Core 引用可清，但 **Hosting** 引用仍违反「集成包只依赖 Abstractions」；需单独削薄或接受过渡期（建议在 W3-6 显式拆分）。
4. **Scheduler → Skills：** 嵌入资源/模块组合，不是 Core 问题，但影响独立打包叙事。
)
