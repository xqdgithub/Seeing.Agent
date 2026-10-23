# 内置工具描述与提示词路由优化 — 设计

- **日期**：2026-10-23
- **范围**：`src/capabilities/**`、`src/hosting/Seeing.Agent.Hosting/Tools/**`、`src/capabilities/Seeing.Agent.Agents.BuiltIn/**`、`docs/architecture/**`、测试
- **状态**：待评审

## 1. 背景

内置工具已达 52 个（不含 MCP 动态工具）。工具对模型暴露的信息只有两处：

1. API `tools[]` 里的 `FunctionSchema`（`Description` + `ParametersSchema`），由 `ToolManager.BuildSchemaAsync` 生成；
2. 默认 `auto` 暴露策略下，工具数 ≥ 30 或 schema ≥ 8000 token 时升级为分层：未内联工具只在目录中出现**一行**：`id(参数摘要): 描述`，整行按 `CatalogLineMaxChars` 截断，目录整体按 `CatalogBudgetChars` 限额（`ToolExposureEngine.BuildMeta` / `BuildCatalog`）。

当前默认值 `CatalogLineMaxChars = 120`、`CatalogBudgetChars = 2000`。按默认钉选 16 个工具计算，**35 个工具进目录，而 2000 字符只够显示约 15 行**——剩下 20 个工具在目录里完全不可见（`BuildCatalog` 按“最短行优先”填，超预算的条目直接不渲染）。这是“模型不知道该用某个工具”的最大来源，比单行长度更关键。

也就是说：**描述的首句决定该工具在目录里是否“看起来该用”**。整行前缀（`id(参数摘要): `）通常 20–50 字符，长 id + 多长参数名时可到 ~90 字符；在 200 字符行宽下，描述可见长度约 110–180 字符。

当前描述是历史累积产物，存在系统性缺陷（均已核对代码）：

| # | 问题 | 证据 |
|---|------|------|
| 1 | 语言混杂 | `git_status` / `git_diff` / `git_log` / `git_commit` 与 `skill` 为英文；Git 参数说明也是英文（如 `Optional path to check status for`） |
| 2 | 无“何时不用/改用哪个” | `read`↔`open_file`、`grep`↔`glob`↔`bash`、`write`↔`edit`、`task`↔`task_status`↔`session_*`、`memory_*`↔`read`、`editor_*`↔文件工具、`acp`↔`task`、`websearch`↔`webfetch`↔`codesearch` 全部缺失 |
| 3 | 路径语义与实现不符 | `read`/`edit`/`write`/`delete`/`open_file` 写“必须是绝对路径”，实现均接受相对路径并按工作目录解析（`ReadTool.cs:96`、`EditTool.cs:116`、`WriteTool.cs:87`、`DeleteTool.cs:64`、`OpenFileTool.cs:83`） |
| 4 | `todowrite` 描述无换行 | 字符串以 `+` 拼接且分隔符为空串，实际渲染为 `…组织复杂任务。## 何时使用- 复杂多步任务…`（`TodoWriteTool.cs:28-49`） |
| 5 | `bash.description` 被列为必填 | schema `required = ["command","description"]`，实现缺失时自动兜底（`BashTool.cs:46,73`） |
| 6 | `write` 诱导覆盖 | 描述强调“已存在会覆盖”，未引导改现有文件用 `edit` |
| 7 | 工具名大小写不一致 | `edit` 描述写“必须先使用 Read 工具”，实际 id 为 `read`（`EditTool.cs:42`） |
| 8 | 首句不带区分度 | `todowrite`、`cron_create`、`question`、`session_read`、`task` 等首句过长，目录行截断后丢失关键信息 |
| 9 | 参数 schema 信息缺失 | `todowrite` 的 `todos` 只有 `type=array` 无 `items`；`task.subagent_type` 无候选来源提示；`memory_search` 无 TopK 说明；`git_*` 无路径语义；部分数值无默认/边界 |
| 10 | 无防回退约束 | 无任何测试约束描述语言、首句长度、参数描述完整性 |

