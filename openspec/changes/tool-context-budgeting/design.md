## Context

`tools[]` 是每轮 LLM 请求的固定前缀。当前实现把会话结算出的全部工具（内置 51 个 + 任意数量 MCP）完整注入，形成 15K–35K input tokens 的固定成本，并随 MCP 线性增长。调研（`docs/superpowers/specs/2026-10-21-tool-context-economy-research-and-design.md`）确认业界有四条主线：服务端延迟加载（Claude）、客户端目录+检索（opencode）、代码模式（Cloudflare/PTC）、会话内工具生命周期（DeepSeek dsh）。本项目多 Provider、无服务端 `defer_loading`，必须纯客户端实现。

现有架构已具备关键前提：schema 计算单点在 `src/hosting/Seeing.Agent.Hosting/Execution/SessionToolSchemaResolver.cs`（`SettleAsync`），注入单点在 `AgentExecutor.BuildRequestAsync`（`context.ToolSchemas` → `ChatRequest.Tools`），结算过滤已有四层。因此新增能力应是"**在单点之后插入裁剪阶段**"，而非另起计算路径。

约束：
- Core 瘦脊柱（零工具）；能力经 `AddSeeingModule`；契约在 Abstractions 零实现。
- `AllowedTools` / `DeniedTools` 同时承担权限白名单语义，不能挪用为可见性开关。
- 会话持久化需向前兼容；`SessionForker` 逐字段复制，新增会话字段必须显式同步。
- WebUI 禁用 CPM。

## Goals / Non-Goals

**Goals**

1. 消除 schema 生成的重复与浪费（先交集后生成、稳定排序、缓存、token 计量、依赖接口化）。
2. 引入分级延迟加载，使 51 内置工具与大规模 MCP 场景的固定定义成本降 60%–85%。
3. 提供可插拔的发现通道（检索元工具 + 目录分节），发现态在本会话内持久、可继承。
4. 默认 `auto`：达到阈值即自动启用分级，无需人工配置即获得收益。
5. 让每次请求的上下文成本可观测（inline/deferred 计数、定义 token、目录 token）。

**Non-Goals**

- 不实现 Code Mode（见 Known Limitations：设计与实现均不在本变更范围）。
- 不依赖任何 Provider 服务端 `defer_loading` 或非标准协议字段。
- 不改变权限判定链、审批通道、Hook 语义。
- 不引入进程/容器沙箱。
- 不实现**轮内** schema 刷新（发现结果自下一轮生效，见 D11）。
- 不实现目录增量注入与目录落盘。

## Decisions

### D1：策略插入点在 SessionToolSchemaResolver，位于 schema 生成之后、返回之前

**选择**：在 `SettleAsync` 末尾接入暴露策略，产出 `ToolExposurePlan`，并与会话发现态合并后返回。

**理由**
- `ExecutionRunner` 与 Scheduler 旁路（`AgentJob` / `HeartbeatJob`）都经该出口，改一处即全覆盖。
- `AgentExecutor` 零改动，杜绝"executor 二次计算 schema"的反模式。
- 策略只消费已生成的 schema 目录，不触碰结算公式。

**备选与弃用**
- 在 `ToolManager` 内做裁剪：会让"工具可见性"耦合进注册表，且 Scheduler 旁路需重复实现。
- 在 `AgentExecutor` 做裁剪：破坏单点注入，任何新增执行入口都会漏。

### D2：策略必须是纯函数，禁止访问 DI / 会话 / 存储

**选择**：`IToolExposurePolicy.Plan(exposureInput) → ToolExposurePlan`，输入是一个**不可变快照**，包含：
- 已生成的 `FunctionToolSchema` 列表（唯一真相源）
- 每个工具的元信息（id / namespace / pinned / 单行签名 / 截断摘要 / 估算 token）
- 会话发现态集合
- 生效配置

策略实现 **MUST NOT** 注入 `IToolManager`、`ISessionManager`、任何 Store。所有 I/O 由 resolver 在调用策略前完成。

