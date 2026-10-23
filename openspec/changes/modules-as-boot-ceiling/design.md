## Context

Seeing 进程内模块生命周期是单例挂载面；会话只能滤镜。既往用 `Scenario.Modules` 兼任天花板与工作模式，并与 `Modules.Enabled` 叠加，概念过载且行为反直觉。

现有实现已正确分层（`SettlementEngine` 操作模块；`SessionSettlement.Compute` 先求交模块、再展开工具、再扣 `Tools.Disabled`）。本变更保持该分层，把「启动天花板」从 Scenario 抽到独立 CapabilitySet + Boot，不让工具层概念回渗模块层。

本变更按域正交一次重构到位：**CapabilitySet ⊥ Scenario**，两者都是 module id 列表但职责不同（启动素材 vs 会话滤镜愿望），运行时 `∩ bootEnabled` 解耦，**不**用配置外键绑定。命名全局统一（PascalCase JSON 键、模块层减法统一 `Disabled`）。

## Goals / Non-Goals

**Goals:**

- 能力集（`Modules` + `Disabled`）与工作模式（`Modules` + `DefaultAgent` + `Tools.Disabled` + `Seams`）类型分离；两者均只含 module id 在模块层，职责不同。
- `Boot` 唯一决定 Activate 集；`Scenario` 唯一表达默认/会话工作模式。
- 进程 BoundSeams 绑定来源明确（`Seams` + `HostDefaultSeams`），与 Scenario 解耦。
- 命名全局一致：JSON 键 PascalCase；模块层减法统一 `Disabled`；工具层统一 `Tools.Disabled`。
- 严格两层：模块层（求交/Activate）与工具层（展开 ProvidedTools/裁剪/schema）不混。
- 热重载按节分流：boot 节变更才 Activate diff；Scenarios 变更不跑 boot diff。
- `["*"]` 在解析层展开，不进字面 `IntersectWithAvailable`。
- 配置、UI、架构文档与实现同一套词汇。

**Non-Goals:**

- 每会话独立 Activate。
- 本变更内落地 BoundSeams 运行时换 DI（仍进程级一份；仅改解析来源）。
- MCP 动态工具进 settled 的完整方案（另案；Boot 含 `mcp` 仅保证模块级启停）。
- 保留「Scenario 兼天花板」或双轨结算。
- 让 Scenario 强制引用 CapabilitySet（外键耦合，已否决）。
- CLI `--boot` 为必交付（可降级 P2）。

## Decisions

### D1：两轴模型（最优域分解，无外键耦合）

```
CapabilitySet  = { Name, Modules[], Disabled? }        // 纯能力素材，仅模块层
Boot           = capabilitySetName | "*"                // 进程指针 → bootEnabled → Activate
Scenario       = { Name, Modules[], DefaultAgent, Seams?, Tools.Disabled? }  // 工作模式
```

- `Boot = "*"` → `bootEnabled = Available − Modules.Disabled`
- `Boot = name` → `bootEnabled = (CapabilitySets[name].Modules ∩ Available) − CapabilitySets[name].Disabled − Modules.Disabled`
- `Modules` 可为 `["*"]` → 解析层展开为全 `Available`，**不**进 `IntersectWithAvailable` 字面交。
- 未知 Boot 名 → `SettlementException` 拒启。
- Scenario **不**引用 CapabilitySet；两者各自持有 `Modules`，运行时 `scenario.Modules ∩ bootEnabled` 求交。

### D2：严格两层（对齐现有实现）

**模块层**（操作 module id）：
```
bootEnabled    = resolve(Boot) ∩ Available − Disabled(set) − Modules.Disabled(global)
sessionModules = scenario.Modules ∩ bootEnabled
                 （再 ∩ session.ScenarioOverride.Modules.Enabled，若有，仍 ∩ bootEnabled）
```

**工具层**（操作 tool id，模块层之后）：
```
settledToolIds = ⋃ sessionModules.ProvidedTools
                 − scenario.Tools.Disabled
                 − session.ScenarioOverride.Tools.Disabled
                 − Modules.Tools.Disabled
schema         = ToolManager 注册表 ∩ settledToolIds ∩ Agent AllowedTools − DeniedTools
```

`Tools.Disabled` **只**在工具层；**禁止**写入模块层公式。与现有 `SessionSettlement.Compute` 一致。

### D3：废除 Modules.Enabled 主路径

- 结算 **不再** 使用 `Modules.Enabled`。
- 保留 `Modules.Disabled` 作为 boot 全局黑名单（模块层）。
- 检测 `Modules.Enabled` → 警告 + 忽略，文档指向 CapabilitySet + Boot。

### D4：CapabilitySet.Disabled（模块层黑名单，命名统一）

- 能力集内 `Disabled` 与全局 `Modules.Disabled` **同义同层同词根**，作用域不同：
  - `CapabilitySet.Disabled`：随该能力集（换 Boot 即换黑名单）
  - `Modules.Disabled`：全局，所有 Boot 都扣
