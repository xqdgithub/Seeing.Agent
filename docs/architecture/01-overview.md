# 01 系统架构总览

## 1. 目标

Seeing.Agent 是可组合的 AI Agent 运行时：

- **进程里有什么**：由宿主编译期引用的包决定（模块目录 `available`）。
- **这次启用什么**：由 `seeing.json` 的 scenario / modules 结算决定（`enabled`）。
- **换沙箱不改工具**：工具只依赖 `IFileSystem` / `ISubprocess` / `IExecutionWorld`。

设计规格：`docs/superpowers/specs/2026-09-08-modular-architecture-design.md`。

## 2. 分层（依赖只能向下）

```
原语层     Seeing.Session / Seeing.TokenEstimation / Seeing.ConfigSchema
           磁盘: src/primitives/
   ↑
契约层     Seeing.Agent.Abstractions   （接口/DTO/常量，零业务实现）
           磁盘: src/abstractions/
   ↑
脊柱层     Seeing.Agent.Core         （配置脊柱、结算、权限、Hook、Native 循环、空 ToolManager）
           磁盘: src/spine/
   ↑
宿主运行时 Seeing.Agent.Hosting      （队列、Orchestrator、Task/Todo；禁止引用 ACP）
           磁盘: src/hosting/
   ↑
能力层     Tools.* / Skills / Mcp / Llm.* / Agents.BuiltIn / Scheduler / Memory / Acp / TokenBudget / IO.Local
           磁盘: src/capabilities/
   ↑
协议/集成  Seeing.Gateway* / Seeing.Agent.Gateway
           磁盘: src/gateway/
   ↑
Host Shape Hosting.Web / Headless / Embed / Gateway   （UI 表面；不决定能力集）
           磁盘: src/hosting/
   ↑
Sample     WebUI / Gateway.Server / Cli / Embed.Demo  （自己 ProjectReference 能力包）
```

| 层 | 允许引用 | 禁止 |
|----|----------|------|
| Abstractions | 原语（Session 等） | 任何实现包 |
| Core | Abstractions + 原语 | 任何能力包、IO.Local、Hosting |
| Hosting | Abstractions + Core + Session + Tools.Support | ACP、Skills、具体 Tools 能力包 |
| 能力包 | Abstractions + 原语（+ Tools.Support） | **Core 具体类型**；避免能力包互硬引用（优先 Abstractions） |
| Gateway 集成 | Abstractions + 原语 + Gateway 协议/通道 | Core、Hosting |
| Host Shape | Abstractions + Core（+ Hosting） | 能力包 / Gateway 集成包 |
| Sample | 任意组合 | 把能力塞回 Core |

## 3. 两个正交轴

| 轴 | 决定什么 | 谁选 | 热重载 |
|----|----------|------|--------|
| **Host Shape** | UI / 进程形态（Web、Headless、Embed、Gateway） | 宿主代码编译期 | 否 |
| **Scenario** | 启用模块集 + 默认 Agent + seams + tools.disabled | `seeing.json` / UI | 是（进程级） |

同进程可多会话：侧栏跟**进程场景**；会话工具栏徽标/默认 Agent/插槽跟**会话场景**（只能收窄）。

## 4. 执行主路径

```
用户输入 → ChatOrchestrator / ExecutionJobService
        → 会话级结算（sessionScenario ∩ 进程 enabled）
        → 唯一计算 ToolSchemas + SchemaSnapshotEvent
        → IAgentExecutor（Router → Native | ACP）
        → 工具经权限 / Hook / 装饰器
        → 事件 → SessionEventBus → UI
```

**硬规则：** Native executor **不得**自行 `GetToolSchemas(agent)`；只读 `AgentContext.ToolSchemas`。  
`/tools` 等命令可查询 ToolManager 当前注册表（诊断用），**不得**用其结果覆盖下一次 ChatRequest.Tools。

## 5. 数据一致性边界（防分叉）

下列四者必须来自**同一次结算结果**（进程 `enabled` ∩ 会话收窄）：

| 视图 | 真相源 | 禁止 |
|------|--------|------|
| 模块是否启用 | `IModuleCatalog.IsEnabled` | 页面 `GetService<T>()!=null` 当开关 |
| LLM 可见工具 | `ExecutionJobService` → `context.ToolSchemas` | executor / 别处再算一版 schema |
| 运行时可调用工具 | `IToolManager`（仅 Activate 注册） | `ConfigureServices` 永久挂 `ITool` |
| 侧栏 / 路由 / 插槽 | `IUiContributionRegistry`（Activate 登记） | 模块页写死 `@page` 绕过 registry |

不一致的典型后果：配置关了模块但模型仍能调工具；或 UI 仍显示入口但执行失败。

## 6. 禁止补丁式旁路

| 诱惑 | 正确做法 |
|------|----------|
| sample 缺工具 → 在 Core 里硬装模块 | 改 sample 的 `AddSeeingModule` / Scenario |
| 热重载不生效 → 手写 `File.Read` 再解析 | 走 `IConfigSectionStore` + ReloadHandler |
| 某页要 Memory → 直接注入具体服务判空 | `IModuleCatalog` + 贡献可见性 |
| 赶工把能力包引用 Core 类型 | 端口上提 Abstractions |
| 为修一 bug 复制一份 Settlement | 复用 `SettlementEngine` |

补丁旁路会在下一轮热重载或换 Scenario 时放大成「数据不一致 + 无法卸载」。

## 7. 模块生命周期

```
AddSeeingModule<T>  → ConfigureServices（登记 DI / 配置节；勿无条件开连接）
AddSeeingCore
InitializeSeeingAsync → Load 配置 → SettlementEngine → Activate 启用集
                      → Activate 内 IToolManager.RegisterTool*
配置变更 → ModuleSettlementReloadHandler → 再结算
        → Activate 新增 / Deactivate 移除（Unregister 工具、停 HostedService、断连接）
```

- `DependsOn` 未启用 → **拒启**。  
- 场景引用目录外 id → 告警忽略。  
- 在途执行时 Deactivate **默认推迟**到执行结束（可用 `ForceCancelInFlight`）。  
- `IModuleHostedService`：Deactivate 后 `IsRunning=false`，再 Activate **自动复活**循环。

## 8. 配置边界

- Agent/Gateway/ACP 主配置只写 `~/.seeing/seeing.json` 与 `./.seeing/seeing.json`。  
- **禁止** `appsettings.json` 的 SeeingAgent 节作为真相源。  
- 配置节必须经 `IConfigSectionRegistry.Register`；能力包自登记。

## 9. 与旧架构的断裂点（勿回潮）

| 已删除 / 禁止 | 替代 |
|---------------|------|
| `AddSeeingAgent()` | `AddSeeingCore` + 显式模块 |
| Core 内全套工具 / MCP SDK | 能力包 + 宿主引用 |
| `ConfigureServices` 永久 `AddSingleton<ITool>` | Activate `RegisterTool` / Deactivate `Unregister` |
| Hosting → Skills 硬引用 | Skills 经 `ICommand` / `ISkillManager` 贡献 |
| Hosting.Gateway → Agent.Gateway | sample 显式 `AddSeeingGatewayServer` |
| 双算 schema | 仅 `ExecutionJobService` |
| `src/` 根下平铺新项目 | 放入 `primitives|abstractions|spine|hosting|capabilities|gateway` |