**理由**
- 若策略能读 `IToolManager` 自行取描述，就绕过了 `ToolManager.GetToolSchemasAsync` 的 `IsToolEnabled` 过滤与 `tool.definition` Hook（`ToolManager.cs:294-321`），形成第二个真相源。
- 纯函数可无条件单测，不需要 DI 搭建。

**备选与弃用**
- 策略链数组（`IEnumerable<IToolExposurePolicy>`）：全案只有一个分级实现 + 一个全量实现，"多策略合并时谁的 inline 集合赢"无定义，语义空洞。改为**单接口 + 按 Mode 分派**。

### D3：默认 `Mode=auto`；`auto` 用双条件 OR + 单调升级

**选择**：`Tools.Exposure.Mode` 默认 `auto`。进入条件为两个正交条件取"或"：

```
进入 tiered：  availableToolCount >= 30   OR   schemaTokens >= 8000
退出：        不自动退出（会话内单调升级），仅用户显式改 Mode 或新建会话重新判定
```

**理由**
- **默认 `auto` 而非 `all`**：51 个内置工具（15–35k token）远超 8000 阈值，默认即进入分级，用户无需配置即获得收益。代价是首次升级后所有会话的 `tools[]` 变一次、前缀缓存失效一次，这是可接受的一次性成本。
- **为什么要双条件**：数量与成本是两个正交的触发理由，任一单独判据都会漏。
  - 30 个工具：Anthropic 官方口径"选择精度在 30–50 个工具后开始下降"——精度依据，与 token 无关。30 个各 100 token 的相似工具总价仅 3k，纯 token 阈值会漏判。
  - 8000 token：成本依据。51 个内置工具约 15–35k 稳定触发；`explore`（约 11 工具 / 4.4k）稳定不触发，落在两个真实 Agent 之间的安全区。
- **为什么单调升级而不是滞回**：滞回带（进 30 / 退 24）只能降低翻转频率，边界附近仍会翻。而"进入后不自动退出"是**结构上不可能翻转**——MCP 中途连接导致 5 → 45 个工具时，只发生一次 `all → tiered` 升级，`tools[]` 前缀只变这一次。
- **粘性状态存哪**：`SessionData.ExposureEscalated`（`bool`），随会话持久化。跨进程重启保持；fork 继承。

**与 Claude Code 的差异**：Claude Code 用 `auto:N`（默认上下文 10%），面向 200k 窗口；本项目 200k × 10% = 20k，恰落在 51 内置工具区间内会导致翻转，故改用固定双阈值。

### D4：pinned 集合由配置声明，工具自身只能"建议"

**选择**：pinned 由 `Tools.Exposure.PinnedTools` 与 Agent 级覆盖决定；工具/服务可通过能力标记建议常驻，但最终由配置裁决。

**理由**
- Claude Code 明确允许配置方覆盖 server 的 `alwaysLoad` 声明，避免上游滥用常驻。
- 常驻集合直接影响每次请求成本，属部署决策而非组件自决。

### D5：发现态存会话、只增不减、fork 显式继承、消失求交

**选择**：`SessionData.DiscoveredTools`（`List<string>`）随会话持久化；`SettleAsync` 读取并与可用集求交后合并进 `inline`；排序为 `pinned（声明序）+ discovered（发现序）`，append-only。
**无容量上限、无 LRU 淘汰**——上限会与"只增不减"语义冲突，且需要额外的使用时间戳字段。

**理由**
- 发现态若每轮重排，会反复打破 provider 前缀缓存，收益归零。
- 只增不减保证"模型已经知道的工具不会突然消失"，避免重试风暴。
- 求交处理 MCP 断开/模块停用，不产生错误。

**`SessionForker` 必须显式复制**：`SessionForker.cs:48-53` 是逐字段复制，不新增复制逻辑则 fork 后静默丢失发现态，D5 的继承场景直接失效。必须在 fork 处加一行复制并加不变量测试。

