# 19 · 工具上下文预算与分层暴露 Release Notes

**变更:** `tool-context-budgeting`（openspec）
**落地时间:** 2026-10-21
**范围:** 工具 schema 结算成本优化 —— 分层暴露（inline / 目录 deferred）+ `tool_search` 按需发现 + 每轮暴露可观测 + MCP 工具 id 归一。

---

## 1. 一句话

默认 `auto`：小工具集会话保持全内联（零额外往返），大工具集（≥30 工具或 schema ≥8000 token）自动升级为分层——只内联 pinned / 已发现 / `tool_search`，其余进**目录**（系统提示里的轻量清单），模型用 `tool_search` 检索后**下一轮**生效。每轮 `tools[]` 与目录 token 计入可观测（`SchemaSnapshotEvent` / `SourceBreakdown`）。

## 2. 破坏性变更（必读）

1. **MCP 工具 id 归一为 `mcp__{server}__{tool}`，零兼容。** 旧 `mcp_{server}_{tool}` 形式不再接受，不做别名、不做迁移告警。所有权限规则、`/mcp list`、`Allowed/DeniedTools`、`IsToolEnabled` 一律按新 id 匹配。含 `_` 的 server/tool 名不再产生歧义碰撞（`a_b`+`c` 与 `a`+`b_c` 的 id 不同）。
2. **同名 MCP server 重复注册改为报错。** 原 `McpToolRegistry` 静默覆盖；现跨连接抢占 server 名抛 `McpDuplicateServerException`。同 owner 重连仍允许（覆盖语义，与 `ToolManager` 重复 id 覆盖一致）。
3. **默认暴露策略由"全量内联"变为 `auto`。** 51 内置工具场景（约 15–35k token）默认即触发升级，首轮 `tools[]` 显著变小、目录分节出现。前缀缓存有一次性失效成本（可接受）。设 `Tools.Exposure.PolicyMode = "All"` 即回到改造前全内联行为（回退开关）。

## 3. 配置（`~/.seeing/seeing.json` 或 `.seeing/seeing.json` 的 `Tools.Exposure`）

| 键 | 默认 | 说明 |
|---|---|---|
| `PolicyMode` | `Auto` | `All` / `Auto` / `Tiered`。`All` 全内联（回退）；`Tiered` 恒分层；`Auto` 阈值+粘滞升级 |
| `PinnedTools` | `[]` | 恒内联工具 id（参与 inline 顺序：pinned 声明序最前） |
| `CatalogBudgetChars` | 2000 | 目录整体字符预算（按字符，非 token；参照 opencode catalog.ts） |
| `CatalogLineMaxChars` | 120 | 目录单行摘要截断上限 |
| `EscalateToolCount` | 30 | auto 升级阈值：可用工具数 |
| `EscalateSchemaTokens` | 8000 | auto 升级阈值：全内联估算 token |
| `SearchDetail` / `SearchDefaultLimit` / `SearchMaxLimit` / `SearchFullMaxLimit` | `summary`/10/30/3 | **当前未被 `tool_search` 消费**（见 §6 已知边界） |

四级优先级：会话 `ScenarioOverride.Tools.PolicyMode` > Agent `Exposure` > Scenario `Tools.PolicyMode` > 全局。字符串层大小写不敏感解析；非法值记 Warning 并回退下一优先级层，绝不抛。

## 4. 架构不变量

