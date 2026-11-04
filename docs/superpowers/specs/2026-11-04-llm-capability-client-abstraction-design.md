# LLM 能力客户端抽象与统一注册设计（v5）

- **日期：** 2026-11-04（v5：12 条澄清决议回写 + 全文重写修复编码）
- **状态：** 待实现
- **范围：** `src/abstractions`、`src/spine/Seeing.Agent.Core/Llm`、`src/capabilities/Seeing.Agent.Llm.*`、`src/capabilities/Seeing.Agent.SystemOne`、`src/capabilities/Seeing.Agent.Memory`、`samples/Seeing.Agent.WebUI`
- **破坏性：** 是（`ILlmClient` / `ILlmProvider` / `ILlmClientFactory` / Hook 点名 / `ModelType` / `memory.json` / `systemone.json` / `DefaultModel` 全部变更）
- **评审记录：** `docs/superpowers/specs/2026-11-04-llm-capability-review.md`

> **v5 修订摘要**：回写 12 条澄清决议（15–26）。删除 `ILlmCapabilityClient.Capabilities`（第三份真相）；`capability` 字符串统一为「配置端小驼峰 + Hook 端点分」；补第 5 层「实现可用性」校验；明确 `TranscriptionRequest.Audio` 所有权、热重载在途语义、默认值必须带 provider 前缀、删除 `ProviderConfig.DefaultModel`、失效默认值保留并标记、Speech 缺 `Modalities` 报错、非 chat 连接测试发真实探针、本次不接语音工具。
>
> **v4**：新增 §7.14 按能力配置的全局默认模型。
> **v3**：回写 10 条澄清决议；确立 §13 三条原则。
> **v2**：修掉 3 个阻断项与 5 个升级即出错的回归。
> **v1 中「新增 `SystemOneLlmProvider : LlmProviderBase`」是错误的，已删除。**

---

## 0. 已确认决议（不可再改）

| # | 议题 | 决议 |
|---|---|---|
| 1 | 非 chat 用量记账 | **只 chat 记账**；embedding/decision/speech 的 `usage` 只进 `llm.after_complete` 载荷 |
| 2 | Hook 载荷适配器 | **可注册适配器**（契约在 Abstractions） |
| 3 | 内置与插件共存 | **内置五个 + 插件可覆盖（后注册胜）+ 通用兜底** |
| 4 | `ModelType.Image` | **删除**，只留 `ImageGeneration`；多模态输入由 `Modalities.Input` 表达 |
| 5 | Provider 能力 UI | **加能力多选框** |
| 6 | 两层配置不一致 | **模型页展示 + 告警**，不阻止保存 |
| 7 | `OpenAiResponsesClient` | **接入为独立协议 `openai-responses`** |
| 8 | 默认模型选择 | **归插件，Core 不参与启发式**；Core 负责存储/查询用户显式配置的默认值 |
| 9 | 连接测试 | 签名加 `capability` 参数 |
| 10 | Memory embedding 模型 | **必须显式配置，不自动挑** |
| 11 | 全局默认模型 | **按能力配置**：`DefaultModels` 字典，删除旧单值 `DefaultModel` |
| 12 | 默认值查询 API | **都在 `IModelConfigManager`**：单查 / 全量 / 直接解析 |
| 13 | 未配默认值的行为 | **严格查表返回 null**，无回落、无启发式 |
| 14 | 默认值写入 | `SetDefaultModelAsync(capability, modelId, level)`；写时校验模型存在且具备该能力 |
| 15 | `ILlmCapabilityClient.Capabilities` | **删除**。`ProviderConfig.Capabilities` 是唯一真相，`ILlmProvider.Capabilities` 是其运行时投影；客户端不再自述 |
| 16 | 非 chat 的 `llm.params` | **按能力给不同键**（见 §7.6） |
| 17 | `TranscriptionRequest.Audio` | **调用方拥有**，客户端只读，**不得 Dispose** |
| 18 | `capability` 字符串格式 | **配置端小驼峰**（`speechSynthesis`）；**Hook 载荷点分**（`speech.synthesis`） |
| 19 | 配置声称能力 vs 实现 | **以配置为准**；工厂 `Create` 返回 null → Router 报错，不回退 |
| 20 | 热重载在途请求 | **不中断**；旧实例不再可达但不强制 Dispose |
| 21 | 默认值的模型引用 | **必须带 `provider/` 前缀**；裸 ID 写入时报错 |
| 22 | `ProviderConfig.DefaultModel` | **删除**，默认模型只在全局 `DefaultModels` 一处 |
| 23 | 模型能力变更后失效的默认值 | **保留并标记失效**（`ValidateDefaultModels` 报告），不静默清除 |
| 24 | Speech 缺 `Modalities` | **报错要求补**，不按单能力推断 |
| 25 | 非 chat 连接测试 | **每种能力发真实探针** |
| 26 | 语音工具 | **本次不接** `speak`/`listen`，只出契约与客户端实现 |
| 27 | `ILlmRouter` 的定位 | **保留 public 与 `Resolve<T>`**；边界按 §7.4.1：常规单次调用走 `ILlmInvoker`，探针/长生命周期/框架内部走 Router。`SystemOneTool`/`SystemOneService` 改走 Invoker |

---

## 13. 架构原则（全文判据）

### 原则 1：Core 不含任何"启发式模型选择"逻辑

`ILlmRouter.Resolve<T>(modelId)` **只接受显式 modelId**，Core 不提供"未指定时取目录首个"的重载，也不内置任何回落策略。

**但"存储并暴露用户显式配置的默认值"不属于此列**：

| 行为 | 归属 | 说明 |
|---|---|---|
| 用户配 `DefaultModels.embedding = "x"` → 查表返回 `"x"` | **Core**（`IModelConfigManager`） | 纯查表，零策略 |
| 未配 embedding 默认 → 去目录里挑第一个 | **禁止** | 启发式选择，Core 不得参与 |
| 未配时回落到 chat 默认模型 | **禁止** | 会选出并不具备该能力的模型 |

理由：把"取首个匹配"写进 Core，等于把各能力的业务策略塞进脊柱。而**默认值是用户的意图，不是框架的推断**。

各能力包的职责边界：

- `SystemOne` 工具：未配 `DefaultModels.decision` 时，从目录挑 `Decision` 模型；多个则报错列候选；
- `Memory`：**必须显式配 `MemoryOptions.Embedding.ModelId`**，未配置即不启用，**不读 `DefaultModels.embedding`**（决议 10）；
- 任何新能力包：自行决定，Core 零改动。

**这条原则把"升级即自动开跑向量索引"从"靠默认值防"变成"结构上不可能发生"**。

### 原则 2：Core 不负责任何能力的语义解释

Core 提供「注册表 + 路由 + 调用管道 + 装饰器」，不包含"这个 `ModelType` 意味着什么"之外的判断：

- `LlmCapabilityMap` 的映射放 **Abstractions**（契约，不是策略），Core 只消费；
- `Image` 的语义问题已通过删除该枚举值消除（决议 4）；
- `Speech` 的 TTS/ASR 消歧由 `Modalities` 决定，`Modalities` 是模型元数据而非 Core 策略。

### 原则 3：插件只依赖 Abstractions，且能完整参与

| 角色 | 依赖 | 例子 |
|---|---|---|
| 提供客户端 | `ILlmClientFactory` | 第三方 embedding/TTS 服务 |
| 订阅 Hook | `IHookHandler` + 载荷 key 常量 | 日志、计费、审阅 |
| 覆盖载荷组装 | `ILlmHookPayloadAdapter` | 自定义能力的载荷形状 |

三者都不需要引用 Core。

**已知限制**：`LlmCapabilityKind` 是 `[Flags] enum`，在 Abstractions 中，**插件无法新增能力值**。要加新能力必须改 Abstractions 并发版。可注册适配器的价值因此是「覆盖既有能力的载荷形状」，不是「定义全新能力」——后者当前设计不支持，不要误以为支持。

---

## 1. 意图与缘由

框架目前只有一个真正可用的文本通道。`ModelType` 枚举里躺着 `Embedding` / `Rerank` / `Image` / `Video` / `Speech`，但**它们背后没有任何客户端**——枚举是空壳，只能用于 UI 过滤。

同时，两条"非 chat"路径已以**绕过基础设施**的方式存在：

1. **Embedding**：`Memory/Core/Embedding/ProviderEmbeddingService.cs:36-78` 直接 `POST {baseUrl}/embeddings`，端点靠 `IProviderEndpointLookup` 从配置里捞。重试装饰器、代理、拦截器、Hook、用量统计对它**全部无效**；`EmbeddingConnectionTester.cs:45-71` 又把同样的 HTTP 抄了第二遍。
2. **Decision（Jev）**：`Seeing.Agent.SystemOne` 是一整套**平行实现**——自己的 `systemone.json`、`SYSTEMONE_*` 环境变量、`ISystemOneProviderRegistry`（337 行）、`ISystemOneClientFactory`、重试参数、门面 `ISystemOneService`，外加 `SystemOneTool`。它和 LLM provider 注册表结构一一对应却完全不相通：`jev` 的模型不出现在目录里，选不了、测不了、算不到钱。

真正的问题不是"缺 embedding 客户端"，而是：

> **「Provider + 协议 + 能力 + 模型目录 + 横切机制」这套骨架只长了一个 chat 出口，别的能力只能各自另起炉灶。**

### 1.1 为什么现在做

再晚，`SystemOne` 那套平行设施会被当成先例复制——下一个接 TTS 的人会把它的三件套再抄一遍，收敛成本随模态数量线性上升。

---

## 2. 目标

1. **能力即端口**：每种能力一个窄接口，各自保留真实请求/响应形状。
2. **一个 Provider 供出多种能力**：同 endpoint、同 key，可同时供 chat 与 embedding。
3. **模型与客户端显式配对**：`ModelConfig.Types` + `ProviderCapabilities` 校验，不靠运行期试错。
4. **横切机制对所有能力一致生效**：`ILlmInvoker` 统一触发 Hook、合并出站头、计算耗时；载荷经可注册适配器组装。
5. **消灭平行注册表**：`SystemOne` 的三件套并入 `Providers` 一节。
6. **Core 零业务策略**：默认模型选择、能力语义解释全部归插件（§13 原则 1/2）。
7. **插件可完整参与**：提供客户端、订阅 Hook、覆盖载荷组装，都不需要引用 Core（§13 原则 3）。
8. **默认值可按能力配置并对外可查**：`DefaultModels` + `IModelConfigManager` 四个读方法（§7.14）。

## 3. 非目标

- 不引入新的 Provider 协议实现（除把 TypeSafe 降级为能力客户端）。
- 不改动 `SessionToolSchemaResolver` / 工具暴露 / 权限子系统 / Gateway 协议。
- 不为 rerank / image / video 提供可跑实现（§7.11）。
- 不新增 `speak` / `listen` 工具（决议 26）。
- 不做历史配置迁移（本项目无历史数据）。

## 4. 反目标（做到就算失败）

