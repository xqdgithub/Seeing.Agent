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

1. **`tool_search` 的检索参数不来自 `Tools.Exposure`**：能力包不得依赖 Core 的 `SeeingAgentOptions`，故三档 detail 默认值、limit 上限、full 硬上限 3 以**工具内常量**实现（10 / 30 / 3，detail 默认 summary）。原先计划但从未接线的 `Tools.Exposure.Search*` 四个字段已**删除**（避免"配置可见而无效"）。如需用户可调，需另立 Abstractions 级配置契约或由 Hosting 注入适配器。
2. **`tool_search` 语料单源经 `IToolSearchProvider`→resolver 再结算**：检索时重新解析会话 catalog（一次性开销；search 为模型偶发调用，可接受）。升级粘滞使重算幂等。
3. **发现下一轮生效**：`detail=full` 当轮不可直接调用命中工具，需下一轮。模型侧文案已明示。
4. **`tool_search` 无专属配额**：连续调用计入既有 `MaxSteps`；`LoopDetector` 按 (tool,argsHash) 判定，检索参数各异不误触发。
5. **`ToolSearchEvent.ExecutionId/LoopId` 暂空**：`ToolContext` 未携带二者，宿主接线（`AgentExecutor`）可后续补。
6. **运行期降幅度量（spec 17.1–17.3）需实运验证**：51 内置工具场景 input token 降幅 ≥60%、50-MCP 仿真 ≥85% 属真实 LLM 会话基线对比，本环境不含 live provider，未产出数字，**不臆造**；由 `baseline.md` 流程在联调环境记录。自动化侧已用「前缀稳定 / 升级只一次不回退 / 目录不含权限 / full 硬上限3 / 分页 total-next」等确定性测试锁定行为。
7. **检索语料为全量 deferred（不受显示预算裁剪）**：`tool_search` 经 `IToolSearchProvider` 取 resolver 的 `DeferredEntries`（全量），显示目录（`CatalogNamespaces`）另做命名空间预算裁剪且**每命名空间恒出现含总数、0 展示仍列出**。二者正交，预算只影响"展示哪些"，不影响"可发现哪些"。
8. **目录 `tool_search` 指引条件化**：当本轮可用集不含 `tool_search`（如宿主未启用 `tool-discovery` 模块）时，目录正文不出现"先用 tool_search 检索"文案，避免指向不存在的工具。宿主若启用分层暴露应在能力集内挂载 `tool-discovery`。
9. **配置容错不对称**：Agent 层 `Exposure` 为强类型枚举，其 JSON 绑定大小写敏感（写错值在配置反序列化期报错）；会话/场景/全局字符串层为忽略大小写 + 非法值回退不抛。字符串层承载者（`PolicyMode`）统一 ignoreCase；Agent 枚举层如需 JSON 大小写容错需全局配置 `JsonStringEnumConverter(ignoreCase)`（本次未改全局）。
10. **MCP server/tool 名不得含 `__`**：id 归一 `mcp__{server}__{tool}` 的双下划线为分隔约定；若 server 或 tool 名本身含 `__`，`DeriveNamespace` 按首个 `__` 切分会归错命名空间。应由连接配置侧校验拒收含 `__` 的命名。
11. **分层依赖检索入口的安全钳制**：若本轮可用集不含 `tool_search`（模块未挂载，或被 Agent 白名单/会话禁用排除），分层会降级为全内联并记 Debug（避免无检索入口宿主刷屏），防止"工具被藏进目录却无检索手段"的静默能力损伤。全量能力集宿主均已挂载 `tool-discovery`（WebUI、Cli、Tui、Embed.Demo）；`Gateway.Server` 为精简工具集宿主、未挂，靠钳制兜底；自定义宿主接全量工具集时应自行挂载。
12. **`tool_search` 每次调用重算会话结算**：检索语料经 `IToolSearchProvider`→resolver 现算（保证单源、无额外持久化 store），因此 `tool_search` 的调用会在该时点重写 `CachedSchemaTokens/CatalogTokens` 与 `ExposureEscalated`（同 session/agent/配置下幂等；跨 agent 并发或热重载期间的短暂口径差异属已知边界）。真正的"turn 级只读快照"需新增结算快照载体，留作后续演进。
13. **目录签名为 `id(截断描述)`，不含参数列表**：spec 示例的 `(path, offset?, limit?)` 形式需 `ToolMeta` 携带参数摘要，当前未采；目录用途为"可发现性索引"，参数详情由 `detail=full` 检索获取。
14. **`tool_search` 空查询返回按 id 排序的全部条目**（而非"命名空间+计数概览"）；有 query 时零分过滤、命名空间计数在未命中时给出。0-shown 命名空间仍以标题行 `### ns (N tools, 0 shown)` 出现（见 §6.7 与目录命名空间契约）。
15. **`SessionData.DiscoveredTools` 并发访问已收敛为加锁 API**：读走 `GetDiscoveredToolsSnapshot()`、裁剪走 `ReplaceDiscoveredTools(...)`、追加走 `AppendDiscoveredTools(...)`（内部专用 `_discoveryGate`）；resolver 与 `SessionToolDiscoveryStore` 均改用这些方法，消除"快照替换 vs 原地追加"的枚举异常/丢项窗口。直接读写公开属性 `DiscoveredTools` 仅供序列化与测试。

## 7. 回退

`Tools.Exposure.PolicyMode = "All"` → 全量内联、无目录、无 `tool_search`（该模式下被标 Hidden），`tools[]` 与改造前逐工具一致。MCP id 变更无回退（破坏性）。

## 8. 测试

新增/关联：`ToolManagerSchemaSettlementTests`、`ExecutionRunnerResolverInjectionTests`、`ToolTokenAccountingTests`、`SessionToolSchemaResolverExposureTests`、策略/目录/配置/发现存储单测（Hosting）、`PromptBuilderPlacementTests`、`ToolCatalogSectionContributorTests`、`SchemaSnapshotEventTests`、`ToolSearchToolTests`、MCP 命名/注册测试。全量 `dotnet build Seeing.Agent.slnx` = 0 error；测试仅 `InstructionManagerTests.InjectIfNeededAsync_FirstDiscovery` 因读真实 `~/.agents/AGENTS.md` 属基线环境依赖失败，与本变更无关。