**会话重置**：`SessionData.ClearMessages()` 不清发现态（发现态是工具可见性，不是对话内容）。

### D6：检索工具合并为单一 `tool_search`，三档 detail

**选择**：单一工具，`detail=name|summary|full`，默认取配置（`summary`）；仅 `full` 写入发现态。不提供独立的 `tool_load`。

**理由**
- Anthropic 明确建议 detail level，让模型自行决定花多少 token。
- 业界无 search+load 双工具先例（Anthropic 只有 `tool_search`，opencode 只有 `search`）。
- 少一个工具 = 少一份常驻 schema（约 100–150 token/轮），也少一个模型选错的入口。
- `name` 档位用于"确认存在性"；`summary` 用于评估；`full` 才代表"我要用"。

### D7：检索采用 offset 分页，优先引导缩窄而非翻页

**选择**：`tool_search` 接受 `offset`（默认 0）与 `limit`（默认 10），返回 `total` / `returned` / `remaining` / `next`。`detail=full` 单页**硬限 3**。结果尾部在 `remaining > 0` 时**优先**建议缩窄，附 `offset=M` 作为兜底。

**理由**
- **必须有分页**：否则模型看到"还有 10 条"却无法继续，等于信息黑洞。
- **`total` 是关键字段**：只有区分"命中 2 条"与"命中 12 条但被截断"，模型才能判断"该翻页"还是"该换词"。
- **为什么优先引导缩窄**：本项目 `tool_search` 是独立工具，**翻一页 = 一次完整 LLM 往返 + 全量上下文重发**；opencode 的 `search` 是解释器内同步函数，翻页零成本。直接照搬会放大成本。
- **`full` 硬限 3**：`full` 每条含完整 schema（500–1500 token），`limit=10` 则单页 15k token，比全量暴露还贵。
- **offset 而非 cursor**：实现简单、模型只需递增整数；工具集变动频率低，offset 偏移问题影响有限。

**排序算法**（确定性）：tokenize（驼峰拆分、小写、`s`/`es` 词形还原）→ 打分（精确 path 20 / path 单词 12 / path 子串 8 / 描述子串 4 / 全文 2）→ 零分过滤 → 分数降序、同分按 id 字典序。

### D8：MCP id 归一为 `mcp__{server}__{tool}`，直接改名、零兼容

**选择**：统一命名，**不保留**旧 id、**不做**别名映射、**不做**旧 id 检测或转换、**不做**配置迁移提示。同步修正**全部** id 生产点与消费点。

**必须修改的生产点**（不仅是 `McpTool.Id`）：
- `McpTool.cs:18` — `Id => $"mcp__{_serverName}__{_realName}"`
- `McpConnectionCoordinator.cs` 中三处 id 拼接（约 `:164`/`:329`/`:570`），它们生成注册表键与 ToolCall 名
- `McpToolRegistry` 的键与 Hook payload

**必须核对的消费点**：工具禁用集匹配、Agent 白/黑名单、权限规则 `PermissionKind.Tool` 的 resource 匹配、`/mcp` 列表判定、目录命名空间推导。

**理由**
- `{server}_{tool}` 存在拼接歧义（`a_b`+`c` vs `a`+`b_c`），且与命令层 `mcp_` 前缀判定不一致（当前 `/mcp list` 失效）。
- 双下划线分隔是社区事实标准（Claude Code、opencode 均如此），模型对它有先验。
- 兼容层会形成两套 id 长期并存的技术债。

**后果（明确接受）**
- 用户配置中旧 MCP 工具 id 立即失效：禁用列表不再匹配（工具重新可见）、`AllowedTools` 条目不再匹配（工具被排除）、`DeniedTools` 条目不再匹配（工具重新可用）、权限规则中 `PermissionKind.Tool` 的 resource 不再匹配（**回落到默认效果，可能从 Ask 变为 Allow**）。
- 代码中不留任何 `if (legacy)` 分支、不留别名映射、不留启动期扫描。
- 唯一交代：Release Notes 提供机械替换规则与需要检查的四处配置位置。
- **本项不可回滚**：代码回退才可恢复。