- 把 `EmbeddingRequest` / `DecisionRequest` 塞进 `ChatRequest` 的变体字段。
- 保留 `ILlmClient` 作为兼容层长期并行。**一次性删干净。**
- 让能力包引用 `Seeing.Agent.Core`（本项目唯一的硬性分层红线）。
- 让 `SystemOne` 保留任何自有注册表/自有配置文件。
- 让 `Resolve<T>` 在运行期靠捕获异常判断能力不匹配。
- 让同一个事实存在三份数据（配置 / Provider 投影 / 客户端自述）。

---

## 5. 约束

| 约束 | 来源 | 对本设计的影响 |
|---|---|---|
| 能力包只依赖 Abstractions + 原语（+ support） | `AGENTS.md`、`04 §4`、`06` 已签字全绿项 | 能力契约/DTO/`ILlmRouter`/`ILlmInvoker` 全部放 `Abstractions/Llm/`；**任何 `Llm.*` 包不得引用 Core** |
| Core 瘦脊柱，零工具 | `AGENTS.md` | Router / Invoker 实现放 Core 可以 |
| 模块经 `AddSeeingModule`，Activate 挂载 / Deactivate 卸载 | `04 §4` | SystemOne 合流后仍是一个模块，但不再自管注册表 |
| 禁止静默吞异常 | `04` | `Resolve` 失败必须抛出带原因异常 |
| 禁止 `async void` | `04` | 音频/转写流一律 `IAsyncEnumerable` |
| `dotnet build Seeing.Agent.slnx` 必过 | `04 §82` | 删 `ILlmClient` 会同时打断 8 个项目 → 见 §11 的 wedge 切分 |

---

## 6. 现状勘察（实现前必读）

### 6.1 现有 chat 链路

```
LlmService → IProviderManager → IProviderRegistry → ILlmProvider.GetClient()
                                                        │
                                          ILlmClientFactory.Create(config)
                                                        │   ← 装饰器在此处应用
                                          ILlmClientDecoratorRegistry.Apply()
                                                        │
                                          OpenAiChatClient / AnthropicClient
```

关键事实（v1 画错过，此处按实际代码）：

- **装饰器在工厂内部应用**：`Llm.OpenAI/OpenAiLlmClientFactory.cs:49`、`Llm.Anthropic/AnthropicLlmClientFactory.cs:49`。`ConfiguredLlmProvider` 只做 `Lazy` 缓存（`ConfiguredLlmProvider.cs:41-43`）。
- 4 个 plug provider **直接调工厂**（`DeepSeekProvider.cs:176-195`，MiMo/OpenCodeZen/SiliconFlow 同构），从不经 `ConfiguredLlmProvider`。
- **仓库里没有任何 LLM 超时装饰器**。超时只由 `HttpClient.Timeout` 提供（`OpenAiHttpHelper.cs:46`），单次尝试有效。v1 说"超时上提"是空承诺，已撤回。
- `ILlmClient` 三个方法全是 chat 语义；`ILlmClientFactory.Create(ProviderConfig)` 一个 config 只产一个客户端；`ILlmProvider.GetClient()` 无参。

### 6.2 现有 embedding 旁路

- `ProviderEmbeddingService.cs`、`EmbeddingConnectionTester.cs`：两段重复裸 HTTP。
- `MemoryOptions.IsEmbeddingConfigured` 要求 `Provider` **且** `Model` 非空（`MemoryOptions.cs:12-14`），实际用户 `memory.json` 没有这两个键 → **今天 embedding 恒关闭**，`NullEmbeddingService` 生效（`MemoryServiceExtensions.cs:83-94`）。
- `HybridMemoryIndex.cs:34,43,51,59` 用 `IEmbeddingStatus.IsAvailable` 决定是否建向量索引。
- `VectorIndex.CosineSimilarity` 维数不等**直接抛 `ArgumentException`**（`VectorIndex.cs:294-297`）。
- `SqliteEmbeddingCache` 主键只有 `text_hash`，**不含模型**（`SqliteEmbeddingCache.cs:47-54`）。
- `IProviderEndpointLookup` / `ProviderEndpoint` / `IProviderEndpointInfo` 的**唯一生产消费者就是这两个待删类**，且 `IProviderEndpointInfo` 在 `src` 中零实现者（仅测试 mock）→ 整组删除。

### 6.3 现有 decision 平行栈

| 文件 | 作用 | 去处 |
|---|---|---|
| `Configuration/SystemOneConfigStore.cs` | 读 `systemone.json` | **删除** |
| `Configuration/SystemOneProviderManager.cs`（337 行） | 自有注册表 | **删除** |
| `Configuration/SystemOneReloadHandler.cs` | 自有热重载 | **删除** |
| `Configuration/SystemOneEnvironmentConfig.cs` | `SYSTEMONE_*` | **删除** |
| `Runtime/ConfiguredSystemOneProvider.cs` | 延迟建客户端 | **删除** |
| `Runtime/SystemOneService.cs` | 门面 | 改为 `IDecisionClient` 调用方 |
| `Clients/SystemOneHttpClientFactory.cs` | 专用 handler（`UseProxy=false`、10s 超时） | **删除**（复用框架 handler） |
| `Clients/SystemOneHttpHelper.cs` | 自带 429/529 退避 | 保留序列化/错误映射，**删除重试部分** |
| `Clients/TypeSafeSystemOneClient.cs` | `/v1/systemone` | 改造为实现 `IDecisionClient` |
| `Clients/TypeSafeSystemOneClientFactory.cs` | 按 type 建客户端 | 改造为实现 `ILlmClientFactory` |
| `Abstractions/SystemOne/*.cs`（7 个） | 契约 | 迁到 `Abstractions/Llm/Decision/` |
| `PredefinedSystemOneProviders.cs` | 内置默认 provider | **删除**（成为配置默认值） |

`SystemOneModel` 只有 `{ Name, Description, ReleaseDate }`。

### 6.4 现有 Hook 与消费方

`HookRegistry.ChatParams/ChatHeaders/ChatMessage/ChatAfterComplete/ChatOnError`。消费方：

- `TokenBudget/Hooks/BudgetUpdateHook.cs:24` → `ChatAfterComplete`；**第一步就要求 `payload.SessionId` 非空且能载入会话（`:48-67`），无会话直接返回不记账**；记账动作是写 `session.CachedInputTokens`（`:85-87`）。
- `Memory/Integration/MemoryHookHandler.cs:27` → `ChatAfterComplete`，读 `content`。
- `AgentExecutor.cs:373` → `ChatOnError`。
- `Core/Services/HookManagerAdapter.cs:20-27` 允许**插件以任意字符串**注册；已提交的 `samples/Seeing.Agent.WebUI/plugins/*.dll` 是旧产物（`06-compliance-audit.md:94`）→ 改名后它们**不会编译失败，而是注册到永不触发的桶**。

---

## 7. 设计

### 7.1 维度一：协议 vs 能力

```csharp
// Abstractions/Llm/LlmCapabilityKind.cs
[Flags]
public enum LlmCapabilityKind
{
    None = 0,
    Chat = 1 << 0,
    Embedding = 1 << 1,
    Rerank = 1 << 2,
    Decision = 1 << 3,
    SpeechSynthesis = 1 << 4,
    SpeechTranscription = 1 << 5,
    ImageGeneration = 1 << 6,
    VideoGeneration = 1 << 7,
}
```

```csharp
// Abstractions/Llm/ProviderConfig.cs（新增两个字段，删除一个）
[JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
[JsonPropertyName("listModels")]   public bool ListModels { get; set; }
// DefaultModel 已删除（决议 22）
```

`ProviderTypes` 增加 `TypeSafe = "typesafe"` 与 `OpenAiResponses = "openai-responses"`；`ProviderTypes.Normalize` 同步扩展（否则 `"TypeSafe"` 不会被归一）。

**缺省能力（`ProviderCapabilityDefaults`）——只给 `Chat`：**

| 协议 | 缺省 |
|---|---|
| `openai` | `Chat` |
| `openai-responses` | `Chat` |
| `anthropic` | `Chat` |
| `typesafe` | `Decision` |

> **v1 的 `openai → Chat, Embedding` 是错的**：所有 OpenAI 兼容 provider 都会自动声明 embedding，Provider 层校验对 embedding 恒真，"缺能力时给明确异常"退化成运行期 400。要 embedding 就显式写 `capabilities: ["chat","embedding"]`。

### 7.2 能力端口

```csharp
// Abstractions/Llm/ILlmCapabilityClient.cs
public interface ILlmCapabilityClient
{
    string ProviderId { get; }
    string ProviderType { get; }          // 协议

    /// <summary>
    /// 在指定能力上测试连通性。实现应发最小真实请求（决议 25）。
    /// </summary>
    Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default);
}
```

**没有 `Capabilities` 属性（决议 15 / 原则 2）**。v1 的单值 `Capability`、v2 的位集 `Capabilities` 均删除。它是第三份真相：`ProviderConfig.Capabilities`（用户配置）与 `ILlmProvider.Capabilities`（运行时投影）已是两处，客户端再自述一份必然漂移（客户端声称支持 Embedding 但用户没开 → 两处矛盾），而 Router 从不需要读它。诊断改用 `ProviderInfo.Capabilities`。

**连接测试的真实探针（决议 25）**：

| 能力 | 探针 |
|---|---|
| `Chat` | 发一次 5 token 补全（现状 `OpenAiChatClient.cs:194-214`） |
| `Embedding` | 单条短文本，校验返回维度 > 0 |
| `SpeechSynthesis` | 合成一句短句，校验返回非空音频字节 |
| `SpeechTranscription` | 传极短音频，校验返回结构合法 |
| `Decision` | 一次单 `noul` 问题，校验返回概率字段 |

会产生少量真实费用，但这是唯一能验证"模型本身可用"的方式；仅探端点会把"模型不存在"误判为连通。

各能力端口：

```csharp
public interface IChatCompletionClient : ILlmCapabilityClient
{
    Task<ChatResponse> CompleteAsync(ChatRequest request, LlmCallContext? call = null, CancellationToken ct = default);
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(ChatRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface IEmbeddingClient : ILlmCapabilityClient
{
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface IRerankClient : ILlmCapabilityClient
{
    Task<RerankResponse> RerankAsync(RerankRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface IDecisionClient : ILlmCapabilityClient
{
    Task<DecisionResponse> EvaluateAsync(DecisionRequest request, LlmCallContext? call = null, CancellationToken ct = default);
    Task<IReadOnlyList<DecisionModel>> ListModelsAsync(CancellationToken ct = default);
}

public interface ISpeechSynthesisClient : ILlmCapabilityClient
{
    Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, LlmCallContext? call = null, CancellationToken ct = default);
    IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(SpeechSynthesisRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface ISpeechTranscriptionClient : ILlmCapabilityClient
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, LlmCallContext? call = null, CancellationToken ct = default);
    IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(TranscriptionRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface IImageGenerationClient : ILlmCapabilityClient
{
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, LlmCallContext? call = null, CancellationToken ct = default);
}

public interface IVideoGenerationClient : ILlmCapabilityClient
{
    Task<MediaJobHandle> SubmitAsync(VideoGenerationRequest request, LlmCallContext? call = null, CancellationToken ct = default);
    Task<MediaJobStatus> GetStatusAsync(MediaJobHandle handle, LlmCallContext? call = null, CancellationToken ct = default);
    Task CancelAsync(MediaJobHandle handle, LlmCallContext? call = null, CancellationToken ct = default);
    IAsyncEnumerable<MediaJobStatus> PollAsync(MediaJobHandle handle, TimeSpan interval, LlmCallContext? call = null, CancellationToken ct = default);
}
```

