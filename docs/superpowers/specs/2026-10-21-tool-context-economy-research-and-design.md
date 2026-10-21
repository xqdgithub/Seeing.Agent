# 工具上下文经济学：业界 Harness 调研与本项目架构设计

**日期**：2026-10-21
**状态**：调研 + 设计提案（未实施）
**范围**：Claude（Tool Search / Programmatic Tool Calling / Code Execution with MCP）、Claude Code、Cloudflare Code Mode、opencode（含其 Code Mode 与目录预算）、DeepSeek Harness（dsh）
**关联**：`docs/architecture/01-overview.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`18-gateway-live-subscription-and-schema-resolver-release-notes.md`

---

## 0. 摘要（TL;DR）

**问题**：LLM 的 `tools[]` 是一段**每轮都会重发的固定前缀**。工具越多、schema 越细，模型在读到用户问题之前就已经付了几万 token，并且工具选择精度在 30–50 个工具后明显下降（Anthropic 官方口径）。MCP 把这个成本变成了无上限的线性增长。

**业界收敛出的四条主线**：

| 主线 | 代表 | 一句话机制 | 官方量级 |
|---|---|---|---|
| 分级延迟加载（Deferred） | Claude Tool Search Tool | `defer_loading:true`，模型用工具搜索工具，命中后服务端把 `tool_reference` 展开成完整定义 | 55K → 8.7K tokens（−85%）；Opus 4 选择精度 49% → 74% |
| 元工具检索（客户端等价物） | Claude Code 的 `ToolSearch`、opencode 的 `search()` | 目录摘要进提示词，模型搜一次、按 path 精确调用 | 目录预算 opencode 默认 2,000 字符（≈500 tokens） |
| 代码模式（Code Mode / PTC） | Cloudflare、Anthropic、opencode、DeepSeek dsh | 只暴露一个 `run_code`/`execute`，工具变成代码里的 SDK，中间结果留在沙箱 | 150K → 2K tokens（−98.7%）；PTC 实测 −37% 总 token、−19 次推理往返 |
| 会话内工具生命周期 | DeepSeek dsh | `tool-addition` / `tool-removal` 作为 developer 消息，工具集合在会话中途可增删且**可从日志重建** | 使"长任务中途长出工具"成为一等公民 |

**本项目的结论**：

1. 现有架构的**单点出口已经具备**（`SessionToolSchemaResolver` → `ChatRequest.Tools`），这是所有方案得以低侵入落地的前提。
2. 但当前是**全量静态暴露**：51 个内置工具 + 任意数量 MCP 工具全量注入，无预算、无延迟、无缓存、无 token 计量，且存在"先对全部工具生成 schema 再过滤"的浪费。
3. 由于本项目面向**多 Provider**（OpenAI / Anthropic / DeepSeek / 兼容网关），不能依赖 Anthropic 服务端 `defer_loading`，必须走**客户端实现**——这在设计上等价于「Claude Code 的客户端行为 + opencode 的目录预算 + dsh 的会话发现态」。
4. **推荐路线**：先做「分级延迟 + 元工具检索」（方案 A）作为主干（收益 60–85%，风险与工程量最低）；把「Code Mode」（方案 B）作为**可选能力包**，只在长链数据流场景开启；「文件系统目录」（方案 C）作为 A 的补充呈现，不做主路径。

---

## 1. 问题分解：工具上下文的四项成本

把"工具占上下文"拆成四笔可分别优化的账，后面所有产品的设计都能映射到这四项。

| # | 成本 | 产生位置 | 特点 | 现有缓解手段 |
|---|---|---|---|---|
| **T1 定义税** | 每轮请求的 `tools[]` 固定前缀 | `ChatRequest.Tools` | 每轮重发、破坏前缀缓存、与工具数线性相关 | 分级、延迟、代码模式 |
| **T2 结果税** | 工具返回值进入消息历史并被反复重发 | `ToolResult` → `assistant/tool` 消息 | 与数据量线性、压缩前一直存在 | 输出限长 + 落盘（spill）、历史裁剪（prune） |
| **T3 中间态税** | 多步数据流中，中间结果必须在模型上下文里"过一遍"才能进下一次调用 | 多轮 tool call 循环 | 每步一次完整推理 + 一次全量结果拷贝 | Code Mode / PTC：结果留在执行环境 |
| **T4 选择税（精度损失）** | 工具过多导致选错、参数错、命名混淆 | 模型决策 | 不是 token 但是成功率 | 检索、命名空间、示例、pinned |

> **关键区分**：Anthropic 服务端 `defer_loading` 节省的是 **T1 的上下文窗口占用**，请求体里仍然发送全部工具定义（"You still send every tool's full definition in the `tools` array on every request"）。本项目没有这个服务端能力，必须节省**请求体本身**，因此实现形态更接近「客户端检索 + 选择性注入」。

---

## 2. 业界方案谱系（按"暴露粒度"分层）

```
L0  全量静态暴露          tools[] = 所有 schema                    ← 本项目现状
L1  分层过滤             Allowed/Denied、按 Agent/场景/模块收窄      （只能线性减少）
L2  分级延迟 + 检索       pinned 常驻 + 其余 deferred + search 元工具
L3  目录渐进披露         目录摘要（name/签名）→ 按需读完整定义        （含 filesystem-as-API）
L4  代码模式             单一 run_code + 生成 SDK + 沙箱执行          （同时解决 T1/T2/T3）
L5  会话内工具生命周期    tool-addition / tool-removal / 发现态持久化
```

四者不是互斥的，成熟产品都是**组合**：
- Claude = L2（服务端）+ L5（deferred 定义随会话历史保留、可复用）
- Claude Code = L2（客户端策略 + 阈值 auto:N + 服务端执行）+ L5（server 连接后增量可见）
- opencode = L3（目录预算）+ L2（`search` 分页）+ L4（`execute` 解释器）
- dsh = L1（ToolRestriction）+ L2（`deferLoading`）+ L4（`mode: ptc`）+ L5（tool-addition）+ T2（spill / pruner）
- Cloudflare = L4（整包 TypeScript API，作者自述"未来可搜索"→ 即计划演进到 L2/L3）

---

## 3. 逐产品剖析

### 3.1 Anthropic：Tool Search Tool（服务端延迟加载）

**机制**（[官方文档](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)、[发布博客](https://www.anthropic.com/engineering/advanced-tool-use)）

1. 请求里照旧给出**全部**工具定义，但给非核心工具打 `defer_loading: true`。
2. 至少要有一个工具不延迟（通常是搜索工具本身；全部延迟会 400）。
3. 模型上下文初始只含「搜索工具 + 少量非延迟工具」。
4. 模型调用搜索工具（两种变体：`tool_search_tool_regex_20251119` 用 Python 正则，≤200 字符；`tool_search_tool_bm25_20251119` 用自然语言，≤500 字符）。
5. 服务端在**服务端**执行检索，返回 `tool_reference` 块（默认最多 5 条，模型可传 `limit`，上限 10000）。
6. 服务端把 `tool_reference` 自动展开为完整定义，模型再选工具调用。

**关键工程细节（直接影响我们的设计）**

- **不破坏 prompt caching**：deferred 工具**完全不进**系统前缀；发现时才以 `tool_reference` 内联进对话，前缀不变。
- **发现结果跨轮复用**：`tool_reference` 留在历史里，API 在后续轮次继续展开，模型不必重复搜索。
- **与 strict mode 兼容**：语法仍从**全集**构建，不需要重新编译语法。
- **与 `cache_control` 互斥**：deferred 工具上不能同时打缓存断点。
- **上限**：单请求最多 10,000 个 deferred 工具。

**实测收益**

| 指标 | 无 Tool Search | 有 Tool Search |
|---|---|---|
| 5 个 MCP server（58 工具）的启动开销 | ~55K tokens | ~8.7K（约 95% 上下文保留） |
| Anthropic 内部最坏情况 | 134K tokens | — |
| Opus 4 MCP 评测准确率 | 49% | 74% |
| Opus 4.5 | 79.5% | 88.1% |
| 上下文窗口 | 122,800 可用 | 191,300 可用 |

**引导发现（官方 best practice，原文归纳）**

- 保留**最常用的 3–5 个工具**不延迟，其余延迟。
- 在系统提示里写清楚"有哪些能力类别、可以用工具搜索找到"（"You have access to tools for Slack messaging, Google Drive file management… Use the tool search to find specific capabilities"）。
- 命名**按服务加前缀**（`github_`、`slack_`），一次搜索命中一整组。
- description 里放**用户会说的词**，并把关键词放**开头**（延迟工具的 description 上限 16,384 字符，未延迟的 4,096 字符，检索只读前 4,096）。
- 监控"模型实际搜到了哪些工具"来反推 description 质量。

**自定义检索**：允许客户端自建搜索工具，返回标准 `tool_result` + `tool_reference` 内容块（可用 embedding），服务端同样展开。这条对我们是"能力上限"的证明：**检索策略可插拔**。

**适用性判断**：依赖模型/平台支持。本项目多 Provider、且大量走 OpenAI 兼容协议，**不可直接依赖**；但它的"语义契约"（deferred + reference + 展开）可以作为我们的**概念蓝本**。

---

### 3.2 Claude Code：把 Tool Search 工程化

Claude Code 的价值不在算法，而在**策略层**（[MCP 文档 · Scale with MCP tool search](https://code.claude.com/docs/en/mcp)）：

| 设计点 | 做法 | 为什么重要 |
|---|---|---|
| 默认开启 | MCP 工具默认 deferred，只加载名字与 server instructions | 增量接入 server 几乎不增加上下文 |
| **阈值模式** | `ENABLE_TOOL_SEARCH=auto`（默认阈值为上下文窗口的 10%，可 `auto:5`） | 工具定义总量小于阈值时**照旧全量**，避免为了小工具集白付一轮搜索延迟 |
| server 级默认 + tool 级覆盖 | `default_config.defer_loading` + `configs.<tool>.defer_loading` | 粒度贴合真实场景（一个 server 里 90% 冷门 + 1 个热门） |
| 强制常驻 | server 级 `alwaysLoad: true`；工具可在 `_meta["anthropic/alwaysLoad"]` 声明 | 高频工具零搜索成本；server 作者可自证"我应该常驻" |
| 强制延迟 | server 级 `alwaysLoad: false` 可覆盖工具自声明 | 配置方有最终决定权（防止 server 滥用常驻） |
| 失败可解释 | server 连接失败、需授权的信息也会**通过 ToolSearch 结果**告知模型 | 发现通道同时承担"能力可用性"语义 |
| 连接中的 server | 若当前请求需要它，等待发生在 `ToolSearch` 调用内部 | 把"加载"折叠进已有的搜索轮次，不额外增加轮次 |
| 禁用搜索 | `permissions.deny: ["ToolSearch"]` 或 `--tools` 不含它 → 退回全量 | 明确的可回退路径 |
| 网关兼容 | 上游拒绝 `defer_loading` 时，自动重试一次"不用工具搜索"的请求 | 异构后端下的**优雅降级** |

**对我们最有价值的三条**：
1. **阈值化开启**（不是全有全无）——避免小工具集倒退。
2. **多粒度覆盖 + 配置方最终裁决**——pinned 列表必须可被部署配置覆盖，而不是硬编码在工具里。
3. **降级路径**——Provider 不支持/网关拒绝时自动回退全量，不能让请求直接失败。

---

### 3.3 Anthropic：Code execution with MCP（"文档版" Code Mode）

[原文](https://www.anthropic.com/engineering/code-execution-with-mcp)。核心主张：**把 MCP server 呈现为文件系统上的代码 API**，而不是 tools。

```
servers
├── google-drive
│   ├── getDocument.ts      // 一个工具 = 一个文件，含 TS 接口 + JSDoc
│   └── index.ts
└── salesforce
    └── updateRecord.ts
