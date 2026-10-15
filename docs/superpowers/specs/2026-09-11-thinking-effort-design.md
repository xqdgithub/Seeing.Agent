# 思考强度（Thinking Effort）设计

**日期:** 2026-09-11  
**状态:** 已审查通过（可实现）  
**目标:** 快速落地——模型目录声明档位，会话可选，兼容 API 统一出站；一次做完整，无分期、无 `wire`。

---

## 1. 目标与非目标

### 目标

- 按模型配置其支持的思考档位（`key` + 可选 `label`）
- 会话页可改当前档位；Gateway / `ChatOptions` 可覆盖
- OpenAI 兼容路径统一传 `thinking` + `reasoning_effort`；Anthropic 路径由 Client 只读 `ChatRequest`（`ThinkingEffort` + `ThinkingBudgetTokens`）补 Messages 字段
- 多轮工具场景出站回传 `reasoning_content`
- Provider（如 DeepSeek）自行填充 `ModelConfig.Options.Thinking`

### 非目标

- **不做** `wire` / `compat` / 协议策略枚举（现在与以后都不做）
- **不做** per-level 任意 `emit` JSON
- **不做** 跨厂商统一语义档（low/medium/high 全局枚举）；档位完全跟模型走
- **不把** 思考强度挂到 `AgentDefinition`（与 Temperature 分离；跟 ModelId 同路）

---

## 2. 数据模型

### 2.1 `ModelConfig.Options.Thinking`（扩展现有类型）

路径：`Seeing.Agent.Abstractions/Llm/ModelConfig.cs`

保留现有 `type`、`budgetTokens` 以兼容旧序列化；新增：

| 字段 | 类型 | 说明 |
|------|------|------|
| `supported` | `bool` | 默认 `false`；为 true 且 `levels` 非空才启用 UI/出站 |
| `default` | `string?` | **可选**默认档 `key`。有值时应落在 `levels` 内；**不填则表示「不主动传思考字段」**（与今日行为一致，不影响使用） |
| `levels` | `List<ThinkingLevel>?` | 模型支持的档位 |

`ThinkingLevel`：

| 字段 | 类型 | 说明 |
|------|------|------|
| `key` | `string` | 会话存储值；非关闭档时作为 `reasoning_effort` / Anthropic `effort` **原样**出站 |
| `label` | `string?` | UI 文案；缺省显示 `key` |
| `budgetTokens` | `int?` | 仅 Anthropic 固定预算模式使用 |

关闭档约定（大小写不敏感）：`disabled` | `off` | `none`。

配置示例（DeepSeek；`default` 可省略）：

```json
"options": {
  "thinking": {
    "supported": true,
    "levels": [
      { "key": "disabled", "label": "关闭" },
      { "key": "high", "label": "高" },
      { "key": "max", "label": "最大" }
    ]
  }
}
```

### 2.2 会话与请求

| 位置 | 字段 | 说明 |
|------|------|------|
| `SessionData` | `SelectedThinkingEffort` | 选中 `key`；空 = 尝试模型 `default`；**default 也空则出站不传思考字段** |
| `ChatOptions` | `ThinkingEffort` | 请求级覆盖 |
| `ChatRequest` | `ThinkingEffort` | **仅**经 `LlmService.PrepareClientRequest` 规范化后的 key；Execution 可先写入原始候选值 |
| `ChatRequest` | `ThinkingBudgetTokens` | **可选**；由 `LlmService` 在规范化时写入（规则见 §4.2）。`null` = Anthropic 走 adaptive。OpenAI 客户端忽略此字段 |
| `GatewayRequest` | `ThinkingEffort` | 可选；映射到 `ChatOptions` |

**Client 不得回查 `ModelConfig` / 目录。** Anthropic 只读 `ThinkingEffort` + `ThinkingBudgetTokens`；OpenAI 只读 `ThinkingEffort`。关闭档判定用约定 key（`disabled`/`off`/`none`），无需再查 levels。

Fork / 子会话：与 `SelectedModel` 一并拷贝 `SelectedThinkingEffort`。

---

## 3. 职责切分（快速实现约束）