`IDecisionClient.ListModelsAsync` 存在是为了保住 typesafe 的**动态模型目录**，返回 `DecisionModel { Name, Description, ReleaseDate }`。

#### 7.2.1 映射表：三张，全部集中在一个静态类

```csharp
// Abstractions/Llm/LlmCapabilityMap.cs
public static class LlmCapabilityMap
{
    /// <summary>ModelType → 能力（可能一对多）。</summary>
    public static IReadOnlyList<LlmCapabilityKind> FromModelType(ModelType type);

    /// <summary>能力 → 客户端接口类型。Resolve&lt;T&gt; 的 T→Kind 反查唯一来源。</summary>
    public static IReadOnlyDictionary<LlmCapabilityKind, Type> ClientTypes { get; }

    /// <summary>协议 → 缺省能力。</summary>
    public static LlmCapabilityKind DefaultCapabilities(string protocol);

    /// <summary>能力 → 配置键（小驼峰）。用于 ProviderConfig.Capabilities 与 DefaultModels 的 key。</summary>
    public static string ToConfigKey(LlmCapabilityKind kind);

    /// <summary>能力 → Hook 载荷值（点分）。用于 llm.* 的 capability 字段。</summary>
    public static string ToHookValue(LlmCapabilityKind kind);
}
```

| ModelType | 能力 |
|---|---|
| `Text` | `Chat` |
| `Embedding` | `Embedding` |
| `Rerank` | `Rerank` |
| `Decision` | `Decision` |
| `Speech` | `SpeechSynthesis` + `SpeechTranscription`（按 `Modalities` 消歧，见 §7.4） |
| `ImageGeneration` | `ImageGeneration` |
| `Video` | `VideoGeneration` |
| `Other` | `None` |

> **`ModelType.Image` 已删除（决议 4）**。它在存量语义里就是**生成**（`ModelTypeInference.cs:31-32` 把 `dall-e`/`flux`/`sdxl` 标成 `Image`），映射到 Chat 会让 `Resolve<IChatCompletionClient>("openai/dall-e-3")` 通过校验并发出 chat 请求，制造 400。
>
> **图像理解不需要独立 `ModelType`**：一个 `Text` 模型只要 `Modalities.Input` 含 `image` 就是多模态输入模型。

**两种字符串格式（决议 18）**：

| 用途 | 格式 | 示例 | 投影函数 |
|---|---|---|---|
| 配置文件（`ProviderConfig.Capabilities`、`DefaultModels` key） | 小驼峰 | `speechSynthesis` | `ToConfigKey` |
| Hook 载荷 `capability` 字段 | 点分 | `speech.synthesis` | `ToHookValue` |

两个投影函数都在 `LlmCapabilityMap`，**禁止各调用点自行拼字符串**。选点分给 Hook 是为了与现有 `chat.after_complete` 命名风格一致。

#### 7.2.2 DTO 要点

```csharp
public sealed record EmbeddingRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public int? Dimensions { get; init; }
    public string? EncodingFormat { get; init; }
}
public sealed record EmbeddingVector(string Text, float[] Values, int Index);
public sealed record EmbeddingResponse
{
    public required IReadOnlyList<EmbeddingVector> Vectors { get; init; }
    public int Dimensions => Vectors.Count > 0 ? Vectors[0].Values.Length : 0;
    public TokenUsage? Usage { get; init; }
}

// Decision：沿用 Jev 三原语，命名去掉 "SystemOne"
public static class DecisionQuestionTypes
{
    public const string Noul = "noul";
    public const string Choice = "choice";
    public const string Score = "score";
}
public sealed record DecisionRequest
{
    public object? State { get; init; }
    public string Model { get; init; } = string.Empty;
    public Dictionary<string, DecisionQuestion> Questions { get; init; } = new();
}
public sealed record DecisionQuestion
{
    public string Type { get; init; } = "";
    public object? Instructions { get; init; }
    public object? Criteria { get; init; }
}
public sealed record DecisionAnswer
{
    public string? Type { get; init; }
    public double? Noul { get; init; }
    public string? Choice { get; init; }
    public double? Score { get; init; }
    public Dictionary<string, string>? Legend { get; init; }
    public Dictionary<string, double>? Probabilities { get; init; }
    public double? Confidence { get; init; }
}
public sealed record DecisionResponse
{
    public string? Model { get; init; }
    public Dictionary<string, DecisionAnswer> Answers { get; init; } = new();
    public TokenUsage? Usage { get; init; }
}

// Speech：音频流独立类型，不复用 StreamUpdate
public sealed record AudioChunk(ReadOnlyMemory<byte> Data, string? MimeType);
public sealed record SpeechSynthesisRequest
{
    public required string Model { get; init; }
    public required string Text { get; init; }
    public string? Voice { get; init; }
    public string? Format { get; init; }
    public double? Speed { get; init; }
}
public sealed record SpeechSynthesisResult(ReadOnlyMemory<byte> Audio, string MimeType, TokenUsage? Usage);

/// <summary>
/// 转写请求。
/// <para><b>Audio 由调用方拥有，本契约的所有实现均不得 Dispose 它</b>（决议 17）。
/// 这是 .NET 惯例：接受 Stream 参数的方法不负责释放。</para>
/// </summary>
public sealed record TranscriptionRequest
{
    public required string Model { get; init; }
    public required Stream Audio { get; init; }
    public string? MimeType { get; init; }
    public string? FileName { get; init; }
    public string? Language { get; init; }
}
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscriptSegment>? Segments, TokenUsage? Usage);

// Image / Video（仅契约）
public sealed record ImageGenerationRequest
{
    public required string Model { get; init; }
    public required string Prompt { get; init; }
    public string? Size { get; init; }
    public int? Count { get; init; }
    public string? NegativePrompt { get; init; }
}
public sealed record GeneratedImage(string? Url, ReadOnlyMemory<byte>? Data, string? MimeType, string? RevisedPrompt);
public sealed record ImageGenerationResult(IReadOnlyList<GeneratedImage> Images, TokenUsage? Usage);
public sealed record VideoGenerationRequest
{
    public required string Model { get; init; }
    public required string Prompt { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Size { get; init; }
}
public sealed record MediaJobHandle(string JobId, string ProviderId);
public enum MediaJobState { Queued, Running, Succeeded, Failed, Cancelled }
public sealed record MediaJobStatus(MediaJobState State, string? Url, string? Error, double? Progress);
```

### 7.3 Provider 与工厂

```csharp
public interface ILlmProvider
{
    string Id { get; }
    string? Name { get; }
    string Protocol { get; }                  // openai | openai-responses | anthropic | typesafe
    LlmCapabilityKind Capabilities { get; }   // 由 ProviderConfig.Capabilities 解析而来
    int MaxRetries { get; }

    /// <summary>按能力取客户端；不供出该能力返回 null（不抛）。</summary>
    ILlmCapabilityClient? GetClient(LlmCapabilityKind capability);

    Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>测试指定模型在指定能力上的连通性（决议 9）。Provider 不推导、不猜。</summary>
    Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken = default);
}

public interface ILlmClientFactory
{
    IReadOnlySet<string> SupportedProtocols { get; }
    bool SupportsProtocol(string protocol);

    /// <summary>按协议 + 能力创建一个客户端；不支持该组合返回 null。</summary>
    ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability);
}
```

**装饰器唯一应用点：工厂内部**（`Create` 返回前）。

```csharp
public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
{
    ILlmCapabilityClient? client = capability switch
    {
        LlmCapabilityKind.Chat                => new OpenAiChatClient(...),
        LlmCapabilityKind.Embedding           => new OpenAiEmbeddingClient(...),
        LlmCapabilityKind.SpeechSynthesis     => new OpenAiSpeechSynthesisClient(...),
        LlmCapabilityKind.SpeechTranscription => new OpenAiSpeechTranscriptionClient(...),
        _ => null,
    };
    return client is null ? null : _decorators?.Apply(client, config) ?? client;
}
```

> **v1 §7.5 说"Apply 移到 `ConfiguredLlmProvider`"是错的**：4 个 plug provider 直接调工厂，上移会让它们**静默失去重试**（无 429 退避），是当前工作行为的回归。`ConfiguredLlmProvider` 只做 Lazy 缓存。

**动态模型目录**：可选端口，由客户端自行实现，`ConfiguredLlmProvider` 检测：

```csharp
// Abstractions/Llm/IModelCatalogClient.cs
public interface IModelCatalogClient
{
    Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken cancellationToken = default);
}
```

`ProviderConfig.ListModels = true` 且客户端实现该端口时，`GetModelsAsync` 返回「静态 `config.Models` ∪ 远端目录」。`TypeSafeSystemOneClient` 实现它并把 `DecisionModel` 映射为 `ModelConfig { Types = [Decision] }`。**不需要专用 Provider 类，也不需要引用 Core。**

**`openai-responses` 接入为独立协议（决议 7）**：`ProviderTypes` 增加 `OpenAiResponses = "openai-responses"`，`OpenAiLlmClientFactory` 同时支持 `openai` 与 `openai-responses`；`OpenAiResponsesClient`（375 行，目前无工厂接线、仅测试构造）据此接入，消除悬空死代码。`ProviderCapabilityDefaults` 对两者都给 `Chat`。

### 7.4 Router：模型与客户端配对

```csharp
public interface ILlmRouter
{
    TClient Resolve<TClient>(string modelId) where TClient : class, ILlmCapabilityClient;
    bool TryResolve<TClient>(string modelId, out TClient? client, out string? reason)
        where TClient : class, ILlmCapabilityClient;
}
```

**校验顺序固定，五层，失败即抛 `LlmCapabilityMismatchException`**（消息含：modelId、模型声明类型、Provider 声明能力、请求能力、缺失原因）：

1. **模型存在**：`ModelConfigManager.GetModel(modelId)` 非 null。
2. **类型声明**：`GetEffectiveTypes(config)` 中至少一个 `ModelType` 映射到请求能力（`LlmCapabilityMap.FromModelType`）。
3. **Speech 消歧（决议 24）**：请求 `SpeechSynthesis` 要求 `Modalities.Output` 含 `audio`；请求 `SpeechTranscription` 要求 `Modalities.Input` 含 `audio`。缺失则抛"声明不足，请补 Modalities"，**不猜**。
4. **Provider 声明**：该 Provider 的 `Capabilities` 含请求能力。
5. **实现可用性（决议 19）**：工厂 `Create(capability)` 返回 `null` 时抛"Provider '{id}' 配置声明了能力 {capability}，但工厂未提供对应客户端"。**以配置为准、不回退**——配置与实现对不上属于配置/部署错误，必须暴露而不是静默降级。

