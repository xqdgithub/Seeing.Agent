# LLM 能力客户端抽象设计 —— 实现前评审

- **被评审文档：** `docs/superpowers/specs/2026-11-04-llm-capability-client-abstraction-design.md`
- **评审日期：** 2026-11-04
- **评审立场：** 接受既定方向（能力端口 + Router + `chat.*`→`llm.*` + SystemOne 合流），只做可行性、内部一致性、遗漏与回归风险审查
- **审查基线：** 当前工作树 `master`；引用行号均为审查时实际行号

> 结论速览：方向成立，但**有 3 个阻断项（B1–B3）**、**6 个必须回写的回归（R1–R6）**、**13 处文档内部矛盾或影响面遗漏（G1–G13）**。文档对「新增契约」描得很细，对「旧行为如何不丢」几乎没写——这是本设计最大的系统性风险。

---

## 1. 架构合理性

### 1.1 整体判断

能力端口 + Router 的方向是对的，而且**确实解决了问题，不是搬家**：

- 现状 embedding 旁路（`Memory/Core/Embedding/ProviderEmbeddingService.cs:36-78`）与 `EmbeddingConnectionTester.cs:45-71` 是两段相同 HTTP，绕开了重试/代理/拦截器/Hook；SystemOne 是第二套完整 provider 栈。两者都不是「换个地方调用」能修的，必须把「协议 → 能力 → 模型 → 横切」这条链显式化。**这一点成立，不再展开。**
- 但设计只把「链」建到**传输层**。横切里真正有价值的 Hook 与用量，仍然没有落点（见 §1.3 / G4）。所以它解决的是「客户端形状」，没有解决「横切对所有能力生效」——而后者才是文档 §2 目标 4 的原文。

### 1.2 文档内部矛盾（同一决定有两个真相源）

| # | 矛盾 | 证据 | 后果 |
|---|---|---|---|
| **C1** | 装饰器在哪一层应用，文档给两个答案 | §6.1 现状图把 `ILlmClientDecoratorRegistry.Apply` 画在客户端创建之后；§7.5 又规定顺序是「能力客户端 → `Apply` → `ConfiguredLlmProvider` 缓存」。而**现状实际发生在工厂内部**：`Llm.OpenAI/OpenAiLlmClientFactory.cs:49`（`_decorators?.Apply(client, config)`）、`Llm.Anthropic/AnthropicLlmClientFactory.cs:49` 同款 | 按 §7.5 字面实现会**双重包裹**（重试套重试，退避预算翻倍）；按现状保留则与 §7.5 文字不符。必须二选一写死 |
| **C2** | SystemOne 合流后到底是"配置驱动 Provider"还是"专用 Provider" | §7.8 配置示例（`Providers.typesafe`）走的是 `ConfiguredLlmProvider`（`Core/Llm/ProviderManager.cs:342`）；同一节又要求「新增一个薄类 `SystemOneLlmProvider : LlmProviderBase`」 | 同一个 provider 两个实现；且后者是分层阻断项（B1）|
| **C3** | 「六契约 + 四实现」与实际接口数不符 | §7.2 实际列了 **8** 个能力端口（chat/embedding/rerank/decision/speech-synthesis/speech-transcription/image/video），§7.3 的 OpenAI 工厂给 **4** 个实现，最终需写 **6** 个客户端类（见 §2.2） | 工作量与排期口径错误 |
| **C4** | §7.4 宣布「校验分两层，顺序固定」，紧接着引入第三层 | `...:436-441` 两层；`...:458` 的 `Speech` 判定要求 `Modalities`，且「缺失则抛」——这是模型声明与 Provider 声明之外的第三个条件 | 校验逻辑无法按「两层」实现；`TryResolve` 的 reason 文案也就没有唯一口径 |
| **C5** | 删除 vs 修改同一文件的边界 | §9.1「修改：`Llm/ILlmClient.cs`」没有出现，写的是「删除」；但同时要求「`ILlmClientFactory` 新定义」——工厂契约到底落在 `ILlmClient.cs` 还是新文件未定。同类问题：`OptionsProviderEndpointLookup.cs` 被列为「修改」，但其两个消费者（ProviderEmbeddingService / EmbeddingConnectionTester）都被删除，改它等于留死代码 | 违反 §4「一次性删干净」的自定目标 |

### 1.3 `ILlmRouter.Resolve<T>()` 的形状问题

三个真实破坏点：

1. **需要两张映射表，文档只定义了一张。** `LlmCapabilityMap` 是 `ModelType → LlmCapabilityKind`（`...:259`）。但从 `Resolve<IEmbeddingClient>()` 的这个 `T` 反推 `LlmCapabilityKind` 需要第二张 `Type → Kind` 表。§10 阶段 1 只在测试里提到「每个 `LlmCapabilityKind` 有对应接口类型」，正文从未定义它是谁、放在哪、由谁维护。**两张手写映射表必然漂移**，而这正是文档反复承诺的「单点」。
2. **`Image → Chat` 这条映射把「图像生成模型」变成了合法 chat 客户端。** `...:454` 写 `Image`（理解/输入）→ `Chat`，`ImageGeneration` → `ImageGeneration`。但 `ModelType.Image` 在**存量语义里就是生成**：`Llm.Support/Inference/ModelTypeInference.cs:31-32`（`dall-e`/`flux`/`sdxl`/`stable-diffusion` → `ModelType.Image`），仓库自带的 `~/.seeing/providers.json` 里 `qwen3.8-flash` 就是 `["Text","Image"]`。于是 `Resolve<IChatCompletionClient>("openai/dall-e-3")` 会**通过两层校验并发出 chat 请求**，拿到 400——这正是 §7.4「不做第三层运行期试错，让 400 冒出来」的意图，但在这里 400 是**设计制造的**，不是配置错误。`Image → Chat` 只有在新旧语义严格切分后才成立，而 §7.11 明确「不写迁移逻辑」。
3. **多值 `Types` 与「一客户端一能力」在 Speech 上必然打架**（见下）。另外 `Resolve<T>` 是纯泛型 API，**无法表达"同模型多能力同 Provider 但不同客户端实例"的缓存语义**——§10 阶段 2 要求「两层都过 → 返回同一实例（缓存）」，但缓存实例的键应该是 `(providerId, kind)` 而不是 `(modelId, kind)`（同 provider 下 100 个模型不应有 100 个客户端）。文档未定义缓存键。

