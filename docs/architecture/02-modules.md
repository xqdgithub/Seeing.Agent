# 02 系统模块地图

路径相对仓库根；程序集名 = 目录末段名。

## 1. 脊柱与原语

| 包 | 磁盘路径 | 模块 id | 职责 |
|----|----------|---------|------|
| `Seeing.Agent.Abstractions` | `src/abstractions/` | — | 契约 |
| `Seeing.Agent.Core` | `src/spine/` | — | 结算、`IScenarioCatalog`、权限、Hook、Prompt、Native 循环、空 ToolManager |
| `Seeing.Session` | `src/primitives/` | — | 会话存储；`SessionData.Scenario` |
| `Seeing.TokenEstimation` | `src/primitives/` | — | Token 估算 |
| `Seeing.ConfigSchema` | `src/primitives/` | — | 配置 schema 辅助 |
| `Seeing.Agent.Hosting` | `src/hosting/` | `subagent`（Task/Todo） | 执行队列、命令注册表、Task/Todo 工具 |

## 2. 执行世界

| 包 | 路径 | 模块 id | 提供 seam |
|----|------|---------|-----------|
| `Seeing.IO.Local` | `src/capabilities/` | `io.local` | `executionWorld` |

工具 **必须** 经 `IExecutionWorld` / `IFileSystem` / `ISubprocess`，禁止生产路径直接 `File.*` / `Process.Start`（测试替身除外）。

## 3. 工具与能力包（`src/capabilities/`）

| 包 | 模块 id | 典型工具 / 能力 |
|----|---------|-----------------|
| `Seeing.Agent.Tools.FileSystem` | `filesystem` | read/write/edit/glob/grep/delete… |
| `Seeing.Agent.Tools.Shell` | `shell` | bash |
| `Seeing.Agent.Tools.Web` | `web` | fetch/search |
| `Seeing.Agent.Tools.Git` | `git` | git_* |
| `Seeing.Agent.Tools.Session` | `session.tools` | session_list/search/read/trim/handoff |
| `Seeing.Agent.Tools.Basic` | `basic` | current_time |
| `Seeing.Agent.Tools.Support` | — | ToolBase 等共享；**不是** `ISeeingModule` |
| `Seeing.Agent.Skills` | `skills` | skill；命令经 `ICommand` 贡献 |
| `Seeing.Agent.Mcp` | `mcp` | MCP 连接与代理工具 |
| `Seeing.Agent.Llm.OpenAI` | `llm.openai` | ILlmClientFactory |
| `Seeing.Agent.Llm.Anthropic` | `llm.anthropic` | ILlmClientFactory |
| `Seeing.Agent.Agents.BuiltIn` | `agents.builtin` | build/plan/explore/general… |
| `Seeing.Agent.Scheduler` | `scheduler` | cron / heartbeat |
| `Seeing.Agent.Memory` | `memory` | 记忆 |
| `Seeing.Agent.Acp` | `acp` | ACP 执行实现 |
| `Seeing.Agent.TokenBudget` | — | Token 预算 Hook（可无模块 id；经扩展方法挂 Hook） |
| `Seeing.Provider.DeepSeek`（`plugs/providers/`） | `provider.deepseek` | DeepSeek 网关 Provider；`DependsOn: llm.openai` |
| `Seeing.Provider.OpenCodeZen`（`plugs/providers/`） | `provider.opencodezen` | OpenCode Zen 网关 Provider；`DependsOn: llm.openai` |

`subagent` 为 Hosting 级能力（Task 工具）；内置场景字符串里可出现该 id。

**Provider 插件 vs 协议模块：** `llm.openai` / `llm.anthropic` 登记 `ILlmClientFactory`；`provider.*` 在 Activate 时登记 `ILlmProvider` 到 `IProviderRegistry`，创建客户端时经 `IEnumerable<ILlmClientFactory>` 按 `SupportsType` 解析（禁止注入单个工厂）。

**Support 包注意：** Hosting 可引用 `Tools.Support` 以复用 Task/Todo 基类；**不得**因此再引用 `Tools.FileSystem` 等具体能力包。

## 4. Gateway 族（`src/gateway/`）

| 包 | 模块 id | 职责 |
|----|---------|------|
| `Seeing.Gateway` | — | 协议 DTOs（仅 Abstractions） |
| `Seeing.Gateway.Client` | — | HTTP/SSE/WebSocket 客户端 |
| `Seeing.Gateway.WeCom` / `.QQ` | — | 通道桥 |
| `Seeing.Agent.Gateway` | `gateway` | 集成包：独立 Kestrel、通道宿主；**不**引用 Core/Hosting |

详阅 [`docs/gateway/README.md`](../gateway/README.md)。

## 5. Host Shape（`src/hosting/`）

| 包 | 职责 | 能力包 |
|----|------|--------|
| `Seeing.Agent.Hosting.Web` | Circuit、`EventStreamPermissionChannel`（事件流宿主通道，Singleton） | **不**引用 |
| `Seeing.Agent.Hosting.Headless` | 无 UI 宿主辅助 | **不**引用 |
| `Seeing.Agent.Hosting.Embed` | 进程内嵌入 | **不**引用 |
| `Seeing.Agent.Hosting.Gateway` | Gateway Host Shape 描述符 | **不**引用 Agent.Gateway；sample 组合 |
| `Seeing.Agent.Hosting.Tui` | 无 UI 终端宿主编排 + `TuiPermissionChannel`（事件流宿主通道，Singleton） | **不**引用 |