**缓存键是 `(providerId, capability)`，不是 `(modelId, capability)`。** 同 provider 下 100 个模型只应有一个 embedding 客户端实例。由 `ConfiguredLlmProvider` 的 `Lazy<ILlmCapabilityClient>[]` 天然承担。

**热重载语义（决议 20）**：改 `capabilities` 触发的 Provider 重建**不中断在途请求**——旧实例从注册表移除后不再可达，但不强制 `Dispose`；已取出的客户端由其持有者用完。与现有 `ProviderRegistry` 的释放竞态约定一致（`06-compliance-audit.md` 已记录该边界）。

**不做运行期试错**：模型声明正确但端点不支持 → 让 HTTP 400 冒出来，那是配置错误。

#### 7.4.1 Router 与 Invoker 的适用边界（必须遵守）

**`ILlmRouter` 保持 public 且保留 `Resolve<T>`**——有三个真实场景需要绕过 Invoker 直接取客户端。但**常规单次调用必须走 `ILlmInvoker`**。

| 场景 | 用哪个 | 理由 |
|---|---|---|
| **常规单次业务调用**（chat 一轮、embedding 一批、TTS 一段、decision 一次） | **`ILlmInvoker`** | 只有它触发 `llm.params`/`llm.headers`/`llm.after_complete`/`llm.on_error`、合并出站头、计算耗时 |
| **连接测试探针**（`TestConnectionAsync`） | **`ILlmRouter`** | 探针不应产生业务副作用。走 Invoker 会让"点一下测试按钮"污染预算账与审计日志 |
| **长生命周期调用**（video `Submit` + N 次 `GetStatus`/`Poll`） | **`ILlmRouter`** | 一次任务解析一次、多次调用。走 Invoker 会记 N 次 `after_complete`，语义错误 |
| **框架内部**（`LlmInvoker` 自身、`ModelConfigManager.TryResolveDefault`、`IProviderManager`） | **`ILlmRouter`** | 这些是 Invoker 的依赖，无法自举 |

**判别口诀**：**这次调用该不该被观测成"一次 LLM 调用"？** 该 → Invoker；不该（探针、轮询、内部装配）→ Router。

**已知违反**：`SystemOneTool` / `SystemOneService` 曾直连 Router，导致 decision 的 Hook 全不触发。已改为走 Invoker（决议 27）。

`ILlmRouter` 与 `ILlmInvoker` 的 XML 注释必须互相点名，把这条边界写在使用处，而不是只写在本文档里。

### 7.5 横切层：`ILlmInvoker`

v1 把 Hook 触发留在 `LlmService`，又让非 chat 调用方"直接用 Router"，导致"横切对所有能力生效"无落点。本层补上：

```csharp
// Abstractions/Llm/ILlmInvoker.cs
public interface ILlmInvoker
{
    Task<TResponse> InvokeAsync<TClient, TResponse>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, Task<TResponse>> call,
        LlmInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient;

    IAsyncEnumerable<TChunk> StreamAsync<TClient, TChunk>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, IAsyncEnumerable<TChunk>> stream,
        LlmInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient;
}

public sealed record LlmInvocationOptions
{
    public string? SessionId { get; init; }
    public string? ParentSessionId { get; init; }
    public bool EmitHooks { get; init; } = true;   // 旁路调用（TextCompletionService）置 false
}
```

Core 实现 `LlmInvoker` 的固定流程：

```
Resolve<TClient>(modelId)                       ← 五层校验
  → 构造 LlmCallContext（SessionId / CorrelationId）
  → llm.params（Blocking，可变参数袋）+ llm.headers（Blocking）
  → call()  /  stream()
  → llm.message（Parallel）
  → llm.after_complete（FireAndForget，载荷经适配器组装）
  → 异常时 llm.on_error（FireAndForget）后原样抛出
```

**载荷适配改为可注册适配器（决议 2 / 3）**：

```csharp
// Abstractions/Llm/Hooks/ILlmHookPayloadAdapter.cs
public interface ILlmHookPayloadAdapter
{
    /// <summary>本适配器负责的能力（单一值；组合能力需注册多个适配器）。</summary>
    LlmCapabilityKind Capability { get; }

    /// <summary>按能力把响应组装成 llm.after_complete 的 result 字典。</summary>
    IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context);
}

public sealed record LlmPayloadContext
{
    public required LlmCapabilityKind Capability { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public object? Response { get; init; }        // 各能力的响应 DTO
    public TokenUsage? Usage { get; init; }
    public TimeSpan Duration { get; init; }
    public string? SessionId { get; init; }
    public bool Streaming { get; init; }
}

public interface ILlmHookPayloadAdapterRegistry
{
    void Register(ILlmHookPayloadAdapter adapter);
    bool Unregister(ILlmHookPayloadAdapter adapter);

    /// <summary>按能力解析；未注册任何适配器时返回通用兜底，不返回 null。</summary>
    ILlmHookPayloadAdapter Resolve(LlmCapabilityKind capability);
}
```

**共存规则（决议 3）**：

| 场景 | 行为 |
|---|---|
| Core 内置五个适配器 | `Chat` / `Embedding` / `Decision` / `SpeechSynthesis` / `SpeechTranscription`，在 `RegisterLlmServices` 中登记 |
| 插件注册同 `Capability` 的适配器 | **后注册胜**，覆盖内置；与 Provider 注册表「最后 wins + warning」一致 |
| 未实现的能力（`Rerank` / `ImageGeneration` / `VideoGeneration`） | 走**通用兜底**适配器：只产出 `capability` / `provider` / `model` / `usage` / `duration`，**绝不抛异常** |
| 组合能力（位集多个 bit） | 按 `Capability` 精确匹配；一个适配器只声明一个能力，组合能力需注册多个适配器 |

> 兜底**不抛异常**是刻意的：非 chat 能力本次只出契约不实现（§7.11），若它们的调用让 Hook 爆炸，等于把诊断功能变成故障源。

`llm.after_complete` 的 key 常量同时放在 Abstractions 的 `HookDataContract.LlmAfterComplete`，供只读订阅方（插件）使用，避免魔法字符串。

`LlmService` 退化为 **chat 专用门面**：做 `MaxTokens` / `ThinkingEffort` / `Temperature` 的 catalog 归一化（chat 独有语义），然后调 `ILlmInvoker`。非 chat 调用方直接调 `ILlmInvoker`。

```
chat      → ILlmService → ILlmInvoker → IChatCompletionClient
embedding →              ILlmInvoker → IEmbeddingClient
decision  →              ILlmInvoker → IDecisionClient
speech    →              ILlmInvoker → ISpeechSynthesisClient / ISpeechTranscriptionClient
```

> **插桩顺序**：`ILlmClientDecorator`（重试）在客户端内部（工厂应用），`ILlmInvoker` 在其外层 → 重试发生在**一次 Hook 周期之内**，`llm.on_error` 只在重试耗尽后触发一次。与现状 `LlmService` 的 `ChatOnError` 语义一致。

### 7.6 Hook 泛化：`chat.*` → `llm.*`

彻底重命名，不留别名。

```csharp
public static readonly HookSpec LlmParams        = new(HookPolicy.Blocking,      "llm.params");
public static readonly HookSpec LlmHeaders       = new(HookPolicy.Blocking,      "llm.headers");
public static readonly HookSpec LlmMessage       = new(HookPolicy.Parallel,      "llm.message");
public static readonly HookSpec LlmAfterComplete = new(HookPolicy.FireAndForget, "llm.after_complete");
public static readonly HookSpec LlmOnError       = new(HookPolicy.FireAndForget, "llm.on_error");
```

载荷统一增加 `capability` 字段，值由 `LlmCapabilityMap.ToHookValue` 给出（点分，决议 18）。

**`llm.params` 的能力特化键（决议 16）**——订阅方按 `capability` 决定读哪些：

| 能力 | 特化键 |
|---|---|
| `Chat` | `temperature` / `topP` / `maxTokens` / `thinkingEffort`（现状不变） |
| `Embedding` | `dimensions` / `encodingFormat` |
| `SpeechSynthesis` | `voice` / `format` / `speed` |
| `SpeechTranscription` | `language` |
| `Decision` | 无特化键（仅通用 `modelId` / `provider` / `capability`） |

**`llm.after_complete` 的 `result` 是超集**（不是分能力子集）：

| 键 | 适用 | 说明 |
|---|---|---|
| `capability` | 全部 | 必有，点分格式 |
| `provider` / `model` | 全部 | 必有 |
| `usage` | 全部 | `TokenUsage?` |
| `duration` | 全部 | `TimeSpan` |
| `content` | chat / transcription | 文本正文 |
| `reasoning` / `toolCalls` | chat | |
| `messageId` / `streaming` / `step` | chat | 与 `HookDataContract.ChatAfterComplete` 现有字段对齐 |
| `vectorCount` / `dimensions` | embedding | |
| `answers` | decision | |
| `audioBytes` | speech.synthesis | |

**消费方改造**：
- `BudgetUpdateHook`：订 `LlmAfterComplete`，**但只在 `capability == "chat"` 且 `SessionId` 非空时记账**（§7.6.1）。
- `MemoryHookHandler`：订 `LlmAfterComplete`，加 `capability == "chat"` 守卫。
- `AgentExecutor.cs:373`：`ChatOnError` → `LlmOnError`。

**废弃点告警**：`HookManager.AddHandler` 对 `chat.params|headers|message|after_complete|on_error` 五个字符串注册时打 `LogWarning`（"已改名 llm.*，本次注册不会触发"）。已提交的插件 dll 是编译产物，改名**不会**让它编译失败，只会静默失效——必须靠运行期告警兜住。

#### 7.6.1 计费范围（决议 1）

`BudgetUpdateHook` 的第一步就是 `payload.SessionId` 非空并 `ISessionManager.GetOrLoadAsync`，**无会话直接返回不记账**（`BudgetUpdateHook.cs:48-67`）。而 embedding 的主要调用方是记忆后台 worker、TTS 可能来自无会话工具，它们**没有 session**。

本设计明确：

- **只有 chat 计入 `SessionData.Cached*` 预算账**（保持现状语义）。
- 非 chat 能力的 `usage` 只通过 `llm.after_complete` 暴露给订阅方与 `ExecutionOutcome`，**不写会话预算**。
- 不做 `LlmUsageLedger`（新子系统，超出本次范围）。
- `capability == "chat"` 守卫是**必需**的，不是优化：否则 embedding 后台 worker 会在无会话时反复空转进入 hook。

### 7.7 装饰器

```csharp
public interface ILlmClientDecorator
{
    int Order { get; }
    ILlmCapabilityClient Wrap(ILlmCapabilityClient inner, ProviderConfig config);
}

public interface ILlmClientDecoratorRegistry
{
    void Register(ILlmClientDecorator decorator);
    bool Unregister(ILlmClientDecorator decorator);
    ILlmCapabilityClient Apply(ILlmCapabilityClient client, ProviderConfig config);
}
```