```

模型"发现工具"的方式就是**浏览目录**（`ls servers/`，再 `read servers/google-drive/getDocument.ts`）——即 **L3 渐进披露**，而且是**复用模型最强的能力：读文件**。

**它解决的四件事**：

1. **按需加载定义**：150K → 2K tokens（−98.7%）。
2. **结果在环境内过滤**：1 万行表格在沙箱里 `filter`，只 `console.log` 前 5 行；模型只看到 5 行。
3. **控制流下沉**：`while` 轮询、`if` 分支在代码里跑，不用"调一次工具→推理一次→再调"。
4. **隐私**：中间结果默认不出沙箱；可对 PII 做 tokenize/untokenize，真实数据从不进模型上下文。

**它还给了"引导发现"一个可插拔形态**：

> Alternatively, a `search_tools` tool can be added to the server to find relevant definitions… Including a **detail level parameter** that allows the agent to select the level of detail required (such as **name only, name and description, or the full definition with schemas**) also helps the agent conserve context.

**三档 detail level** 是这轮调研里最值得直接抄的设计（见 §5）。

**代价（作者自己强调）**：需要安全沙箱（隔离、资源限制、监控），运维成本高于直连工具调用。

---

### 3.4 Anthropic：Programmatic Tool Calling（PTC，服务端代码编排）

[原文](https://www.anthropic.com/engineering/advanced-tool-use)。与 3.3 的差异：**执行在服务端 `code_execution` 工具里**，通过 `allowed_callers` 把普通工具"授权给代码调用"。

```
tools: [
  { type: "code_execution_20250825", name: "code_execution" },
  { name: "get_team_members", allowed_callers: ["code_execution_20250825"] },
  ...
]
```

调用链：模型写 Python → 代码执行环境调工具 → 工具请求带 `caller: { type: "code_execution_...", tool_id }` → 宿主返回结果 → **结果进入代码环境而不是模型上下文** → 只有最终 `stdout` 回到模型。

**数据**：复杂研究任务平均 token 43,588 → 27,297（−37%）；20+ 次工具调用省掉 19+ 次推理往返；内部知识检索 25.6% → 28.5%，GIA 46.5% → 51.2%。

**组合建议（官方）**：按瓶颈选工具——
- 定义膨胀 → Tool Search
- 中间结果污染 → PTC
- 参数错误 → Tool Use Examples（内部实测 72% → 90%）

**注意**：`allowed_callers` 是**显式 opt-in**，且要求工具可并行/幂等才适合放进代码编排。这就是权限与语义的边界设计。

---

### 3.5 Cloudflare：Code Mode（isolate 沙箱 + bindings）

[原文](https://blog.cloudflare.com/code-mode/)。两个洞察：

1. **LLM 见过海量真实 TypeScript，却只见过少量人造 tool-call 语料**。"让莎士比亚上一个月普通话课再写剧本"——所以用代码表达调用，模型表现更好。
2. **MCP 的真正价值是"统一"**（连接、鉴权、文档），不是"必须是 tool"。把 tool 转成 TS API 后，统一性保留，调用形态升级。

**实现要点**：
- MCP schema → TypeScript 声明（带 JSDoc）→ 整包注入上下文（作者明确说"当前全量，未来可搜索"）。
- 只暴露一个工具：执行 TypeScript。
- 执行在 **V8 isolate**（不是容器）：毫秒级启动、几 MB 内存，每次代码片段新建一个、用完即弃。
- 沙箱**禁网**；外部访问只能经 `env` 的 RPC bindings → 回宿主 → 宿主分发到 MCP。
- **密钥不进沙箱**：binding 是已鉴权客户端，token 只在宿主侧注入，AI 写的代码无法泄露 key。
- 返回值：`console.log` 的输出。

**对我们的启示**：
- "**隔离面即能力面**"比"网络过滤"更清晰可控——这也解释了为什么它的权限模型可解释。
- 但 isolate 依赖平台能力；.NET 侧等价物是「受限解释器（无 I/O）」或「独立进程沙箱」，前者成本低得多（opencode 走的就是这条）。

---

### 3.6 opencode：目录预算 + 受限解释器

opencode 的形态值得单独拆，因为它是**与本项目最接近的工程约束**（单体 CLI/服务、自有工具集、多 Provider、无服务端 defer 能力）。

#### (a) 内置工具是"全量但很少"

内置工具约 13 个（shell/edit/write/read/grep/glob/lsp/apply_patch/skill/webfetch/websearch/question/task…），官方文档对 MCP 的告警非常直白：

> "When you use an MCP server, it adds to the context. This can quickly add up if you have a lot of tools… Certain MCP servers, like the GitHub MCP server, tend to add a lot of tokens and can easily exceed the context limit."

也就是说：**opencode 的默认路径仍是 L0/L1**，它的答案是把 MCP 收进 Code Mode。

#### (b) Code Mode = 单工具 + 受限 JS 解释器（`packages/codemode`）

- 不是"沙箱化的任意 JS"，而是**只实现支持子集的解释器**：没有网络、文件、进程、定时器、import/模块；只能通过宿主提供的 `tools` 与显式 `extensions`（如 `fetch`）接触外部。
- 因此**不需要真沙箱**——能力面就是语言子集本身，这是很聪明的成本权衡。
- 工具以**命名空间**组织：`tools.orders.lookup({...})`；非标识符名字用括号：`tools.context7["resolve-library-id"](...)`。
- 结果统一为 `Result`：`value / logs / warnings / truncated / toolCalls`，诊断分类很细（`UnknownTool`、`InvalidToolInput`、`ToolCallLimitExceeded`、`TimeoutExceeded`…）。
- 限制：`timeoutMs` / `maxToolCalls` / `maxOutputBytes`（**默认无限制**，由宿主显式设置）；数据最多 32 层嵌套。
- 每次执行的所有值**跨边界都复制**（convert, never share），避免宿主对象泄漏进程序。

宿主侧（`packages/core/src/codemode`）：
- 只有一个模型可见工具 `execute`，description 里写明"只有目录里列出的工具可调用、不要猜名字、独立调用用 `Promise.allSettled`"。
- **工具目录不走 `tools[]`，走 Instructions**（系统提示里的一个 section），这是关键取舍：目录是**文本**，不参与 function-calling 语法。
- 目录变更用 **delta 注入**：新增/变更/删除分别成段；能压成 delta 就用 delta，否则退回全量替换。

#### (c) 目录预算（`catalog.ts`）——最可直接复用的算法

```
INLINE_BUDGET      = 2_000 字符   （≈ 500 tokens，按 4 字符/token 估）
DESCRIPTION_LIMIT  = 120 字符     （每行只留 description 首行，超长截断 + "..."）
每个 namespace 至少出现一行；然后每轮为每个 namespace 补一条完整签名，短的优先，直到预算耗尽
未被选中的条目 → namespace 行显示 "(35 tools, 3 shown)"
pinned 工具永远入选
```

**为什么这个算法值得抄**：它在**固定预算**下同时保证了
1. 覆盖面（每个 namespace 都可见 → 模型知道"存在这类能力"）；
2. 密度（先放签名的短工具，信息/字符比最高）；
3. 确定性（排序稳定，利于前缀缓存）；
4. 可解释（"35 tools, 3 shown" 直接告诉模型"还有更多，去搜索"）。

#### (d) `search()` 内置函数

同步、无需 await；支持精确 path、namespace 作用域、空查询浏览、分页；返回**可调用 path + 完整签名**；计入 `maxToolCalls` 配额。

> 注意这个细节：**搜索本身也被计费和限流**，防止模型陷入"无限探索"。

---

### 3.7 DeepSeek Harness（dsh）：把"上下文成本"写进类型系统

dsh 是这批产品里**对上下文账目最较真**的一个：它的工具文档每个模型可见面都带 **Token effect** 与 **KV Cache effect** 两栏（`packages/core/tools/README.md`、`docs/subsystems/tools.md`）。这句话可以直接作为我们的评审标准：

> "Fixed per-request cost proportional to the visible definitions. Restrictions that hide tools remove their entire schema cost for that agent."
> "Prefix-stable while visible definitions and their order are unchanged. Registration, disposal, or scoped restriction may invalidate reuse from the first changed schema token."

#### (a) 两种呈现模式，一等公民

```yaml
- name: '@deepseek-ai/dsh-tools'
  config:
    mode: native   # 每个可见 schema
    # 或 ptc       # 只给 run_code + 生成的 SDK