**权限授权子系统归属（2026-09-17 重构后）：**

- **Abstractions（契约）**：`IPermissionService`、`IPermissionAuthorizer`（+`IPermissionAuthorizerFactory`）、`IPermissionRequestManager`、`IPermissionGrantStore`、`IPermissionPresenter`、`IPermissionPresentationStore`、`IPermissionChannel`、请求/结果/授权模型。
- **Core（脊柱）**：`PermissionService`（规则/策略 + `AuthorizeAsync` 资源门编排）、`PermissionRequestManager`（在途唯一权威）、`PermissionPresentationStore`（呈现端登记表）、`PermissionGrantStore`、`EffectivePermissionPolicy`、`ExecutionContextPermissionAuthorizer` + `DefaultPermissionAuthorizerFactory`、`DenyAllPermissionChannel`、`PermissionKindMapper`。
- **Hosting.Web（Host Shape）**：`EventStreamPermissionChannel`（事件流宿主通道；`WebHostingServiceCollectionExtensions.cs:46` 注册为 Singleton）。
- **Gateway**：`GatewayPermissionChannel`（`TryAutoApprove` 读 `GatewayOptions.PermissionMode`）；每个执行订阅注册 `GatewaySubscriptionPresenter`（固定单会话呈现端，随订阅注销）。
- **WebUI（sample）**：`PermissionInbox`（全局在途投影，Singleton）、`PermissionInboxView`（scoped 投影 + 合并）、`WebUiPermissionPresenter`（circuit 维度呈现端）、`PermissionInteractionService`。

## 6. Samples 与插件

| 路径 | 角色 |
|------|------|
| `samples/Seeing.Agent.WebUI` | Blazor 主开发宿主；组合能力 + Web Shape |
| `samples/Seeing.Agent.Tui` | `seeing-tui` 内联流式终端组合根；组合能力 + Tui Shape |
| `samples/Seeing.Gateway.Server` | 无 UI Gateway 宿主；`AddSeeingHostingGateway` + `AddSeeingGatewayServer` |
| `samples/Seeing.Agent.Cli` | 命令行管理；无参数/`tui` 在前台原地进入 TUI，`web` 默认前台、`--background` 回退后台 |
| `samples/Seeing.Agent.Embed.Demo` | Embed Shape 演示 |
| `samples/Seeing.Gateway.ChannelHost` | 通道外进程宿主 |
| `samples/Seeing.Gateway.*.Demo` | 通道演示 |
| `plugs/providers/Seeing.Provider.*` | 可选 LLM Provider 插件（DeepSeek、OpenCodeZen） |

Sample **自己** `ProjectReference` 能力包；Shape 包不代引用。

## 7. 内置 Scenario

定义：`Seeing.Agent.Core.Scenarios.BuiltInScenarios`（`src/spine/`）。  
自定义：`seeing.json` → `SeeingAgent.Scenarios`（经 `IScenarioCatalog` 合并；同名覆盖内置）。

| 名 | 默认 Agent | 模块意图（摘要） |
|----|------------|------------------|
| minimal | general | io.local + agents.builtin + llm.openai + basic + provider.deepseek/opencodezen |
| code | build | + filesystem/shell/git/subagent + providers |
| work | general | + filesystem/web/memory/scheduler + providers |
| research | explore | + web/memory + providers |
| full | build | **全部已实现模块 id**（含 acp/gateway/providers；与 available 求交） |

结算：`scenario.modules ∩ available`；未引用包的 id 告警忽略。宿主引用了 plug 但场景模块列表未含其 id 时，Provider **不会** Activate（模型页看不到、无法拉模型）。

`full` 维护约定：新增 `ISeeingModule` 时同步写入 `BuiltInScenarios` 的 `s_fullModules`，并更新 `BuiltInScenariosTests.Full_Should_Enable_All_Implemented_Module_Ids`。

## 8. WebUI 贡献

- `IUiContribution` / `IUiContributionRegistry`：Nav / SettingsCard / Slot / MessageRenderer / **RouteContribution**  
- `NavContribution.Group`：控制中心 / 工作区 / 设置  
- `ModuleRouter`：路由表**只**来自 registry；未启用 → `NotEnabledFragment`  
- 壳页无 `@page`（仅 `_Host.cshtml` 保留宿主页）  
- 页面可见性 **只读** `IModuleCatalog` + Scenario

## 9. 测试布局

| 分类 | 路径 |
|------|------|
| 脊柱 / 不变量 | `tests/spine/Seeing.Agent.Tests`、`Seeing.Agent.Invariants.Tests` |
| 原语 | `tests/primitives/*` |
| 能力 | `tests/capabilities/*` |
| Gateway | `tests/gateway/*` |
| Hosting | `tests/hosting/*` |
| 应用 / 插件 | `tests/apps/*`、`tests/plugs/*` |

工具单测放 `tests/capabilities`，勿塞回 `Seeing.Agent.Tests`。