- 统一公式：`base = (Modules 缺省或含 "*") ? Available : (Modules ∩ Available)`；`result = base − Disabled(set) − Modules.Disabled(global)`。
- **不**用 `exclude`（与 `Disabled` 同义换词根增加概念）。

### D5：进程 BoundSeams 绑定来源（P0 修复）

- 进程级 seam 绑定**只**来自：
  1. `SeeingAgent.Seams`（用户覆盖）
  2. `HostDefaultSeams`（宿主默认，如 `{ executionWorld: io.local }`）
- Scenario.`Seams` 降为**会话/文档语义**（工作模式建议），**不**驱动进程 Activate、**不**驱动进程 seam 绑定。
- `SettlementEngine.ResolveSeamsMap` 改为读 `Seams` + `HostDefaultSeams`，不再用进程 Scenario 名查 seams。
- `ValidateAndBindExclusiveSeams` 仍对 `bootEnabled` 校验（提供方必须在 boot 内）。

### D6：热重载按节分流（P0 修复）

`ModuleSettlementReloadHandler.s_relevantSections` 拆分：

| 节变更 | 行为 |
|-------|------|
| `Boot` / `CapabilitySets` / `Modules.Disabled` / `Seams` / `Workspace` | 再结算 → Activate/Deactivate diff（模块层） |
| `Scenarios` / `Scenario`（默认名） | **不**跑 boot Activate diff；只刷 `IScenarioCatalog` / OptionsMonitor（会话滤镜/UI 下次生效） |

- `Scenarios` 从 boot reload relevant 列表移除。
- 改 `Scenarios.*.Modules` / `Tools.Disabled` → 不触发 Activate diff。
- 改 `Seams` → 可能改 BoundSeams → 再结算（可能拒启）。

### D7：目录与 API

- `ICapabilitySetCatalog`：`Get(name)` / `ListAll()`；内置 ∪ `seeing.json CapabilitySets`（同名配置覆盖内置）；注册到 DI（`ServiceCollectionExtensions` TryAddSingleton）。
- `IScenarioCatalog`：解析 Scenario 自带 Modules（不依赖 CapabilitySetCatalog）；注册已在。
- `IModuleCatalog.Enabled` ≡ bootEnabled。
- `ConfigSectionRegistry.RegisterSpineSections` 增 `Boot` / `CapabilitySets` 节登记。
- `SettlementInput` 增 `ConfiguredBoot` / `BootOverride` / `HostDefaultBoot`；`SettlementResult` 增 `Boot` 字段；`Scenario` 字段**保留**，语义为「进程默认工作模式名（仅诊断/日志）」，**不**被 Activate 消费（见 D11）。
- 宿主 `ProcessSettlementOptions` 增 `HostDefaultBoot` / `HostDefaultSeams` / `BootOverride`；优先级 `BootOverride > 文件 Boot > HostDefaultBoot`。

### D8：UI / 文案

- 设置分两块：**启动能力（Boot + 能力集）**、**工作模式（Scenario）**。
- 侧栏：Requires ⊆ bootEnabled；**移除** `UiContributionVisibility.MatchesProcessScenario` 对进程 Scenario 名的硬过滤（Scenario 白名单仅用于会话壳/插槽）。
- `ScenariosSettingsCard` 文案改：场景 = 工作模式；启动能力 = Boot + 能力集。

### D9：BuiltIn SSOT 防漂移

- `BuiltInCapabilitySets` 为 boot 模块列表 SSOT。
- `BuiltInScenarios.Modules` **编译期引用** `BuiltInCapabilitySets` 常量（如 `BuiltInCapabilitySets.Code`），不复制数组。

#### 内置能力集枚举（SSOT，对应现有 `BuiltInScenarios` 模块列表）

| 能力集名 | 模块 id 列表 | 对应 BuiltInScenarios 引用 |
|----------|--------------|----------------------------|
| `minimal` | `io.local`, `agents.builtin`, `llm.openai`, `basic`, `provider.deepseek`, `provider.opencodezen` | `BuiltInScenarios.Minimal.Modules` → `BuiltInCapabilitySets.Minimal` |
| `code` | `io.local`, `agents.builtin`, `llm.openai`, `basic`, `filesystem`, `shell`, `git`, `subagent`, `provider.deepseek`, `provider.opencodezen` | `BuiltInScenarios.Code.Modules` → `BuiltInCapabilitySets.Code` |
| `work` | `io.local`, `agents.builtin`, `llm.openai`, `basic`, `filesystem`, `web`, `memory`, `scheduler`, `provider.deepseek`, `provider.opencodezen` | `BuiltInScenarios.Work.Modules` → `BuiltInCapabilitySets.Work` |
| `research` | `io.local`, `agents.builtin`, `llm.openai`, `basic`, `web`, `memory`, `provider.deepseek`, `provider.opencodezen` | `BuiltInScenarios.Research.Modules` → `BuiltInCapabilitySets.Research` |
| `full` | `io.local`, `agents.builtin`, `llm.openai`, `llm.anthropic`, `basic`, `filesystem`, `shell`, `git`, `subagent`, `web`, `memory`, `scheduler`, `skills`, `mcp`, `acp`, `gateway`, `provider.deepseek`, `provider.opencodezen` | `BuiltInScenarios.Full.Modules` → `BuiltInCapabilitySets.Full` |
| `secure` | `Modules: ["*"]`，`Disabled: [shell, mcp, acp, gateway]` | 无对应 Scenario；仅 Boot |
| `dev` | 与 `full` **同一模块列表引用** | 无对应 Scenario；仅 Boot |