**同名 server 重复注册语义**：当前 `McpToolRegistry` 为静默覆盖。本变更改为**注册失败并给出明确错误**，已注册工具不受影响。这是行为变更，须在 Release Notes 列明。

### D9：token 计量以 SessionData 缓存字段为归属，不做跨包通道

**选择**：schema 结算产出的估算 token 写入 `SessionData.CachedSchemaTokens` 与 `CachedCatalogTokens`——与既有 `CachedInputTokens` 同属"每轮 token 标量"，同受 `ClearMessages`/`ClearUsageCache` 清理与 fork 逐字段复制约束。`TokenBudgetManager.CalculateBreakdown(session)` 内部直接读这两字段填 `SourceBreakdown.ToolDefinitions`/`Catalog`；**移除** `CalculateBreakdown` 上无人能填的 `toolTokens` 形参。估算复用 `ITokenCounter`（`CharBasedTokenCounter`），`Seeing.Agent.Hosting` 通过 ProjectReference 引用原语层 `Seeing.TokenEstimation`。

**理由**
- 每轮 token 标量**已有归属先例**（`CachedInputTokens`，`SessionData.cs:175-191`）；沿用同一归属，避免为搬一个整数新增跨包魔法字符串通道。
- 旧「`ChatExecutionContext` 字段 → `AgentContext.Metadata` → 能力包读串」在缝合处**根本不成立**：`BudgetUpdateHook` 在 `ChatAfterComplete`（`LlmService.cs:418-428` 触发）执行，其 payload 仅 `{modelId,messageId,response}`，无 `AgentContext`；强行接线还要再往 hook payload 开口，属补丁叠补丁。
- 单一计算点：resolver 结算时估算**一次**，写 session（预算出口）与 `SchemaSnapshotEvent`（可观测出口）皆取同一次结果，不二次估算、不分叉口径。

**口径分离**：工具定义与目录分列——目录不进入 `ToolDefinitions`，单独计入 `SourceBreakdown.Catalog`（新增）。避免"工具定义看起来变小了"的误读。

### D10：缓存键显式包含 Hook 版本

**选择**：schema 缓存键 = 目标 id 集 + Agent 白/黑名单 + 禁用集版本 + `tool.definition` Hook 版本。

**理由**
- `tool.definition` Hook 可改写 description/parameters，是有副作用的扩展点；不纳入键会导致缓存吞掉用户改写。
- 提供 `cacheable=false` 能力标记作为逃生舱。

**前置条件**：`IHookManager` 当前**没有** `Version` 成员（`IHookManager.cs` 全部成员已核对）。需新增 `long Version { get; }`，在 `Register`/`RegisterMulti`/`Remove`/`Clear` 时递增。该属性是只读观测，不改变 Hook 语义。

### D11：发现结果自**下一轮执行**生效，不引入轮内刷新

**选择**：`tool_search`（`detail=full`）只做两件事：写入会话发现态、返回完整定义文本。**不写任何执行控制元数据**。发现态在下一次 `SettleAsync` 时自然合并进 `inline`。模型侧通过 `tool_search` 的返回文本明确得知"新工具将在下一轮可用"。

**理由（这是本设计最重要的取舍）**

曾考虑"轮内生效"（对齐 Claude 服务端 `tool_reference` 即时展开），但经架构审查否决，原因如下：

1. **信号载体错位**：轮内生效需要在 `ToolResult.Metadata` 写标记，由 `AgentExecutor` 读取并触发 schema 重取。但 `ToolResult.Metadata` 的既有消费者是 UI 投影（`ChatEventTracker.cs:145-151` 仅做展示映射），`ToolCapabilityKeys` 的定位是 `IToolCapabilities` 的能力声明键、由装饰器消费。用展示通道承载执行契约，是字符串魔法补丁。
2. **击穿三条既有不变量**：
   - `AgentContext.cs:65-68` 注释："schema 由 ExecutionRunner 唯一注入；executor 不得计算"
   - `AgentExecutor.cs:565` 同义声明
   - `ExecutionRunner.cs:245-259` 在执行前冻结并落盘 `SchemaSnapshotEvent`；轮内换 schema 后"已发送"与"已记录"不一致，击穿 "model-visible means logged"。