```
Provider / providers.json
  → 填充 ModelConfig.Options.Thinking

Session / ChatOptions / Gateway
  → 只存/传 key 字符串

ExecutionJobService（与 ModelId 同路）
  → 把候选 key 写入 ChatRequest.ThinkingEffort

LlmService.PrepareClientRequest（已有 GetModel）
  → 规范化 ThinkingEffort（不支持清空；∉ levels 则：有 default 回落 default + Warning，无 default 则清空不传）
  → 按 §4.2 解析并写入 ThinkingBudgetTokens
  → 不拼 vendor JSON（不出现 reasoning_effort / thinking.type 等字段名）

OpenAiChatClient / AnthropicClient
  → 只读 ChatRequest 上已填字段，按本协议写 HTTP body
```

**禁止：** 在 `LlmService` / Core 中硬编码出站 JSON 字段名；**禁止** Client 注入目录查找。

---

## 4. 出站映射（Client 内完成）

### 4.1 OpenAI 兼容（`OpenAiChatClient`）

统一一种写法（DeepSeek / OpenAI / 其它兼容端共用）：

| `ThinkingEffort` | Body |
|------------------|------|
| 关闭档（`disabled`/`off`/`none`，忽略大小写） | `"thinking": { "type": "disabled" }` |
| 其它非空 | `"thinking": { "type": "enabled" }` + `"reasoning_effort": "<key 原样>"` |
| null / 空 | 两字段都不发 |

`ChatCompletionRequest` 增加可空字段；序列化时缺省省略。忽略 `ThinkingBudgetTokens`。

### 4.2 Anthropic（`AnthropicClient`）

无策略枚举。Client **只**根据 `ChatRequest`：

| 条件 | Body |
|------|------|
| 关闭档或 `ThinkingEffort` 空 | 不发 `thinking` |
| `ThinkingBudgetTokens` 有值 | `thinking: { type: "enabled", budget_tokens: N }`（N&lt;1024 → 抬到 1024） |
| 否则（有非关闭 key） | `thinking: { type: "adaptive" }` + `output_config: { effort: "<key 原样>" }` |

**`ThinkingBudgetTokens` 由 LlmService 解析（Client 不查表）：**

```
匹配 levels 中与最终 ThinkingEffort 相等的 ThinkingLevel：
  若 level.budgetTokens 有值 → 用该值
  否则若顶层 Thinking.BudgetTokens 有值 → 用顶层（legacy 兼容）
  否则 → null（adaptive）
关闭档或 ThinkingEffort 清空后 → ThinkingBudgetTokens = null
```

若启用 budget 且 `budget_tokens` 接近/超过 `max_tokens`，将 `max_tokens` 提升为至少 `budget_tokens + 1024`（或与现有客户端一致的安全余量）。

### 4.3 思考内容出站回传（对齐 OpenCode；OpenAI 兼容 + Anthropic 完整方案）

> 与「思考强度档位」正交：档位控制**想不想 / 想多深**；本节控制**历史思考如何写回下一轮请求**。

#### 厂商差异（依据 OpenCode）

| 协议 | 出站形态 | 能力开关 |
|------|----------|----------|
| OpenAI 兼容 Chat Completions | 消息级 `reasoning_content` | 模型 `interleaved: "reasoning_content"`（DeepSeek 等）；多数原生 OpenAI **不设** |
| Anthropic Messages | content 块 `thinking` + **`signature`** | **协议内建**：有思考数据则回写块；**必须持久化 signature**，否则工具多轮会失败 |

OpenCode：`interleaved` 主要用于 openai-compatible；Anthropic 路径把 reasoning part **连同 signature** 降成 `thinking` 块（见 `anthropic-messages.ts` + `signature_delta`）。

---

#### A. 目录：`ThinkingOptions.interleaved`（仅 OpenAI 兼容回传）

```json
"interleaved": "reasoning_content"   // 或缺省不设
```

| 值 | 含义 |
|----|------|
| 缺省 / null | OpenAI 兼容 Client **不**写 `reasoning_content` |
| `"reasoning_content"` | OpenAI 兼容 Client 写出该字段 |
| 其它 | 忽略 + Debug 日志 |

预置：DeepSeek → `"reasoning_content"`；原生 OpenAI → 不设。  
**Anthropic 不依赖此字段**（见 B）。

---

#### B. 存储（Anthropic 正确回传的前提）

扩展（Abstractions + Session 对称持久化）：

