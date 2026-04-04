# Provider 数据流评审

**评审日期:** 2026-04-04  
**范围:** `ProviderConfig` → 配置绑定 → `LlmService` → `ILlmClient` → 上游 API  
**关联代码:** `SeeingAgentOptions`, `LlmService`, `DefaultLlmClientFactory`, `OpenAiClient`, `AnthropicClient`, `ModelConfig`

---

## 一、端到端数据流

```mermaid
flowchart TB
    subgraph config [配置来源]
        ICFG[IConfiguration / seeing.json]
        CODE[代码 Configure Action]
    end

    subgraph opts [SeeingAgentOptions]
        PROV[Providers: Dict key → ProviderConfig]
        MS[ModelScope.models]
        GMOD[Models]
        DEFP[DefaultProvider / DefaultModel]
    end

    subgraph llm [LlmService 单例构造]
        IM[InitializeModels 合并模型表]
        IC[InitializeClients 创建客户端]
    end

    subgraph cache [运行时缓存]
        MC[_modelConfigs]
        CL[_clients]
    end

    subgraph call [调用链]
        GM[GetModelConfig]
        GC[GetClient]
        CA[CompleteAsync / CompleteStreamAsync]
        REQ[ChatRequest.Model → ILlmClient]
    end

    ICFG --> PROV
    CODE --> PROV
    ICFG --> MS
    ICFG --> GMOD
    PROV --> IC
    MS --> IM
    GMOD --> IM
    PROV --> IM
    IM --> MC
    IC --> CL
    CA --> GM
    GM --> MC
    CA --> GC
    GC --> CL
    CA --> REQ
```

### 1.1 配置注入

| 步骤 | 行为 |
|------|------|
| 注册 | `AddSeeingAgent(IConfiguration)` 调用 `services.Configure<SeeingAgentOptions>(configuration.GetSection("SeeingAgent"))` |
| 绑定 | `Microsoft.Extensions.Options` 将 `SeeingAgent:Providers:*` 绑定到 `Dictionary<string, ProviderConfig>` |
| 键语义 | **字典的 key**（如 `openai`）是运行时查找 Provider 的主键；`ProviderConfig.Id` 应与之保持一致，但 **框架未校验二者相等** |

### 1.2 模型目录合并（`InitializeModels`）

执行顺序（后者覆盖同名字典键）：

1. `PredefinedModels.GetAll()`（键形如 `openai/gpt-4o`）
2. `SeeingAgentOptions.ModelScope.Models`
3. `SeeingAgentOptions.Models`
4. 每个 `ProviderConfig.Models`：注册 `"{providerId}/{modelId}"`，若短名 `modelId` 未被占用则再注册短名

`EnsureModelDefaults`：补全 `config.Id`；仅当从 **Provider.models** 加载且 `Provider` 为空时写入 `config.Provider = providerId`。

### 1.3 客户端创建（`InitializeClients`）

| 步骤 | 行为 |
|------|------|
| 遍历 | `_options.Providers` 的 **字典键** `providerId` |
| 工厂 | `DefaultLlmClientFactory.Create(ProviderConfig)` 按 `ProviderType` 构造 `OpenAiClient` 或 `AnthropicClient` |
| 缓存 | `_clients[providerId] = client`（与 **字典键** 一致，而非 `providerConfig.Id`） |
| 失败 | 捕获异常并记录日志，**该 Provider 无客户端**，后续调用会失败 |

### 1.4 解析模型并调用

| 步骤 | 行为 |
|------|------|
| `GetModelConfig(modelId)` | 先查 `_modelConfigs` 精确键；失败则对每个已配置 Provider 尝试 `{provider}/{modelId}` |
| 选客户端 | `GetClient(modelConfig.Provider)`，要求 `ModelConfig.Provider` 与 `Providers` 字典键一致 |
| 设置 API 模型名 | `request.Model = modelConfig.Id`（若为空则退回传入的 `modelId`） |
| 下游 | `ILlmClient.CompleteAsync(request)` |

---

## 二、问题清单

### 严重

| # | 问题 | 说明 | 影响 |
|---|------|------|------|
| P1 | **OpenAI：`ChatRequest.Model` 可能未参与实际选模** | `OpenAiClient` 使用 `new ChatClient(config.DefaultModel, ...)` 创建 SDK 客户端；`BuildOptions` 未设置 `ChatCompletionOptions.Model`。OpenAI SDK 2.x 虽提供 `ChatCompletionOptions.Model`，当前实现未赋值。若 SDK 在 `Model` 为空时固定使用构造时模型，则 **`LlmService` 中根据 `ModelConfig` 写入的 `request.Model` 不会生效**，多模型路由会失效。 | 同一 OpenAI Provider 下切换模型时行为与配置不一致（需对照 SDK 实测确认）。 |

### 高

| # | 问题 | 说明 | 影响 |
|---|------|------|------|
| P2 | **`ModelScope` / 顶层 `Models` 缺少 `provider`** | `EnsureModelDefaults` 仅在 **Provider.models** 分支注入 `Provider`；若仅在 `ModelScope` 或 `Models` 中声明条目且未写 `provider`，则 `ModelConfig.Provider` 为空。 | `GetClient` 失败或匹配错误。 |
| P3 | **`Providers` 字典键与 `ProviderConfig.Id` 不一致** | `_clients` 使用字典键；`GetClient` 使用 `modelConfig.Provider`。若 JSON 中 `id` 与外层键不同（如键 `openai` 而 `id` 为 `OpenAI`），会导致 **客户端存在但按 `ModelConfig.Provider` 查不到**。 | 运行时难以排查的「有 Provider 配置却无客户端」问题。 |

