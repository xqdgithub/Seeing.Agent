# Thinking Effort Implementation Plan

> **For agentic workers:** Reviews only at Critical Gates (spec §10).

**Goal:** Model-declared thinking levels → session/UI select → LlmService normalize key+budget → OpenAI/Anthropic clients emit body; no wire.

**Status:** Implemented. G1/G2/G3 APPROVED. Tests: ThinkingCatalogNormalizeTests 5/5; DeepSeekModel* 7/7.

## Critical Gates

| Gate | Result |
|------|--------|
| G1 LlmService normalize, no vendor JSON in Core | APPROVE |
| G2 OpenAI body + reasoning_content; Anthropic ChatRequest-only | APPROVE |
| G3 DeepSeek levels; UI; no AgentDefinition coupling | APPROVE |

## Delivered

- Abstractions: `ThinkingLevel` / `ThinkingOptions` / `ChatRequest.ThinkingEffort|ThinkingBudgetTokens` / ChatOptions / GatewayRequest
- Session: `SelectedThinkingEffort`, `SetThinkingEffortAsync`, Fork/clone
- LlmService: `ApplyThinkingCatalog`; chat.params `thinkingEffort`
- OpenAI: `thinking` + `reasoning_effort`; assistant `reasoning_content` echo
- Anthropic: budget vs adaptive from ChatRequest fields
- DeepSeek + PredefinedModels levels (`default` optional)
- WebUI toolbar thinking select