### 1.4 「一客户端一能力」假设被违反的真实能力

文档自己已经撞上了，只是没承认：

- **`ILlmCapabilityClient.Capability` 是单值，但 `LlmCapabilityKind` 是 `[Flags]`**（`...:138-150` vs `...:192`）。标志位枚举 + "单一值，便于校验" 的注释是自相矛盾的契约。双向语音模型（`{input:[audio], output:[audio]}`，`...:597`）在 OpenAI 上是 `/v1/audio/speech` 与 `/v1/audio/transcriptions` 两个端点、两个类，尚可；但 Gemini Live / gpt-realtime 这类**单端点单客户端同时产出两种能力**的协议根本无法表达——`Create(config, kind)` 只能返回一个 kind 的客户端。
- **一个 chat 模型同时能 embed 是真实存在的**（如 Qwen3 系列统一模型）。`ModelConfig.Types = [Text, Embedding]` 表达得了，`ILlmProvider.GetClient(kind)` 也表达得了；真正断掉的是 `Capability` 单值属性——它会强迫同一个协议实现写两个类，而两者 HTTP 路径不同其实无所谓。**建议把 `Capability` 改为 `LlmCapabilityKind` 的位集合并允许组合**，或干脆删掉该属性（Router 已经用 `ModelType` + `ProviderCapabilities` 判了，客户端自述是第三份真相）。

---

## 2. 实现可行性

### 2.1 顺序风险：§11 的九步不可按步提交

§11 第 2 步自述「此步不碰实现，编译仍失败（`ILlmClient` 删除）」。也就是说**第 2、3、4 步必须落在同一个 commit**，否则违反 `04-development-standards.md:82` 的 `dotnet build Seeing.Agent.slnx` 必过门槛。具体绑定关系：

- 删 `ILlmClient` → 立刻打断 **8 个 src 项目**：`Core`（6 文件）、`Llm.OpenAI`（3 客户端 + 工厂）、`Llm.Anthropic`（2）、4 个 plug provider。
- 第 3 步（`ConfiguredLlmProvider` 改用 `GetClient(kind)`）必须与第 4 步（OpenAI/Anthropic 工厂改签名）**同一 commit**——否则 `ProviderManager` 无法编译。
- 第 6 步（SystemOne）依赖第 3 步的 `ILlmRouter`，且**必须同时改** `Seeing.Agent.slnx:52,107`（项目路径）、`Core/CapabilitySets/BuiltInCapabilitySets.cs:110-111`（模块 id）、`tests/spine/Seeing.Agent.Invariants.Tests/Invariants/ModuleArchitectureInvariantTests.cs:33`（`Full` 守门白名单）、`Tools.SystemOne/SystemOneToolsModule.cs` 的 `Id`/`DependsOn`（`"systemone.tools"` / `DependsOn: ["systemone"]`）。文档只提了 `systemone` → `llm.systemone`，漏了 `systemone.tools` 与守门测试。
- 第 7 步（Memory）依赖第 4 步的 `OpenAiEmbeddingClient`；第 5 步（Hook 改名）与 2–4 无编译耦合，可以独立前置。

**可执行的最小切分（建议写回 §11）：** ①表征测试 →②③④合并为一个"编译恢复"楔子 →⑤Hook 改名（独立、可先合）→⑥SystemOne →⑦Memory →⑧契约补齐。§11 现在的编号会被执行者误读为可逐步提交。

### 2.2 需要写的具体类有多少

**能力客户端（6 个）：**
1. `Llm.OpenAI/Clients/OpenAiEmbeddingClient.cs`（新）
2. `.../OpenAiSpeechSynthesisClient.cs`（新，含流式）
3. `.../OpenAiSpeechTranscriptionClient.cs`（新，multipart + SSE）
4. `Llm.OpenAI/Clients/OpenAiChatClient.cs:13`（改造，453 行）
5. `Llm.OpenAI/Clients/OpenAiResponsesClient.cs:14`（改造，375 行 —— 见 G9：它当前**没有被任何工厂引用**，只有测试构造）
6. `Llm.Anthropic/Clients/AnthropicClient.cs:14`（改造）
7. `Llm.SystemOne/Clients/TypeSafeSystemOneClient.cs:7`（改造为 `IDecisionClient`）

**泛型重试装饰器（文档 §12 已定"每接口一个内部类"）：** 8 个能力接口，其中 4 个有流式/轮询方法（chat 2 方法、speech×2 各 2 方法、video 4 方法）→ **8 个外层类 + 3 个 `StreamOnceAsync` 变体 ≈ 11 个嵌套类、600–900 行**，且没有任何一个能用现有 `RetryLlmClientDecorator.cs:114-195` 的流式边界逻辑直接复用（`StreamUpdate` 换成 `AudioChunk`/`TranscriptSegment`）。**这是文档低估最严重的一块**，§12 只说"乏味但可读"。

**其它必须新写/大改：** `LlmRouter.cs`（Core）、`ProviderCapabilityDefaults`、`LlmCapabilityMap`、2 张映射表、8 个 DTO 文件，加上 `ConfiguredLlmProvider` 从「1 个 `Lazy<ILlmClient>`」改为「N 个 `Lazy<ILlmCapabilityClient>`」并重写 Dispose（`ConfiguredLlmProvider.cs:148-172` 目前只释放单个）。

### 2.3 分层违规（BLOCKER）

**B1. §7.8 的 `SystemOneLlmProvider : LlmProviderBase` 要求能力包引用 Core。**

`LlmProviderBase` 在 `src/spine/Seeing.Agent.Core/Llm/LlmProviderBase.cs:7`；`Seeing.Agent.Llm.SystemOne` 现在只有 `Abstractions` 一个 ProjectReference（`Seeing.Agent.SystemOne.csproj`）。加 Core 引用直接违反：