3. **最小改动即最小风险**：下一轮生效使 `AgentExecutor` 真正零改动，删掉整个重取端口、降级分支、幂等约束。

**接受的代价**：模型在同一轮内无法使用刚搜到的工具，需要多一次用户/调度触发。实际使用中模型可能"搜到就试"，得到未知工具错误后纠正。缓解：`tool_search` 返回文本明确写"这些工具将在下一轮可用，请在本轮结束时告知用户/等待下一轮"。

**对齐依据**：DeepSeek dsh 的 native 模式正是此语义（`tool-addition` 作为 developer 消息，下一次请求才生效）。

### D12：目录走系统提示分节，并重构分节注册机制

**选择**：目录通过 `IPromptSectionContributor` 注入为 `## Tool Catalog` 分节。**同时重构** `PromptBuilder` 的分节定位机制：

- `IPromptSectionContributor` 新增 `Heading`（分节标题）与 `Placement`（`AppendAtEnd` 或 `After(sectionName)`）
- `PromptBuilder` 删除硬编码的 `SectionAnchors` 字典，改为按 `Placement` 排序的纯渲染器

**理由**
- 目录是**文本**，不参与 function-calling 语法；避免模型把目录条目误当可调用函数。与既有 `## Skills` 同构（`SkillManager.cs:75-97` 只渲染 name+description）。
- `PromptBuilder.cs:19-26` 的 `SectionAnchors` 是硬编码的 4 个键（tools/skills/agents/environment），`InjectSectionsAsync:80-81` 未登记即 `continue`（**静默丢弃**）。这违反"Core 瘦脊柱"（Core 不应为每个能力包新增的分节改代码），且属于"静默吞掉"反模式。
- 直接"再加一个键"是补丁。正确做法是让 contributor 自述放置契约，builder 纯粹按契约渲染。

**备选与弃用**
- 给 `PromptContext` 加 `ToolCatalogEntry[]` 字段：会让 Abstractions 的核心契约被 exposure 语义污染。改为给 `PromptContext` 加通用 `Sections: IReadOnlyDictionary<string,string>`，由 Hosting 填。

### D13：策略与 resolver 的职责边界

**选择**：
- resolver（`SessionToolSchemaResolver`）一次产出**完整结算产物**：`ToolSchemas` + `ToolCatalogEntry[]` + 计数 + token 估算 + 是否升级。
- 策略是纯函数（D2），只做"分配模式 + 预算裁剪"。
- resolver 不因本变更新增依赖类型（仍为 catalog / toolManager / options / scenarioCatalog / discoveryStore / policy）。

**理由**
- 避免 resolver 变成上帝对象（现状 5 职责 / 4 依赖，`SessionToolSchemaResolver.cs:75-141`）。
- 元信息由 resolver 在生成 schema 的同一次遍历中产出（复用 `ToolManager` 已有的 `Description` / `ParametersSchema` 读取，以及 `tool.definition` Hook 结果），策略不再二次读取。

## Risks / Trade-offs