```

- `native`：传统 function calling。
- `ptc`：只暴露 `run_code` + 目标语言的**精确 SDK 声明**；`run_code` 里 `tools.<name>(args)` 走子分发。
- `presentAs(mode)` 可以**按 Agent 作用域**覆盖（"one declaration per scope"），同一进程里并存 PTC agent 与 native agent。
- `maxParallelSubCalls`（默认 10）控制代码内并发。

#### (b) PTC 的子调用不污染上下文，但**全部可审计**

- 子调用 id 形如 `<parent>:ptc:<n>`；落 `tool/ptc-dispatch` 事件，只记 pairing id / name / arguments / 渲染后的 content。
- **description 与 parameters 永不进入 Session 事件**（"transient execution metadata"）。
- 成功且带图片的子结果，作为 `source.kind = ptc-mode` 的 user 消息附在外层结果之后——**只有需要看的东西才被"抬"进上下文**。
- 外层 `run_code` 输出才有硬上限；中间值只活在执行环境（作者也明确这是已知风险：内存可被打爆、不可从历史重建）。

#### (c) 会话内工具生命周期（L5，本项目完全缺失的能力）

- 工具**增删**是 developer 消息里的 `tool-addition` / `tool-removal` 块；定义通过**会话事件的历史 header 引用**绑定，因此"模型当时看到了什么"可从日志重建。
- `deferLoading` 是独立标记（借用 Anthropic 术语），表示"请延迟加载定义"，但不产生 addition 记录。
- 适配器按 `in-history`（把更新插进历史）或 `addition-only` 投影；不支持该能力的路由退化为"当前活跃工具集"。
- 注册/注销/限制变更触发 `tools/change`（**unfiltered**，因为一个全局变更影响所有 agent 的下一次组装）。

**这条对我们的意义**：MCP 连接/断开、模块 Activate/Deactivate、Scenario 切换都会让工具集合在会话中途变化。若把"变化"表达为**增量消息**而不是"每轮整体重算"，就能同时获得正确性与前缀缓存收益。

#### (d) 结果侧（T2）的两个不同机制

1. **spill（落盘 + 定位符）**：超过 `maxInlineTokens` 的文本/图片结果 → 头尾预览 + `SpillLocator` + `retrievalHint`（"用 read 或 grep 读这个路径"）。存储**逐字保存全文**，保存失败则保留原结果（best-effort，不把成功调用变成错误）。
2. **tool-result pruner（历史裁剪）**：压缩时对"当前 surface"的每个超预算 tool result 做 **head/middle/tail** 裁剪，**按 Unicode 码点**切（不切坏代理对），并追加 `compaction/prune` 事件**记录被遮蔽的原节点**——历史可审计、可回溯。

**这是 T2 的完整答案**：入口限长（spill）+ 存量裁剪（prune）+ 不可变事件（shadow 而非删除）。本项目的 `ToolOutputLimiterDecorator` 已有前半段（落盘 + 头尾预览），缺的是"历史裁剪"与"隐私策略钩子"。

#### (e) 能力收窄的三个正交机制

| 机制 | 语义 | 是否可被外层推翻 |
|---|---|---|
| `ToolRestriction`（`allow`/`deny`） | scope 化，多个 restriction **求交**；scoped 注册的工具豁免 | 交集，只能更窄 |
| `guard` | 前置单调守卫，只能 deny | **不能**（无 allow 返回值，顺序无关） |
| `tools/pre-execute` waterfall | allow / deny / cancel / ask | 可被后续 guard 再收紧 |

对本项目的启示：**"可见性"与"可执行性"必须分离成两套机制**（本项目 `AllowedTools` 现在同时承担两者，是个坑，见 §6）。

---

### 3.8 横向补充：三个容易忽略的杠杆

| 杠杆 | 出处 | 要点 |
|---|---|---|
| **Skills = 指令的渐进披露** | Claude Skills、dsh `skill`、本项目 `SkillTool` | 系统提示只放 `name + description`，命中后才加载正文。这是"延迟加载"在**指令**上的成功先例，可平移到工具 |
| **MCP resources vs tools** | MCP 规范、dsh `mcp-resources` | 只读数据不该做成工具；资源有独立目录与按需读取，避免把"取数据的 20 个工具"塞进 `tools[]` |
| **Tool Use Examples** | Anthropic | 用 1–5 条真实示例替代描述性长文，参数正确率 72% → 90%；比扩 schema 更省 token |

---

## 4. 引导发现（Discovery Affordance）设计学

所有"延迟加载"方案成败都取决于**模型知不知道自己该去找什么、去哪找、怎么找**。归纳出 8 条可执行准则：

1. **三档 detail level**（Anthropic 明确建议）
   `name` → `name + description` → `full schema`。默认返回 `summary`，让模型自己决定要不要 `full`。
2. **目录必须"看起来完整"**
   即使只内联 3 条，也要显示 `(35 tools, 3 shown)`，否则模型会误判"没有这个能力"。
3. **命名即检索键**
   按服务/模块加前缀（`mcp__github__`、`session_`、`scheduler_`），一次关键词命中一整组；path 稳定（不要因注册顺序变化）。
4. **description 的第一行是"路由句"**
   第一行回答"什么时候该用我"，而不是"我是什么"。检索与摘录都只读前 120 字符（opencode）/ 前 4096 字符（Claude）。
5. **常驻 3–8 个高频工具**
   读、写、bash/exec、搜索、任务管理这类每轮都可能用到的，**不要**放进需要搜索的一侧——否则每轮多一次往返。
6. **空结果不是失败**
   返回"未命中 + 可用 namespace 列表 + 建议关键词"，而不是空字符串。Claude Code 甚至把"server 连不上/需要授权"也通过搜索通道告知模型。
7. **发现即持久**
   一次搜索/加载后，该工具在**本会话后续所有请求**里都保持可见（Claude：`tool_reference` 留在历史；dsh：`tool-addition`）。否则模型会反复搜索，收益归零。
8. **变化要"增量播报"**
   MCP 连接、模块启停导致的目录变化，用 delta（新增/变更/移除）注入；只有无法表达为 delta 时才全量替换（opencode `instructions.update`）。同时**顺序稳定 + 只增不减**是保住前缀缓存的关键。

**反面清单（会毁掉收益的做法）**：

- 目录摘要里塞完整 schema（等于没延迟）。
- 搜索工具没有分页/limit（模型一次拉 200 条，比全量还贵）。
- 每次请求重新排序工具（缓存全失效）。
- 发现态不持久（每轮重搜）。
- 把权限白名单当作可见性开关（一收紧就报"工具不存在"，模型会反复重试）。

---

## 5. 横向对比与选型矩阵

### 5.1 量级估算（以本项目 51 个内置工具为基准）

| 呈现形态 | 首轮定义开销（估） | 稳态每轮 | MCP 50 工具时 |
|---|---|---|---|
| 全量 schema（现状） | 51 × 300–700 ≈ **15K–35K** | 同左 | +25K–40K |
| 分层：8 pinned + 43 deferred + 目录摘要 | ≈ 2.5K–5K + **0.5K** | 2.5K–5K + 已发现工具 | +目录 0.3–0.8K |
| 目录摘要（仅 name/签名，opencode 预算） | **≈0.5K** | 同左 + 已发现 | 同左 |
| Code Mode（SDK stub 51 个） | 51 × 60–90 ≈ **3K–4.5K** + 单一 transport schema | 同左 | +N × 80 |
| Code Mode（目录 + 搜索，opencode 形态） | **≈0.5K** | 同左 | 同左 |

> 结论：**只要不做 L0，量级立刻从"几万"掉到"几千"**；L2 与 L4 在同量级，差别在 L4 额外省掉 T2/T3。

### 5.2 多准则对比

| 维度 | 现状 L0 | A：分级+检索 L2 | B：Code Mode L4 | C：文件系统目录 L3 |
|---|---|---|---|---|
| T1 定义税 | ✗ 15–35K | ✓ −60%~−85% | ✓✓ −90%+ | ✓ −70%（靠模型主动探索） |
| T2 结果税 | ✗ | ✗（仍进上下文） | ✓✓ 结果留在执行环境 | ✗ |
| T3 中间态税 | ✗ | ✗ | ✓✓ 一次编排＝一次推理 | ✗ |
| 选择精度 | ✗ 30–50 后下降 | ✓ 明显改善 | ✓✓（代码即约束） | △ 依赖模型探索意愿 |
| 额外延迟 | 0 | +1 轮（仅冷门工具） | 0（一次代码内完成） | +N 轮读文件 |
| 实现工程量 | — | **小**（1 策略 + 1 元工具 + 会话状态） | **大**（解释器/沙箱 + 子分发 + 权限复用 + UI） | **极小**（复用 read/glob/grep） |
| 安全面 | 现有权限链 | 不变（延迟≠放权） | **新增**：代码执行面、子调用授权、资源限制 | 不变 |
| Provider 无关 | — | ✓ 纯客户端 | ✓ 纯客户端 | ✓ |
| 前缀缓存友好 | ✗（顺序不稳） | ✓（稳定排序 + 只增不减） | ✓（SDK 随目录稳定） | ✓ |
| 可观测性 | 低 | 高（可见/延迟计数、命中率） | 中（子调用需专门事件） | 低 |
| 失败可恢复 | — | ✓ 搜索失败→回退全量 | ✗ 代码失败需重试整段 | ✓ |
| 对现有纪律侵入 | — | **低**（只在 resolver 之后加 plan） | 中（新增执行端口 + 事件） | 低 |

### 5.3 结论

- **A 是"必须做"的**：它是所有方案的公共底座（目录、检索、发现态、可见性计数），而且单独就能拿到 60–85% 的定义税收益。
- **B 是"值得做但要等"的**：它解决 A 解决不了的 T2/T3，但工程量与安全面是数量级差异；且本项目的 `bash` + `task` 子会话已覆盖一部分编排需求。
- **C 是"顺手做"的**：把 A 生成的目录**落盘**成 `.seeing/tool-catalog/*.md`，模型可用现有 `read`/`grep` 深挖完整 schema——零新增执行面，等于给 A 加一个"详情页"。

---

## 6. 本项目现状诊断

### 6.1 已有的好基础

| 能力 | 位置 | 价值 |
|---|---|---|
| schema 计算单点 | `src/hosting/Seeing.Agent.Hosting/Execution/SessionToolSchemaResolver.cs:75`（`SettleAsync`） | 主链与 Scheduler 旁路共用，**插入策略层只需改一处** |
| 注入单点 | `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`（`ToolSchemas` → `ChatRequest.Tools`） | executor 无需感知延迟加载 |
| 结算四层过滤 | `SessionSettlement.Compute`（模块 ∩ boot − Tools.Disabled）+ `ToolManager.FilterSchemasByAgentToolLists` | L1 已具备 |
| 动态工具并入 | `IDynamicToolContributorRegistry`（MCP） | L5 的入口已存在（缺"增量播报"） |
| 结果限长 + 落盘 | `ToolOutputLimiterDecorator`（50KB 阈、头 4K/尾 2K、`~/.seeing/sessions/{id}.ref/`） | T2 前半段已有 |
| 延迟加载的**先例** | `SkillTool` + 系统提示 `## Skills`（只列 name+description） | 模式已在项目内被验证 |
| 提示分节机制 | `IPromptSectionContributor` / `PromptBuilder` / `SectionIds` | 目录可以直接做成一个分节 |

### 6.2 明确的缺陷（按影响排序）

| # | 缺陷 | 证据 | 影响 |
|---|---|---|---|
| 1 | **全量暴露、无任何预算** | `ToolManager.GetToolSchemasAsync(settled, agent)` 返回 settled 全集 | T1 线性增长；MCP 无上限 |
| 2 | **先全量生成再过滤** | `ToolManager.cs:98` 先 `GetToolSchemasAsync()`（遍历 `_tools.Values` + 每工具一次**阻塞 Hook**），再 `settled.Contains` 过滤 | 设 N=全部注册工具、M=实际可用，做了 N 次反射读 + N 次 Hook，只用 M 个 |
| 3 | **schema 无缓存、每轮重建** | `ExecutionRunner` 每轮 `SettleAsync`；`AgentExecutor` 每 step 重建 `ChatRequest` | CPU 与分配浪费；与 #2 叠加放大 |
| 4 | **顺序不稳定，破坏前缀缓存** | `_tools` 是 `ConcurrentDictionary`，遍历序不保证 | 同一会话相邻请求的 `tools[]` 顺序可能抖动 → provider prefix cache 失效 |
| 5 | **工具定义 token 不可见** | `SourceBreakdown.ToolDefinitions` 字段存在但无赋值；`CalculateBreakdown(..., toolTokens)` 无调用方 | 预算/压缩对工具膨胀不敏感 |
| 6 | **`AllowedTools` 双语义** | 同时用于 schema 白名单与权限白名单（`PermissionService.EvaluateRulesAsync`） | 想"只调可见性"就会误收紧权限 → 不能用它做延迟加载 |
| 7 | **MCP 命名与发现不一致** | `McpTool.Id => $"{server}_{tool}"`；`ToolsCommands` 用 `mcp_` 前缀判定 | `/mcp list` 失效；`a_b`+`c` 与 `a`+`b_c` 歧义 |
| 8 | **`AllowedMcpServers` 未接线** | 仅有 policy 传递，无消费点 | 无法按 server 收窄 MCP |
| 9 | **`ExecutionRunner` 依赖具体类** | 构造参数是 `SessionToolSchemaResolver`（`:72`），回退时 `new`（`:82`） | 替换/装饰解析器需改 Hosting |
| 10 | **无调用内并行/编排** | 每个 tool call 独立往返 | T3 无法缓解 |

> **重要提醒**：缺陷 2/3/4 是**零语义变更**的纯工程债。先修它们，再做延迟加载，收益叠加且风险最低。

---

## 7. 目标架构设计

### 7.1 设计原则

1. **单点纪律不破**：schema 仍在 `SessionToolSchemaResolver` 算，注入仍在 `AgentExecutor`；新增的是"**裁剪**"，不是"另一条计算路径"。
2. **Provider 无关**：不假设任何服务端 `defer_loading`；纯客户端实现。
3. **可见性 ≠ 可执行性**：延迟加载只减少"暴露"，不改变权限、审批、Hook、事件语义。
4. **可回退**：任何层级都要能一键回到"全量暴露"，且默认行为在首个版本保持不变。
5. **可观测**：每次请求都能回答"暴露了几个 / 延迟了几个 / 估了多少 token / 本轮新发现了谁"。
6. **缓存优先**：输出必须**确定性排序**且**只增不减**（发现是单调的）。

### 7.2 分层与组件

```
                 ┌──────────────────────────────────────────────┐
                 │ SessionToolSchemaResolver (唯一出口, 不变)     │
                 │  1) SessionSettlement  → settledToolIds       │
                 │  2) ToolManager        → 目标 id 的 schema     │
                 │  3) IToolExposurePolicy[] → ToolExposurePlan   │
                 │  4) 会话发现态合并 (discovered ∪ inline)        │
                 └───────────────┬──────────────────────────────┘
                                 │ ToolExposurePlan
                    ┌────────────┴─────────────┐
                    ▼                          ▼
        ChatExecutionContext.ToolSchemas   SectionIds += "tools.catalog"
                    │                          │
                    ▼                          ▼
        AgentExecutor → ChatRequest.Tools   PromptBuilder → system prompt
                                 │
                    ┌────────────┴─────────────┐
                    ▼                          ▼
        IToolCatalogStore（全量 schema 真相源）  IToolDiscoveryStore（发现态）
```

| 组件 | 层 | 职责 |
|---|---|---|
| `ToolExposureMode` / `ToolCatalogEntry` / `ToolExposurePlan` | Abstractions | 纯 DTO |
| `IToolExposurePolicy` | Abstractions | 策略链：输入目录 + agent + 预算 → plan |
| `IToolCatalogStore` | Abstractions | 按 id 取 entry（摘要 + token 估算），按 id 取完整 schema（懒生成） |
| `IToolDiscoveryStore` | Abstractions | 会话级"已发现 id 集"的读写（持久化、fork 继承、可裁剪） |
| `ToolCatalogComposer`（策略实现） | Core 或 Hosting | 默认 `tiered` 算法：pinned + 预算内滚轮选签名（opencode 式） |
| `ToolSearchTool` / `ToolLoadTool` | capabilities（新模块） | 元工具，返回 entry；`detail=full` 时记录发现态 |
| `ToolCatalogSectionContributor` | Hosting | 把目录渲染成 `tools.catalog` 分节（固定预算） |

> 落点选择：策略实现放 **Hosting**（它需要 `IModuleCatalog` / `IScenarioCatalog` / Options），契约放 Abstractions，元工具放独立能力包。符合"Core 瘦脊柱、能力经 `AddSeeingModule`"。

### 7.3 数据流（一轮执行）

```
ExecutionRunner.RunOnceAsync
  └─ SchemaResolver.SettleAsync(session, agent)
       ├─ SessionSettlement.Compute                        （不变）
       ├─ dynamicContributors.GetDynamicToolIds            （不变）
       ├─ ToolManager.GetToolSchemasAsync(ids, agent)       ← 改造为"先求交集再生成" + 稳定排序 + 缓存
       ├─ discovery = IToolDiscoveryStore.Get(session)
       ├─ plan = await policies.PlanAsync(catalog, agent, discovery, budget)
       │     · inline  = pinned ∪ agent 显式 include ∪ discovery
       │     · deferred= 其余（进入目录摘要）
       │     · catalog = 预算内签名（确定性排序）
       ├─ IToolDiscoveryStore.PruneIfObsolete(discovered)   （可选，压缩后）
       └─ return { ToolSchemas = inline ∪ discovered, SectionIds += "tools.catalog" }
AgentExecutor.BuildRequestAsync                             （零改动）
```

**关键点**：`discovery` 的合并发生在 resolver 内，因此 `AgentExecutor` 完全不需要知道"延迟加载"的存在——这是保证不出现"双算 schema"的唯一干净做法。

### 7.4 契约草案

```csharp
namespace Seeing.Agent.Abstractions.Tools;

/// <summary>工具对模型的暴露形态。</summary>
public enum ToolExposureMode
{
    /// <summary>完整 schema 直接进入 ChatRequest.Tools。</summary>
    Inline,
    /// <summary>仅出现在目录摘要；模型需 tool_search / tool_load 才可见。</summary>
    Deferred,
    /// <summary>本轮完全不可见（含目录）。</summary>
    Hidden
}

/// <summary>目录条目：模型可见的最小信息 + 成本估算。</summary>
public sealed record ToolCatalogEntry(
    string Id,
    /// <summary>命名空间（如 "filesystem" / "mcp__docs"），用于分组与检索加权。</summary>
    string? Namespace,
    /// <summary>单行摘要（含签名，截断到 CatalogLineMaxChars）。</summary>
    string Summary,
    /// <summary>完整描述（tool_load 的 detail=full 返回）。</summary>
    string? Description,
    /// <summary>完整 schema 的估算 token（用于预算与回灌）。</summary>
    int EstimatedTokens,
    bool Pinned,
    ToolExposureMode Mode);

/// <summary>本轮暴露计划。</summary>
public sealed record ToolExposurePlan(
    IReadOnlyList<FunctionToolSchema> InlineSchemas,
    IReadOnlyList<ToolCatalogEntry> CatalogEntries,
    int DeferredCount,
    int EstimatedDefinitionTokens,
    IReadOnlyList<string> DiscoveredIds);

/// <summary>暴露策略：在 schema 结算之后、注入之前裁剪。</summary>
public interface IToolExposurePolicy
{
    /// <summary>策略名（用于配置与诊断）。</summary>
    string Name { get; }

    /// <summary>计算本轮暴露计划；实现必须确定性、无副作用、可取消。</summary>
    Task<ToolExposurePlan> PlanAsync(ToolExposureContext context, CancellationToken cancellationToken = default);
}

/// <summary>策略输入。</summary>
public sealed record ToolExposureContext(
    string SessionId,
    AgentDefinition Agent,
    IReadOnlyList<ToolCatalogEntry> Available,
    /// <summary>会话内已发现（已 inline）的工具 id；调用方保证只读快照。</summary>
    IReadOnlySet<string> DiscoveredIds,
    /// <summary>本会话的目录预算（token）。</summary>
    int CatalogBudgetTokens);

/// <summary>按 id 提供摘要与完整 schema 的真相源。</summary>
public interface IToolCatalogStore
{
    Task<IReadOnlyList<ToolCatalogEntry>> ListAsync(
        IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken = default);

    Task<FunctionToolSchema?> GetSchemaAsync(string toolId, CancellationToken cancellationToken = default);
}

/// <summary>会话级发现态（单调增长、可裁剪）。</summary>
public interface IToolDiscoveryStore
{
    IReadOnlySet<string> Get(string sessionId);
    void Add(string sessionId, IEnumerable<string> toolIds);
    void Prune(string sessionId, IEnumerable<string> keepToolIds);
}
```

> 兼容性：`SessionToolSchemaResolution` 增加可选字段（`CatalogEntries` / `DeferredCount` / `EstimatedDefinitionTokens`），保留既有两参构造重载，避免破坏 Scheduler 旁路与测试。

### 7.5 会话发现态（最容易做错的地方）

| 问题 | 设计 |
|---|---|
| 存哪里 | `SessionData` 新增 `ToolDiscovery`（或 `Metadata["tool_discovery"]`），随会话持久化 |
| 何时写 | `tool_search(detail=full)` / `tool_load` 成功时；**只增** |
| 何时读 | 每次 `SettleAsync` 合并进 inline 集 |
| fork 怎么办 | 子会话继承父会话的发现集（与 MCP 定位符同理：不复制产物，只继承 id） |
| 压缩后怎么办 | 压缩**不移除**发现态（它只是 schema 可见性，不占消息 token）；若发生 Session 重建/清空，回退到 pinned + 重搜 |
| 工具消失怎么办 | 合并前与可用集求交；失效 id 静默丢弃（不要报错，模型会重试） |
| 缓存一致 | 合并顺序 = `pinned(声明序) + discovered(发现序)`，**append-only**，绝不重排 |

### 7.6 提示词与发现引导（本项目具体写法）

新增分节 `tools.catalog`（`IPromptSectionContributor`，order 放在 `## Tools` 之前）：

```
## Tool catalog (partial)

You can call the tools in the API `tools` array directly.
The following capabilities exist but are NOT loaded yet. Call `tool_search` with
keywords (or a namespace name) to load them. Loaded tools become available for
the rest of this session.

- filesystem (8 tools)      // read/write files in the workspace
  - read(path, offset?, limit?)      // Read a file; returns numbered lines
  - edit(path, oldString, newString?) // Replace exact text in a file
  - ... (8 tools, 2 shown)
- session (8 tools)         // list/search/trim sibling sessions
- scheduler (6 tools)       // cron jobs
- mcp__docs (35 tools, 0 shown)
- mcp__sentry (12 tools, 0 shown)

Rules:
- Search before assuming a capability is missing. An empty result lists available namespaces.
- Do not call tools that are not in `tools` and were not returned by `tool_search`.
```

配套改动：

1. **Agent 级引导**：`build` 的 `SystemPrompt` 加一段"何时搜索"（对应 Claude 的 "Use the tool search to find specific capabilities"）。
2. **description 规范化**：每个工具的 description 第一行改成"路由句"（`读取工作区文件；当需要精确行号或大文件分段时使用`），第二行才是细节。这条改动**零成本、立刻提升检索命中**。
3. **命名空间归一**：MCP 工具 id 改为 `mcp__{server}__{tool}`（同时修 `ToolsCommands` 的 `mcp_` 判定），`ToolCatalogEntry.Namespace` = `mcp__{server}`。
4. **空结果语义**：`tool_search` 未命中时返回 `未命中；可用命名空间：filesystem(8), session(8), mcp__docs(35)…`，不返回空串。
5. **一次搜索即发现**：`detail=full` 默认对 top-N（默认 3）自动写入发现态，避免"搜一次→再 load 一次"的双轮成本。
6. **目录落盘（方案 C 融合）**：把完整目录写到 `.seeing/tool-catalog/{namespace}.md`（含完整 schema 与示例），在目录摘要里给一行"完整定义见 `.seeing/tool-catalog/`"。模型可用 `read`/`grep` 深挖——零新增执行面。

### 7.7 可观测与预算

| 指标 | 落点 | 用途 |
|---|---|---|
| `SchemaSnapshotEvent` 增加 `InlineToolCount` / `DeferredToolCount` / `EstimatedDefinitionTokens` / `DiscoveredIds` | `ExecutionRunner`（已发该事件） | 前端与日志可见"每轮暴露成本" |
| `SourceBreakdown.ToolDefinitions` 接线 | `TokenBudgetManager.CalculateBreakdown(..., toolTokens)` | 压缩决策感知工具膨胀 |
| `ToolSearchEvent`（query / hit ids / detail） | 新事件 | 评估 description 质量与命中率 |
| `tool-definition` Hook 缓存键含 `hookVersion` | `ToolManager` | 有副作用 Hook 下不死缓存 |
| 指标口径 | 每次请求打印 `inline / deferred / catalogTokens / schemaTokens` 一行 Debug 日志 | 回归与压测 |

预算策略（建议默认值，全部可配）：

```jsonc
"Tools": {
  "Exposure": {
    "Mode": "tiered",              // all | tiered | search | code
    "PinnedTools": ["read","edit","write","bash","grep","glob","task","todowrite"],
    "InlineBudgetTokens": 4000,     // inline schema 的软上限
    "CatalogBudgetTokens": 600,     // 目录摘要预算（opencode 默认 ≈500）
    "AutoThresholdRatio": 0.10,     // 定义总量 < 上下文 10% 时退回 all（Claude Code 策略）
    "SearchDetail": "summary",      // name | summary | full
    "MaxCatalogLines": 24
  }
}
```

`AutoThresholdRatio` 是**防倒退开关**：小工具集（子 Agent、summary Agent）自动保持全量，不白付搜索延迟。

### 7.8 配置面与作用域

| 层级 | 字段 | 说明 |
|---|---|---|
| 全局 | `SeeingAgentOptions.Tools.Exposure.*` | 默认策略 |
| Scenario | `Scenario.Tools.Exposure` | 覆盖（如 `secure` 场景全量 pinned、不延迟） |
| Agent | `AgentDefinition.ToolExposure`（**新增字段**） | 如 `explore` 用 `search` 模式、`summary` 用 `all` |
| 会话 | `Session.ScenarioOverride.Tools.Exposure` | 运行期切换，热生效（下一轮 `SettleAsync` 读到） |

**明确不动**：`AllowedTools` / `DeniedTools` 仍只做"能不能执行"（权限 + 可见性的取交集），不参与 exposure 计算之外的语义变更；exposure 只决定"是否 inline"，不授予任何新权限。

---

## 8. 三种方案详设

### 8.1 方案 A：分级延迟 + 元工具检索（推荐主干）

**形态**：pinned 常驻 + 其余进目录 + `tool_search` / `tool_load` + 会话发现态。

**落地清单**：

| 步骤 | 改动 | 文件 |
|---|---|---|
| A0 | 修"先全量生成再过滤"：`GetToolSchemasAsync(settled, agent)` 先求 id 交集，再逐个生成 | `ToolManager.cs:88-103` |
| A1 | 生成结果按 id **稳定排序**；per-request 缓存（key = ids+agent+disabledVersion+hookVersion） | `ToolManager.cs:290-325` |
| A2 | 新契约 | `Abstractions/Tools/ToolExposure*.cs` |
| A3 | 默认 `TieredToolExposurePolicy`（pinned + 预算滚轮） | `Hosting/Execution/` 新文件 |
| A4 | `SettleAsync` 末尾接入策略链 + 发现态合并 | `SessionToolSchemaResolver.cs` |
| A5 | `tools.catalog` 分节贡献者 | `Hosting/Prompts/` |
| A6 | 新模块 `Seeing.Agent.Tools.ToolDiscovery`（`tool_search` / `tool_load`） | `src/capabilities/` |
| A7 | `ExecutionRunner` 依赖改为 `ISessionToolSchemaResolver` | `ExecutionRunner.cs:61,72,82` |
| A8 | 事件/指标接线 | `SchemaSnapshotEvent`、`SourceBreakdown` |
| A9 | 配置 + Scenario/Agent 覆盖 | `SeeingAgentOptions`、`AgentDefinition` |

**优点**：多 Provider 通用；不引入执行面；可逐场景灰度；与现有 Skill 模式同构；发现态天然持久。
**缺点**：T2/T3 不动；冷门工具多一轮；依赖 description 质量（需治理）。
**不适用**：极长链数据流（此时上 B）。

### 8.2 方案 B：Code Mode（可选能力包）

**形态**：只暴露 `run_code` + 代码内 `tools.<ns>.<name>()` 子分发 + 受限执行器。

**两种实现，强烈建议先做 B1**：

| | B1 受限解释器（opencode 风格） | B2 进程/沙箱（Cloudflare 风格） |
|---|---|---|
| 语言 | 自定义子集（调用/算术/await/if/for） | TS / Python 真运行时 |
| 隔离 | 能力面 = 语言子集（无 I/O） | 进程/容器 + 文件与网络策略 |
| 依赖 | 无（纯 C#） | 容器/沙箱基础设施 |
| 安全 | 高（无逃逸面） | 取决于沙箱实现 |
| 表达力 | 中（够用：串联、过滤、聚合） | 高（任意库） |
| 工程量 | 中 | 高 |

**必须解决的五个问题**：

1. **子调用复用完整执行链**：`run_code` 内的每次调用必须走 `ToolManager.InvokeAsync`，从而复用 pre-execute / guard / 权限 / 审批 / 装饰器 / 事件。**禁止旁路**。
2. **事件语义**：子调用产生 `tool/ptc-dispatch` 类事件（只记 id/name/args/结果摘要），**不把 description/parameters 写进事件**。
3. **结果回流边界**：子调用结果默认**不回上下文**，只进执行环境；只有 `console.log` / `return` 的值回模型（外层受 `ToolOutputLimiter` 约束）。
4. **UI**：子调用需要在卡片里聚合（可复用 `TaskCardAggregator` 的思路），否则用户看不到"代码里调了什么"。
5. **降级**：解释器不支持某语法 → 明确诊断 + 建议改用直连工具调用（不要静默失败）。

**优点**：同时解决 T1/T2/T3；一次推理完成编排；天然适合 MCP 长尾。
**缺点**：模型需要学一门"受限方言"；调试/可观测更复杂；权限语义变复杂（"代码能不能调用 ask 级工具"需要明确策略）。
**建议**：作为 `mode: code` 的场景化能力，默认关闭；先在 Scheduler 批量任务 / 大 MCP 集合场景试点。

### 8.3 方案 C：文件系统目录（L3，补充）

**形态**：把工具目录与完整 schema 渲染到工作区只读目录，模型用 `read`/`glob`/`grep` 浏览。

```
.seeing/tool-catalog/
├── index.md                    // 命名空间 → 工具数 → 一句话
├── filesystem/
│   ├── read.md                 // 完整 schema + 示例 + 权限说明
│   └── edit.md
└── mcp__docs/
    └── search_docs.md
```

**优点**：零新增执行面（复用现有文件工具）；完整定义不占 token（只在你读的时候才占）；对超大 MCP 集合友好；可被 `grep` 跨工具检索。
**缺点**：依赖模型主动探索；`grep` 噪音大；无法强制"目录→可用"的可见性闭环（读了文件 ≠ 工具可用）。
**定位**：A 的**详情页**——目录摘要里给路径，`tool_search` 返回 entry 时也附路径。让模型"想深挖时能深挖"，而不是作为主发现通道。

### 8.4 推荐组合形态

```
默认（build / general）  : A（tiered + search，pinned 8 个）
小工具集（summary/explore）: all（AutoThresholdRatio 自动判定，避免倒退）
大 MCP / 长链任务        : A + B（code 模式）+ C（目录落盘）
受限场景（secure）        : all + 全 pinned（不延迟，可审计）
```

---

## 9. 落地路线与验收

### P0：清债（不改行为，1–2 周）

- 修"先全量生成再过滤"；稳定排序；schema 缓存；`ToolDefinitions` token 接线；`ExecutionRunner` 依赖接口化；MCP 命名归一 + 修 `/mcp list`。
- **验收**：`tools[]` 顺序在连续 100 次请求中完全一致；`SourceBreakdown.ToolDefinitions > 0`；单轮 schema 生成的 Hook 调用次数 = 目标工具数（而非全部注册数）；全部既有测试绿。

### P1：方案 A 主干（2–3 周）

- 契约 + `TieredToolExposurePolicy` + `tools.catalog` 分节 + `tool_search`/`tool_load` + 发现态持久化 + 指标。
- 默认 `Mode=all` 上线（行为不变），随后在 WebUI `full` 场景切 `tiered` 灰度。
- **验收**：
  - 51 内置工具场景：首轮 input tokens 降 ≥60%；
  - 50 个 MCP 工具场景：降 ≥85%；
  - 工具调用成功率（对比基线）不下降，理想情况上升；
  - 前缀缓存命中率不下降（同会话相邻请求的 `tools[]` 前缀稳定）；
  - 冷门工具首次调用平均多 1 轮，但端到端步数增幅 ≤10%。

### P2：目录增量 + 落盘（1–2 周）

- MCP 连接/断开、模块启停 → 目录 **delta 注入**（新增/变更/移除各一段）；无法表达的才全量替换。
- 目录落盘 `.seeing/tool-catalog/`（方案 C），摘要里带路径。
- **验收**：MCP 动态增删后，模型不再"看到旧工具"或"看不到新工具"；目录变更不导致全量前缀失效（delta 场景）。

### P3：可选 Code Mode（3–6 周，按需）

- B1 受限解释器 + `run_code` + 子分发复用执行链 + 子调用事件 + UI 聚合 + `mode: code` 配置。
- **验收**：一个"取 1 万行数据→过滤→写文件"的任务，input tokens 相比直连调用降 ≥70%，步数从 N 降到 1–2。

### 回退开关

每个阶段独立开关（`Tools.Exposure.Mode`），任一阶段出问题可退回 `all`；发现态与目录都是**附加物**，退回后完全不影响既有行为。

---

## 10. 风险与对策

| 风险 | 触发条件 | 对策 |
|---|---|---|
| 搜索未命中，模型放弃/幻觉 | description 差、命名混乱 | 空结果带命名空间建议；`detail=full` 自动发现前 3；目录显示 "N tools, M shown"；description 第一行路由句治理 |
| 每轮多一轮延迟 | 冷门工具首次使用 | pinned 3–8 个高频；`AutoThresholdRatio` 小工具集不延迟；一次搜索即发现（后续零成本） |
| 前缀缓存失效 | 发现集每次重排 / 工具顺序抖动 | 稳定排序 + 发现态 append-only；移除只发生在"整体重建"（压缩/重连）时 |
| 权限被绕过 | 把 exposure 当权限 | 显式分离：exposure 只影响可见性；执行仍走 `IToolPermissionPolicy` + 审批；`tool_load` 不授予权限 |
| Hook 副作用被缓存吞掉 | `tool.definition` Hook 有状态 | 缓存键含 `hookVersion`；或提供 `cacheable: false` 能力标记 |
| 工具定义与消息共享预算导致误压缩 | 只看 messages token | `SourceBreakdown.ToolDefinitions` 接线；压缩优先降 exposure 档位（`tiered`）而不是砍对话 |
| MCP 工具名冲突 | `server_tool` 拼接歧义 | 统一 `mcp__{server}__{tool}`；`a_b`/`c` 不再歧义；同步修 `ToolsCommands` |
| 发现态膨胀 | 长会话不断发现 | 发现态有上限（如 200 个 id，超出按最近使用裁剪）；工具消失时求交丢弃 |
| Code Mode 子调用失控 | 无限循环/海量调用 | `maxToolCalls` / `timeout` / `maxOutputBytes` 强制默认值（**不要学 opencode 的"无默认"**，我们应给安全默认） |
| 网关/Provider 拒绝 | 某些兼容网关不支持新字段 | 我们走纯客户端，不引入非标准字段；若未来接 `defer_loading`，必须带自动降级（Claude Code 的重试策略） |

---

## 11. 与现有架构纪律的对齐检查

| 纪律 | 本设计是否遵守 | 说明 |
|---|---|---|
| Core 瘦脊柱（零工具） | ✓ | 策略实现放 Hosting，元工具放 `src/capabilities/` |
| 能力经 `AddSeeingModule` | ✓ | `Seeing.Agent.Tools.ToolDiscovery` 独立模块，`ProvidedTools` + Activate 挂载 |
| schema 只在 `SessionToolSchemaResolver` 计算 | ✓ | plan 是 resolver 的内部阶段，不新增第二个计算点 |
| 会话驱动唯一入口 `DeliverAsync` | ✓ | 不新增执行入口；发现态在 resolver 阶段读写 |
| Abstractions 零实现 | ✓ | 只放枚举/记录/接口 |
| 依赖只能向下 | ✓ | 新契约只依赖 Abstractions + 原语 |
| 禁止 executor 双算 schema | ✓ | `AgentExecutor` 零改动 |
| Hook 点用 `HookPoints.*` | ✓ | 不新增魔法字符串 |
| 配置只在 `seeing.json` | ✓ | `Tools.Exposure` 走既有配置节 |
| WebUI 禁 CPM | ✓ | 不涉及 |

---

## 12. 附录

### 12.1 参考资料

| 资料 | 结论一句话 |
|---|---|
| https://www.anthropic.com/engineering/advanced-tool-use | Tool Search / PTC / Tool Use Examples 三件套；55K→8.7K；精度 49%→74% |
| https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool | `defer_loading` 语义、`tool_reference` 展开、缓存不影响、上限 10000、三档 detail、自定义检索 |
| https://www.anthropic.com/engineering/code-execution-with-mcp | 工具即文件树；`search_tools` + detail level；150K→2K；需沙箱 |
| https://blog.cloudflare.com/code-mode/ | isolate 沙箱；bindings 隔离与密钥隐藏；整包 API 注入（未来可搜索） |
| https://code.claude.com/docs/en/mcp | 阈值 `auto:N`（默认 10%）；`alwaysLoad` 双向覆盖；失败经搜索通道告知；网关降级 |
| https://opencode.ai/docs/mcp-servers/ | MCP 上下文膨胀的官方告警；per-agent 工具开关；glob 前缀 |
| https://opencode.ai/docs/tools/ | 内置工具集规模（≈13）与权限模型 |
| https://github.com/anomalyco/opencode/tree/dev/packages/codemode | 受限 JS 解释器；无 I/O；`Result` 诊断分类；限制项 |
| `packages/core/src/codemode/catalog.ts`（opencode） | 目录预算算法：2000 字符、120 字符描述截断、namespace 轮转、pinned |
| `packages/core/src/codemode/instructions.ts`（opencode） | 目录以 Instruction 注入；delta 增量更新；partial 目录的提示语 |
| https://github.com/deepseek-ai/deepseek-harness | 一切皆插件（Cordis）；`mode: native \| ptc` |
| `docs/subsystems/tools.md`（dsh） | ToolDefinition/执行 pipeline；`schemas()` 白名单（不泄漏 execute/timeout）；每个可见面标 Token/KV Cache 影响 |
| `docs/subsystems/llm-streaming.md`（dsh） | `tool-addition`/`tool-removal` developer 消息；`deferLoading`；`toolHistory` 重建 |
| `docs/subsystems/spill.md` / `compaction.md`（dsh） | 落盘 + retrievalHint；head/middle/tail 按码点裁剪；`compaction/prune` 记录被遮蔽节点 |
| `docs/subsystems/skills.md`（dsh） | `<available_skills>` 只含 name+description；digest 比对增量注入 |
| https://duckduckgo.com（失败） | 未采用（改用官方站点与源码） |

### 12.2 术语表

| 术语 | 含义 |
|---|---|
| 定义税（T1） | `tools[]` 作为每轮固定前缀的 token 成本 |
| 结果税（T2） | 工具结果进入历史后被反复重发的成本 |
| 中间态税（T3） | 多步数据流中结果必须在模型上下文里过一遍的成本 |
| deferred / 延迟加载 | 工具定义不进入本轮上下文，只在被检索到后展开 |
| `tool_reference` | Claude 的最小引用块；服务端负责展开成完整定义 |
| pinned / 常驻 | 永远 inline 的高频工具集合 |
| 发现态（discovery set） | 会话级"已被发现/已加载"的工具 id 集，单调增长 |
| 目录（catalog） | 模型可见的工具摘要集合（通常放系统提示，不进 `tools[]`） |
| Code Mode / PTC | 用代码编排工具调用，中间结果留在执行环境 |
| spill | 超长结果落盘 + 返回定位符与检索提示 |
| prune | 对历史 tool result 做裁剪，并以新事件遮蔽旧事件（可审计） |