- `AGENTS.md`「能力包引用 Core 具体类型」反模式；
- `04-development-standards.md:48` 强制检查清单「新能力包 **无** `ProjectReference` → `Seeing.Agent.Core`」；
- `06-compliance-audit.md:17,134` 已签字的全绿项（`rg Core.csproj` 仅命中 `src/hosting/*`）。

而且它是**多余的**：§7.8 自己的配置示例把 typesafe 放进 `Providers`，走 `ConfiguredLlmProvider`（`ProviderManager.cs:342-348`）即可，不需要专用 Provider。**唯一代价是丢掉动态 `ListModelsAsync`**——那就在 `ConfiguredLlmProvider.GetModelsAsync`（`ConfiguredLlmProvider.cs:120-135`）加一个可选"目录客户端"钩子，或让 `ProviderConfig` 支持 `listModels: true`。这属于"设计是错的"，必须改。

**B2. 装饰器位置的二选一（C1）会让 plug provider 静默失去重试。**
现状 plug provider **直接调工厂**，从不走 `ConfiguredLlmProvider`：`plugs/providers/Seeing.Provider.DeepSeek/DeepSeekProvider.cs:176-195`（`factory.Create(new ProviderConfig{...})`），MiMo / OpenCodeZen / SiliconFlow 同构。若按 §7.5 把 `Apply` 上移到 `ConfiguredLlmProvider`，这 4 个 provider 的客户端就**不再被装饰**（无重试、无 429 退避），属于当前工作行为的回归。→ 必须明确保留「工厂内 Apply」为唯一应用点。

**B3. Hook 泛化的落点缺失（架构性）。**
§2 目标 4 与 §10 阶段 5 要求 `llm.params`/`llm.headers`/`llm.after_complete`/`llm.on_error` 对**四种能力**都触发。但文档同时把 Hook 触发留在 `LlmService`（§7.12「chat 专用门面」），而 embedding/decision/speech 的调用方"直接用 `ILlmRouter`，不经过 `LlmService`"（`...:632-639`）。**没有任何组件负责在非 chat 调用前后触发 Hook。** 也不会是装饰器：`ILlmClientDecorator` 在 Abstractions（不能依赖 `IHookManager`），放在 Core 装饰器里又会让「重试 vs Hook 时机」变成隐式耦合。
→ 这不是"漏写一段"，是骨架缺一层：需要一个 `ILlmInvocationPipeline`（或让 Router 承担「resolve + hook 前后置」），且它必须能被 Core 实现、被能力包以抽象调用。文档必须先回答"谁触发"，再谈载荷键。

### 2.4 被删除类型的隐藏消费者（文档漏列）

文档只点了 `ILlmClient`、`ILlmProvider.GetClient`、`HookRegistry.Chat*`、`ISystemOneService`、`ISystemOneProviderRegistry`。实际还有：

| 消费者 | 位置 | 文档是否提及 |
|---|---|---|
| `IProviderManager.GetClient(string)` / `GetClientForModel(string)` 返回 `ILlmClient` | `Core/Llm/IProviderManager.cs:26,34` | ❌（§9.1 未列该文件）|
| `ILlmService.GetClient` / `GetClientForModel` | `Core/Llm/LlmService.cs:24,28,102-109` | ❌ |
| `LlmProviderBase.GetClient()` 抽象 + `TestConnectionAsync` + `CreateBuiltInClient` | `Core/Llm/LlmProviderBase.cs:27,41,46-53` | 仅笼统写「修改」 |
| `LlmService.PrepareClientRequest` 内部 `_providerManager.GetClient(modelConfig.Provider)` | `Core/Llm/LlmService.cs:161` | 仅笼统 |
| 4 个 plug provider 的 `_client` 字段/`InvalidateClient`/`DisposeClientAsync` | `DeepSeekProvider.cs:22,58-67,197-225`（MiMo/OpenCodeZen/SiliconFlow 同构） | ❌（只说改 `GetClient()` 签名）|
| `SystemOneHttpClientFactory`（Timeouts / `UseProxy=false`） | `SystemOne/Clients/SystemOneHttpClientFactory.cs:11-39` | ❌ 既不在删除清单也不在保留清单 |
| `SystemOneToolParser.cs` 使用 `SystemOneQuestionTypes`（4 处） | `Tools.SystemOne/SystemOneToolParser.cs:191-241` | ❌（只说 `SystemOneTool.cs` 改 Router）|
| `SystemOneTool.cs:90` catch `SystemOneException`（命名空间随目录迁移） | `Tools.SystemOne/SystemOneTool.cs:3,90` | ❌ |
| `MemoryOptionsProvider.Reload` 读 `IsEmbeddingConfigured` 写日志 | `Memory/Configuration/MemoryOptionsProvider.cs:48-50` | ❌ |
| `HybridMemoryIndex` 用 `IEmbeddingStatus.IsAvailable` 决定是否建向量索引 | `Memory/Core/Index/HybridMemoryIndex.cs:34,43,51,59` | ❌（决定 R1 的爆炸半径）|
| `SqliteEmbeddingCache` 缓存键只有 `text_hash`，无模型维度 | `Memory/Core/Embedding/SqliteEmbeddingCache.cs:49-51,123` | ❌（见 R1）|
| `VectorIndex.CosineSimilarity` 维数不等直接抛 | `Memory/Core/Index/VectorIndex.cs:294-297` | ❌ |
| `PluginSpec`/已提交插件 dll 以字符串订阅 hook | `Core/Services/HookManagerAdapter.cs:20-27`；`06-compliance-audit.md:94` | ❌（见 R6）|

### 2.5 测试爆炸半径

**文档写「约 12 个测试文件」，实际 17 个文件直接出现 `ILlmClient`：**