`RetryLlmClientDecorator` 泛型化。**工作量实测：8 个能力外层类 + 3 个流式变体 ≈ 600–900 行**（v1 只说"乏味"，严重低估）。共用 `LlmRetryPolicy` 与 `LlmCallContext.Items` 记账键。`SystemOneHttpHelper` 的自带退避删除，避免双重退避。

流式重试边界保持一致：**一旦产出过 chunk 绝不重试**（无法回滚已交付内容），已产出后的重开由应用层负责。

`ILlmCallInterceptor` / `OutboundPipeline` 不变（本就不依赖 chat 类型）。

### 7.8 Embedding 落地

**新实现**：`Llm.OpenAI/Clients/OpenAiEmbeddingClient.cs`，`POST {baseUrl}/embeddings`，复用 `LlmHttpClientFactory` 与 `OutboundPipeline`。

**Memory 侧改造**：

1. `MemoryEmbeddingOptions` 只保留 `Enabled`（**默认 `false`**）与 `ModelId`（`string?`，如 `openai/text-embedding-3-small`）。删除 `Provider` / `Model` / `Dimensions`。
2. `IsEmbeddingConfigured` = `Enabled && ModelId 非空`。**不检查目录**——目录异步填充，DI 期检查会产生启动竞态（首次解析早于目录刷新 → 永久 Null 服务直到重启）。
3. **必须显式配置 `ModelId`，绝不自动挑（决议 10 / §13 原则 1）**。也不读 `DefaultModels.embedding`。
4. 解析时机：**每次调用时**经 `ILlmRouter.Resolve<IEmbeddingClient>(ModelId)`，失败抛明确异常，由 `RouterEmbeddingService` 上抛。不在 DI 期做一次性二选一。
5. 删除 `ProviderEmbeddingService`、`EmbeddingConnectionTester` 的裸 HTTP；新增 `RouterEmbeddingService`（Memory 包内），经 `ILlmInvoker` 调 `IEmbeddingClient`。
6. `IEmbeddingConnectionTester.TestAsync(string modelId)` 改为经 `ILlmInvoker` 调一次单条 `EmbedAsync` 探针，返回维度。
7. `IEmbeddingStatus` / `ConfigurableEmbeddingStatus` 依赖 `IOptionsMonitor<MemoryOptions>` + `IModelManager`：`IsAvailable` = `Enabled && ModelId 非空 && 目录中存在该模型`。依赖边 `IEmbeddingStatus → IModelManager` 是 Abstractions 接口，合规。

> **DI 期不再二选一**：`MemoryServiceExtensions.cs:83-94` 现在按 `IsEmbeddingConfigured` 决定注册真实现还是 `NullEmbeddingService`。改为**始终注册 `RouterEmbeddingService`**，它在内部判 `Enabled` 并在未启用时返回 `Unavailable`/抛。判定发生在调用期而非注册期。

#### 7.8.1 为什么禁止隐式回落

「目录中第一个 Embedding 模型」会引发连锁故障：

- `ModelTypeInference.cs:22-23` 把任何含 `embedding`/`bge-*`/`gte-` 的模型标成 Embedding → 四个 provider 的目录里这类模型很多。
- 今天 `memory.json` 无这两个键 → embedding 恒关闭、`NullEmbeddingService` 生效。改成"自动选第一个"后，**升级即自动开启向量索引并产生后台成本**，用户从未同意。
- "第一个"随目录刷新变化（字典顺序）→ **同一个库先后写入不同模型的向量**。
- 叠加 `VectorIndex.CosineSimilarity` 维数不等**直接抛**（`VectorIndex.cs:294-297`）与 `SqliteEmbeddingCache` 键**不含模型**（`SqliteEmbeddingCache.cs:47-54`）→ 间歇性异常。

**连带必须修的三处**：

- `SqliteEmbeddingCache` 主键改为 `(text_hash, model_fingerprint)`；`EmbeddingResult` 增 `ModelId`。
- `VectorIndex.CosineSimilarity` 维数不等 → **跳过该条 + Warning**，不抛。
- `HybridMemoryIndex` 在 `IsAvailable == false` 时不建向量索引（保持现状，语义随上改变）。

**WebUI**：`MemorySettingsCard.razor` 四处随动——`ResolveEmbeddingKey`（`:137-153`）、`OnEmbeddingKeyChanged`（`:183-199`，改为只写 `ModelId`）、`当前: provider/model` 提示行（`:72-77`）、`Dimensions` 回写（`:229-235`，改为只显示不落盘）。占位文案"空则使用 DefaultModel"改为"空则不启用向量检索"。

### 7.9 Decision 合流

**包重命名**：`src/capabilities/Seeing.Agent.SystemOne/` → `src/capabilities/Seeing.Agent.Llm.SystemOne/`，程序集同名，`RootNamespace` 改 `Seeing.Agent.Llm.SystemOne`。同步 `Seeing.Agent.slnx:52,107`。

**模块 id**：`systemone` → `llm.systemone`；`Tools.SystemOne` 的 `systemone.tools` → `llm.systemone.tools`，其 `DependsOn: ["systemone"]` → `["llm.systemone"]`。同步 `BuiltInCapabilitySets.cs:110-111`、`ModuleArchitectureInvariantTests.cs:33`、`FullCapabilityComposition.cs:115-116`、`HostComposition.Tests`。

**保留并改造**：
- `TypeSafeSystemOneClient` → 实现 `IDecisionClient` + `IModelCatalogClient`；`ProviderType = "typesafe"`。
- `TypeSafeSystemOneClientFactory` → 实现 `ILlmClientFactory`；`SupportedProtocols = { "typesafe" }`；`Create(config, Decision)` 返回 `TypeSafeSystemOneClient`。
- `SystemOneService` → 改为 `IDecisionClient` 门面，内部经 `ILlmRouter.Resolve<IDecisionClient>`。

**不新增任何 Provider 类。** typesafe 走 `Providers` + `ConfiguredLlmProvider`（`ProviderManager.cs:342-348`），动态模型靠 `IModelCatalogClient` 端口。

> **v1 的 `SystemOneLlmProvider : LlmProviderBase` 是阻断项**：`LlmProviderBase` 在 Core，`Seeing.Agent.SystemOne.csproj` 只引用 Abstractions，加 Core 引用会命中 `04 §4` 强制检查与 `06` 已签字全绿项。

**配置迁移**（`systemone.json` → `Providers.typesafe`），**必须显式写回四个非默认值**：

```json
{
  "Providers": {
    "typesafe": {
      "type": "typesafe",
      "capabilities": ["decision"],
      "baseURL": "https://api.typesafe.ai",
      "apiKey": "...",
      "timeout": 10000,
      "max_retries": 2,
      "retry_base_delay": 500,
      "retry_max_delay": 5000,
      "useProxy": false,
      "listModels": true,
      "models": { "jev-latest": { "types": ["decision"], "name": "Jev" } }
    }
  }
}
```

| 参数 | SystemOne 现值 | `ProviderConfig` 默认 | 不写会怎样 |
|---|---|---|---|
| `timeout` | 10000 ms | 300000 ms | 决策调用从 10s 变 5min |
| `max_retries` | 2 | **0 = 不限次数** | 退避到 120s 预算耗尽 |
| `useProxy` | `false` | `true` | 流量走系统代理，企业代理下 Jev 不可达 |

`SYSTEMONE_*` 环境变量支持删除（统一由框架的环境变量覆盖机制处理）。

**工具改造与默认模型归属（决议 8 / 11–14）**：`Tools.SystemOne/SystemOneTool.cs:74` 的 `GetService<ISystemOneService>()` → `ILlmRouter.Resolve<IDecisionClient>(modelId)`。

`modelId` 的解析顺序（唯一，只在此处定义）：

1. 工具参数 `model` 显式传入；
2. `IModelConfigManager.GetDefaultModel(LlmCapabilityKind.Decision)`——**这是 `DefaultModels.decision` 的唯一消费点**；
3. 仍未配置时，从模型目录挑 `Decision` 模型：恰好一个则用，多个则报错并列出候选；
4. 一个都没有则报明确错误（"未配置 Decision 模型"），**不静默回落**。

> v3 曾在此处写"读插件自己的配置"，与 §7.14.4 的 `GetDefaultModel(Decision)` 冲突。**以本条为准**：第 2 步读全局 `DefaultModels`，插件不再有独立的"decision 默认模型"配置项。

`SystemOneToolParser.cs:191-241` 的 `SystemOneQuestionTypes` 引用随命名空间迁移同步；`SystemOneTool.cs:3,90` 的 `SystemOneException` 引用同理。

### 7.10 Speech 落地

放在 `Llm.OpenAI`（同一 endpoint）：

- `OpenAiSpeechSynthesisClient`：`POST /v1/audio/speech`。非流式返回完整音频；流式按响应体自然分块产出 `AudioChunk`。
- `OpenAiSpeechTranscriptionClient`：`POST /v1/audio/transcriptions`（multipart）。非流式返回文本 + 可选段落；流式按 SSE 增量给 `TranscriptSegment`。

`Modalities` 判定：`{ "input": ["audio"], "output": ["audio"] }` 为双向。

**已知死角**：`ModelConfig.Modalities` 默认是两个空列表，存量配置全是 `{"input":[],"output":[]}`。只标 `Speech` 而不填 `Modalities` → `Resolve` 必然抛"声明不足"（决议 24 确认这是正确行为）。但**模型管理页必须给出可操作提示**，否则变成"标了就用不了"。

本次不新增 `speak`/`listen` 工具（决议 26）；Gateway 语音消息接入留后续。

### 7.11 Rerank / Image / Video：只出契约

- 契约齐全，`Create` 对这几种能力返回 `null`。
- 模型可标 `Rerank` / `ImageGeneration` / `Video`；`Resolve` 会因"Provider 未声明该能力"或"工厂未提供客户端"给明确异常（第 4/5 层）。
- 第三方 provider 包只需实现 `ILlmClientFactory` 并在 `Create` 返回对应客户端即可接入，无需改框架。

### 7.12 `ModelType` 枚举治理

```csharp
public enum ModelType
{
    Text,
    Embedding,
    Rerank,
    Decision,          // 新增
    ImageGeneration,   // 新增（原 Image 的生成语义）
    Video,
    Speech,
    Other,
}
```

- **`Image` 已删除（决议 4）**。原值是生成语义，现统一为 `ImageGeneration`。图像理解不占用枚举值，由 `Modalities.Input` 含 `image` 表达。
- `UnifiedConfigManager` 的 `JsonSerializerOptions` 启用 `JsonStringEnumConverter` → 按名字序列化。
- `ModelConfig.Types` 上没有显式转换器，实际取决于调用方 options → **实现时统一为字符串序列化并加 `Invariants` 测试锁定**（`JsonSerializer.Serialize(ModelType.Decision)` 必须产出 `"Decision"`）。
- **不写迁移逻辑**（本项目无历史用户数据）。
- **同步切推断**：`ModelTypeInference.cs:31-32` 的 `stable-diffusion` / `sdxl` / `flux` / `dall-e` / `-image` / `image-` **全部**返回 `ImageGeneration`（含糊模式也归生成，因为已无 `Image` 值可归）。