| 风险 | 说明 | 缓解 |
|---|---|---|
| 检索未命中导致模型放弃 | description 质量差或命名混乱 | 空结果返回命名空间清单；目录标注 "N tools, M shown"；description 首行规范为"路由句"；`full` 命中自动发现 |
| 冷门工具首次使用多一轮 | 需要先检索 | 保留 3–8 个高频 pinned；阈值回退让小工具集不延迟 |
| 前缀缓存被破坏 | 发现集重排或工具顺序抖动 | 稳定排序 + 发现态 append-only + 单调升级；工具移除只发生在整体重建 |
| 权限被误认为被放松 | 延迟/隐藏被误解为放权 | 明确 spec：exposure 只影响可见性；执行仍走 `IToolPermissionPolicy` + 审批；目录不含授权信息 |
| Hook 副作用被缓存吞掉 | 有状态 `tool.definition` Hook | 缓存键含 Hook 版本；提供 `cacheable=false` |
| 工具定义 token 与消息预算混淆 | 只看 messages token | 接线 `ToolDefinitions`；目录单独计入 `Catalog`；压缩时优先降 exposure 档位 |
| Scenario 覆盖用字符串导致拼错 | 配置值拼错无法在编译期发现 | 读取处解析为枚举；非法值记 Warning 并回退到上一级生效模式；不抛异常、不静默接受 |
| MCP 改名破坏既有配置 | 用户配置里写的是旧 id（**零兼容**） | Release Notes 给出批量替换清单；升级即失效；permission 规则失效是最高危项，验收中专门验证 |
| `SessionForker` 漏复制发现态 | 逐字段复制，新增字段不自动继承 | fork 处显式复制 + 不变量测试 |
| 默认 `auto` 导致首次升级前缀失效 | 所有会话 `tools[]` 变一次 | 一次性成本；文档说明；可显式改 `all` 恢复 |
| 目录分节与既有工具描述重复 | 两处都列 schema | 目录只列签名与截断描述；`deferred` 工具不进入既有工具描述分节 |
| Scheduler 旁路遗漏 | 只改主链 | 策略接入点位于共享出口；新增不变量测试覆盖旁路 |
| `tool_search` 结果同轮不可用 | 模型可能"搜到就试" | 返回文本明确说明"下一轮可用"；`tool_search` 的 description 也写明 |

## Migration Plan

**阶段 0（P0 清债，行为不变）**

1. `ToolManager` 改为先交集后生成 + 稳定排序 + 缓存（含 Hook 版本）+ 稳定 Hook 触发次数。
2. `IHookManager` 新增 `Version`。
3. 接线 `ToolDefinitions` token 计量（Hosting 引用 `Seeing.TokenEstimation`）。
4. `ExecutionRunner` 依赖改为 `ISessionToolSchemaResolver`，删除 `new` 回退。
5. MCP id 归一（`McpTool` + `McpConnectionCoordinator` 三处 + registry + 全部消费点）；同名 server 重复注册改为报错。
6. 回滚：纯内部重构，逐提交可回退；无配置变更（除 MCP 改名）。

**阶段 1（P1 分级延迟，默认 `auto`）**

7. 新增 exposure 契约（`ToolExposureMode` / `ToolExposurePolicyMode` 分离）+ 策略纯函数。
8. `SettleAsync` 接入策略 + 发现态求交 + 升级判定。
9. 重构 `PromptBuilder` 分节机制（D12），新增目录分节贡献者并注册 DI。
10. 新增 `Seeing.Agent.Tools.ToolDiscovery` 模块（单一 `tool_search`）。
11. 会话发现态 + `ExposureEscalated` 持久化 + fork 显式继承。
12. `SchemaSnapshotEvent` 增加计数；新增 `ToolSearchEvent`。
13. 灰度：默认 `auto` 上线；保留 `Mode=all` 回退开关。

## Open Questions

（无。原 Scenario 覆盖落点问题已按下方 Resolved Decisions 定稿。）

## Known Limitations（明确接受，非遗漏）