```
tests/spine/Seeing.Agent.Tests/Llm/            : ConfiguredLlmProviderTests, LlmTransportOnlyTests,
                                                 ModelCatalogAggregationTests, ProviderConfigMigrationTests,
                                                 ProviderConfigStringTests, ProviderManagerCharacterizationTests,
                                                 ProviderManagerConcurrencyTests, ProviderManagerTests,
                                                 RetryLlmClientDecoratorTests
tests/spine/Seeing.Agent.Tests/Invariants/     : MigrationTests, LlmAndExecutorTests
tests/plugs/Seeing.Provider.{DeepSeek,MiMo,OpenCodeZen}.Tests : ProviderTests + LlmModuleTests（6 个）
```

另外文档完全没提的测试：

- `tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests/`（**8 个文件**）——`SystemOneToolExecuteTests.cs:13` 用 `ISystemOneService` 造假件，`SystemOneToolParserTests`/`SystemOneJsonTests` 依赖 `SystemOneQuestionTypes` 命名空间。文档测试影响面只写了 `SystemOne.Tests`。
- `tests/capabilities/Seeing.Agent.Memory.Tests/Embedding/EmbeddingConnectionTesterTests.cs`（签名从 `provider,model` 改 `modelId`）。
- `tests/capabilities/Seeing.Agent.Memory.Tests/Configuration/MemoryOptionsTests.cs:10-19`（`IsEmbeddingConfigured` 语义整体改变）。
- `tests/capabilities/Seeing.Agent.Memory.Tests/Integration/{MemoryModuleHookLifecycleTests,ChatMemoryHandlerEnqueueTests}`（`HookRegistry.ChatAfterComplete`）。
- `tests/spine/Seeing.Agent.Tests/Hooks/HookManagerTests.cs:37-47`（`HookRegistry.ChatParams`）。
- `tests/spine/Seeing.Agent.Tests/Llm/LlmTransportOnlyTests.cs:51-53`（断言**字符串** `"chat.params"`）。
- `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests/OpenAiLlmModuleTests.cs:42-57`、Anthropic 同名文件（`SupportsType`/`SupportedTypes` 全部消失）。
- `tests/apps/Seeing.Agent.HostComposition.Tests`（模块 id 清单守门）。
- `tests/plugs` 只有 **3** 个 provider 测试项目（DeepSeek/MiMo/OpenCodeZen），文档写「4 个 plug provider 测试」。

**SystemOne.Tests 可保留度（1667 行 / 20 文件）：约 40% 可救，不是"整套重写"。**

| 文件 | 行 | 判定 |
|---|---|---|
| `SystemOneSerializationTests.cs` | 218 | **可留**（DTO JSON 往返，仅命名空间改名）|
| `TypeSafeSystemOneClientTests.cs` | 107 | **可留**（改断 `IDecisionClient.EvaluateAsync`）|
| `SystemOneHttpHelperTests.cs` | 183 | **部分留**（错误映射/鉴权头留；429/529/退避用例随重试迁出而删）|
| `Clients/SystemOneHttpClientFactoryTests.cs` | 34 | 若复用框架 handler 则**删** |
| `SystemOneProviderManagerTests`(197)、`SystemOneConfigStoreTests`(53)、`SystemOneEnvironmentConfigTests`(44)、`SystemOneReloadHandlerTests`(61)、`SystemOneClientFactoryResolverTests`(85)、`ConfiguredSystemOneProviderTests`(82)、`SystemOneServiceTests`(135)、`SystemOneModuleDiTests`(88)、`PredefinedSystemOneProvidersTests`(27)、`SystemOneTestDoubles`(66)、`TestConfigSectionStore`(31) | ~869 | **失效**（对象被删）|
| `SystemOneModuleTests`(15)、`ScaffoldSmokeTests`(13) | 28 | 改 id 即可 |

即：**13 个文件删除、3 个文件改造、4 个文件保留**。文档「整套重写」会让执行者多写 ~600 行无谓测试。

---

## 3. 遗漏与风险

### 3.1 真正的回归（最高价值）

**R1（严重）：embedding 从"默认关闭"变成"默认自动开启 + 模型不确定"。**

- 现状：`MemoryOptions.IsEmbeddingConfigured` 要求 `Embedding.Provider` 与 `Embedding.Model` 都非空（`Memory/Configuration/MemoryOptions.cs:12-14`）。实际用户 `memory.json` 里根本没有这两个键（本机 `~/.seeing/memory.json` 是旧形状 `{"Memory":{"MemoryStore":...}}`），所以**今天 embedding 恒为关闭**，`NullEmbeddingService` 生效（`MemoryServiceExtensions.cs:83-94`）。
- 设计后：`IsEmbeddingConfigured = Embedding.Enabled(=true) && 目录中存在 Embedding 模型`（§7.7 第 2 条），且解析顺序是「`ModelId` → **目录中第一个 `Embedding` 模型**」（§7.7 第 4 条）。而 `ModelTypeInference.cs:22-23` 会把任何含 `embedding`/`bge-*`/`gte-` 的模型标成 `Embedding`——SiliconFlow / DeepSeek / OpenCodeZen 的目录里这类模型很多。
- 结果：**升级后第一次启动，向量索引自动开跑**（`HybridMemoryIndex.cs:34` 立刻开始建索引、调远端"第一个 embedding 模型"），用户从未同意，产生后台成本。更糟的是"第一个"随目录刷新变化（`ModelConfigManager.ReplaceCacheLocked` 字典顺序），**同一个库先后写入不同模型的向量**。
- 次生爆炸：`VectorIndex.CosineSimilarity`（`VectorIndex.cs:294-297`）维数不等**直接抛 `ArgumentException`**；`SqliteEmbeddingCache` 的键只有 `text_hash`（`SqliteEmbeddingCache.cs:49`），**不含模型**，换模型后回放旧向量。§7.7 第 6 条还明确"缓存层保留不变"。
- 这三点叠加就等于：**升级后记忆检索可能直接抛异常**，且是间歇性的（取决于缓存命中）。

必须写回：`Enabled` 默认 **false**；无 `ModelId` 时不自动回落（或回落但显式告警+记录"已选定"到配置，形成幂等）；缓存键加模型指纹；`CosineSimilarity` 维数不等降级为跳过而非抛。

**R2（严重）：typesafe 三个关键参数在合流中被"默认值"偷换。**

