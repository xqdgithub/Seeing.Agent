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