- **schema 单点**：唯一计算在 `SessionToolSchemaResolver`（Hosting），`SettleAsync` 为私有核心，仅经端口 `ISessionToolSchemaResolver.ResolveAsync`（`sessionId` / `SessionData` 双重载）访问。`ExecutionRunner` 必需注入端口、无回退构造。主链与 Scheduler 旁路同源。
- **单轮 schema 不变**：一次执行内 `tools[]` 一经结算不再变；`tool_search` 的 `detail=full` 只写发现态，**下一轮**才并入 inline（对齐 dsh native）。无同轮换 schema、无 `ToolResult.Metadata` 执行控制信号。
- **exposure ≠ permission**：分层只决定"是否进本轮 schema / 目录"，不决定"能否调用"。发现后的工具经普通执行链走完整权限审批（无绕过）。目录正文只描述可发现性与下一轮，不含任何权限裁决语。
- **token 归属单点**：内联定义 token 与目录 token 由 resolver 一次结算产出，写 `SessionData.CachedSchemaTokens`/`CachedCatalogTokens`（与 `CachedInputTokens` 同族：`ClearMessages`/`ClearUsageCache` 清、`Clone` 继承、Fork 不继承）。`TokenBudgetManager.CalculateBreakdown` 读该缓存填 `SourceBreakdown.ToolDefinitions`/`Catalog`（**已移除无人填的 `toolTokens` 形参**）。无跨包 `AgentContext.Metadata` 通道。
- **升级单调粘滞**：`SessionData.ExposureEscalated` 一旦置位不回退（fork 继承，`ClearMessages` 不清）。

## 5. 可观测

`SchemaSnapshotEvent` 新增 `ExposureMode` / `InlineToolCount` / `DeferredToolCount` / `EstimatedDefinitionTokens` / `CatalogTokens`。`SourceBreakdown` 新增 `Catalog`（与 `ToolDefinitions` 分列）。resolver 结算记 Debug 度量日志。`ToolSearchEvent`（`tool.search`）记录检索动作。

## 6. 已知边界

1. **`tool_search` 不消费 `Tools.Exposure.Search*`**：能力包不得依赖 Core 的 `SeeingAgentOptions`，故三档 detail 默认值、limit 上限、full 硬上限 3 以**工具内常量**实现（10 / 30 / 3，detail 默认 summary）。如需用户可调，需另立 Abstractions 级配置契约或由 Hosting 注入适配器。
2. **`tool_search` 语料单源经 `IToolSearchProvider`→resolver 再结算**：检索时重新解析会话 catalog（一次性开销；search 为模型偶发调用，可接受）。升级粘滞使重算幂等。
3. **发现下一轮生效**：`detail=full` 当轮不可直接调用命中工具，需下一轮。模型侧文案已明示。
4. **`tool_search` 无专属配额**：连续调用计入既有 `MaxSteps`；`LoopDetector` 按 (tool,argsHash) 判定，检索参数各异不误触发。
5. **`ToolSearchEvent.ExecutionId/LoopId` 暂空**：`ToolContext` 未携带二者，宿主接线（`AgentExecutor`）可后续补。
6. **运行期降幅度量（spec 17.1–17.3）需实运验证**：51 内置工具场景 input token 降幅 ≥60%、50-MCP 仿真 ≥85% 属真实 LLM 会话基线对比，本环境不含 live provider，未产出数字，**不臆造**；由 `baseline.md` 流程在联调环境记录。自动化侧已用「前缀稳定 / 升级只一次不回退 / 目录不含权限 / full 硬上限3 / 分页 total-next」等确定性测试锁定行为。

## 7. 回退

`Tools.Exposure.PolicyMode = "All"` → 全量内联、无目录、无 `tool_search`（该模式下被标 Hidden），`tools[]` 与改造前逐工具一致。MCP id 变更无回退（破坏性）。

## 8. 测试

新增/关联：`ToolManagerSchemaSettlementTests`、`ExecutionRunnerResolverInjectionTests`、`ToolTokenAccountingTests`、`SessionToolSchemaResolverExposureTests`、策略/目录/配置/发现存储单测（Hosting）、`PromptBuilderPlacementTests`、`ToolCatalogSectionContributorTests`、`SchemaSnapshotEventTests`、`ToolSearchToolTests`、MCP 命名/注册测试。全量 `dotnet build Seeing.Agent.slnx` = 0 error；测试仅 `InstructionManagerTests.InjectIfNeededAsync_FirstDiscovery` 因读真实 `~/.agents/AGENTS.md` 属基线环境依赖失败，与本变更无关。