| 参数 | SystemOne 现值 | 合流后（走 `ProviderConfig`） | 差异 |
|---|---|---|---|
| `timeout` | 10000 ms（`SystemOneProviderConfig.cs:32`）| 300000 ms（`ProviderConfig.cs:82`）| 决策调用从 10s 变 5 min |
| `max_retries` | 2（`SystemOneProviderConfig.cs:33`）| **0 = 不限次数**（`ProviderConfig.cs:84-88` 注释明写"<=0 表示不限次数"）| 退避到 120s 预算耗尽 |
| 代理 | `UseProxy = false`（`SystemOneHttpClientFactory.cs:33`）| `UseProxy = true` 默认（`ProviderConfig.cs:74`）| **流量走系统代理**；企业代理下 Jev 直接不可达 |
| 端点 | `/v1/systemone`、`/v1/models`（`SystemOneDefaults.cs:44-45`）| `BaseUrl` 无协议路径概念 | 需确认拼装一致 |

§7.8 的配置示例只给了 `type/capabilities/baseURL/apiKey/models`，**没有一个重试/超时/代理字段**。照着示例写配置就是三个回归。必须把这三个默认值写进示例，或让 `ProviderCapabilityDefaults` 同时提供"协议级默认重试/超时"。

**R3（中）：`ModelType.Image` 语义收窄，没有迁移也不承认有存量数据。**

§7.11「不写迁移逻辑（确认：本项目无历史用户数据）」。事实是：本机 `~/.seeing/providers.json` 里 `qwen3.8-flash.types = ["Text","Image"]`；`ModelTypeInference.cs:31-32` 每天在把 `dall-e`/`flux`/`sdxl` 标成 `Image`；`ModelOverride` 里也可能有 `["Image"]`（`ModelOverride.cs:15-16`）。按 C1/§1.3 的分析，这些模型会被 `Image → Chat` 映射成合法 chat 目标。**至少要在 WebUI 与 `ModelTypeInference` 同步切语义，并对"标了 Image 但无 ImageGeneration"的模型在模型页给出迁移提示。**

**R4（中）：OpenAI 协议默认带 Embedding，把"Provider 声明"这一层校验废掉。**

`ProviderCapabilityDefaults`：`openai → Chat, Embedding`（`...:171`）。于是**所有 openai 兼容 provider**（含仓库自带的聚合网关 `trip`、4 个 plug provider、第三方自定义）都自动声明了 embedding 能力。`Resolve` 的第二层校验因此对 embedding **恒真**；只要目录里有 embedding 模型，就能通过校验并向一个不提供 `/embeddings` 的端点发请求。§7.10 又声称"`Resolve` 会因 Provider 未声明该能力给出明确异常"——这在新默认下不成立。
→ 缺省必须是 **`Chat` only**，embedding 要显式写 `capabilities: ["chat","embedding"]`。文档 §7.1 的"一个条目同时给 chat 和 embedding，不必配两遍 key"的目标并没有因此受损（显式写一行即可）。

**R5（中）：typesafe 的动态模型目录丢失。**
`ConfiguredSystemOneProvider.GetModelsAsync` 走 `ISystemOneClient.ListModelsAsync`（`ConfiguredSystemOneProvider.cs:65-66`），即 `/v1/models` 动态拉取。`ConfiguredLlmProvider.GetModelsAsync` 只读静态 `_config.Models`（`ConfiguredLlmProvider.cs:120-135`）。§7.8 的 JSON 示例只静态声明 `jev-latest`。若不上 `SystemOneLlmProvider`（B1 又禁止），**jev 的模型列表就从"动态"退化为"手写一条"**，`jev` 的别名/新版本永远不出现在目录里——恰好违背 §1「jev 的模型不出现在模型目录里」这个立项动机。

**R6（中）：Hook 改名的静默失效面。**
仓库里提交了预构建插件二进制（`06-compliance-audit.md:94` 明确记录 `samples/Seeing.Agent.WebUI/plugins/*.dll` 为已提交旧产物）。`HookManagerAdapter` 用**任意字符串**注册（`Core/Services/HookManagerAdapter.cs:20-27`），`HookManager.AddHandler` 按 `spec.Point` 字符串分桶（`Core/Hooks/HookManager.cs:32,136`）。旧插件里的 `chat.after_complete` 在改名后**不会编译失败**（二进制已编译），而是**注册到一个永不触发的桶**——完全静默。这与 `docs/architecture/24-llmservice-transport-only-release-notes.md:19` 承诺的「编译期报错优于运行期静默失效」相反。
→ 至少在 `HookManager` 加一张"已知废弃点"表，对 `chat.*` 注册打 Warn 或直接抛。

**R7（中）：`TextCompletionService` 的旁路收到能力校验后可能开始抛异常。**
`TextCompletionService` 用 `_options.CurrentValue.DefaultModel`（`Core/Llm/TextCompletionService.cs:63,97`）经 `CompleteRawAsync` 走 `PrepareClientRequest`（`LlmService.cs:148-167`）。改造后 `PrepareClientRequest` 换 `ILlmRouter.Resolve<IChatCompletionClient>`，于是**若默认模型不是 `Text` 类型**（例如用户把默认模型设成 embedding，或 `Image` 语义切换后成了 chat，见 R3），标题生成 / 记忆抽取 / 会话压缩（`SessionTitleService.cs:220`、`LlmMemoryExtractor.cs:49`、`LlmSummarizer.cs:126`）会**从"发出去被远端拒绝"变成"本地抛 `LlmCapabilityMismatchException`"**。这大概率是想要的，但必须写进破坏性清单，并检查 `TextCompletionService` 的调用方是否有 catch。

