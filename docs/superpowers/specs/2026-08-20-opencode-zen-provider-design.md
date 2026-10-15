# OpenCode Zen Provider 插件设计

**日期:** 2026-08-20
**状态:** 已批准
**目标框架:** .NET 10.0

---

## 1. 背景与目标

OpenCode Zen（`https://opencode.ai/zen/v1`）是 OpenCode 官方的 AI 网关，提供 **6 个免费模型**（免 API Key，仅限流）与大量付费模型。本项目（Seeing.Agent）现有 DeepSeek Provider 插件（`plugs/providers/Seeing.Provider.DeepSeek`），但缺少 OpenCode Zen 的接入能力。

**目标**：新增 `Seeing.Provider.OpenCodeZen` 插件，实现 OpenCode Zen 的免费模型接入，同时保留付费模型目录（需 API Key），在 WebUI 中对免费模型做明确标记。

**参考实现**：
- `plugs/providers/Seeing.Provider.DeepSeek`：插件结构（Extension / Provider / ModelsClient / ConfigStore / Options / HostedService / DI 扩展 / ModelCapabilities / 测试）
- QwenPaw `PROVIDER_OPENCODE`（`OpenCodeProvider`）：OpenAI 兼容桥接、`require_api_key=False`、`-free` 后缀免费模型判定

## 2. OpenCode Zen 端点事实（已验证）

| 端点 | 认证 | 行为 |
|------|------|------|
| `GET https://opencode.ai/zen/v1/models` | 免认证 | 返回全量模型（约 100 个），仅含 `id/object/created/owned_by` |
| `POST https://opencode.ai/zen/v1/chat/completions` | 免费模型免认证 | 免费模型无需 Key；限流时返回 429 `FreeUsageLimitError` |
| 付费模型 | 需 API Key（`opencode.ai/auth` 注册） | 计费 |

**免费模型清单**（官方文档）：
`big-pickle`、`mimo-v2.5-free`、`hy3-free`、`nemotron-3-ultra-free`、`nemotron-3.5-lightning-free`、`muse-spark-1.2-contributor-free`

## 3. 架构

### 3.1 契约扩展（Abstractions）— 通用扩展点

`src/Seeing.Agent.Abstractions/Llm/ModelConfig.cs` 新增元数据字段：

```csharp
/// <summary>扩展元数据（插件可写入任意键值，供 UI 等消费）</summary>
[JsonPropertyName("metadata")]
public Dictionary<string, object?>? Metadata { get; set; }
```

- 默认 `null`，向后兼容（现有 JSON 反序列化不受影响）
- 免费标记统一写入 `Metadata["isFree"] = true`

`src/Seeing.Agent/Llm/ModelConfigManager.cs` 的 `CloneModelConfig`（:383）同步深拷贝 `Metadata`，避免配置驱动路径丢失元数据。

### 3.2 插件项目结构

```
plugs/providers/Seeing.Provider.OpenCodeZen/
├── OpenCodeZenModelCatalog.cs         # 私有模型目录：Id/Name/IsFree/能力/价格
├── OpenCodeZenProvider.cs             # LlmProviderBase + IConfigurableLlmProvider
├── OpenCodeZenExtension.cs            # IExtension + IProviderExtension
├── OpenCodeZenModelsClient.cs         # 免认证 GET /models → 填充目录
├── OpenCodeZenConfigStore.cs          # ~/.seeing/opencode-zen.json
├── OpenCodeZenOptions.cs              # ApiKey（可选）
├── OpenCodeZenProviderHostedService.cs # IHostedService 注册
└── ServiceCollectionExtensions.cs     # AddOpenCodeZenProvider()
```

`tests/Seeing.Provider.OpenCodeZen.Tests/` 镜像 DeepSeek 测试结构。

### 3.3 关键配置

| 项 | 值 |
|----|----|
| Provider Id | `opencode-zen` |
| Provider Name | `OpenCode Zen` |
| Provider 类型 | `ProviderType.OpenAI` |
| BaseUrl | `https://opencode.ai/zen/v1` |
| 配置路径 | `~/.seeing/opencode-zen.json` |
| 配置字段 | `ApiKey`（可选，`Required=false`） |

## 4. 数据流

### 4.1 模型目录

```
免认证 GET /models（全量 ~100 个）
  → 私有目录 OpenCodeZenModelCatalog（每条：Id/Name/IsFree/Context/Output/Modalities）
  → 免费判定：id 以 "-free" 结尾 或 ∈ {big-pickle, muse-spark-1.2-contributor-free}
  → 能力预置：6 个免费模型静态表；其余模型默认 context=128000, output=8192
  → 展示过滤：未配置 API Key 时仅返回免费模型（避免全量列表过大）；
              配置 API Key 后返回全量（含付费）
```

