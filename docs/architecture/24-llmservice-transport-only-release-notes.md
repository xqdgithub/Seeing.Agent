# LlmService 纯传输层 Release Notes

- 日期：2026-11-01
- 范围：A-1（`LlmService` 职责收缩）
- 规格：`docs/superpowers/specs/2026-11-01-llmservice-responsibility-and-memory-contextsource-design.md` §4.1
- 计划：`docs/superpowers/plans/2026-11-01-llmservice-transport-only-plan.md` Task 1

## 一句话

`LlmService` 退回纯传输层：只负责「发请求、收响应、触发可观测性钩子」，**不再参与构建或改写 prompt**。

## Breaking

1. **`HookRegistry.ChatBeforeStart` 已删除**（原 `chat.before_start`，Blocking）。
2. **`HookRegistry.LlmSystemPrompt` 已删除**（原 `llm.system_prompt`，Blocking）。
3. **`Seeing.Agent.Memory` 的 `MemoryRecallHandler` 已删除**（连同 `MemoryModule` 的登记/撤销与 `MemoryServiceExtensions` 的 DI 注册）。
4. `LlmService` 不再赋值 `ChatRequest.SystemPrompt`。调用方传入什么，发出去就是什么。

第三方若订阅了上述两个点，会在**编译期**报错（字段不存在），优于运行期静默失效。

## 不变

`ChatParams` / `ChatHeaders` / `ChatMessage` / `ChatAfterComplete` / `ChatOnError` 五个钩子的触发时机、输入/可变字典与结果载荷**完全不变**。

`LlmService` **仍会写** `MaxTokens` / `Model` / `ThinkingEffort` / `Temperature` / `TopP`（`PrepareClientRequest`、`ApplyThinkingCatalog`、`ApplyChatParamsToRequest`）——这些是**传输参数适配**，不属于 prompt 构建。

## 已知空窗

A-1 与 A-2 之间，**记忆注入完全不生效**。

**这不是回归**：`MemoryRecallHandler` 此前也从未工作过。`ChatBeforeStart` 的两处触发只传了 `modelId` / `provider`，handler 读 `userMessage` / `message` / `content` 三个键全部落空，恒在提前返回分支结束。A-2 会用 `MemoryContextSource`（`Target = Message`）把这件事重做一遍。

## A-2：记忆注入迁回 `IContextSource`

`MemoryContextSource`（`src/capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs`）以 `Target = Message` 注入本轮召回，由 `AddMemoryServices` 以
`services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>())` 登记。

### `ValueSchemaVersion` 1 → 2 的副作用

本改为 `Target = Message` 并更换了 `Canonical` 形状，因此 `ValueSchemaVersion` 从 1 提到 2。

**副作用（安全但可见）**：装配器发现版本不匹配会**丢弃该源的既有基线状态并全量重建**——
会话历史里会多出一条记忆初始渲染消息，旧的记忆块不会消失（历史消息不可变）。

这是预期行为（缓存失效），不是 bug；但升级后第一次执行会发现历史里多一条消息。

### 已失效的配置

`MemoryRetrievalOptions.MaxInjectTokens` **对记忆注入没有任何效果**（全仓零消费者）。

| 项 | 实际决定者 |
|---|---|
| 注入多少条记忆 | `MemoryRetrievalOptions.TopK`（`MemoryRecallService.RecallAsync` 内 `Take(opts.TopK)`） |
| 注入文本多长 | `MemoryContextSource.BaselineCharLimit = 8000`（**字符**上限，非 token） |

该属性保留只为不破坏既有 `memory.json` 的读入；改它不会产生任何效果。

### 值结构与消息元数据

- `Canonical` = `{"hits":[{"path","score","contentHash"}]}`（`contentHash` 为**标题 + 正文（剥离 frontmatter）**的 SHA256 前 16 位）。
  **`contentHash` 不可省**：`score` 是 RRF 分数、由排名决定，记忆正文更新后排名常常不变 →
  分数逐位相同。缺了指纹，正文更新就永远是「值未变」，模型拿到的正文永久停在旧值，
  且每轮触发装配器的「同值不同文本」告警。
  **标题必须参与哈希**：标题来自 `Metadata.Title` 而非正文，只算正文会让「仅改标题」被判为值未变 →
  每轮告警且基线永久停在旧标题。
  **正文必须先剥离 YAML frontmatter**（`YamlParser.ExtractMarkdownBody`）：真实记忆文件头约 200 字符，
  `SummaryCharLimit = 240` 会被吃光，模型看到的「摘要」几乎全是元数据噪声。口径与同包
  `MemorySearchTool` / `MemoryReadTool` 一致。
  **正文为空即整条跳过**：索引层读失败返回 `Content = ""` 的占位节点，若让它进入 `Canonical`，
  一次瞬时 IO 失败会被 `contentHash` 判成「记忆内容已更新」并叙述给模型——这是 `score` 时代不存在的伪造更新。
  跳过只让本轮少一条记忆（可恢复），伪造叙述会污染历史。
- 初始渲染消息带 `SessionMetadataKeys.ContextUpdate`（`bool`）与
  `SessionMetadataKeys.ContextUpdateSources`（`string[]`）——**复用既有键**，不是新造：
  `SessionTitleService.IsIntentionalUserMessage` 与 WebUI `MessageViewModelFactory` 已按此判定机器注入。
  缺这两个键时记忆块是裸 user 消息，会被计为「意图用户消息」并喂进标题模型。

### 已知边界

- 无命中 / 召回失败 / 模块停用 / `ToolsOnly` 模式 / 无本轮查询 → 一律 `Unavailable`（**不**叙述「记忆已移除」）。
  `RecallAsync` 内部吞掉异常返回空列表，源无法区分「确实无命中」与「IO 失败」，
  故一次瞬时失败也不会被叙述成删除。
- Scheduler / Heartbeat 旁路不填 `IContextExecutionScope.UserQuery` → 定时任务里记忆永不注入。