| 类型 | 字段 | 说明 |
|------|------|------|
| `ChatMessage` | `ReasoningSignature` | Anthropic thinking 块 `signature`；OpenAI 路径通常为空 |
| `SessionMessage` | `ReasoningSignature` | 落盘；与 `ReasoningContent` 一并克隆 / Fork / 映射 |

`ExecutionJobService` / AgentExecutor：ChatMessage ↔ SessionMessage 双向拷贝 `ReasoningContent` **与** `ReasoningSignature`。

流式：`StreamUpdate` 增加可选 `ReasoningSignature`（或在 reasoning 结束事件带 signature）；AgentExecutor 在拼最终 assistant 消息时写入 `ReasoningSignature`。  
非流式：从 content block `type=thinking` 的 `signature` 字段读取。

---

#### C. 运行时标志（OpenAI 兼容）

| `ChatRequest` | 说明 |
|---------------|------|
| `EchoReasoningContent` | bool；`LlmService` 根据 `interleaved == reasoning_content` 且思考未关闭时置 true |

Anthropic **不**使用该标志决定是否回写 thinking 块（见 D）。

---

#### D. OpenAiChatClient

- `EchoReasoningContent &&` 有非空 `ReasoningContent` → 写出 `reasoning_content`
- 否则不写  
- **忽略** `ReasoningSignature`

---

#### E. AnthropicClient（本期必须做完）

**入站**

- 解析 `thinking` / `thinking_delta` → `ReasoningContent`（已有）
- 解析 `signature`（非流式 block）/ `signature_delta`（流式）→ `ReasoningSignature`
- 多个 thinking 块：文本拼接；**signature 取最后一个非空**（与单块续写主流用法一致；若同轮多块需多 signature，后续再扩展为列表——本期单 signature 足够对齐 OpenCode 常见路径）

**出站 `BuildContent`（assistant）**

在 text / tool_use **之前**插入 thinking 块，当且仅当：

```
有非空 ReasoningContent，或有非空 ReasoningSignature
```

```json
{ "type": "thinking", "thinking": "<ReasoningContent 或 \"\">", "signature": "<ReasoningSignature>" }
```

- 有 signature：必须带上（Anthropic 工具多轮硬需求）
- 仅有文本、无 signature：仍写出 `thinking` 文本块（无 signature 字段）；若 API 拒绝，属模型/模式限制，打 Warning——但**有 signature 时绝不能丢**
- tool_use 轮次同样前置 thinking（当前 `BuildContent` 在 ToolCalls 分支未插 thinking → **必须改**）

**与 §4.2 关系**

- §4.2：请求级打开 thinking（budget / adaptive）
- §4.3 E：历史消息级回传 thinking 块  
两者都要：只开 §4.2 不回传签名 → 工具多轮仍会坏。

---

#### F. Token

- OpenAI：仅 `interleaved` 模型回传，默认不增  
- Anthropic：回传 thinking 计入后续输入；这是 extended thinking + tools 的协议成本（与 OpenCode 相同）

---

#### G. 本节审查点

1. OpenAI：默认不回传；`interleaved=reasoning_content` 才回传  
2. Anthropic：持久化 `ReasoningSignature`；出站 thinking 块带 signature；tool_use 分支也前置  
3. Client 零查表（Echo 标志 / 消息自带字段）  
4. 不做半残「只回传文本、丢掉已有 signature」  

---

## 5. 解析优先级与换模型

优先级：`ChatOptions.ThinkingEffort` > 会话 `SelectedThinkingEffort` > `Model.thinking.default`（若有）> **不传**（两字段都不发，与现状一致）。

`default` **可以不配**。未选档且无 default 时，行为与今天相同：请求里不出现 `thinking` / `reasoning_effort`，不影响正常调用。

流程固定：**Execution 写入候选 key → 仅 `PrepareClientRequest` 规范化 `ThinkingEffort` 并填写 `ThinkingBudgetTokens` → 再交给 Client。** Hook `chat.params` 若改 `thinkingEffort`，须在 hook 回写之后再次（或同路径）按当前模型校验并重算 `ThinkingBudgetTokens`。

换模型：若旧 key ∉ 新 `levels` → 清空会话字段；UI 回到「未选择 / 跟随默认」——有 `default` 则显示该档，**无 default 则保持未选（不传）**，不要强行选第一档。

