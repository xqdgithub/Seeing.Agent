# SiliconFlow Provider 插件设计

**日期：** 2026-10-12
**状态：** 已确认（用户批准）

## 背景与目标

新增 SiliconFlow（硅基流动）品牌 LLM Provider 插件，接入其 OpenAI 兼容网关
（`https://api.siliconflow.cn/v1`，API 文档：https://api-docs.siliconflow.cn/docs/api/chat-completions-post）。

复用既有品牌网关插件模式（`plugs/providers/Seeing.Provider.DeepSeek` / `MiMo` / `OpenCodeZen`）：
`ILlmProvider` + `IConfigurableLlmProvider`，仅依赖 Abstractions，经 `llm.openai` 工厂创建客户端。

## 范围内

1. 新插件 `plugs/providers/Seeing.Provider.SiliconFlow/`（五件套，逐文件镜像 MiMo）。
2. **所有品牌网关插件配置文件统一迁移到 `~/.seeing/providers/` 子目录**（SiliconFlow / DeepSeek / MiMo / OpenCodeZen）。
3. 解决方案、示例宿主（WebUI / Tui）接线与架构文档补录。
4. 镜像测试项目 `tests/plugs/Seeing.Provider.SiliconFlow.Tests/`；既有三插件 ConfigStore 测试随路径调整。

## 明确不做（讨论结论）

- **不做 SiliconFlow 思考字段专属映射**（`enable_thinking` / `thinking_budget`）。
  出站思考字段与 DeepSeek 完全一致（`thinking:{type}` + `reasoning_effort`）。
  评估过三个方案（拦截器 JSON 补丁 / 协议方言分支 / 声明式 wire profile /
  虚方法覆写），结论：字段形状差异属协议方言问题，现阶段保持现状；
  若未来确需支持，再以「声明式 profile 或独立协议客户端包」形式设计。
- **模型列表原样透传**（含 embedding/rerank/TTS 等非对话条目），能力补全交
  `IModelCapabilityManager`，与 MiMo 一致。
- **旧配置路径不迁移**：直接切换到新路径 `~/.seeing/providers/<id>.json`。
  存量用户需手动搬运 `~/.seeing/<id>.json`（已确认接受）。

## 组件设计

### Seeing.Provider.SiliconFlow（新包）

| 文件 | 职责 |
|------|------|
| `SiliconFlowOptions.cs` | 仅 `ApiKey`（Required，`apiKey`） |
| `SiliconFlowConfigStore.cs` | 读写 `~/.seeing/providers/siliconflow.json`；原子写（tmp + move） |
| `SiliconFlowModelsClient.cs` | `DefaultBaseUrl = "https://api.siliconflow.cn/v1"`；`GET models` Bearer 鉴权；条目 `Provider="siliconflow"`；失败/异常返回空集并 LogWarning |
| `SiliconFlowProvider.cs` | `Id="siliconflow"`、`ExtensionId="seeing.provider.siliconflow"`；`LlmClientFactoryResolver.Require(..., ProviderTypes.OpenAi)`；模型缓存 TTL 5 分钟；`IModelCapabilityManager.TryEnrichIfEnabledAsync` 补全 |
| `SiliconFlowLlmModule.cs` | 模块 id `provider.siliconflow`，`DependsOn: ["llm.openai"]`；Activate：Warmup + `IProviderRegistry.Register`；Deactivate：Unregister |

依赖：`Seeing.Agent.Abstractions` + `Seeing.ConfigSchema`（与 MiMo csproj 一致）；
不引用 Core / `Llm.OpenAI`，无拦截器。

### 配置存储路径统一（既有三插件）

- `DeepSeekConfigStore` / `MiMoConfigStore` / `OpenCodeZenConfigStore`：
  `ConfigFilePath` 从 `~/.seeing/<id>.json` 改为 `~/.seeing/providers/<id>.json`
  （`deepseek.json` / `mimo.json` / `opencode-zen.json`）。
- 无旧路径回落、无自动迁移（方案 C）。
- `SaveAsync` 依然 `Directory.CreateDirectory` 确保子目录存在。

### 接线

- `Seeing.Agent.slnx`：加入 plug 与测试两个项目。
- `samples/Seeing.Agent.WebUI/Program.cs`、`samples/Seeing.Agent.Tui/Program.cs`：
  `AddSeeingModule<SiliconFlowLlmModule>(registry)`（在 `AddSeeingCore` 之前）+ `using`。
- 文档：`docs/architecture/README.md`（plugs 列表）、`docs/architecture/02-modules.md`
  （模块表 + 能力包清单）补 SiliconFlow 条目。

## 数据流

与 MiMo 完全一致：

```
设置页 SaveConfig → SiliconFlowConfigStore（providers/siliconflow.json）
  → SiliconFlowProvider 失效旧客户端 + 清模型缓存 → IProviderRegistry.Register
模型选择 → SiliconFlowModelsClient（GET /v1/models, Bearer）
  → IModelCapabilityManager enrich → 缓存 5 分钟
对话 → llm.openai 工厂 Create(ProviderConfig{ id=siliconflow, type=openai,
  baseUrl=https://api.siliconflow.cn/v1, apiKey }) → OpenAI 兼容 chat/completions
```

## 错误处理

- List Models 非 2xx / 异常：LogWarning + 返回空集（不抛）。
- ConfigStore 读失败：LogWarning + 视为空配置。
- Provider 未配置 ApiKey：`GetModelsAsync` 返回空集、`TestConnectionAsync` 返回 false。

## 测试策略

- `tests/plugs/Seeing.Provider.SiliconFlow.Tests/` 镜像 MiMo 四套：
  `SiliconFlowProviderTests`（schema/ApiKey 持久化/客户端重建/Warmup/模型缓存）、
  `SiliconFlowLlmModuleTests`（id、Activate 注册、Deactivate 注销）、
  `SiliconFlowConfigStoreTests`（读写、坏文件容错、providers 子目录路径断言）、
  `SiliconFlowModelsClientTests`（mock handler：鉴权头、非 2xx、解析）。
- 既有 DeepSeek / MiMo / OpenCodeZen ConfigStore 测试：路径断言改到 `providers/` 子目录。
- 验证命令：`dotnet build` + `dotnet vstest tests/plugs/.../*.dll`（MTP 环境问题，用 VSTest）。

## 风险与边界

- SiliconFlow 对未知字段（`thinking:{type}`）的容忍度未验证：若服务端 400，
  思考档位将不可用——属已知边界，届时再立项做 wire profile。
- 模型列表含大量非对话模型：依赖能力补全与 UI 分组消化（用户已确认接受）。
- 旧路径不迁移：存量用户首次升级时 Provider 显示未配置（已确认接受）。
