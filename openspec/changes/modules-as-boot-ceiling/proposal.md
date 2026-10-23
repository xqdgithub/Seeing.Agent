## Why

进程 `Scenario` 同时承担「工作模式」与「模块启动天花板」，域模型纠缠：会话无法在更大能力集上切换，且无法干净表达「启动档」与「对话档」。重构一步到位：用正交的 **CapabilitySet（能力集）** 与 **Scenario（工作模式）** 替换过载语义，严格两层（模块层 / 工具层不混），命名全局统一，不做渐进折中。

## What Changes

- **BREAKING**：引入一等配置 `CapabilitySets`（含 `Modules` + 可选 `Disabled`，**仅模块层**）与进程指针 `Boot`（能力集名或 `*`）。进程 Activate/Deactivate **只**由 `bootEnabled` 决定：
  - `Boot = "*"` → `bootEnabled = Available − Modules.Disabled`
  - `Boot = name` → `bootEnabled = (CapabilitySets[name].Modules ∩ Available) − CapabilitySets[name].Disabled − Modules.Disabled`
  - `Modules` 可为 `["*"]` 表示全 `Available`（在解析层展开，不进字面 `IntersectWithAvailable`）
- **BREAKING**：`Scenario` / `Scenarios` 回归纯工作模式，**保持自带 `Modules` 列表**（不引用 CapabilitySet，避免外键耦合）；附带 `DefaultAgent` / `Tools.Disabled` / `Seams`。Scenario **不再**充当启动天花板。
- **BREAKING**：主路径废弃 `Modules.Enabled` 当启动 base；沙箱/缩能力改为换 `Boot` 或编辑 `CapabilitySets`。保留 `Modules.Disabled` 作为 boot 全局黑名单（模块层）；`Modules.Enabled` 若仍出现 → 警告忽略。
- **进程级 BoundSeams 解析重定义**：进程 seam 绑定**只**来自 `SeeingAgent.Seams`（用户覆盖）+ `HostDefaultSeams`（宿主默认）；Scenario.`Seams` 降为**会话/文档语义**，不驱动进程 Activate 或进程 seam 绑定。
- 进程字段 `Scenario`：**仅**新会话默认工作模式名（及 session 空回退），**不**驱动 Activate、**不**驱动进程 seam 绑定。
- 严格两层（对齐现有 `SettlementEngine` / `SessionSettlement` 实现）：
  - **模块层**（求交、Activate，操作 module id）：`sessionModules = scenario.Modules ∩ bootEnabled`（再 ∩ `session.ScenarioOverride.Modules.Enabled`，若有）
  - **工具层**（展开、裁剪、schema，操作 tool id）：`settledToolIds = ⋃ sessionModules.ProvidedTools − scenario.Tools.Disabled − session.Tools.Disabled − Modules.Tools.Disabled`；`schema = registry ∩ settledToolIds ∩ Agent AllowedTools − DeniedTools`
  - `Tools.Disabled` **只**在工具层生效，**不**进模块层公式。
- 热重载按节分流：`Boot` / `CapabilitySets` / `Modules.Disabled` / `Seams` / available 变更 → 再结算装卸；`Scenarios` / 默认 `Scenario` 名变更 → **不**触发 boot Activate diff（只刷 `IScenarioCatalog` / OptionsMonitor）。
- 侧栏等进程 chrome：跟 `bootEnabled`（Requires），**不**按默认 Scenario 名硬藏已挂载模块入口。
- 内置：抽出 `BuiltInCapabilitySets`（boot 模块列表 SSOT）；`BuiltInScenarios.Modules` **编译期引用** CapabilitySet 常量，防漂移。
- 启动切换：配置 `Boot` 或宿主参数/环境变量覆盖（`--boot` / `BootOverride`），优先级 `BootOverride > 文件 Boot > HostDefaultBoot`。
- 会话结算场景解析与 `IScenarioCatalog` 同源（不再 BuiltIn-only）；文档与设置 UI 按两轴 + 两层重写。

## Capabilities

### New Capabilities

- `capability-set-boot`：能力集目录（`Modules` + `Disabled`，仅模块层）、Boot 指针、`["*"]` 解析、bootEnabled 结算与 Activate 边界、进程 BoundSeams 绑定来源、启动参数覆盖优先级。
- `session-scenario-filter`：工作模式自带 Modules、在 bootEnabled 上求交（模块层）、工具层展开裁剪、进程 Scenario 仅默认名、Catalog 同源、进程 chrome 跟 bootEnabled。

### Modified Capabilities

- （无既有 `openspec/specs/`；本变更为新 specs + 架构文档同步。）

## Impact

- **Core**：`SettlementEngine`（boot 公式重写、`["*"]` 解析、BoundSeams 来源改 Seams+Host）、`ScenarioDefinition`/`ScenarioConfig`（保持自带 Modules）、新建 `CapabilitySetDefinition` + `ICapabilitySetCatalog`、`BuiltInCapabilitySets`、`ModuleSettlementReloadHandler`（节分流）、`SeeingAgentOptions`（+`Boot`/`CapabilitySets`）、`ConfigSectionRegistry`（登记新节）、`SettlementInput`/`SettlementResult`（Boot 字段）、`InitializeSeeingAsync`、单测。
- **Hosting**：`SessionSettlement`（保持两层分离）、`ExecutionJobService`（传 `IScenarioCatalog`）、会话创建默认 Scenario。
- **WebUI / Host Shape**：设置页（Boot+能力集 vs Scenario）、侧栏可见性（去 Scenario 名硬过滤）、可选 CLI `--boot`、`HostDefaultBoot`/`HostDefaultSeams`。
- **配置迁移**：旧「进程 Scenario=minimal 当沙箱」→ `Boot=minimal`；旧 `Modules.Enabled` → 自定义 CapabilitySet + `Boot`；`ToolsDisabled` 扁平字段 deprecate，统一 `Tools.Disabled`。
- **文档**：`docs/architecture/01`、`03`（§3/§4 公式、§5 热重载表、§8 禁止清单、§9 排障）、`05` 示例全面替换；`03 §4` 修正既有 tools 进模块层错误。