提示词侧：内置 Agent 的 `SystemPrompt` 只有零星工具提示（build 的「## 工具使用」、general 的一段），`explore`/`plan` 几乎没有；且模板刻意不含 `## Tools` 锚点（`ToolsPromptSectionContributor` 只在有锚点时注入全量工具清单），跨工具的路由规则当前**无处安放**。

## 2. 目标 / 非目标 / 反目标

### 目标

- 全部内置工具使用**统一中文描述规范**，首句核心信息在目录行内可见。
- **目录覆盖全部延迟工具**：35 个延迟工具的条目全部出现在目录中（当前只显示约 15 个）。
- `tool_search` 能用完整描述与参数名做检索（而非截断签名），中文查询可命中描述中段。
- 每个工具明确「何时用 / 何时不用 / 改用哪个（精确 id）」。
- 参数 schema 补齐类型、默认值、枚举、边界、单位、路径语义。
- 内置 Agent 增加静态「工具选择」路由段，且只提及该 Agent 实际可用的工具。
- 新增约束测试，防止新增工具退回一句话描述。

### 非目标

- 不改工具行为、执行链、权限判定、工具 id、`ToolCategory`。
- 不改暴露策略的**判定语义**（分层/升级/发现集/安全钳制）；只调整目录宽度与预算的默认值、检索语料来源、summary 输出长度。
- 不引入集中式描述注册表 / `[ToolDoc]` 特性 / 构建期生成（方案 C）。
- 不重写 MCP 动态工具描述（由 server 定义）。
- 不新增 `## Tools` 锚点，不改变提示词分节注入机制。
- 不做多语言，只中文。

### 反目标

- 描述膨胀导致工具 schema token 显著上升（用 R3 的总长预算 + 实测增量控制，不设百分比硬门槛）。
- 描述与实现不符（写进去的每条约束都必须能从代码验证）。
- 用强命令式语气堆砌“必须/禁止”。

## 3. 方案取舍

| 方案 | 内容 | 结论 |
|------|------|------|
| **A（选定）** | 统一规范 + 全量重写 + 静态路由段 + 约束测试 | 52 个工具在默认 `auto` 下大部分会进目录，描述质量是当前最短的板；一次做完长期受益 |
| B | 只改高频高危 8 个工具 + 路由段 | 长尾工具仍是弱首句，语言/路由问题仍在，半成品 |
| C | 集中描述表 / `[ToolDoc]` + 构建期校验 + 双场景渲染 | 需要新增抽象与 schema 构建链路，52 工具规模下收益不抵复杂度，且触碰 schema 单点计算红线 |

另：目录宽度/预算与检索语料调整（§12）是让描述“被看见、被搜到”的前置增益，与 A 一并纳入；不改变分层判定语义。

## 4. 描述规范（R1–R9）

适用于所有实现 `ITool.Description` 的内置工具；MCP 动态工具除外。

**R1 语言**：简体中文。工具 id、参数名、命令、专有名词保留原文（如 `read`、`--no-verify`、Monaco）。

**R2 首句**（必需，且必须能独立成立）：

硬约束只有四条：

1. `Description` 按换行取第一行，长度 **≤ 60 字符**（`string.Length`，含标点）；
2. 首句必须自足，不依赖后文，不出现“详见下文”；
3. **前端加载**：首句的前 20 个字符内必须出现「动作 + 对象」核心词（如「读取文件」「网页搜索」）。原因：目录行被截断后，模型是“扫一行”做判断，核心词越靠前越可靠；
4. 首句必须包含该工具的**主用途中文词**，因为 `tool_search` 只对签名（`id(参数): 描述前约 70 字符`）做子串匹配，首句同时是可检索语料。

推荐写法（**非强制**，可按工具裁剪）：

```
<动词短语>：<做什么/返回什么>；<何时该用>；不用于<场景>（用 `other_tool`）。
```

目录行预算（`ToolExposureEngine.BuildMeta`，`CatalogLineMaxChars = 200`、`CatalogBudgetChars = 8000`）：