### 7.13 ProviderInfo、能力 UI 与连接测试

```csharp
public record ProviderInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string Protocol { get; init; } = "";          // 新增
    public LlmCapabilityKind Capabilities { get; init; }  // 新增
    public ProviderSource Source { get; init; }
    public string? OwnerExtensionId { get; init; }
    public int MaxRetries { get; init; }
}
```

**能力配置 UI（决议 5）**：Provider 编辑表单加一组能力多选框（`Chat` / `Embedding` / `Decision` / `SpeechSynthesis` / `SpeechTranscription` / `ImageGeneration` / `VideoGeneration` / `Rerank`）。需要同步的**四处硬编码字段清单**：

- `ConfiguredLlmProvider.LoadConfigAsync`、`ApplyValues`、`CloneConfig`
- `ProviderManager.CloneConfig`
- `ModelsPage.CloneProviderConfig`

**漏任何一处 = 保存一次配置就抹掉 `capabilities`。**

**两层配置不一致的呈现（决议 6）**：模型管理页每个模型旁显示「可路由能力」= `Provider.Capabilities ∩ LlmCapabilityMap.FromModelType(model.Types)`。不一致时给**告警文案但不阻止保存**：

> 此模型已标记 `Embedding`，但所属 Provider 未开启该能力 → 调用时会被拒绝。

**连接测试（决议 9 / 25）**：

```csharp
Task<bool> TestConnectionAsync(
    string modelId,
    LlmCapabilityKind capability,
    CancellationToken cancellationToken = default);
```

UI 的"测试连接"按钮针对**当前正在编辑/查看的能力**发测，不做推导。Provider 实现直接 `GetClient(capability)`，不查目录、不猜（§13 原则 1）。

### 7.14 按能力配置的全局默认模型（决议 11–14、21–23）

**问题**：现状只能配一个 `SeeingAgent.DefaultModel`（`SeeingAgentOptions.cs:16`），且 `CanSetAsDefaultModel` 硬性要求 `ModelType.Text`（`ModelConfigManager.cs:188-192`）。"指定 embedding 默认用哪个模型"无处可配。

**证据**：`TitleGeneration.Model`（`SeeingAgentOptions.cs:120`）**并非死配置**——`SessionTitleService.cs:166,198` 有真实消费者（`var model = options.Model ?? fallbackModelId;`）。spec 早期版本称其"全仓零消费者"是**事实错误**，此处更正。

**但它不能被接进 `ITextCompletion` 的回落链**：`ITextCompletion` 是共享旁路（标题、`LlmSummarizer`、`LlmMemoryExtractor/Evolution` 都用它）。若写成 `model ?? TitleGeneration.Model ?? GetDefaultModel(Chat)`，给标题配一个便宜模型会**静默改掉会话压缩与记忆抽取的模型**——这正是 §13 原则 1 反对的隐式策略。

正确做法：标题生成在 `SessionTitleService` 内部自己用它（现状即如此，无需改动）；共享旁路只回落 `GetDefaultModel(Chat)`。

#### 7.14.1 配置形状

`seeing.json` 顶层：

```json
{
  "DefaultModels": {
    "chat": "openai/gpt-4o",
    "embedding": "openai/text-embedding-3-small",
    "decision": "typesafe/jev-latest",
    "speechSynthesis": "openai/tts-1"
  }
}
```

- **key 是 `LlmCapabilityKind` 的配置键投影**（小驼峰，`LlmCapabilityMap.ToConfigKey`）。
- **value 必须带 `provider/` 前缀（决议 21）**。裸模型 ID 在写入时直接报错并提示补全——目录里同名模型可能来自多个 provider，歧义无法可靠消解。
- **旧单值 `SeeingAgent.DefaultModel` 删除（决议 11/22）**。`ConfigSectionRegistry`（`ConfigSectionRegistry.cs:42`）与 `SeeingAgentOptions` 同步改。
- **`ProviderConfig.DefaultModel` 也删除（决议 22）**：默认模型只在全局一处，避免"Provider 级默认"与"全局默认"两套。`ProviderManager.RequiresRebuild`（`:402`）对该字段的引用一并清理。
- `LegacyGatewayConfigMigrator`（`:26-34`）迁移目标改指向 `DefaultModels.chat`；若无历史数据可直接删除该迁移器。

#### 7.14.2 接口（全部在 `IModelConfigManager`，决议 12）

```csharp
public interface IModelConfigManager
{
    // ... 现有成员 ...

    /// <summary>
    /// 取指定能力的全局默认模型引用。
    /// <para>严格查表：未配置返回 null，<b>不回落到其他能力、不查目录猜</b>（决议 13）。</para>
    /// </summary>
    string? GetDefaultModel(LlmCapabilityKind capability);

    /// <summary>取全部已配置的能力默认值（只含已配置项）。</summary>
    IReadOnlyDictionary<LlmCapabilityKind, string> GetAllDefaultModels();

    /// <summary>
    /// 便捷解析：查默认值 → 经 ILlmRouter 解析客户端。
    /// <para>未配置返回 false 且 reason="未配置该能力的默认模型"；
    /// 配置了但解析失败返回 false 并带 Router 的失败原因。</para>
    /// </summary>
    bool TryResolveDefault<TClient>(
        LlmCapabilityKind capability,
        out TClient? client,
        out string? reason)
        where TClient : class, ILlmCapabilityClient;

    /// <summary>
    /// 设置某能力的默认模型。写时校验（决议 14 / 21）：
    /// 引用必须带 provider 前缀、模型存在、且 GetEffectiveTypes ∩ LlmCapabilityMap.FromModelType 含该能力。
    /// 任一不满足 → 抛 ArgumentException。
    /// </summary>
    Task SetDefaultModelAsync(
        LlmCapabilityKind capability,
        string? modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>某模型能否被设为该能力的默认（打破原硬编码 ModelType.Text 限制）。</summary>
    bool CanSetAsDefaultModel(string modelId, LlmCapabilityKind capability);

    /// <summary>
    /// 校验全部默认值：模型是否仍存在、是否仍具备所声明的能力（决议 23）。
    /// 返回每项的问题描述（空字典 = 全部有效）。供设置页与启动诊断使用。
    /// <para><b>只报告，不自动清除</b>：失效的默认值保留在配置里，等用户修。</para>
    /// </summary>
    IReadOnlyDictionary<LlmCapabilityKind, string> ValidateDefaultModels();
}
```

**破坏性变更**：

| 原成员 | 变更 |
|---|---|
| `GetDefaultModel()` | → `GetDefaultModel(LlmCapabilityKind capability)` |
| `SetDefaultModelAsync(string? modelId, ...)` | → 加 `LlmCapabilityKind capability` 首参 |
| `CanSetAsDefaultModel(string modelId)` | → 加 `LlmCapabilityKind capability` |

> `CanSetAsDefaultModel` 必须改签名而不是保留旧重载：旧的硬编码 `ModelType.Text` 会让"设 embedding 默认"永远失败。保留旧重载等于留一个会静默误判的入口。

#### 7.14.3 chat 默认值的解析优先级（保持既有语义）

`ModelManager.ResolveNativeModel`（`ModelManager.cs:32-46`）的优先级**不变**，只把最后一档从 `DefaultModel` 换成 `GetDefaultModel(Chat)`：

```
请求 model > 会话 SelectedModel > Agent.Model > GetDefaultModel(Chat)
```

`ResolveAcpModel` 仍然**不回退** Native 默认（`ModelManager.cs:51-60`，现有行为）。

#### 7.14.4 各能力包如何使用

**Core 只提供查表 + 解析，不替任何能力选模型。**

| 能力包 | 接入方式 |
|---|---|
| `Memory` | **不读 `DefaultModels.embedding`**（决议 10）：必须显式配 `MemoryOptions.Embedding.ModelId`。唯一例外——记忆索引一开跑就有后台成本，不应被全局默认隐式激活 |
| `SystemOne` 工具 | `GetDefaultModel(Decision)` → 有则用；无则从目录挑（§7.9） |
| `TextCompletionService` | 改用 `GetDefaultModel(Chat)`；**不接 `TitleGeneration.Model`**——它是 `SessionTitleService` 专用的，接进共享旁路会污染摘要与记忆抽取（§7.14.4 说明） |
| 未来的 TTS/ASR 工具 | `GetDefaultModel(SpeechSynthesis)` / `GetDefaultModel(SpeechTranscription)` |

> `TitleGeneration.Model` 的修复是**顺带**的：它现在是纯死配置，接成"标题生成优先用它，未配则用 chat 默认"，成本几乎为零。若不想扩大范围可留作后续，但必须标注它是已知死配置。

#### 7.14.5 WebUI

- **设置页**（`Settings.razor:97`）：单值 `ModelSelectDropdown` 改为**按能力分组的多个下拉框**，每项 `FilterType` 对应能力，`AllowClear` 允许留空（留空 = 未配置）。
- **模型管理页**（`ModelsPage.razor:33,230,638,796-811`）：
  - 顶部提示从"默认模型: X"改为按能力列出已配置项；
  - 每行的"设为默认"改为**下拉选择设为哪个能力的默认**；
  - `SetDefaultModel` 调用改为传 `capability`；
  - `CanSetAsDefaultModel` 判定改为带 `capability`；
  - 显示 `ValidateDefaultModels()` 的失效告警。
- **记忆设置卡**（`MemorySettingsCard.razor:46`）：占位文案改为"空则不启用向量检索"。

---

## 8. 考虑过的其他路线

| 路线 | 形态 | 为什么没选 |
|---|---|---|
| **A. 能力端口 + 客户端束**（选中） | 每能力一个窄接口；Provider 供出多客户端；Router 按 `ModelConfig.Types` 解析 | 保留各模态真实形状，横切机制可上提；代价是接口增长，但每个都对应真实协议差异 |
| B. 统一调用信封 | `InvokeAsync(LlmInvocation)`，`Kind` 区分，载荷 `object` | C# 类型安全全丢，调用方全要 cast；Jev 的多问题结构塞不进扁平信封；编译期发现不了错配 |
| C. 能力契约注册表 | 能力/请求/响应都是可注册 JSON schema，client 只做 HTTP | 使用方要自己拼 schema 解包，比现有 chat 更难用 |

A 的原型是 Semantic Kernel 的 `IAIService` + `IAIServiceSelector`：能力接口按模态拆、共享元数据基座、独立可替换的选择器。区别在于"元数据"换成已有的 `ModelConfig.Types` + `ProviderConfig.Capabilities`，"选择器"落成 `ILlmRouter`，不引入 SK 的 DI 容器语义（本仓库已有配置驱动的 `ProviderRegistry` 与热重载体系）。

`ILlmInvoker` 相当于 SK 的 `KernelFunction` 调用管道：它才是在非 chat 路径上触发 Hook、合并出站头、计算耗时的位置，Router 只负责解析。