### D10：Host Shape 默认值表（SSOT）

各 Host Shape 须通过 `ProcessSettlementOptions` 注册 `HostDefaultSeams`（至少绑 `executionWorld`，避免空 seam 拒启），可选 `HostDefaultBoot`。

| Host Shape | `HostDefaultSeams` | `HostDefaultBoot` | 说明 |
|------------|-------------------|-------------------|------|
| Web (`Hosting.Web`) | `{ executionWorld: io.local }` | （不设；回退 `*`） | 桌面/服务器开发机，全量挂载 |
| Headless (`Hosting.Headless`) | `{ executionWorld: io.local }` | （不设；回退 `*`） | 网关/服务进程，按需由文件 Boot 收窄 |
| Embed (`Hosting.Embed`) | `{ executionWorld: io.local }` | `minimal` | 嵌入式资源受限，默认窄启动 |
| Gateway (`Hosting.Gateway`) | `{ executionWorld: io.local }` | （不设；回退 `*`） | 网关通道进程 |

- `HostDefaultSeams` 在所有形态至少绑 `executionWorld=io.local`（与现有 `BuiltInScenarios.s_defaultSeams` 一致），保证剥离 Scenario.Seams 后不回归拒启。
- `HostDefaultBoot` 仅 Embed 设 `minimal`；其余回退 `*`（文件 `Boot` 优先于 Host 默认）。

### D11：SettlementResult 字段定案

- `SettlementResult` 新增 `Boot` 字段（有效 boot 指针，override 后值）。
- `SettlementResult.Scenario` **保留**，语义定为**「进程默认工作模式名（仅诊断/日志）」**，**不**被 settlement 消费者当 Activate 来源。
- 不再使用「deprecate」措辞：字段保留、语义重定义、文档写明。

### D12：为何不采用「Scenario 必须引用 CapabilitySet」

外键绑定让 Scenario 依赖 CapabilitySet 命名/拆分；改名/拆能力集时场景跟着坏。两轴各自持有 Modules，运行时求交，才是真正正交。

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| 配置键变多 | UI 向导；内置齐全；文档一张图 |
| 旧 seeing.json 失效 | 迁移说明；`Modules.Enabled` 警告 |
| 两处 Modules 列表「重复」 | 职责不同（启动 vs 滤镜）；BuiltIn 编译期引用防漂移 |
| 默认 Boot=`*` 资源更重 | Host Shape 少引用包缩小 Available；或 `HostDefaultBoot` 覆写 |
| BoundSeams 来源改 | `Seams`+`HostDefaultSeams` 显式；BuiltIn 全用同 seams 掩盖的问题暴露后修复 |
| `["*"]` 误进字面交 | 解析层展开；单测覆盖 |

## Migration Plan

1. 代码：抽出 `BuiltInCapabilitySets`；`BuiltInScenarios.Modules` 引用常量；改 `SettlementEngine`（boot 公式 + BoundSeams 来源）；`ModuleSettlementReloadHandler` 节分流；`ConfigSectionRegistry` + DI + `SettlementInput`/`Result`；`ExecutionJobService` 传 catalog；UI 侧栏。
2. 配置：示例与 WebUI 保存写入 `Boot` + `CapabilitySets`（PascalCase）；`Scenario` 保持 `Modules`；`ToolsDisabled` 扁平 deprecate，统一 `Tools.Disabled`。
3. 用户：`Scenario=minimal` 当沙箱 → `Boot=minimal`；`Modules.Enabled=[...]` → 新建 CapabilitySet + `Boot`。
4. 文档 01/03（含两层分离 + §4 既有错误修正）/05 示例同步后合入；无双轨兼容期逻辑（仅警告忽略 `Modules.Enabled`）。
5. `SaveLevel` / `RemoveSeeingAgentKeys` 支持 `Boot`/`CapabilitySets` null 删除键。
6. 回滚：git revert；配置需人工回旧字段。

## Open Questions

- （已决）未配置 `Boot` 时默认 `*`；`HostDefaultBoot` 可覆写（如 Embed→`minimal`）。
- （已决）未知 Boot → `SettlementException`。
- （已决）不保留 `Modules.Enabled` 结算语义。
- （已决）Scenario 不引用 CapabilitySet；各自 Modules，运行时求交。
- （已决）CapabilitySet 用 `Disabled`（不用 `exclude`）；`Modules.Disabled` 全局黑名单保留。
- （已决）进程 BoundSeams = `Seams` + `HostDefaultSeams`；Scenario.Seams 降为会话语义。
- （已决）热重载按节分流；`Scenarios` 不触发 boot diff。
- （已决）`["*"]` 在解析层展开，不进字面交。