- 行 = `id(参数摘要): ` + 空白折叠后的描述，**整行**超长截断（不是只截描述）。
- 参数摘要最多列 4 个（必填不加 `?`）。典型前缀 20–50 字符 → 描述可见约 150–180 字符；长 id + 多长参数名的工具前缀可达 ~90 字符（如 `send_channel_message(channelId, targetUserId, conversationExternalId, conversationKind): `）→ 描述可见约 110 字符。
- 因此首句（≤60）在任何情况下都能完整显示；**但首位仍应放核心动作与对象**，因为摘要行的读者（模型）只扫一行。
- “不用于 X”放不进首句时，下移到正文，不为了凑模板牺牲可读性。

**R3 正文**（可选，仅在存在真实信息时写）：

- 使用短行 + `-` 列表或 `；` 分隔，**不使用 Markdown `##` 标题**（在 API 描述中无渲染收益，且污染目录行）。
- 推荐小节（按需，不必全有）：`何时不用`、`约束`、`返回`。
- 每个工具描述总长 **≤ 700 字符**。

**R4 兄弟工具路由**：存在易混工具时，必须点名替代工具，并使用反引号包裹的**精确 id**（`read` 而非 `Read`）。

固定路由对照（写进各自描述，双向可发现）：

| 场景 | 正确工具 | 常见误用 |
|------|----------|----------|
| 读取文件内容/目录列表 | `read` | `open_file`（仅呈现给用户）、用 `bash` 跑 cat |
| 把文件呈现到用户编辑器 | `open_file` | `read` |
| 按文件名/路径模式找文件 | `glob` | `grep`、用 `bash` 跑 find |
| 按内容找代码 | `grep` | `glob`、用 `bash` 跑 grep |
| 修改现有文件 | `edit` | `write`（整体覆盖）、用 `bash` 跑 sed |
| 新建文件/整体重写 | `write` | `edit`（oldString 为空才会新建） |
| 执行命令/构建/测试 | `bash` | 用 `bash` 做文件读写搜索 |
| 委派子代理 | `task` | `acp`（仅 ACP 后端） |
| 查看子代理过程 | `session_read` | `task_status`（只看状态） |
| 搜索历史消息 | `session_search` | `session_read` 全量翻页 |
| 上下文过长交接 | `session_handoff` | `session_trim`（只裁剪，不换会话） |
| 长期记忆读写 | `memory_search`/`memory_read`/`memory_write` | 文件系统 `read`/`write` |
| 需要但不在 `tools[]` 的工具 | `tool_search` | 猜测工具名直接调用 |
| 结构化提问 | `question` | 回复正文里追问 |

**R5 写入语义**：`write`/`edit`/`delete` 必须明说创建/覆盖/递归/不可恢复。

**R6 路径语义**：统一表述为「绝对路径，或相对当前工作目录的路径」；不得再写“必须是绝对路径”。

**R7 权限与前置条件**：需要审批、需要宿主面板、需要模块挂载的，必须在描述中写明（如 `session_list(scope=all)` 需审批、`editor_*` 需 WebUI 文件面板、`tool_search` 需启用分层暴露）。

**R8 动态描述**：`task` 的运行时动态描述同样遵守 R1–R7；首句规则以运行时文本为准。

**R9 术语一致**：统一使用「当前会话」「本次执行」「下一次执行」「工作目录」「子代理」；不使用「本轮」「下一轮」。

### 示例

改前（`write`）：

```
将内容写入文件。

将提供的文本内容写入指定的文件路径。路径必须是绝对路径。
如果文件已存在，会覆盖原有内容。
如果文件不存在，会创建新文件（包括必要的目录）。
```

改后：

```
新建文件或整体重写文件内容；修改现有文件的一小段请用 `edit`，避免整体覆盖。

- 路径：绝对路径，或相对当前工作目录的路径；父目录不存在会自动创建。
- 覆盖：目标已存在时**整文件替换**，原有内容不保留。
- 返回：写入成功时返回路径与差异摘要。
- 不用于：读取内容（用 `read`）、把文件呈现给用户（用 `open_file`）。
```