---

## 9. 影响面清单

### 9.1 Abstractions

**新增**（`Llm/`）：`LlmCapabilityKind.cs`、`LlmCapabilityMap.cs`、`ILlmCapabilityClient.cs`、`IChatCompletionClient.cs`、`IEmbeddingClient.cs`、`IRerankClient.cs`、`IDecisionClient.cs`、`ISpeechSynthesisClient.cs`、`ISpeechTranscriptionClient.cs`、`IImageGenerationClient.cs`、`IVideoGenerationClient.cs`、`IModelCatalogClient.cs`、`ILlmRouter.cs`、`ILlmInvoker.cs`、`LlmInvocationOptions.cs`、`LlmCapabilityMismatchException.cs`、`ProviderCapabilityDefaults.cs`、`Hooks/ILlmHookPayloadAdapter.cs` + `ILlmHookPayloadAdapterRegistry.cs`、`Embedding/EmbeddingModels.cs`、`Decision/DecisionModels.cs`、`Speech/SpeechModels.cs`、`Image/ImageModels.cs`、`Video/VideoModels.cs`

**删除**：`Llm/ILlmClient.cs`、`Llm/IProviderEndpointLookup.cs`（整组：`ProviderEndpoint` / `IProviderEndpointInfo` / `IProviderEndpointLookup`，唯一生产消费者是待删的两个 Memory 类，且 `IProviderEndpointInfo` 零实现者）、`SystemOne/`（7 个文件迁入 `Llm/Decision/`）

**修改**：`Llm/ILlmProvider.cs`、`Llm/ILlmClientFactory.cs`、`Llm/ILlmClientDecorator.cs`、`Llm/LlmClientFactoryResolver.cs`（`SupportsType`→`SupportsProtocol`）、`Llm/ModelType.cs`、`Llm/ProviderConfig.cs`（+`Capabilities`/`ListModels`，**-`DefaultModel`**，`Normalize` 加两个协议）、`Llm/ProviderInfo.cs`（+`Protocol`/`Capabilities`）、`Llm/IModelConfigManager.cs`（§7.14.2 六个成员）、`Hooks/HookRegistry.cs`、`Hooks/HookDataContract.cs`

### 9.2 Core

**新增**：`Llm/LlmRouter.cs`、`Llm/LlmInvoker.cs`、`Llm/Hooks/LlmHookPayloadAdapterRegistry.cs`（+ 内置五个适配器实现）

**修改**：
- `Llm/ConfiguredLlmProvider.cs` —— N 个 `Lazy<ILlmCapabilityClient>`；`DisposeAsync` 释放全部（现只释放单个，`:148-172`）；`GetModelsAsync` 支持 `IModelCatalogClient`；**三处硬编码字段清单都要加 `Capabilities`/`ListModels` 并删 `DefaultModel`**（`LoadConfigAsync:74-99`、`ApplyValues:187-227`、`CloneConfig:361-380`）
- `Llm/ProviderManager.cs` —— `ResolveFactory` 走 `SupportsProtocol`；`RequiresRebuild` 纳入 `Capabilities`/`ListModels`、移除 `DefaultModel`（`:389-403`）；`CloneConfig`（`:415-434`）
- `Llm/ModelConfigManager.cs` —— §7.14.2 六个成员；`CanSetAsDefaultModel` 去 `ModelType.Text` 硬编码（`:188-192`）
- `Llm/ModelManager.cs` —— 透传新成员；`ResolveNativeModel` 末档改 `GetDefaultModel(Chat)`
- `Llm/LlmService.cs` —— 改走 `ILlmInvoker`
- `Llm/TextCompletionService.cs:63,97` —— `DefaultModel` → `GetDefaultModel(Chat)`；接上 `TitleGeneration.Model`
- `Llm/ProviderRegistry.cs`、`Llm/LlmProviderBase.cs`（`GetClient(kind)` + `TestConnectionAsync` 能力参数 + `RequireFactory`）、`Llm/IProviderManager.cs`（`GetClient`/`GetClientForModel` 返回类型）
- `Configuration/SeeingAgentOptions.cs:16` —— 删除 `DefaultModel`，新增 `Dictionary<string,string> DefaultModels`
- `Configuration/ConfigSectionRegistry.cs:42` —— `"DefaultModel"` → `"DefaultModels"`
- `Configuration/LegacyGatewayConfigMigrator.cs:26-34` —— 迁移目标改 `DefaultModels.chat`，或整体删除
- `Extensions/ServiceCollectionExtensions.cs` —— 注册 `ILlmRouter` / `ILlmInvoker` / 适配器注册表 + 内置五适配器
- **删除** `Llm/OptionsProviderEndpointLookup.cs`

### 9.3 能力包

- `Llm.OpenAI`：新增 `OpenAiEmbeddingClient` / `OpenAiSpeechSynthesisClient` / `OpenAiSpeechTranscriptionClient`；工厂按能力分派（含 `openai-responses`）；改造 `OpenAiChatClient` / `OpenAiResponsesClient`
- `Llm.Anthropic`：工厂仅 `Chat`
- `Llm.SystemOne`（原 `SystemOne`）：见 §7.9
- `Memory`：见 §7.8；`ConfigurableEmbeddingStatus` 依赖改 `IModelManager`；`MemoryOptionsProvider:48-50` 日志口径；`SqliteEmbeddingCache` 主键
- `TokenBudget`：`BudgetUpdateHook` 改订 `LlmAfterComplete` + `capability == "chat"` 守卫
- `Tools.SystemOne`：`SystemOneTool.cs` / `SystemOneToolParser.cs` 改 Router；默认模型走 `GetDefaultModel(Decision)`
- 4 个 plug provider：`GetClient()` → `GetClient(LlmCapabilityKind)`，仅 `Chat`；`_client` 字段/`InvalidateClient`/`DisposeClientAsync` 随之改

### 9.4 samples / docs

- `WebUI/Components/Settings/MemorySettingsCard.razor`（四处 + 占位文案）
- `WebUI/Pages/Settings.razor:97,345` —— 单值下拉改按能力分组
- `WebUI/Pages/ModelsPage.razor:33,230,638,796-811,1145` —— 默认模型面板、按能力设默认、`CloneProviderConfig`
- `HostComposition/FullCapabilityComposition.cs`
- `docs/architecture/02-modules.md:65`、`README.md:241-246`（hook 表与模块 id）、`06-compliance-audit.md:188-199`（纯函数豁免清单加 `LlmCapabilityMap` / `ProviderCapabilityDefaults`）

### 9.5 破坏性变更清单

| 项 | 变更 |
|---|---|
| `ILlmClient` | 删除，拆为 `IChatCompletionClient` + `ILlmCapabilityClient` |
| `ILlmProvider.GetClient()` | → `GetClient(LlmCapabilityKind)`；+`Protocol` / `Capabilities`；`TestConnectionAsync` +`capability` |
| `ILlmClientFactory` | `Create(config)` → `Create(config, capability)`；`SupportedTypes` → `SupportedProtocols` |
| `ILlmClientDecorator.Wrap` | `ILlmClient` → `ILlmCapabilityClient` |
| `HookRegistry.Chat*` | → `Llm*`，载荷加 `capability`；旧点注册打 Warn |
| `ModelType` | **删除 `Image`**；加 `Decision` / `ImageGeneration` |
| **`SeeingAgentOptions.DefaultModel`** | **删除**，改 `DefaultModels` 字典（按能力索引） |
| **`ProviderConfig.DefaultModel`** | **删除**（决议 22） |
| **`IModelConfigManager.GetDefaultModel()`** | → `GetDefaultModel(LlmCapabilityKind)` |
| **`SetDefaultModelAsync` / `CanSetAsDefaultModel`** | 均加 `LlmCapabilityKind` 首参 |
| `systemone.json` / `SYSTEMONE_*` | 删除，并入 `Providers.typesafe` |
| `memory.json` `Embedding.Provider/Model/Dimensions` | 删除，改 `Enabled`(默认 false) + `ModelId`（**必须显式配置**） |
| `IProviderEndpointLookup` 整组 | 删除 |
| `ISystemOneService` / `ISystemOneProviderRegistry` / `ISystemOneProvider` | 删除 |
| `Seeing.Agent.SystemOne` 包名 | → `Seeing.Agent.Llm.SystemOne` |
| 模块 id | `systemone` → `llm.systemone`；`systemone.tools` → `llm.systemone.tools` |
| `ProviderConfig` | 新增 `capabilities` / `listModels`；**已存配置若不写 `capabilities` 只获得 `Chat`** |
| `ProviderTypes` | 新增 `openai-responses` / `typesafe` |
| `TextCompletionService` 调用方 | 默认模型非 chat 时从"远端 400"变为本地抛 `LlmCapabilityMismatchException` |

---

## 10. 测试计划

### 10.1 测试爆炸半径（实数）

| 组 | 文件数 | 说明 |
|---|---|---|
| `tests/spine/Seeing.Agent.Tests/Llm/` | 9 | `ConfiguredLlmProviderTests`、`LlmTransportOnlyTests`、`ModelCatalogAggregationTests`、`ProviderConfigMigrationTests`、`ProviderConfigStringTests`、`ProviderManagerCharacterizationTests`、`ProviderManagerConcurrencyTests`、`ProviderManagerTests`、`RetryLlmClientDecoratorTests` |
| `tests/spine/Seeing.Agent.Tests/Invariants/` | 2 | `MigrationTests`、`LlmAndExecutorTests` |
| `tests/plugs/` | 6 | DeepSeek/MiMo/OpenCodeZen **各 2 个**。**只有 3 个 provider 测试项目**，v1 写"4 个"有误 |
| `tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests/` | 8 | v1 完全漏掉 |
| `tests/capabilities/Seeing.Agent.Memory.Tests/` | 6 处 | `EmbeddingConnectionTesterTests`、`MemoryOptionsTests`、`MemoryModuleHookLifecycleTests`、`ChatMemoryHandlerEnqueueTests` 等 |
| `tests/spine/.../Hooks/HookManagerTests.cs` | 1 | `HookRegistry.ChatParams` |
| `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests` / `.Anthropic.Tests` | 2+ | `SupportsType` / `SupportedTypes` 消失 |
| `tests/apps/Seeing.Agent.HostComposition.Tests` | 1+ | 模块 id 清单守门 |
| **合计直接受影响** | **17** 个 src/plugs 文件含 `ILlmClient` + 上述新增项 | |

`LlmTransportOnlyTests.cs:51-53` 断言**字符串** `"chat.params"`，改名后必改。

### 10.2 SystemOne.Tests 处置（1667 行 / 20 文件）：13 删 / 3 改 / 4 留

