## Why

LLM 的 `tools[]` 是每轮请求都会重发的固定前缀。当前 Seeing.Agent 采用"全量静态暴露"：51 个内置工具 + 任意数量 MCP 工具全部注入，每轮固定付出 15K–35K input tokens（叠加 MCP 后线性增长），且工具数超过 30–50 后模型选择精度明显下降。同时存在三处纯工程债放大问题：schema 先对全部注册工具生成再过滤、schema 无缓存每轮重建、工具顺序不稳定破坏 provider 前缀缓存。

调研结论（`docs/superpowers/specs/2026-10-21-tool-context-economy-research-and-design.md`）：业界已收敛出四条主线——分级延迟加载（Claude Tool Search，55K→8.7K）、客户端目录+检索（opencode，目录预算约 500 tokens）、代码模式（Cloudflare/PTC，150K→2K）、会话内工具生命周期（DeepSeek dsh）。本项目多 Provider、无服务端 `defer_loading` 能力，必须纯客户端实现。目前唯一具备的资本是 schema 单点出口（`SessionToolSchemaResolver`），这使低侵入落地成为可能。

## What Changes

**P0 清债（行为不变）**

- `ToolManager.GetToolSchemasAsync(settled, agent)` 改为"先求 id 交集、再逐工具生成 schema"，消除对全部注册工具做反射读取与阻塞 Hook 的浪费。
- schema 输出按工具 id 稳定排序，保证同会话相邻请求的 `tools[]` 前缀稳定。
- 增加 schema 缓存（键含目标 id 集、Agent 白/黑名单、禁用集版本、`tool.definition` Hook 版本）。
- `IHookManager` 新增单调递增的 `Version`（缓存失效依据）。
- 工具定义 token 估算接线到 `SourceBreakdown.ToolDefinitions`；目录占用单独计入 `SourceBreakdown.Catalog`。
- `ExecutionRunner` 对 schema 解析器的依赖由具体类改为 `ISessionToolSchemaResolver`，删除回退构造。
- MCP 工具 id 归一为 `mcp__{server}__{tool}`（含连接协调器全部 id 生成点），**零兼容**；同名 server 重复注册改为报错。

**P1 分级延迟 + 元工具检索（方案 A，默认 `auto`）**

- 新增契约：`ToolExposureMode`（单工具：Inline/Deferred/Hidden）、`ToolExposurePolicyMode`（策略：All/Auto/Tiered）、`ToolCatalogEntry`、`ToolExposurePlan`、`ToolExposureInput`、`IToolExposurePolicy`、`IToolDiscoveryStore`。
- 策略为**纯函数**：不访问 DI、会话、存储；输入为不可变快照。
- 默认策略 `TieredToolExposurePolicy`：pinned 常驻 + 其余进目录摘要（字符预算 2000、描述截断 120）。
- `SessionToolSchemaResolver.SettleAsync` 在 schema 生成之后接入策略、发现态求交、升级判定。
- 自动升级为双条件 OR（≥30 个工具 或 ≥8000 token）+ 单调升级 + 会话粘性（`SessionData.ExposureEscalated`）。
- **重构提示分节机制**：贡献者自述 `Heading`/`Placement`，删除 `PromptBuilder` 的硬编码分节白名单。
- 新增系统提示分节 `## Tool Catalog`（只含签名与命名空间计数，标注 "N tools, M shown"，说明"检索结果自下一轮生效"）。
- 新增能力模块 `Seeing.Agent.Tools.ToolDiscovery`，提供**单一** `tool_search`（三档 detail `name`/`summary`/`full`，offset 分页，`full` 单页硬限 3）。
- 会话发现态持久化（`SessionData.DiscoveredTools`，无上限淘汰）、`SessionForker` 显式继承、工具消失时求交裁剪。
- 新增配置 `Tools.Exposure`，支持 全局 < Scenario < Agent < 会话 四级覆盖。
- `SchemaSnapshotEvent` 增加 inline/deferred 计数与 token 估算；新增 `ToolSearchEvent`。

**非目标**

- 不实现**轮内** schema 刷新（发现结果自下一轮生效）。
- 不实现 **Code Mode**（不以任何形式提供代码编排工具调用，也不预留无效开关）。
- 不实现目录增量注入（system prompt 每轮整串重发，delta 不省 token 也不保缓存）。
- 不实现目录落盘（已有两条获取通道：提示词目录 + `tool_search full`）。
- 不依赖任何 Provider 服务端 `defer_loading`；不引入非标准协议字段。
- 不改动 `AllowedTools` / `DeniedTools` 的权限语义（exposure 只决定可见性）。
- 不引入容器/进程沙箱。

## Capabilities

### New Capabilities

- `tool-schema-settlement`: schema 结算/注入的确定性、缓存、Hook 版本、token 计量、单点出口依赖接口化。
- `tool-exposure-policy`: 暴露模式、策略纯函数约束、目录条目与预算、双条件自动升级与单调粘性、四级配置覆盖、exposure 不改授权。
- `tool-discovery`: 会话级发现态（只增不减、无上限淘汰、持久化、fork 继承）、单一 `tool_search` 的三档 detail、offset 分页、确定性排序、下一轮生效语义。
- `tool-catalog-instructions`: 分节自述标题与放置位置（替代硬编码白名单）、目录分节的内容边界/预算/计数标注/确定性渲染。
- `mcp-tool-naming`: `mcp__{server}__{tool}` 命名、全部 id 生产点、零兼容、重复 server 名拒绝。