改前（`edit`）：

```
对文件进行精确的字符串替换编辑。

使用此工具对现有文件进行精确编辑。工具会尝试多种匹配策略来找到要替换的内容。
必须先使用 Read 工具读取文件，确保了解要编辑的内容。
oldString 和 newString 必须完全匹配，包括空白和缩进。
```

改后：

```
对现有文件做精确字符串替换；改代码片段用此工具，不要用 `write` 覆盖整文件。

- 前置：先用 `read` 读取目标文件，确认上下文。
- 匹配：`oldString` 必须唯一；文件中有多处相同文本时提供更多上下文，或设 `replaceAll=true`。
- 严格性：空白、缩进、行尾必须一致；匹配失败会报错且不写入。
- 新建：仅当 `oldString` 为空串时新建文件（通常直接用 `write` 更清晰）。
```

## 5. 参数 Schema 规范

- 每个属性（含数组 `items`、嵌套对象）必须有非空 `description`。
- 有默认值写进描述，且尽量使用 `default` 字段。
- 离散取值使用 `enum`，描述中给出语义映射。
- 数值给 `minimum` / `maximum`，描述中写单位（毫秒/秒/字符/条）。
- 路径参数按 R6 表述；数组参数说明元素结构。
- `required` 必须与实现一致（可缺省就不列 required）。
- **不扩展** `Tools.Support` 的共享 `BuildObjectSchema`：它是公共基类入口，扩展会波及全部内置工具的 schema 生成路径，超出本次范围。需要 `items`/边界时，在该工具内手写匿名对象 schema（与 `ReadTool` 现有写法一致）。

确定要修的 schema：

| 工具 | 改动 |
|------|------|
| `bash` | `required` 改为 `["command"]`；`timeout` 注明默认 120000 毫秒；`workdir` 注明默认工作目录 |
| `todowrite` | `todos` 补 `items` 对象：`content`(必填)、`status`(enum: pending/in_progress/completed/cancelled/paused)、`priority`(enum: low/medium/high)。该工具改为手写 schema，不动共享 helper |
| `task` | `subagent_type` 描述指向动态列出的可用子代理；`task_id` 说明“传入即续跑” |
| `memory_search` | 说明返回条数上限由记忆配置 `TopK`（默认 5）决定 |
| `git_status`/`git_diff`/`git_log`/`git_commit` | 参数说明改中文；`path`/`files` 语义明确 |
| `acp` | `backend` 说明为已注册 ACP 后端；`cwd` 说明默认工作目录 |
| `current_time` | `timezone` 给 IANA 示例（已在描述，参数也补） |

## 6. 逐组改造清单

工具总数 52（不含 MCP）。