**R8（回归，非破坏性）：每个能力一个客户端 = 每 Provider 4 套连接池。**
`LlmHttpClientFactory.Create(config)` 每次 new 一个 `SocketsHttpHandler`（`Llm.OpenAI/Clients/LlmHttpClientFactory.cs:11-29`）。`ConfiguredLlmProvider` 改成"每能力一个 `Lazy`"后，chat/embedding/tts/asr 各持一个 handler，同一个 baseUrl 4 个连接池。文档 §7.7 只说"复用 `LlmHttpClientFactory`（代理/连接池）"，没说要**共享同一 handler/HttpClient**。另外这个工厂是 `internal static` 且 `Llm.Anthropic` 有一份**独立复制**（`Llm.Anthropic/Clients/LlmHttpClientFactory.cs:9`），"横切上提"在代理/连接池这一层其实**没有上提**，SystemOne 包更用不了它（B1 相关）。

### 3.2 文档漏掉的接线点

| # | 遗漏 | 位置 | 影响 |
|---|---|---|---|
| G1 | `ProviderManager.RequiresRebuild` 未纳入 `Capabilities` | `ProviderManager.cs:389-403` | 改 `capabilities` 不重建 provider → 新能力永远不出现，直到重启（§10 阶段 3 有一条测试要求重建，但 §9.1 没把该判定列为修改点）|
| G2 | `ProviderManager.ResolveFactory` / `LlmClientFactoryResolver` / `LlmProviderBase.RequireFactory` 用的是 `SupportsType(string)`，改名 `SupportsProtocol` 要一起改 | `ProviderManager.cs:60-61`、`Abstractions/Llm/LlmClientFactoryResolver.cs:19`、`LlmProviderBase.cs:58-61` | 编译期，但 §9.1 只列了 Resolver |
| G3 | `ProviderTypes.Normalize` 未扩展 `typesafe` | `ProviderConfig.cs:20-31` | `"TypeSafe"` 不会被归一，`SupportsProtocol` 大小写敏感分支会漏；`ProviderConfigStringTests` 有同类断言可参照 |
| G4 | 非 chat 能力的 Hook 触发者（B3） | `LlmService.cs:339-403,440-574` | 目标 4 无法落地 |
| G5 | 超时机制：文档 §2 说"超时上提"，但**仓里从来没有 LLM 超时装饰器**（`ToolTimeoutDecorator` 只管工具）。当前超时全靠 `HttpClient.Timeout`（单次尝试） | `Llm.OpenAI/Clients/OpenAiHttpHelper.cs:46` | "超时对所有能力一致生效"是空承诺；`ProviderConfig.Timeout` 与 `RetryTotalBudgetMs` 的关系也没定义 |
| G6 | `ProviderInfo` 无 `Protocol`/`Capabilities` | `Abstractions/Llm/ProviderInfo.cs:6-17` | 模型页/诊断页无法显示能力；§12 说 UI 本次不做，但**诊断与测试**都需要它 |
| G7 | WebUI 连接测试的能力选择 | `ModelsPage.razor:789` → `ProviderManager.TestConnectionAsync(providerId, modelId)` → `ConfiguredLlmProvider.TestConnectionAsync`（`ConfiguredLlmProvider.cs:140-143`）→ 现在无参 `GetClient()` | 改造后 provider 层要"按 modelId 猜能力"，而它不持目录（会与 `ModelConfigManager` 成环）。这一层没有设计 |
| G8 | `ModelCapabilityManager` 的 enrich 路径 | `ConfiguredLlmProvider.cs:127-132`（逐模型 enrich），`ModelCapabilityManager.cs:47-82` | 富化只填 Limits/Modalities/Pricing/Thinking，**从不填 `Types`**（`ModelCapabilityFillEmpty.cs:21-52`）。因此 embedding 模型能否被识别，完全依赖物化时的 `types`——与 R1 的"目录第一个 embedding"强耦合 |
| G9 | `OpenAiResponsesClient` 无工厂接线 | `Llm.OpenAI/Clients/OpenAiResponsesClient.cs:14`（仅 `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests/OpenAiResponsesClientTests.cs` 构造） | 协议清单只有 `openai/anthropic/typesafe`，Responses 到底算不算独立协议、由谁 `Create` 出来，未决；375 行代码悬空 |
| G10 | `ILlmCapabilityClient.TestConnectionAsync(modelId)` 对非 chat 能力的语义 | 现状 chat 实现会**真发一次 5 token 补全**（`OpenAiChatClient.cs:194-214`） | TTS/ASR/embedding 的"连接测试"没有定义探针；`EmbeddingConnectionTester` 迁移后 §7.7 第 5 条说"或直接 `EmbedAsync` 一次探针"——二义 |
| G11 | 模型亲和：`ApplyModelToSession` / `ClearIncompatibleThinkingEffort` | `ModelManager.cs:71-107` | 换到 embedding/speech 模型时思考档位清理逻辑仍按 `Options.Thinking` 判，行为上无害；但**会话 `SelectedModel` 允许指向非 chat 模型**这一点没变，R7 的触发条件即来自此处。需要明确"会话模型必须 `Text`"是否加门 |
| G12 | `ModelOverride` 无 capability 字段 | `Abstractions/Llm/ModelOverride.cs:12-51` | 用户无法在模型页给某模型补 `Types = [ImageGeneration]` 之外的"能力"信息；与 §12「ProviderCapabilities UI 本次不加」一致，但**模型级 types 的补录路径必须可用**（已有 `Types`，够用）→ 仅需文档确认 |
| G13 | `docs/architecture/02-modules.md:65`、`README.md:241-246` 的 hook 表与模块 id | 文档 | 改名后架构文档立刻失真；`06-compliance-audit.md:188-199` 的"Abstractions 纯函数豁免清单"也要加 `LlmCapabilityMap` / `ProviderCapabilityDefaults`，否则下一轮合规扫描会把它记为债 |

### 3.3 `memory.json` 字段删除后的孤儿

删 `Embedding.Provider/Model/Dimensions` 会留下：

