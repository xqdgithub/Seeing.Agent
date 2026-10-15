# Provider Plug → ISeeingModule

**日期：** 2026-09-08  
**方案：** A

## 目标

DeepSeek / OpenCodeZen 改为 `ISeeingModule`；按类型解析 `ILlmClientFactory`（修 Anthropic 误注入）；Activate 注册 / Deactivate 注销。

## 模块

| Id | 类 | DependsOn |
|----|-----|-----------|
| `provider.deepseek` | `DeepSeekLlmModule` | `llm.openai` |
| `provider.opencodezen` | `OpenCodeZenLlmModule` | `llm.openai` |

## 任务

1. Provider 构造注入 `IEnumerable<ILlmClientFactory>`，`CreateClient` 用 `RequireFactory(..., OpenAi)`
2. 新增 Module；`ConfigureServices` 登记 DI；去掉 `*ProviderHostedService`
3. WebUI：`AddSeeingModule<…>`；`BuiltInScenarios.full` 加入两 id
4. 更新 plug 测试 + `docs/architecture/02` / `05`