| 文件 | 行 | 判定 |
|---|---|---|
| `SystemOneSerializationTests` | 218 | **留**（仅命名空间改名） |
| `TypeSafeSystemOneClientTests` | 107 | **改**（断 `IDecisionClient.EvaluateAsync`） |
| `SystemOneHttpHelperTests` | 183 | **部分留**（错误映射/鉴权头留；429/529/退避用例随重试迁出而删） |
| `SystemOneModuleTests` / `ScaffoldSmokeTests` | 28 | **改**（模块 id） |
| `SystemOneProviderManagerTests`(197)、`SystemOneConfigStoreTests`(53)、`SystemOneEnvironmentConfigTests`(44)、`SystemOneReloadHandlerTests`(61)、`SystemOneClientFactoryResolverTests`(85)、`ConfiguredSystemOneProviderTests`(82)、`SystemOneServiceTests`(135)、`SystemOneModuleDiTests`(88)、`PredefinedSystemOneProvidersTests`(27)、`SystemOneTestDoubles`(66)、`TestConfigSectionStore`(31)、`Clients/SystemOneHttpClientFactoryTests`(34) | ~911 | **删**（对象被删） |

**不是"整套重写"**——约 40% 可救。

### 10.3 用例

| 阶段 | 目标 | 用例 |
|---|---|---|
| 0 | **表征测试先行** | 锁定现状：`LlmService` 的 5 个 Hook 时机与载荷、`RetryLlmClientDecorator` 非流式/流式重试边界（未产出 chunk 才重试、产出后绝不重试）、`ProviderManager` 热重载重建条件、`BudgetUpdateHook` 无 session 时短路 |
| 1 | 契约层 | `LlmCapabilityMap` 四张表完备：每个 `ModelType` 有映射（含 `None`）、每个 `LlmCapabilityKind` 有客户端类型、`ClientTypes` 与八个接口一一对应、`ToConfigKey`/`ToHookValue` 双向一致；`ModelType` 字符串序列化锁定；**枚举中不存在 `Image`**；**`ILlmCapabilityClient` 无 `Capabilities` 属性** |
| 2 | Router | 五层校验逐层失败各自抛不同消息；`Speech` 缺 `Modalities` → "声明不足"；**配置声明能力但工厂返回 null → 第 5 层报错**；缓存键是 `(providerId, kind)`（同 provider 两模型返回同一实例）；`TryResolve` 不抛且 reason 可读；**Router 无"未指定模型"重载** |
| 3 | Provider/工厂 | 按 `capabilities` 建 Lazy；未声明能力 `GetClient` 返回 null；`RequiresRebuild` 在 `capabilities` 变化时重建；plug provider 仅出 Chat 且**仍被装饰器包裹**（防 B2 回归）；`listModels=true` 时合并远端目录；**`openai-responses` 协议可创建 chat 客户端** |
| 4 | Invoker | 非 chat 能力经 `ILlmInvoker` 触发 `llm.params`/`llm.headers`/`llm.after_complete`；**`llm.params` 按能力给不同特化键**；`capability` 为点分格式；`EmitHooks=false` 时全不触发；`llm.on_error` 在重试耗尽后只触发一次 |
| 4b | 载荷适配器 | 内置五个适配器各自产出正确 key；**插件注册同能力适配器覆盖内置**；未实现能力走通用兜底且**不抛异常** |
| 5 | 装饰器 | 泛型重试对 `IEmbeddingClient`/`IDecisionClient` 生效；流式音频未产出 chunk 时可重试、产出后不重试；`Items` 记账键一致 |
| 6 | Embedding | `Enabled=false` → `IsAvailable == false` 且不建索引；**`Enabled=true` 但无 `ModelId` → 仍不启用**；**不读 `DefaultModels.embedding`**；模型不可解析 → 明确异常；缓存键含模型指纹；维数不等跳过不抛 |
| 7 | Decision | `TypeSafeSystemOneClient` 实现 `IDecisionClient`；`DecisionModel → ModelConfig{Types=[Decision]}`；`Resolve<IDecisionClient>("jev-latest")` 成功；三原语解析正确；`listModels` 动态目录出现；**工具未传 model 且 `DefaultModels.decision` 未配 → 报错列候选，不静默选** |
| 8 | Speech | TTS 非流式返回字节；流式产出有序 `AudioChunk`；**ASR 不 Dispose 传入 Stream（用可检测 DoubleDispose 的流断言）**；`TranscriptSegment` 顺序 |
| 9 | Hook 兼容 | `chat.*` 字符串注册触发 Warn 且不执行 |
| 10 | UI | Provider 表单能力多选框保存后**不被 clone 抹掉**；模型页展示可路由能力且不一致时告警 |
| 11 | **默认模型系统** | `GetDefaultModel(capability)` 严格查表、未配返回 null；`SetDefaultModelAsync` 对"裸 ID""模型不存在""模型不具备该能力"分别抛错；`CanSetAsDefaultModel` 不再硬编码 Text（设 embedding 默认成功、设 chat 默认对 embed 模型失败）；`TryResolveDefault` 三种失败各给可读 reason；`ValidateDefaultModels` 能查出"默认值指向已删模型"**且不清除它**；**优先级不变**：请求 > 会话 > Agent > `GetDefaultModel(Chat)` |
| 12 | **默认模型 UI** | 设置页按能力分组下拉，留空 = 未配置；模型页"设为默认"可选能力；`DefaultModels` 保存后完整 round-trip（不被 clone 抹掉） |
| 13 | 回归 | 全量测试通过；`dotnet build Seeing.Agent.slnx` 零新增警告；WebUI 记忆设置页 / 模型管理页 / 设置页手工验证 |

---

## 11. 实施顺序（按可提交性重切）

v1 的九步会被误读为"逐步提交"，实际第 2–4 步必须同一 commit。

| # | 楔子 | 内容 | 可独立提交 |
|---|---|---|---|
| 1 | 表征测试 | 锁定 Hook 时机、重试边界、Provider 重建条件、Budget 短路 | 是 |
| 2 | **Hook 改名 + 适配器契约** | `HookRegistry` / `HookDataContract` / `ILlmHookPayloadAdapter` 契约与注册表 + 内置五个适配器 / `BudgetUpdateHook` / `MemoryHookHandler` / `AgentExecutor` / `HookManager` 废弃告警 | 是（与 3–5 无编译耦合） |
| 3 | **编译恢复楔子（大）** | Abstractions 全部新契约 + 删除 `ILlmClient` + Core 接线（`ConfiguredLlmProvider`/`ProviderManager`/`ProviderRegistry`/`LlmProviderBase`/装饰器泛型化/`LlmRouter`/`LlmInvoker`/`LlmService`）+ OpenAI/Anthropic 工厂（含 `openai-responses`）+ 4 个 plug provider + 全部对应测试改造 | **否，必须一个 commit** |
| 4 | SystemOne 合流 | 包重命名、删 12 文件、客户端改造、配置迁移、`slnx`/能力集/守门测试/`Tools.SystemOne` | 是（依赖 3） |
| 5 | Memory 迁移 | 删裸 HTTP、`RouterEmbeddingService`、`memory.json` 字段、缓存键、`CosineSimilarity` 降级、WebUI 设置卡 | 是（依赖 3） |
| 6 | **能力 UI** | Provider 表单能力多选框 + 四处 clone 字段 + 模型页可路由能力展示与不一致告警 | 是（依赖 3） |
| 6b | **默认模型系统** | `DefaultModels` 配置节 + `IModelConfigManager` 六个成员 + `ResolveNativeModel` 末档 + `TextCompletionService` + 设置页/模型页默认值 UI | 是（依赖 3） |
| 7 | 契约补齐 | `IRerankClient` / `IImageGenerationClient` / `IVideoGenerationClient` 插槽与文档 | 是 |
| 8 | 收尾 | 架构文档同步、`06` 豁免清单、全量回归 + 手工验证 | 是 |

### 11.1 复核清单（完成前逐条打勾）

- [ ] `rg "Seeing\.Agent\.Core\.csproj" src/capabilities src/gateway -g "*.csproj"` 结果与审查前一致（无新增分层违规）
- [ ] `rg "ILlmClient\b" src samples tests plugs` 为空
- [ ] `rg "chat\.(params|headers|message|after_complete|on_error)" src samples tests` 仅剩 `HookManager` 废弃告警表
- [ ] `rg "GetClient\(\)" src plugs` 为空
- [ ] `rg "IProviderEndpointLookup|IProviderEndpointInfo" src samples` 为空
- [ ] `rg "\.Capabilities" src` 未出现于 `ILlmCapabilityClient` 实现类（第三份真相已删）
- [ ] `rg "DefaultModel\b" src` 仅剩 `DefaultModels` 相关（单值已删）
- [ ] `dotnet build Seeing.Agent.slnx` 零新增警告
- [ ] 模块守门测试含新 id `llm.systemone` / `llm.systemone.tools`
- [ ] 手工：默认 `memory.json`（无 Embedding 段）→ 向量索引**不**启动、`IsAvailable == false`
- [ ] 手工：`Embedding.Enabled=true` 但 `ModelId` 留空 → 仍不启用（不自动挑模型）
- [ ] 手工：`Providers.typesafe` 不写 `capabilities` → 目录无 jev 模型、`Resolve<IDecisionClient>` 给明确异常
- [ ] 手工：Provider 声明 `embedding` 但工厂未实现 → Router 第 5 层报错而非静默 null
- [ ] 手工：模型只标 `Speech` 不填 `Modalities` → 抛"声明不足"而非 400
- [ ] 手工：配置页保存一次 provider → `capabilities` / `listModels` 未丢失
- [ ] 手工：插件注册自定义 `ILlmHookPayloadAdapter` → 覆盖内置适配器生效
- [ ] 手工：设置页按能力配 `DefaultModels`，保存后重开仍存在；留空的能力返回 null 而非报错
- [ ] 手工：`DefaultModels` 写入裸 ID（无 `provider/` 前缀）→ 报错
- [ ] 手工：模型页把 embedding 模型设为 embedding 默认成功、设为 chat 默认被拒
- [ ] 手工：删掉某模型后 `ValidateDefaultModels` 能报出对应能力的失效项，且配置未被自动清除

---

## 12. 留给实现者决定的事项

以下六条不影响架构形状，实现者可自行决定；其余事项均已由 §0 决议锁定。

- **`RetryLlmClientDecorator` 泛型化方式**：每能力一个内部类（推荐，本仓库无源生成器基建；乏味但可读、可断点）vs `DispatchProxy`。
- **`AudioChunk` 分块大小**：按 HTTP 响应体自然分块（推荐），不固定 8KB。
- **`RouterEmbeddingService` 模型缓存**：每次查目录（目录是内存快照，成本可忽略），不做二级缓存。
- **适配器注册表冲突粒度**：按 `Capability` 精确匹配、后注册胜（已定）；"同一插件重复注册同一能力"是覆盖还是 Warn 后覆盖，建议 Warn（与 Provider 注册表一致）。
- **模型页「可路由能力」的展示粒度**：逐模型一行，还是按能力分组聚合。纯 UI 决策。
- **`TitleGeneration.Model` 是否本轮修复**：它是已知死配置，接上成本极低（§7.14.4），但可留作后续。**若留下，必须在此处标注以免被误认为已生效。**
- **`AllowInsecureHttp` / mTLS 等企业代理细节**：不在本次范围。