1. `ConfigurableEmbeddingStatus.cs:16-19` — 仍读 `IsEmbeddingConfigured`，语义被改成"目录里有没有"后，**这个类不能再只依赖 `IOptionsMonitor<MemoryOptions>`**（它需要 `IModelManager`）。DI 注册在 `MemoryServiceExtensions.cs:82`，会引入 `IEmbeddingStatus → IModelManager` 的新依赖边（`IModelManager` 是 Core 注册，Memory 只依赖 Abstractions 的 `IModelManager` 接口——勉强合规，但**要在文档里写清**）。
2. `MemoryOptionsProvider.cs:48-50` — 日志仍打 `IsEmbeddingConfigured`，字段删了但属性留着，只是含义变了；需确认它不是热重载的唯一触发者（实际是 `MemoryReloadHandler`，但若 embedding 选择改为依赖 `ILlmRouter`，**目录刷新不再触发 memory 配置重载**，`MemorySettingsCard` 的下拉会短时间陈旧）。
3. `MemorySettingsCard.razor:72-76`（`当前: Provider / Model`）、`:131-153`（`ResolveEmbeddingKey`）、`:183-199`（`OnEmbeddingKeyChanged` 写 Provider+Model）、`:229-235`（把测试得到的 `Dimensions` 写回配置）——**四处**，文档只提了"删除当前提示行"。
4. `IEmbeddingConnectionTester.TestAsync(provider, model)` 签名（`Abstractions/IEmbeddingConnectionTester.cs:9-13`）+ 其唯一调用点 `MemorySettingsCard.razor:229-231`。
5. `ProviderEmbeddingService.ProviderName`（`ProviderEmbeddingService.cs:34`）与 `IEmbeddingService.ProviderName`（`IEmbeddingService.cs:22`）——`RandomEmbeddingService`/`NullEmbeddingService` 都实现了；`RouterEmbeddingService` 必须给个值（返回 provider id 即可），否则接口改不动。
6. `IProviderEndpointLookup` / `ProviderEndpoint` / `IProviderEndpointInfo`（`Abstractions/Llm/IProviderEndpointLookup.cs:1-30`）——**全仓唯一消费者就是待删的两个 Memory 类**，且 `IProviderEndpointInfo` **没有任何实现者**（`OptionsProviderEndpointLookup.cs:35` 的注册表分支是死代码）。按 §4「一次性删干净」应**整组删除**，而非 §9.1 的"修改 `OptionsProviderEndpointLookup.cs`"。
7. `MemoryOptions.IsEmbeddingConfigured` 的测试 `MemoryOptionsTests.cs:10-19`。

### 3.4 其它会咬人的点

- **`BudgetUpdateHook` 的"按能力计价"与仓库既有结论冲突。** `docs/architecture/22-memory-dead-ledger-removal-release-notes.md:113` 明确"**不要**用 `SessionData.CachedInputTokens` 做账本"。而 `BudgetUpdateHook` 的唯一记账动作就是写它（`BudgetUpdateHook.cs:85-87`），且它先要 `payload.SessionId` 非空并 `GetOrLoadAsync`（`:48-61`）——embedding/decision/TTS 调用**根本没有 session**（§7.12 的直连 Router 路径不传 session）。所以 §10 阶段 5 的"`BudgetUpdateHook` 对 embedding/decision/tts 都能算出非零成本"在现有契约下**不可实现**，要么改记账模型（新增 `LlmUsageLedger`），要么删掉这条测试目标。
- **`llm.after_complete` 的 `result` 键表缺 `provider` / `model` / `cached` 之外的稳定键**：现表（`...:510-519`）与 `HookDataContract.ChatAfterComplete`（`HookDataContract.cs:33-46`）字段集不一致——后者有 `messageId`/`streaming`/`step`，前者没有；改 `LlmAfterComplete` 时必须决定是"超集"还是"分能力子集"，否则 `ChatMemoryHandler` 与 `BudgetUpdateHook` 两个消费者会拿到不同的东西。
- **`Speech` 与 `Modalities` 的现实冲突**：`ModelConfig.Modalities` 默认是**两个空列表**（`ModelConfig.cs:52-58`），本机 `providers.json` 里所有模型都是 `{"input":[],"output":[]}`。因此用户给某模型标 `Speech` 后，`Resolve<ISpeechSynthesisClient>` 必然抛"声明不足"。方向正确（不猜），但**必须在模型页给出可操作提示**，否则这是"标了就用不了"的死角。
- **`ConfiguredLlmProvider.CloneConfig/ApplyValues/LoadConfigAsync` 三处硬编码字段清单**（`ConfiguredLlmProvider.cs:74-99,187-227,361-380`）都要加 `Capabilities`，否则配置页保存一次就**把 capabilities 抹掉**（`SaveConfigAsync` 会写回不带该字段的配置）。`ProviderManager.CloneConfig`（`:415-434`）与 `ModelsPage.CloneProviderConfig`（`:1136-1151`）同理。**四处克隆点漏一个就是静默丢失配置。**
- **`OutputLimiter` 类语义**：`ConfiguredLlmProvider.CloneModel`（`:382-396`）把 `Types` 引用直接传（`Types = model.Types`，浅拷贝），与 `Modalities`/`Options` 同样共享引用。能力化后 Router 会**读** `Types`，若未来有原地写就会串。属于既有小坑，趁改动顺手修。

---

## 4. 建议（按优先级）

### P0 — 必须在写代码前改文档

