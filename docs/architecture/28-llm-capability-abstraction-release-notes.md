# LLM 能力抽象与统一注册 Release Notes

- 日期：2026-11-05
- 规格：`docs/superpowers/specs/2026-11-04-llm-capability-client-abstraction-design.md`（v5）
- 计划：`docs/superpowers/plans/2026-11-04-llm-capability-abstraction-plan.md`
- 评审：`docs/superpowers/specs/2026-11-04-llm-capability-review.md`

## 一句话

`ILlmClient` 从「只会 chat 的单一客户端」拆成**按能力分端口**的多模态客户端束，并把 `SystemOne` 平行栈与 `Memory` 的 embedding 旁路合流进统一的 Provider 注册表、模型目录与横切机制。

## 为什么

此前 `ModelType` 枚举有 `Embedding`/`Rerank`/`Image`/`Video`/`Speech` 六个值，但**只有 `Text` 背后有客户端**——枚举是空壳。两条真实存在的非 chat 路径各自绕过基础设施：

- **embedding**：`Memory/ProviderEmbeddingService` 裸 `POST /embeddings`，端点靠 `IProviderEndpointLookup` 从配置捞。重试装饰器、代理、拦截器、Hook、用量统计对它全部无效；`EmbeddingConnectionTester` 把同样的 HTTP 抄了第二遍。
- **decision**：`Seeing.Agent.SystemOne` 是一整套平行实现——自有 `systemone.json`、`SYSTEMONE_*` 环境变量、`ISystemOneProviderRegistry`（337 行）、`ISystemOneClientFactory`、重试参数、门面。它与 LLM provider 注册表结构一一对应却完全不通：`jev` 的模型不在目录里，选不了、测不了、算不到钱。

## 核心改动

### 1. 协议 ≠ 能力（两维建模）

`ProviderConfig.Type` 过去同时表「协议」与「供什么客户端」。现在：

- **协议**（`type`）：`openai` / `openai-responses` / `anthropic` / `typesafe`
- **能力**（`capabilities`）：`chat` / `embedding` / `rerank` / `decision` / `speechSynthesis` / `speechTranscription` / `imageGeneration` / `videoGeneration`

同一个 endpoint、同一把 key 可以在一个 Provider 条目里同时供 chat 与 embedding。

**缺省只给 `Chat`**（`typesafe` 给 `Decision`）。要别的必须显式写 `capabilities`——默认宽松会让 Provider 层校验对 embedding 恒真，"缺能力时给明确异常"退化成运行期 400。

### 2. 能力端口束

`ILlmClient` 删除，拆为共享基座 + 8 个窄接口：

```
ILlmCapabilityClient（仅身份 + TestConnectionAsync）
├─ IChatCompletionClient      （CompleteAsync / CompleteStreamAsync）
├─ IEmbeddingClient           （EmbedAsync）
├─ IRerankClient              （RerankAsync）
├─ IDecisionClient            （EvaluateAsync）
├─ ISpeechSynthesisClient     （SynthesizeAsync / SynthesizeStreamAsync）
├─ ISpeechTranscriptionClient （TranscribeAsync / TranscribeStreamAsync）
├─ IImageGenerationClient     （GenerateAsync）
└─ IVideoGenerationClient     （Submit / GetStatus / Cancel / Poll）
```

**`ILlmCapabilityClient` 没有 `Capabilities` 属性**：`ProviderConfig.Capabilities`（用户配置）与 `ILlmProvider.Capabilities`（运行时投影）已是两处真相，客户端再自述一份必然漂移，而 Router 从不需要读它。

### 3. `ILlmRouter`：五层校验

`Resolve<TClient>(modelId)` 依次校验，任一层失败抛 `LlmCapabilityMismatchException`：

1. 模型存在
2. 模型类型声明含所需能力
3. `Speech` 消歧（`Modalities` 必须含 `audio`；缺失即报"声明不足"，不猜）
4. Provider 的 `capabilities` 含所需能力
5. 工厂实际能产出该客户端（配置与实现对不上 → 报错，不回退）

### 4. `ILlmInvoker`：横切层（此前缺失的骨架）

Router 只解析；**Hook 触发、出站头合并、耗时计算全在 Invoker**。固定流程：

```
Resolve → LlmCallContext → llm.params + llm.headers → call/stream
        → llm.message → llm.after_complete（载荷经适配器）
        → 异常时 llm.on_error
```

**Router vs Invoker 的适用边界**（见规格 §7.4.1）：判别口诀是「这次调用该不该被观测成一次 LLM 调用」。