---

## 6. UI（WebUI）

- 位置：`SessionToolbar` 模型选择器旁
- `thinking.supported && levels.Count > 0` 时显示；否则隐藏
- 选项：`label ?? key`；值：`key`；另可有「默认」项表示清空会话值（有模型 `default` 则出站用 default，否则不传）
- 变更 → `SessionManager` 持久化（新增 `SetThinkingEffortAsync` 或并入现有 set API）

---

## 7. Provider 填充

- `DeepSeekModelCapabilities`：填写 `supported` + `levels`；`default` **可选**（可不填，则未选档时不传）。档位 key 与官方对齐（如 `disabled`/`high`/`max`）
- `PredefinedModels` 中带思考的模型：补齐 `levels`，避免仅有旧 `type/budgetTokens` 却无 levels 导致 UI 不出现
- 用户 `providers.json` 可覆盖/手写同一结构

---

## 8. 实现顺序（快速路径）

1. Abstractions DTO（`ThinkingLevel`、扩展 `ThinkingOptions`、`ChatRequest.ThinkingEffort`/`ThinkingBudgetTokens`、`ChatOptions`/`GatewayRequest`）
2. Session 字段 + Manager + Fork
3. Execution 带入 + `LlmService` 规范化（key + budget）+ `chat.params`
4. `OpenAiChatClient` 出站 + `reasoning_content` 回传
5. `AnthropicClient` 出站（只读 ChatRequest 两字段）
6. DeepSeek / PredefinedModels 填充（含 `CloneOptions` 拷贝 levels/supported/default）
7. WebUI 控件
8. 测试（见下）

同 PR 一次交付；Anthropic signature 多轮回传按 §4.3 已知限制，不扩存储。

---

## 9. 测试（最小充分）

| 焦点 | 断言 |
|------|------|
| DTO / DeepSeek preset | levels 反序列化；preset `supported` |
| LlmService | 优先级；非法 key 有 default 则回落否则清空；unsupported / 无选无 default → 不传 |
| OpenAiChatClient | enabled+effort；disabled；无 effort 不发字段；assistant 带 `reasoning_content` |
| AnthropicClient | `ThinkingBudgetTokens` 有值 → enabled+budget；null + 非关闭 key → adaptive+effort；关闭档 |
| ChatRequest  enrichment | LlmService 写入 budget 字段后 Client 零查表 |
| Session | 持久化；Fork 拷贝；换模型回落 |
| Gateway | 字段映射到 ChatOptions |

不做纯 UI 像素测；Toolbar 可用轻量视图逻辑测「支持则有选项」。

---

## 10. 关键审查点（仅这些）

实现/评审只盯：

1. **职责：** JSON 补全只在 Client；LlmService 定 key + 填写 `ThinkingBudgetTokens`，不拼 vendor 字段名  
2. **无 wire：** 目录与代码不出现协议策略枚举  
3. **兼容出站形状：** `thinking` + `reasoning_effort` 与关闭档行为  
4. **key 原样：** 非关闭档不经映射表改写  
5. **ModelId 同路：** 强度不挂 Agent 采样参数  
6. **reasoning 回传：** OpenAI 靠 `interleaved`；Anthropic 持久化并回传 `thinking`+`signature`（含 tool_use 前置）  
7. **Client 零查表：** budget / Echo 标志来自 `ChatRequest`；signature 来自消息字段  

---

## 11. 决策记录

| 决策 | 结论 |
|------|------|
| `default` | **可选**；不填 = 未选档时不传思考字段（与现状一致） |
| 档位模型 | 跟模型走（B） |
| key 含义 | 原样作出站强度值（A） |
| 关闭思考 | 作为 levels 中一档（A） |
| 协议策略配置 | 永不做 wire |
| OpenAI/DeepSeek | 同一兼容 body |
| Anthropic 分支 | LlmService 填 `ThinkingBudgetTokens`；Client 有值走 budget，否则 adaptive |
| 思考回传 | OpenCode 对齐：`interleaved`（OpenAI 兼容）+ Anthropic `ReasoningSignature` 完整回传 |
| Anthropic signature | **本期做完**：存储 + 入站解析 + 出站 thinking 块（含 tool_use 分支） |
| 交付 | 强度 + 双协议回传一次做完整 |