### 中

| # | 问题 | 说明 | 影响 |
|---|------|------|------|
| P4 | **`ProviderConfig` 部分字段未消费** | `Timeout` / `MaxRetries` / `Headers`：`OpenAiClient` 未使用（Anthropic 使用 `Timeout`）。 | 配置与行为不一致，运维误以为已生效。 |
| P5 | **`SeeingAgentOptions.DefaultProvider` / `DefaultModel` 未接入 `LlmService`** | 仅存在于选项模型，**框架内无自动解析默认模型/Provider 的逻辑**。 | 依赖调用方自行读取并传入 `modelId`，否则为「死配置」。 |
| P6 | **配置快照与单例** | `LlmService` 在首次构造时读取 `IOptions<SeeingAgentOptions>.Value`；模型表与客户端 **不随配置热更新**。 | 修改 `appsettings` 后不重建 Host 则仍用旧表。 |
| P7 | **`GetModelConfig` 回退为 O(n)** | 未命中直接键时，遍历所有 `Providers.Keys` 拼前缀尝试。 | Provider 数量很大时增加查找成本（通常可接受）。 |

### 低

| # | 问题 | 说明 |
|---|------|------|
| P8 | **短名模型键冲突** | 多个 Provider 下同名 `modelId` 时，后注册的 Provider 覆盖短名映射，行为依赖合并顺序。 |
| P9 | **`PredefinedProviders` 与内置模型表共享引用** | `Models = PredefinedModels.OpenAI` 为同一字典引用，若运行时修改会相互影响（一般仅测试/误用场景）。 |

---

## 三、与 Agent 配置的关系

`AgentConfig`（`SeeingAgent:Agents:*`）中的 `Provider` / `Model` **不经过 `ILlmService` 自动绑定**。若上层 Agent 编排要选用模型，需自行读取 `AgentConfig` 并调用 `ILlmService.CompleteAsync(agentModel, ...)`。这是 **编排层职责**，当前不属于 Provider 管道缺陷，但会造成「配置里有 Agent 模型、运行时未用」的错觉，建议在应用层显式衔接或文档说明。

---

## 四、改进建议（按优先级）

1. **P1（OpenAI）**  
   - 在 `OpenAiClient.BuildOptions` 中，当 `request.Model` 非空时设置 `ChatCompletionOptions.Model`（或与 SDK 行为对齐的官方写法），确保与 `LlmService` 解析结果一致。  
   - 补充单测或集成测试：同一 `Provider`、不同 `ModelConfig.Id` 时请求体中的 `model` 字段符合预期。

2. **P2**  
   - 对 `ModelScope` / `Models` 条目在加载时校验：`provider` 为空则记录 **Warning** 或失败 fast（按产品策略二选一）。

3. **P3**  
   - 在 `InitializeClients` 中校验 `providerConfig.Id` 与字典键一致，或统一只使用 `providerConfig.Id` 作为 `_clients` 的键（需一并调整 `GetClient` 与 `ModelConfig.Provider` 约定）。

4. **P4**  
   - 文档标明未实现项；或在 `OpenAiClient` 中接入 `Timeout` / 自定义 `HttpClient` / 重试策略。

5. **P5**  
   - 提供扩展方法如 `ILlmService.ResolveModelId(string? explicitModel)` 读取 `DefaultModel`，或文档声明「仅元数据，需应用层使用」。

---

## 五、结论

| 维度 | 评价 |
|------|------|
| **结构清晰度** | Provider → 模型表 → 客户端 分层明确，`ProviderConfig.models` 与全局 `Models` / `ModelScope` 合并规则在代码中可追踪。 |
| **一致性风险** | 字典键、`ProviderConfig.Id`、`ModelConfig.Provider` 三处需人工对齐，缺少校验。 |
| **正确性风险** | OpenAI 路径上 **每请求模型** 是否与 **`ModelConfig` 一致** 存疑（P1），建议优先验证并修复。 |

**总体结论:** Provider 数据流主路径（配置 → `LlmService` → 选客户端 → Anthropic）闭环完整；**OpenAI 客户端对 `request.Model` 的传递需重点复核**；默认项与部分 `ProviderConfig` 字段存在「配置可见、运行未用」的断层，适合通过文档与校验收紧。

---

## 六、参考文件

| 文件 | 职责 |
|------|------|
| `Configuration/SeeingAgentOptions.cs` | 根选项、`ModelScope`、Agents |
| `Llm/ProviderConfig.cs` | Provider 连接参数与 `models` |
| `Llm/ModelConfig.cs` | 单模型条目（modalities、limit、options） |
| `Llm/LlmService.cs` | 合并模型、缓存客户端、解析与调用 |
| `Extensions/ServiceCollectionExtensions.cs` | `DefaultLlmClientFactory` |
| `Llm/Clients/OpenAiClient.cs` | OpenAI SDK 调用 |
| `Llm/Clients/AnthropicClient.cs` | Anthropic HTTP 调用 |