| 场景 | 用哪个 |
|---|---|
| 常规单次业务调用 | `ILlmInvoker`（默认开 Hook） |
| 连接测试探针 | `ILlmInvoker` + `EmitHooks = false`（复用五层校验与重试，但不产生 Hook 副作用） |
| 一次解析多次调用（video 轮询） | `ILlmRouter` |
| 框架内部装配 | `ILlmRouter` |

> `ILlmRouter` 之所以是 public，是因为 `IModelConfigManager.TryResolveDefault`（Abstractions 成员）要经它解析。该成员目前无生产消费者，且为它引入了 `ModelConfigManager` 的服务定位器环——已登记为接受的残留。

### 5. Hook 泛化 `chat.*` → `llm.*`

| 旧 | 新 |
|---|---|
| `chat.params` | `llm.params` |
| `chat.headers` | `llm.headers` |
| `chat.message` | `llm.message` |
| `chat.after_complete` | `llm.after_complete` |
| `chat.on_error` | `llm.on_error` |

载荷统一带 `capability` 字段（点分格式，如 `speech.synthesis`）。`llm.after_complete` 的 result 是**超集**，各能力按需取键。

**载荷组装可注册**：`ILlmHookPayloadAdapter`（Abstractions）+ 注册表。内置五个适配器覆盖 chat/embedding/decision/speech×2；插件注册同能力适配器**后注册胜**；未实现能力（rerank/image/video）走**通用兜底且绝不抛异常**。

### 6. 按能力配置的全局默认模型

`SeeingAgent.DefaultModel`（单值）→ `SeeingAgent.DefaultModels`（字典）：

```json
{
  "DefaultModels": {
    "chat": "openai/gpt-4o",
    "embedding": "openai/text-embedding-3-small",
    "decision": "typesafe/jev-latest"
  }
}
```

`IModelConfigManager` 提供六个成员：

```csharp
string? GetDefaultModel(LlmCapabilityKind capability);              // 严格查表，未配返回 null
IReadOnlyDictionary<LlmCapabilityKind, string> GetAllDefaultModels();
bool TryResolveDefault<TClient>(LlmCapabilityKind, out TClient?, out string?);
Task SetDefaultModelAsync(LlmCapabilityKind, string?, ConfigLevel, CancellationToken);
bool CanSetAsDefaultModel(string modelId, LlmCapabilityKind capability);
IReadOnlyDictionary<LlmCapabilityKind, string> ValidateDefaultModels();  // 只报告，不清除
```

**写时校验**：必须带 `provider/` 前缀、模型存在、能力匹配。任一不满足抛 `ArgumentException`。

**严格查表，零回落**：未配置不回落其他能力、不查目录猜。这条把"升级即自动开跑向量索引"从"靠默认值防"变成"结构上不可能发生"。

### 7. SystemOne 合流

- 包重命名：`Seeing.Agent.SystemOne` → `Seeing.Agent.Llm.SystemOne`
- 模块 id：`systemone` → `llm.systemone`；`systemone.tools` → `llm.systemone.tools`
- **删除**：`systemone.json`、`SYSTEMONE_*` 环境变量、自有 provider 注册表（337 行）、自有 ReloadHandler、`ConfiguredSystemOneProvider`、`SystemOneHttpClientFactory`、`ISystemOneService` 门面、`Abstractions/SystemOne/` 整个命名空间
- `TypeSafeSystemOneClient` 实现 `IDecisionClient` + `IModelCatalogClient`
- 配置改走 `Providers.typesafe`，动态模型靠 `listModels: true`

## 破坏性变更

### 公开 API

| 项 | 变更 |
|---|---|
| `ILlmClient` | 删除，拆为 `IChatCompletionClient` + `ILlmCapabilityClient` |
| `ILlmProvider.GetClient()` | → `GetClient(LlmCapabilityKind)`；新增 `Protocol` / `Capabilities` |
| `ILlmProvider.TestConnectionAsync` | 新增 `LlmCapabilityKind capability` 参数 |
| `ILlmClientFactory` | `Create(config)` → `Create(config, capability)`；`SupportedTypes` → `SupportedProtocols` → **再删除**（见下） |
| `ILlmClientDecorator.Wrap` | `ILlmClient` → `ILlmCapabilityClient` |
| `HookRegistry.Chat*` | → `Llm*` |
| `HookDataContract.ChatAfterComplete` | → `LlmAfterComplete` |
| `ModelType` | **删除 `Image`**；新增 `Decision` / `ImageGeneration` |
| `SeeingAgentOptions.DefaultModel` | **删除**，改 `DefaultModels` |
| `ProviderConfig.DefaultModel` | **删除** |
| `IModelConfigManager.GetDefaultModel()` | → `GetDefaultModel(LlmCapabilityKind)` |
| `IModelConfigManager.SetDefaultModelAsync` / `CanSetAsDefaultModel` | 均加 `LlmCapabilityKind` |
| `IProviderEndpointLookup` / `IProviderEndpointInfo` / `ProviderEndpoint` | 整组删除 |
| `ISystemOneService` / `ISystemOneProvider` / `ISystemOneClient` / `ISystemOneProviderRegistry` / `SystemOneException` | 删除（后者→ `DecisionProtocolException`） |
| `ModelTypeInference` | `dall-e`/`flux`/`sdxl`/`stable-diffusion`/`-image`/`image-` 全归 `ImageGeneration` |