### Modified Capabilities

<!-- openspec/specs/ 目前为空；本变更不修改既有 spec。 -->

## Impact

**代码**

- `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`：先交集后生成、稳定排序、缓存、能力键 `cacheable`。
- `src/abstractions/Seeing.Agent.Abstractions/Hooks/IHookManager.cs`：新增 `Version`。
- `src/spine/Seeing.Agent.Core/Core/Hooks/HookManager.cs`：版本递增。
- `src/hosting/Seeing.Agent.Hosting/Execution/SessionToolSchemaResolver.cs`：接入策略、发现态、升级判定。
- `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`：依赖改为接口、必需注入、删除回退。
- `src/hosting/Seeing.Agent.Hosting/Execution/ChatExecutionContext.cs`：新增 token/计数/目录字段。
- `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/`：新增策略、目录构建、升级跟踪、配置解析、发现态 store。
- `src/hosting/Seeing.Agent.Hosting/Prompts/ToolCatalogSectionContributor.cs`：新增目录分节。
- `src/spine/Seeing.Agent.Core/Core/Prompts/PromptBuilder.cs`：**重构**分节定位机制（删除 `SectionAnchors`）。
- `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`：仅新增目录文本注入到 `PromptContext`，**不涉及 schema 生命周期**。
- `src/abstractions/Seeing.Agent.Abstractions/Tools/`：新增 exposure/catalog/discovery 契约。
- `src/abstractions/Seeing.Agent.Abstractions/Prompts/`：`IPromptSectionContributor` 增 `Heading`/`Placement`；`PromptContext` 增通用 `Sections` 槽。
- `src/abstractions/Seeing.Agent.Abstractions/Events/`：`SchemaSnapshotEvent` 增计数字段、新增 `ToolSearchEvent`。
- `src/primitives/Seeing.Session/Core/SessionData.cs`：新增 `DiscoveredTools`、`ExposureEscalated`。
- `src/primitives/Seeing.Session/Management/SessionForker.cs`：显式复制两个新字段。
- `src/primitives/Seeing.TokenEstimation/SourceBreakdown.cs`：新增 `Catalog` 字段。
- `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/`：新增能力模块。
- `src/capabilities/Seeing.Agent.Mcp/`：`McpTool.cs`、`Management/McpConnectionCoordinator.cs`（三处）、`Management/McpToolRegistry.cs`。
- `src/hosting/Seeing.Agent.Hosting/Commands/ToolsCommands.cs`：修正 MCP 前缀判定。
- `src/hosting/Seeing.Agent.Hosting/Seeing.Agent.Hosting.csproj`：引用 `Seeing.TokenEstimation`。
- `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs`：新增 `Tools.Exposure`。
- `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs`：`Full` 加入 `tool-discovery`。
- `samples/Seeing.Agent.WebUI/Program.cs`：注册新模块。
- `Seeing.Agent.slnx`：新增 src 与 tests 项目条目。

**测试**

- `tests/spine/Seeing.Agent.Tests/Tools/`：schema 交集/排序/缓存/Hook 版本。
- `tests/spine/Seeing.Agent.Tests/App/Execution/`：resolver 接入策略、发现态、升级、排序、旁路同源。
- `tests/spine/Seeing.Agent.Tests/Prompts/`：分节放置机制、目录贡献者、DI 注册。
- `tests/spine/Seeing.Agent.Tests/Hooks/`：版本单调。
- `tests/spine/Seeing.Agent.Tests/Permissions/`：MCP 新 id 权限匹配。
- `tests/capabilities/Seeing.Agent.Mcp.Tests/`：命名归一、重复 server 名拒绝、重连 id 稳定。
- `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/`：新项目，检索/分页/detail/发现态。
- `tests/primitives/Seeing.Session.Tests/`：发现态持久化与 fork 继承、清空消息保留。
- `tests/primitives/Seeing.TokenEstimation.Tests/`：`Catalog` 计入口径。
- `tests/spine/Seeing.Agent.Invariants.Tests/`：单点出口、Core 零工具、可见性不变量。

**配置与兼容**

- 新增 `Tools.Exposure` 节，默认 `PolicyMode=Auto`（51 内置工具默认触发分级）。可显式改 `All` 回到现状。
- 会话持久化新增 `DiscoveredTools` 与 `ExposureEscalated`，缺失时按空集/false 处理。
- MCP id 归一是**破坏性变更且零兼容**：禁用列表、Agent `AllowedTools`/`DeniedTools`、permission 规则、`/mcp` 展示均需改配置；permission 规则失效可能使 Ask 变 Allow，须在验收中专门验证。
- 同名 MCP server 重复注册由静默覆盖改为报错（行为破坏）。

**性能预期**

- 51 内置工具场景首轮 input tokens 降 ≥60%；50 个 MCP 工具场景降 ≥85%。
- 单轮 schema 生成的 Hook 调用次数从"全部注册工具数"降为"目标工具数"。