### 4.2 对外投影（GetModelsAsync）

私有目录 → `ModelConfig`：

| 模型 | Pricing | Metadata |
|------|---------|----------|
| 免费 | `{ Input=0, Output=0 }` | `Metadata[ModelMetadataKeys.IsFree]=true` |
| 付费 | `null` | 无 |

**用户自定义覆盖**（`~/.seeing/opencode-zen.json` 的 `modelCapabilities`，Key 大小写不敏感，优先级高于内置预置表）：
- 支持 `context`/`output` 能力覆盖，用于模型列表调整时无需改代码更新能力
- 支持 `isFree` 覆盖（bool?），用于手动标记未带 `-free` 后缀的新免费模型
- 标记免费时价格按 0 处理

**契约扩展**：`ModelConfig.Metadata`（可空字典）+ `ModelMetadataKeys` 常量类；`ModelConfigManager.CloneModelConfig` 与 `ConfiguredLlmProvider.CloneModel` 均复制 Metadata。

**并发一致性**：`GetModelsAsync` 在请求开始时快照 ApiKey；若在途请求期间 Key 被保存（Key 变化），放弃写回缓存，交由下一次刷新重新拉取，避免旧过滤覆盖新缓存。

### 4.3 WebUI 展示

`ModelsPage.razor` 模型表格新增"免费"列：读 `ModelConfig.Metadata["isFree"]` 渲染 `<Tag color="green">免费</Tag>`；付费模型渲染空或"付费"。

### 4.4 无 Key 客户端处理

`OpenAiChatClient`/`AnthropicClient`/`OpenAiResponsesClient` 原本强制要求 ApiKey 或认证头，已取消该限制（`OpenAiChatClient.cs`），允许匿名网关直接构造。免费模型因此可无 Key 调用。

**限流豁免的关键机制**（实测）：OpenCode Zen 服务端按 **User-Agent 识别 opencode 客户端**并豁免免费模型限流：

| 请求头 | 结果 |
|--------|------|
| `User-Agent: opencode` | HTTP 200（成功） |
| 浏览器 / 默认 UA | HTTP 429 `FreeUsageLimitError` |

因此 `CreateClient()` 无论有无 Key 均注入 `Headers: { "User-Agent": "opencode" }`（非认证头），且不发送 `Authorization`（无 Key 时）。

## 5. 接入点

| 文件 | 修改 |
|------|------|
| `Seeing.Agent.slnx` | 新增插件项目 + 测试项目两个 `<Project>` 条目 |
| `samples/Seeing.Agent.WebUI/Program.cs` | `builder.Services.AddOpenCodeZenProvider();` |
| `src/Seeing.Agent.Abstractions/Llm/ModelConfig.cs` | 新增 `Metadata` 字段 |
| `src/Seeing.Agent/Llm/ModelConfigManager.cs` | `CloneModelConfig` 复制 `Metadata` |
| `samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor` | 模型表格新增"免费"列 |

## 6. 测试计划

镜像 `tests/Seeing.Provider.DeepSeek.Tests`，用 `StubHandler` 模拟 HTTP：

| 测试文件 | 覆盖 |
|----------|------|
| `OpenCodeZenModelsClientTests` | `/models` 解析、免费判定（`-free` 后缀 + 显式 id）、HTTP 错误返回空、能力覆盖 |
| `OpenCodeZenProviderTests` | 生命周期（Warmup/SaveConfig/Dispose）、GetModelsAsync TTL 缓存、TestConnection（无 Key 免费模型仍可测）、无 Key 时占位 Authorization 头 |
| `OpenCodeZenModelCatalogTests` | 免费判定逻辑、能力预置默认值 |
| `OpenCodeZenConfigStoreTests` | 读写 ~/.seeing/opencode-zen.json（临时目录） |
| `OpenCodeZenServiceCollectionExtensionsTests` | DI 注册（Singleton + HostedService） |

另需验证：`ModelConfigManager.CloneModelConfig` 复制 `Metadata`（`Seeing.Agent.Tests` 的 `GetModels_PreservesMetadataOnClone` 已锁定）。

## 7. 验收标准

1. `dotnet build Seeing.Agent.slnx` 通过
2. `dotnet test tests/Seeing.Provider.OpenCodeZen.Tests` 全绿
3. WebUI 模型页面出现 `opencode-zen` Provider，免费模型带"免费"标记
4. 不配置 API Key 时，免费模型可用（能发起对话）；付费模型标记为付费