### 配置文件

| 文件/字段 | 变更 |
|---|---|
| `DefaultModel`（单值） | → `DefaultModels`（字典，key 为能力配置键） |
| `Providers[*].default_model` | 删除 |
| `Providers[*].capabilities` | 新增；**不写则只有 `Chat`** |
| `Providers[*].listModels` | 新增（动态模型目录开关） |
| `systemone.json` / `SYSTEMONE_*` | 删除，并入 `Providers.typesafe` |
| `memory.json` `Embedding.Provider/Model/Dimensions` | 删除 → `Enabled`（默认 false）+ `ModelId`（**必须显式配置**） |
| 模型 `types` 中的 `"Image"` | 必须改为 `"ImageGeneration"` 或删除（图像理解改由 `Modalities.Input` 表达） |

### `memory.json` 迁移要点

`MemoryEmbeddingOptions` 现在只有：

```json
{ "Embedding": { "Enabled": true, "ModelId": "openai/text-embedding-3-small" } }
```

- `Enabled` 默认 `false`
- 未配 `ModelId` 即视为未启用，**框架不会自动挑选模型**
- 维度不再可配（首次成功调用后由响应回写，仅作展示）
- 缓存键已加模型指纹：换模型不会命旧向量

## 后续修订：协议→工厂注册表 + Provider 自述能力

初次落地后暴露两个结构性问题，已追加修复（详见提交 `refactor(llm): protocol→factory registry + provider-declared capabilities`）。

### 1. 协议从「工厂自述」改成「注册表服务键」

`ILlmClientFactory` 先前有 `SupportedProtocols` / `SupportsProtocol`，与「协议」是同一事实的两份表达，且 `SupportedProtocols` 零生产消费者（WebUI 也硬编码 `openai`/`anthropic`，导致 `typesafe` 与 `openai-responses` 在界面上**加不出来**）。

现在接口只剩 `Create`：

| 概念 | 归属 |
|---|---|
| 协议 → 工厂 | `LlmClientFactoryRegistration(protocol, factory)` |
| 解析与枚举 | `LlmClientFactoryResolver`（`Find` / `Require` / `Protocols`） |

语义变化：

- 同一协议重复登记 → **构造期抛**（此前 first-wins 静默覆盖，后者永不生效且无从发现）
- 同一工厂登记多个协议 → 允许（OpenAI 工厂服务 `openai` 与 `openai-responses`）
- `Protocols` 可枚举 → WebUI 协议下拉改为数据驱动，`typesafe` 可在界面添加

> **实现陷阱（已修）**：模块必须按**具体类型**登记（`AddSingleton<OpenAiLlmClientFactory>()` + `sp.GetRequiredService<OpenAiLlmClientFactory>()`）。用 `sp.GetRequiredService<ILlmClientFactory>()` 会返回最后登记的实现，导致**所有协议绑到同一个工厂**——症状是四个插件的 chat 与 embedding 全部 `null`。`FullCapabilityCompositionTests.Protocols_ShouldResolveToTheirOwnFactory_InRealContainer` 守门。

### 2. 插件能力由作者声明 + 配置可覆盖

先前 4 个插件都硬编码 `Capabilities => Chat` 且有 `if (capability != Chat) return null`。结果 SiliconFlow 即使端点支持 embeddings，`siliconflow/BAAI/bge-large-zh-v1.5` 也无法用于 embedding。

结构上的原因：插件只有单个 `_client` 字段，多能力必须自己重写实例管理与释放。新增：

| 类型 | 层 | 职责 |
|---|---|---|
| `CapabilityClientBundle` | `Llm.Support` | 能力位集 → N 个 `Lazy` + 统一释放，缓存粒度 `(providerId, capability)` |
| `ConfigurableProviderBase<TOptions>` | `Llm.Support` | 能力解析、束生命周期、配置读写、保存后重建 |