1. **删掉 `SystemOneLlmProvider : LlmProviderBase`（B1）。** 改为：typesafe 走 `Providers` + `ConfiguredLlmProvider`；若要保留动态模型目录，给 `ProviderConfig` 加 `listModelsFrom: "typesafe"`（或 `Capabilities` 同级的 `dynamicModels: true`），在 `ConfiguredLlmProvider.GetModelsAsync` 内通过 `ILlmProvider` 侧的可选端口调 `ListModelsAsync`。理由：能力包引用 Core 会被 `04 §4` 与 `06` 直接打回，且它是唯一的分层阻断项。
2. **`ProviderCapabilityDefaults` 缺省只给 `Chat`（R4）。** embedding/TTS/ASR 一律要求显式 `capabilities`。理由：默认宽松会把 §7.4 的第二层校验变成恒真，并把"缺能力时给明确异常"退化成运行期 400。
3. **`MemoryEmbeddingOptions.Enabled` 默认改 `false`，且无 `ModelId` 时不做"目录第一个"隐式回落（R1）。** 若要回落，必须把选中结果**写回配置**（幂等）并 Warn。同时：`SqliteEmbeddingCache` 键补模型指纹、`VectorIndex.CosineSimilarity` 维数不等改为跳过+Warning、`§7.7 第 6 条"缓存层保留不变"改为"缓存键需加模型维度"**。理由：这是唯一能让"升级即抛异常"的路径。
4. **补 §7.8 配置示例的 `timeout: 10000` / `max_retries: 2` / `useProxy: false`（R2）**，并在 §9.2 破坏性清单里登记这三项；顺带定义"协议级默认重试/超时"是否由 `ProviderCapabilityDefaults` 一并给出。理由：默认值偷换 3 个参数，其中"0=不限次数"会把决策调用拖进 120s 退避。
5. **写清装饰器唯一应用点（C1/B2）：保留在 `ILlmClientFactory.Create` 内。** `ConfiguredLlmProvider` 只做 Lazy 缓存，不再 `Apply`。理由：plug provider 直接调工厂（`DeepSeekProvider.cs:176`），上移即让它们失去重试。

### P1 — 必须在同一楔子里实现

6. **补 B3 的触发者设计**：明确"谁触发非 chat 的 `llm.*`"，并给出 `params`/`headers`/`after_complete` 的输入载荷（现在只有 chat 有）。若要落地，最小形态是在 Router 外包一层 `LlmInvocationDecorator`（Core），或用 `ILlmCapabilityClient` 的基类方法做模板。否则把 §2 目标 4 降级为"仅 chat"，并把 §10 阶段 5 删掉。
7. **把 `Image → Chat` 从映射表里拿掉（R3）。** `Image` 应映射到 `None`（不可路由），由 `ImageGeneration` 承担生成；同时在 `ModelTypeInference.cs:31` 改 `ImageGeneration`，并在模型页对"只有 Image 无 ImageGeneration"的存量模型给出迁移提示。理由：否则 `Resolve<IChatCompletionClient>` 会对 dall-e 返回客户端。
8. **`Capability` 从单值改位集合，或删除（§1.4）**，并明确 `Resolve<T>` 的 `T ↔ Kind` 第二张表由谁维护（放在 `LlmCapabilityMap` 同文件，还是每个 `IXxxClient` 上带 `static Kind` 常量）。理由：两张手写表是长期漂移源。
9. **补全 §9.1 的文件清单**（G1/G2/G3、§2.4 表格全部、`BuiltInCapabilitySets.cs:110-111`、`ModuleArchitectureInvariantTests.cs:33`、`Seeing.Agent.slnx:52,107`、`ProviderManager` 三处 `CloneConfig`、`ModelsPage.CloneProviderConfig`、`docs/architecture/02` 与 `README.md:241-246`）。理由：漏一个克隆点就静默丢 `capabilities`。
10. **补 `ProviderManager.RequiresRebuild` 的 `Capabilities` 判定（G1）**，否则热重载改能力不生效——这正是 `docs/architecture/04` §4「变更能进 ReloadHandler」检查项。

### P2 — 文档口径修正（不改架构）

11. **把测试爆炸半径改成实数**：17 个 `ILlmClient` 文件、**3** 个 plug provider 测试项目、Tools.SystemOne **8 文件**、Memory 6 处、OpenAi/Anthropic 模块测试、HostComposition、Invariants 模块守门。并把 SystemOne.Tests 的处置写成"13 删 / 3 改 / 4 留"（§2.5 表），而不是"整套重写"。
12. **`IProviderEndpointLookup` 整组删除**（§3.3 第 6 条），不要"修改"；同时确认 `IProviderEndpointInfo` 零实现者是已知死代码。
13. **`BudgetUpdateHook` 的按能力计价改为可选**（§3.4 第 1 条）：要么引入 `LlmUsageLedger`（新契约，需在 §7.6 占位），要么把 §10 阶段 5 的该条改成"仅 chat 计价，非 chat 计入执行终态快照"。理由：现有 hook 契约里没有 session，写不了账。
14. **`OpenAiResponsesClient` 定去向（G9）**：要么在 `ProviderTypes` 加 `openai-responses` 并在工厂分派，要么登记为"待删死代码"。悬空的 375 行会在重构成 `IChatCompletionClient` 时被迫二选一，早定早省。
15. **`HookManager` 加废弃点告警（R6）**：对 `chat.*` 注册打 Warn（"已改名 llm.*，本次注册不会触发"）。理由：已提交的插件 dll 只会静默失效，不会编译失败。
16. **`ProviderInfo` 补 `Protocol` / `Capabilities`（G6）**，供诊断、连接测试（G7）与下一轮 UI 使用；顺带在 §7.12 说明 `ConfiguredLlmProvider.TestConnectionAsync(modelId)` 如何取能力（建议：由 `ILlmRouter.TryResolve` 反查，找不到则按 `Capabilities` 取首个）。

---

## 5. 复核清单（实现完成前逐条打勾）

- [ ] `rg "Seeing\.Agent\.Core\.csproj" src/capabilities src/gateway -g "*.csproj"` 结果与审查前一致（B1 未引入）
- [ ] `rg "ILlmClient\b" src samples tests plugs` 为空
- [ ] `rg "chat\.(params|headers|message|after_complete|on_error)" src samples tests` 仅剩 `HookManager` 的废弃告警表
- [ ] `rg "GetClient\(\)" src plugs` 为空（含 4 个 plug provider）
- [ ] `dotnet build Seeing.Agent.slnx` 零新增警告
- [ ] `dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/...`（模块守门含新 id `llm.systemone` / `llm.systemone.tools`）
- [ ] 手工：新建 `Providers.typesafe`（无 capabilities）→ 目录无 jev 模型、`Resolve<IDecisionClient>` 给明确异常
- [ ] 手工：默认 `memory.json`（无 Embedding 段）→ 向量索引**不**启动、`IEmbeddingStatus.IsAvailable == false`
- [ ] 手工：只把某模型标 `Speech` 不填 Modalities → 抛"声明不足"而非 400