| 组 | 工具 | 主要改动 |
|----|------|----------|
| 文件系统（8） | `read` `write` `edit` `delete` `grep` `glob` `open_file` `add_workspace_path` | 首句重写；补 R4 路由；路径语义修正；`read` 补分页/截断/图片 PDF 附件行为；`grep` 补默认 100 条与 include 语法；`glob` 补默认 100；`delete` 补递归删除不可恢复；`add_workspace_path` 补“仅当前会话、需审批” |
| Shell（1） | `bash` | 首句“仅在专用工具无法完成时使用”；`description` 改可选；补超时/工作目录/危险命令拒绝 |
| 宿主（3） | `task` `task_status` `todowrite` | 动态描述收紧首句；`task_status` 补 wait/timeout 语义；`todowrite` 修换行、压缩首句、补 items schema |
| 会话（8） | `session_list` `session_read` `session_search` `session_trim` `session_handoff` `session_inbox_send` `session_takeover` `session_release` | 每个补“与兄弟工具的边界”；`session_read` 首句压缩并补默认 20/上限 50/max_chars 2000；`session_trim` 补“不换会话”；`takeover`/`release` 补“仅改归属，不终止执行” |
| 记忆（3） | `memory_search` `memory_read` `memory_write` | 已有交叉指引，改为统一格式；补 TopK、路径为 memory 相对路径、写入即持久化 |
| 调度（6） | `cron_create` `cron_list` `cron_disable` `cron_resume` `cron_run` `cron_delete` | `cron_create` 首句压缩、`schedule` 结构说明保留；其余补“何时用/幂等/不可恢复”；统一“当前会话”术语 |
| 编辑器（3） | `editor_query` `editor_list_commands` `editor_command` | 补前置条件（WebUI 文件面板）、无副作用/不自动保存、与 `read`/`edit` 的区别 |
| Web（3） | `websearch` `webfetch` `codesearch` | 三者路由：找网页/新闻用 `websearch`、已知 URL 用 `webfetch`、找代码示例与库用法用 `codesearch`；补默认值与上限 |
| 技能（1） | `skill` | 英文改中文；说明技能名来自系统提示词 `## Skills`；不用于一般知识问答 |
| 工具发现（1） | `tool_search` | 首句改“需要的工具不在 tools[] 时先检索”；保留 detail/full 语义与分页 |
| Git（4） | `git_status` `git_diff` `git_log` `git_commit` | 英文改中文；补参数语义；`git_commit` 提示先确认变更、`all` 语义 |
| 通道（3） | `send_channel_message` `list_channel_conversations` `list_channel_users` | 补“已登记通道/外部用户”、target 二选一、分页默认 50 |
| ACP（2） | `acp` `acp_status` | 补 `acp` 与 `task` 的分工（ACP 后端 vs Native 子代理）；`acp_status` 补与 `task_status` 的区别 |
| SystemOne（4） | `systemone_ask` `systemone_noul` `systemone_choice` `systemone_score` | 首句补适用场景；统一“优先单一题型工具”的指引；说明 questions 结构 |
| 基础（1） | `current_time` | 补时区回退与返回格式 |
| 交互（1） | `question` | 首句说明“需要用户选择或确认时用”；补 `kind` 与 `options` 的取值关系、`text` 不得带 options |

## 7. 系统提示词改造

改 `src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs`：

- 新增 `## 工具选择` 段（**不新增 `## Tools` 锚点**，避免 `ToolsPromptSectionContributor` 注入全量工具清单、与 API `tools[]` 重复并破坏前缀缓存）。
- 只写跨工具决策规则，不重复参数说明。
- **改写而非并存**：`build`/`general` 现有的「## 工具使用」直接重写为「## 工具选择」；`explore` 的「## 搜索策略」保留顺序规则、删掉与新段重复/冲突的表述；`plan` 正文中的零散工具句收敛进新段。避免模型同时收到两套路由。
- **只写肯定式引用**：段内反引号中的工具 id 必须是该 Agent 可用的工具（或已知 Agent 名）。禁止用「不要用 `bash`」这种写法表达禁用——改为不带 id 的说明（如「当前模式禁止命令执行与文件写入，权限已强制」）。原因见 8.2 的机械校验规则。
- 段落 ≤ 12 行/Agent。

分配：

| Agent | 段落内容要点 |
|-------|--------------|
| `build` | 专用工具优先于 `bash`；`glob`→`grep`→`read` 探索顺序；改现有文件用 `edit`；独立操作并行；委派用 `task`、看过程用 `session_read`、等待用 `task_status`；上下文过长用 `session_handoff`；结构化提问用 `question`；工具不在 `tools[]` 时用 `tool_search` |
| `plan` | 只读工具边界（`read`/`grep`/`glob`/`webfetch`/`websearch`/`codesearch`/`open_file`）；计划文档写 `.plans/**/*.md`；委派 `explore` 用 `task`；「命令执行与代码编辑在本模式被禁用」用文字说明，不写 `bash`/`edit` 的 id |
| `explore` | `glob`→`grep`→`read`；并行搜索；只读；不委派（用文字说明，不写 `task` 的 id）；不提 `tool_search`/`session_*` |
| `general` | 专用工具优先；并行；不提 `task` 的 id（已 `DeniedTools`，用文字说明“不支持再委派”） |