**能力来源规则**：默认值由插件作者声明（只有作者知道自家端点支持什么）；配置里写 `capabilities` 可覆盖。**不能由工厂推导**——OpenAI 工厂实现了 embedding，但 DeepSeek 端点没有 `/v1/embeddings`，推导会把「协议支持」误当成「端点支持」。

覆盖语义：不写 = 用作者默认值（装完即用）；显式写了就严格按配置走（含「全是不认识的名字 → 不供出任何能力」），**绝不回落默认值**。

各插件默认能力：

| 插件 | 默认 | 理由 |
|---|---|---|
| SiliconFlow | `chat \| embedding` | 官方提供 OpenAI 兼容 embeddings 端点 |
| DeepSeek / MiMo | `chat` | 官方端点无 embeddings；可用配置开启 |
| OpenCodeZen | `chat` | 有私有配置形状，保留自有实现 |

### 3. SystemOne 成为内置 Provider

此前 `llm.systemone` 只注册工厂，Provider 实例完全依赖用户手写 `Providers.typesafe`——等于该能力包装上后不可用。

现在新增 `SystemOneLlmProvider` + `SystemOneConfigStore`（`~/.seeing/providers/systemone.json`）：默认 `decision` 能力、`listModels`、10s 超时、不走代理；模型目录 = 静态 ∪ 远端，并**内置 `jev-latest` 种子模型**（远端目录需要凭据，无种子则目录为空、Router 第 1 层必失败）。

三条约束：

- **Provider id 固定**为 `systemone`，不随配置变化（注册表按 id 索引；可变 id 会让旧键永久残留、`Deactivate` 失效、已配的 `systemone/xxx` 引用全部失效）
- **用户配置优先**：`Activate` 时若注册表已有同 id 的 Provider，内置 Provider 主动让位
- **Activate/Deactivate 挂载与注销**，不再依赖用户配置

---

## 已知边界

1. **rerank / image / video 只出契约不实现**。`Create` 对这三种能力返回 `null`，`Resolve` 会因第 5 层校验给明确异常。第三方 provider 包实现 `ILlmClientFactory` 并在 `Create` 返回对应客户端即可接入，无需改框架。
2. **`Speech` 模型必须填 `Modalities`**。只标 `Speech` 而不填 `{"input":["audio"],"output":["audio"]}` → `Resolve` 抛"声明不足"。这是刻意的（不猜方向），模型管理页已给提示。
3. **非 chat 能力不计入会话预算**。`BudgetUpdateHook` 只处理 `capability == "chat"`；embedding/decision/TTS 的 `usage` 只进 `llm.after_complete` 载荷。跨会话用量账本（`LlmUsageLedger`）未实现。
4. **配置页保存一次会抹掉 capabilities**（若漏改克隆清单）。已用 `ProviderConfigRoundTripTests` 守门，四处克隆点全绿。
5. **同名模型多 provider 时** `DefaultModels` 必须写 `provider/model`，裸 ID 会被拒。
6. **旧插件二进制订阅 `chat.*` 会静默失效**——`HookManager` 对五个旧点注册时打 Warn 且不登记。
7. **本次不新增 `speak` / `listen` 工具**；TTS/ASR 只出契约与客户端实现。

## 验证

- `dotnet build Seeing.Agent.slnx`：0 错误
- 48 个测试项目全绿
- 全量测试耗时从 131.7s 降至约 80s（消除等待型测试，见下）

## 顺带修复

**等待型测试清理**（用户要求「禁止存在这种等待型的测试」）：

| 项目 | 手段 | 耗时 |
|---|---|---|
| WeCom | `WeComStreamState` 注入 `TimeProvider`，测试用 `FakeTimeProvider.Advance` 替代 6s 等待 | 31.2s → 2.7s |
| Files | 事件收集器「命中预期即返回」；并发测试用信号量替代 `Thread.Sleep` | 10.4s → 2.1s |
| Skills | 去抖窗口 400ms → 50ms（保留合并语义） | 9.5s → 3.2s |
| Scheduler | 盲等 2–3s 改为轮询至条件达成；删除两个「等副作用但从不断言」的空转段 | 8.6s → 1.6s |
| WebUI | `RenderCoalescer` 注入 `TimeProvider` | 2.3s → 1.9s |

**`TitleGeneration.Model` 不是死配置**：早期规格称其"全仓零消费者"是事实错误——`SessionTitleService` 一直在用。它也**不能**接进 `ITextCompletion` 的共享旁路，否则给标题配便宜模型会静默改掉摘要与记忆抽取的模型。