| 限制 | 说明 | 为何接受 |
|---|---|---|
| `tool_search` 无独立调用配额 | 每次检索按一次工具调用计入 `AgentDefinition.MaxSteps`。不新增 `MaxToolCalls` 之类的独立配额，也不特例化 `LoopDetector` | `tool_search` 就是一次工具调用，已消耗一个 step；MaxSteps 是既有上限。`LoopDetector` 的语义是"相同参数连续重复"，而每次检索 query 不同，本就不会触发——强行特例化会污染通用逻辑。模型陷入无脑翻页时由 MaxSteps 兜底 |
| 发现结果需下一轮生效 | 同一轮内无法调用刚检索到的工具 | D11 的必然取舍：轮内生效需用 `ToolResult.Metadata` 传执行信号，击穿既有不变量 |
| 发现态无容量上限 | 长会话理论上可累积大量已发现 id | 上限与"只增不减"语义冲突，且需额外时间戳字段；实际会话中工具总数有限（内置 51 + MCP），膨胀风险低 |
| 目录预算按字符而非 token | 与 `ToolDefinitions` 的 token 口径不同 | 需要确定性、不引入第二套估算器；已通过 `SourceBreakdown.Catalog` 单独计量，不混淆 |
| 全量模式仍发送全部 schema | `PolicyMode=All` 或未达阈值时无优化 | 这是"防倒退"设计：小工具集全量更便宜 |
| 不提供 Code Mode | 模型无法用代码编排工具调用，中间结果仍需经上下文 | 执行引擎的设计（语言选择、隔离形态、工具回调协议）尚未确定，不在本变更范围内。调研结论仅记录：若未来实现，优先受限解释器（能力面即隔离面）而非进程内 Roslyn 脚本（无法隔离 I/O 与反射） |

## Resolved Decisions

| 议题 | 结论 | 依据 |
|---|---|---|
| 发现结果生效时机 | **下一轮执行生效**（D11） | 轮内生效需用 `ToolResult.Metadata` 传执行信号，击穿 executor 不参与 schema 生命周期的既有不变量与 `SchemaSnapshotEvent` 同源契约；对齐 dsh native 语义 |
| 重取端口 | **不引入** | D11 的必然结果 |
| 目录预算单位 | **字符 2000 + 描述截断 120 字符** | opencode `catalog.ts` 实际实现；确定性好、不引入第二套估算器 |
| 阈值机制 | **双条件 OR（≥30 工具 / ≥8000 token）+ 单调升级 + 会话粘性（`SessionData.ExposureEscalated`）** | 数量依据 Anthropic "30–50 后精度下降"；成本依据 51 工具 15–35k vs explore 4.4k 的实测分界；单调升级使前缀最多变一次 |
| Scenario 覆盖存储形态 | **`string?`**（非枚举） | `ScenarioDefinition` 的既有约定是"模块 / Agent / seams / 工具裁剪均为字符串 id，避免依赖其他类型"；用枚举会破坏该约定。`SessionScenarioOverride` 同样用 `string?`，两条覆盖路径语义对称 |
| 默认模式 | **`auto`** | 51 工具场景默认即触发分级，无需人工配置；一次性缓存失效成本可接受 |
| 发现态存储 | `SessionData.DiscoveredTools`，**无上限、无 LRU** | "model-visible means logged"（dsh）；LRU 与"只增不减"语义冲突且需额外时间戳 |
| 元工具形态 | **单一 `tool_search`**（`detail=name\|summary\|full`） | 业界无 search+load 双工具先例；少一份常驻 schema |
| `Mode=all` 时模块挂载 | 挂载但策略标为 `Hidden` | 支持热切换；不白占 token |
| MCP id 改名 | **直接改，零兼容；留在本 change** | 双下划线是社区事实标准；拆出会让本 change 丧失整体回滚承诺，但用户已决定保留在本 change |
| 过度设计裁剪 | 砍 `IToolCatalogStore`、`IToolExposurePolicy[]` 数组、目录 delta 注入、目录落盘 | 目录是结算产物不是存储；策略链无合并语义；delta 不省 token 不保缓存；落盘是第三条获取通道 |
| `tool_search` 限流 | **计入既有 `MaxSteps`，不新增配额** | `tool_search` 即一次工具调用，已消耗一个 step；`LoopDetector` 按相同参数判定，检索 query 每不同故本不会触发，特例化会污染通用逻辑 |
| Code Mode | **不做，也不预留开关** | 执行引擎设计（语言、隔离形态、工具回调协议）未定；预留一个永不生效的 `Mode=code` 只会诱导误配。需要时另开 change 完整设计 |