约束：不出现该 Agent 不可用的工具 id（由测试锁定）。

## 8. 防回退测试

### 8.1 工具元数据契约测试（新增项目）

`tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/`

- 引用全部内置工具所在项目 + `Agents.BuiltIn`。
- 用显式工厂列表构造全部 52 个工具（`Mock.Of<ILogger<T>>()` + 接口参数 `Mock.Of<T>()`；只读取 `Description` / `ParametersSchema`，不执行）。
- 断言：
  1. 描述非空、全中文（允许 id/参数名/命令等 ASCII 片段）；
  2. 首句 `≤ 60` 字符；
  3. 描述总长 `≤ 700` 字符；
  4. 描述中 `## ` 标题数为 0；
  5. 每个 schema 属性（含数组 items、嵌套 object）都有非空 `description`；
  6. `enum` 出现在描述提及的取值集合内；
  7. `bash` 的 `required` 不含 `description`；
  8. **目录行复算**：用默认 `ToolExposureOptions`（`CatalogLineMaxChars` / `CatalogBudgetChars`）构造 `id(参数摘要): 描述`，断言整行截断后保留首句前 20 个字符。
  9. 描述中反引号 token 的判定规则（防拼错，也避免误伤参数名）：
     - 若是全局内置工具 id 全集中的成员 → 通过（但不得为该工具自身）；
     - 否则必须属于下列之一：该工具自身 schema 的属性名、包含 `=` / `--` / `/` 的片段、已知 Agent 名（`build`/`plan`/`explore`/`general`）；
     - 其余情况判失败（捕获 `reda` 这类拼错的兄弟工具名）。
     因此约定：**描述里只对工具名、参数名、Agent 名使用反引号**，普通强调不用反引号。

### 8.1.1 `task` 动态描述

`TaskTool.Description` 在 `WarmDescriptionAsync` 前返回 `FallbackDescription`，之后返回 `BuildDescriptionAsync` 的文本。改造要求：

- `BuildDescriptionAsync` 改为「首段复用 `FallbackDescription` 的首句 + 动态子代理列表」，两条路径共享同一首句，避免两份文案漂移。
- 测试同时断言 fallback 与 warmed 文本（用 mock `IAgentRegistry` 返回 2 个 Agent 后调用 `WarmDescriptionAsync`）满足 8.1 规则 1–4。

### 8.2 提示词路由测试（`Seeing.Agent.Agents.BuiltIn.Tests`）

- 四个 Agent 均含 `## 工具选择` 段，且不含 `## Tools` 锚点（防止意外触发全量注入）。
- 段内所有反引号 token 必须是「该 Agent 可用的工具 id」或「已知 Agent 名」，否则失败：
  - `plan` 依据 `AllowedTools`；
  - `explore` 依据 `PermissionRules` 中 `Allow(Tool, x)` 且不在 `DeniedTools`；
  - `build` / `general` 依据全部内置 id 减去 `DeniedTools`。
- 这是一条**机械规则**：路由段不写不可用工具的 id，禁用语义用文字描述。

### 8.3 目录与检索测试

- **目录覆盖**（Hosting 侧，`ToolExposureEngine` 合成用例）：按默认配置构造 35 个延迟工具，断言 `CatalogText` 含**每一个**工具 id；并断言总字符数 ≤ `CatalogBudgetChars`。
- **默认值锁定**：`new ToolExposureOptions()` 的 `CatalogLineMaxChars == 200`、`CatalogBudgetChars == 8000`（防止被误改回 120/2000）。
- **检索语料**（`ToolSearchToolTests`）：构造描述为中文长文的延迟工具，用描述**中段**（首个句号之后）的关键词查询，断言能命中——证明排序语料不是截断签名。
- **summary 长度**：`detail=summary` 输出行的描述部分 ≤ 300 字符；`detail=full` 仍返回完整 schema。
- **中文召回边界**：整句中文查询仅在包含完整子串时命中（`SplitWords` 不切 CJK），作为已知行为写进测试注释，不改 tokenizer。

### 8.4 现有测试回归

- `ToolSearchToolTests` 已有“本次执行内/下一次执行”术语断言，重写时保持。
- `SchemaSnapshotExecutionTests` / `ToolManagerSchemaSettlementTests` 覆盖 schema 生成，不涉及文案，预期不受影响。
- 运行方式沿用仓库约定：`dotnet build` + `dotnet vstest`（MTP 在本环境无法发现测试）。

## 9. 文档更新

- 在 `docs/architecture/04-development-standards.md` 新增「工具描述与参数 Schema 规范（R1–R9）」小节，指向本设计。
- 在 `AGENTS.md` 的 `WHERE TO LOOK` 增加一行：新增/修改工具描述 → 规范见 04。
- 更新 `docs/architecture/19-tool-context-budgeting-release-notes.md`：
  - 默认值表：`CatalogLineMaxChars` 120 → 200、`CatalogBudgetChars` 2000 → 8000，并说明“覆盖全部延迟工具”的取值依据；
  - 目录签名与检索语料的区别：展示行仍是截断签名，`tool_search` 排序改用完整描述 + 参数名；
  - `detail=summary` 描述段上限 300 字符；
  - 回退方式：`Tools.Exposure.CatalogLineMaxChars` / `CatalogBudgetChars` 可配置，`PolicyMode="All"` 仍是全量内联回退开关。

## 10. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 描述变长，schema token 上升 | 硬预算：首句 ≤ 60、总长 ≤ 700（多数工具落在 250–450）。**不设百分比门槛**，批次 1 完成后用 `SchemaSnapshotEvent` 实测 `ToolDefinitions` 增量并在计划报告里记录；若增幅明显超预期，再回头压正文 |
| 描述变长推高 `EscalateSchemaTokens`（默认 8000），使暴露布局在阈值附近翻转 | 接受。分层是 52 工具场景的既定默认；翻转只会让更多工具进目录，不会丢能力。计划中把它写成已知效果，不当作缺陷 |
| `tool_search` 中文检索偏弱 | 本次已缓解（§12.2 排序语料改为完整描述 + 参数名）。剩余边界：`SplitWords` 不切 CJK，整句中文查询需完整子串命中；不改 tokenizer |
| 写出与实现不符的描述 | 每条约束来自代码核对；测试锁定可机械验证的部分；评审逐组对照实现 |
| 目录行仍被截断关键信息 | 首句长度测试 + 目录行复算（截断后必须保留首句前 20 字符）纳入测试 |
| 长 id + 多参数工具目录行描述偏短 | 行宽 200 后仍保留约 110 字符；核心词前置作为硬约束，足以区分 |
| 目录预算放大带来常驻 token 上升 | 8000 字符 ≈ 估算 2000 token（中文实际 4.5–6.5k），可缓存前缀；远低于全量内联 15–35k。用 `SchemaSnapshotEvent.Catalog` 实测 |
| 提示词提到不可用工具 | 8.2 机械校验；用肯定式引用，禁用语义用文字 |
| 新旧提示词段落并存导致自相矛盾 | §7 要求改写合并，不留旧段；评审检查 prompt 全文无重复路由 |

## 11. 验收标准

- [ ] 52 个内置工具描述全部中文，首句 ≤ 60 字符，无 `##` 标题。
- [ ] 描述中反引号 token 符合 8.1 规则 9（工具 id / 参数名 / Agent 名）。
- [ ] 所有参数（含嵌套）有描述；默认值、枚举、边界齐备。
- [ ] `bash.required` 仅 `command`；`todowrite.todos` 有 `items`。
- [ ] `task` 的 fallback 与 warmed 描述首句一致，均满足规则。
- [ ] 默认配置下，35 个延迟工具的 id 全部出现在目录文本中（`CatalogText` 覆盖测试）。
- [ ] `ToolExposureOptions` 默认值为 200 / 8000；`detail=summary` 描述段 ≤ 300 字符。
- [ ] `ToolSearchTool` 能用描述中段关键词命中（排序语料为完整描述 + 参数名）。
- [ ] 四个内置 Agent 含 `## 工具选择`，且不引用不可用工具；未新增 `## Tools` 锚点；旧 `## 工具使用` 已合并。
- [ ] `dotnet build Seeing.Agent.slnx` 0 error；相关测试全绿。
- [ ] `docs/architecture/04` 与 `AGENTS.md` 已更新。
- [ ] 计划报告中记录 schema token 与 catalog token 实测增量（不设门槛）。

## 12. 目录与检索调整（已纳入范围）

### 12.1 目录宽度与预算

`src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs`：

| 选项 | 现值 | 新值 | 依据 |
|------|------|------|------|
| `CatalogLineMaxChars` | 120 | **200** | 整行截断；让典型工具有 150–180 字符描述可见（首句 + 一条“何时不用”），长参数工具仍保留 ~110 |
| `CatalogBudgetChars` | 2000 | **8000** | 35 × (200 + 3) ≈ 7105，加开场白与分组标题 ≈ 7.3k，留 ~9% 余量 → **全部延迟工具可见**，并为 MCP 命名空间标题留出空间 |

只改默认值，不改 `BuildCatalog` 的分配算法（保底 1 行/组、最短优先、超预算不渲染）。

### 12.2 检索语料与签名解耦

`ToolSearchTool.Rank` 目前只对 `entry.Signature`（被截断的一行）打分。`ExposedTool` 已携带完整 `Schema`，因此无需改契约：

- 语料改为 `id + namespace + Schema.Function.Description（完整） + 参数名`；
- 保留现有分档：完全匹配 id、id 词、id 子串、描述子串、命名空间子串；
- 收益：中文/英文查询都能命中描述中段，**目录常驻 token 不变**（只改排序输入）。

### 12.3 summary 长度

`detail=summary` 当前输出 `entry.Signature`（≤200 字符）。改为：`id(参数摘要): ` + 完整描述折叠空白后截断到 **300 字符**（新常量 `SummaryMaxChars = 300`）。`detail=full` 保持原样（签名 + 完整 schema）。

理由：summary 是模型主动检索后才付的 token，放宽不会增加常驻开销；300 字符足以让模型判断“这是不是我要的工具”，而不必直接 `detail=full` 拉全量 schema。

### 12.4 预算与真实 token 的偏差

内部 `CharBasedTokenCounter` 按 **4 字符/token** 估算，中文会被低估约 2–4 倍。8000 字符按估算 ≈ 2000 token，中文实际消耗预计 4.5–6.5k。这是可缓存的前缀，且远低于全量内联的 15–35k，因此接受；批次完成时用 `SchemaSnapshotEvent` 的 `Catalog` 分项做实测记录。

## 13. 实施批次（供计划阶段拆分）

1. 文件系统 + Shell + 宿主工具（高频、问题最集中）。
2. 会话 + 记忆 + 编辑器。
3. 调度 + Web + 技能 + 工具发现 + Git + 通道 + ACP + SystemOne + 基础。
4. 目录宽度/预算默认值 + 检索语料解耦 + summary 放宽（含 8.3 测试）。
5. 提示词路由段（四个 Agent，改写合并旧段落）。
6. 契约测试项目 + 提示词测试 + 文档更新。
7. 全量构建与测试，schema/catalog token 增量实测。

## 14. 未采纳项

- **`CatalogLineMaxChars = 500`**：目录是单行索引，行宽放大到 500 会让 2000 预算只够显示 3–4 个工具；若同步把预算放大到 ~18000，token 已接近全量内联，却仍要 `tool_search` 往返且拿不到完整参数 schema——不如直接配置 `Tools.Exposure.PolicyMode = "All"`。追求极致准确用该配置项即可，无需改代码。
- **目录参数摘要限长**：覆盖率与行宽调整后，长参数工具仍有 ~110 字符描述可见，收益不足以再动 `DescribeParameters`。
