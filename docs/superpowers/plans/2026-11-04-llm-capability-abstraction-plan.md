# LLM 能力客户端抽象 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **执行模式（用户指定）：** 优先子代理并行。**任务之间不设审查关卡**；审查只发生在 Wave 边界（§审查关卡 三个）。
>
> **模型分工（用户指定，强制执行）：**
> - **所有实现子代理** → `trip-coding-plan/qwen3.8-flash`（`subagent` 调用时 `model: "trip-coding-plan/qwen3.8-flash"`）
> - **所有审查子代理** → `trip-coding-plan/deepseek-v4.1-flash`（`model: "trip-coding-plan/deepseek-v4.1-flash"`）
>
> 审查子代理**必须独立于实现子代理**（新会话、无实现上下文），只拿「计划 + spec + 代码」做判断。

**Goal:** 把只支持 chat 的单一 `ILlmClient` 重构为「协议 + 能力」双维度、可并行扩展的多模态客户端抽象，并把 `SystemOne` 平行栈与 `Memory` embedding 旁路合流进统一注册表。

**Architecture:** 每种能力一个窄接口（`IChatCompletionClient` / `IEmbeddingClient` / `IDecisionClient` / 语音 / 生成…），共享只含身份的 `ILlmCapabilityClient` 基座；`ILlmProvider` 按 `LlmCapabilityKind` 供出多个客户端；`ILlmRouter` 用「模型类型 + Provider 能力声明」五层校验解析；新增 `ILlmInvoker` 承担 Hook/出站头/耗时等横切；Hook 从 `chat.*` 泛化为 `llm.*`，载荷经可注册适配器组装；全局默认模型从单值改为按能力配置的 `DefaultModels` 字典。

**Tech Stack:** .NET 10, C#, xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12, Blazor (WebUI), Microsoft.Extensions.DependencyInjection

**Spec:** `docs/superpowers/specs/2026-11-04-llm-capability-client-abstraction-design.md`（v5）

## Global Constraints

以下每条对**所有**任务生效，不再在任务内重复：

- **分层红线**：任何 `src/capabilities/**` 项目**不得** `ProjectReference` 到 `Seeing.Agent.Core`。验证：`rg "Seeing\.Agent\.Core\.csproj" src/capabilities src/gateway -g "*.csproj"` 结果与改动前一致。
- **禁止 `ILlmClient` 残留**：最终 `rg "ILlmClient\b" src samples tests plugs` 必须为空。
- **禁止 `chat.*` Hook 点残留**：最终仅 `HookManager` 的废弃告警表可命中。
- **禁止单值 `DefaultModel` 残留**：`SeeingAgentOptions.DefaultModel` 与 `ProviderConfig.DefaultModel` 均删除。
- **`capability` 字符串双格式**：配置文件用 `LlmCapabilityMap.ToConfigKey`（小驼峰，如 `speechSynthesis`）；Hook 载荷用 `LlmCapabilityMap.ToHookValue`（点分，如 `speech.synthesis`）。**禁止任何调用点手拼字符串。**
- **能力枚举不可扩展**：`LlmCapabilityKind` 是 `[Flags] enum`，位于 Abstractions；插件无法新增值，本计划也不新增（除已定的 `Decision` / `ImageGeneration`）。
- **模型引用必须带前缀**：`DefaultModels` 的 value 必须形如 `provider/model`，裸 ID 写入抛 `ArgumentException`。
- **禁止 `async void`**：所有流式 API 用 `IAsyncEnumerable<T>`。
- **禁止静默吞异常**：`Resolve` / 默认值解析失败必须抛带原因的异常或返回 `reason`。
- **测试框架**：MTP 在本环境无法发现测试，一律用 `dotnet vstest <dll> --TestCaseFilter:"FullyQualifiedName~<类名>"`。
- **构建门槛**：`dotnet build Seeing.Agent.slnx` 必须通过且零新增警告。

## 文件所有权矩阵（并行安全的关键）

**规则：一个文件在同一 Wave 内只能有一个属主。** 子代理只允许改自己名下的文件；需要别人文件的改动，写在该任务的「跨轨道请求」栏，由属主轨道统一落地。

| 文件/目录 | 属主轨道 | Wave |
|---|---|---|
| `src/abstractions/Seeing.Agent.Abstractions/Llm/**`（新增/改/删） | **C** 契约 | 0 |
| `src/abstractions/.../Hooks/HookRegistry.cs`、`HookDataContract.cs` | **H** Hook | 1 |
| `src/abstractions/.../SystemOne/**`（迁移） | **S** SystemOne | 1 |
| `src/spine/.../Llm/LlmRouter.cs`、`LlmInvoker.cs` | **R** 运行时 | 1 |
| `src/spine/.../Llm/ConfiguredLlmProvider.cs`、`ProviderManager.cs`、`ProviderRegistry.cs`、`LlmProviderBase.cs`、`LlmService.cs`、`IProviderManager.cs` | **R** 运行时 | 1 |
| `src/spine/.../Extensions/ServiceCollectionExtensions.cs` | **R** 运行时（唯一属主，承接所有轨道登记请求） | 1 |
| `src/spine/.../Llm/RetryLlmClientDecorator.cs`、`LlmClientDecoratorRegistry.cs` | **D** 装饰器 | 1 |
| `src/spine/.../Llm/ModelConfigManager.cs`、`ModelManager.cs`、`TextCompletionService.cs` | **M** 默认模型 | 1 |
| `src/spine/.../Configuration/SeeingAgentOptions.cs`、`ConfigSectionRegistry.cs`、`LegacyGatewayConfigMigrator.cs` | **M** 默认模型 | 1 |
| `src/spine/.../Llm/Hooks/**`（新增适配器与注册表） | **H** Hook | 1 |
| `src/capabilities/Seeing.Agent.Llm.OpenAI/**` | **O** OpenAI | 1 |
| `src/capabilities/Seeing.Agent.Llm.Anthropic/**` | **A** Anthropic | 1 |
| `plugs/providers/Seeing.Provider.{DeepSeek,MiMo,OpenCodeZen,SiliconFlow}/**` | **A** Anthropic | 1 |
| `src/capabilities/Seeing.Agent.SystemOne/**` → `Seeing.Agent.Llm.SystemOne/**` | **S** SystemOne | 1 |
| `src/capabilities/Seeing.Agent.Tools.SystemOne/**` | **S** SystemOne | 1 |
| `src/spine/.../CapabilitySets/BuiltInCapabilitySets.cs`、`Seeing.Agent.slnx` | **S** SystemOne | 1 |
| `src/capabilities/Seeing.Agent.Memory/**` | **E** Embedding | 1 |
| `src/capabilities/Seeing.Agent.TokenBudget/**`、`Memory/Integration/MemoryHookHandler.cs` | **H** Hook | 1 |
| `src/support/Seeing.Agent.Llm.Support/Inference/ModelTypeInference.cs` | **R** 运行时 | 1 |
| `src/abstractions/.../Llm/IProviderEndpointLookup.cs`（整组删除） | **R** 运行时 | 1 |
| `samples/Seeing.Agent.WebUI/**` | **W** WebUI | 2 |
| `docs/architecture/**` | **R** 运行时（收尾） | 3 |

**冲突预警（已消解，实现者不要重复处理）：**
- `MemoryHookHandler.cs` 物理在 Memory 包内，但属 **H** 轨道；**E** 轨道不得修改它。
- `BuiltInCapabilitySets.cs` / `slnx` 由 **S** 轨道改，**R** 轨道不得碰。
- 所有 DI 登记集中在 **R** 轨道的 `ServiceCollectionExtensions.cs`；其他轨道若需登记，写进「跨轨道请求」。
- **`ModelTypeInference.cs` 与 `IProviderEndpointLookup.cs` 是 Wave 0 报告发现的属主空缺**：前者不改则 `Llm.Support` 永远编译不过（`ModelType.Image` 已删）；后者 spec §9.1 要求整组删除，但它是 `Memory` 待删类的唯一消费者，删除必须与 E 轨道同步。两者都归 **R** 轨道（R 是唯一有能力协调 Core + Memory 交界的轨道）。

---

## Review Focus（五个最可能咬人的输入/失败模式，各自有测试兜住）

1. **老配置升级后能力静默收缩**：已有 `providers.json` 无 `capabilities` 字段 → 缺省只给 `Chat`，原本能用的 embedding 突然不可用，且无提示。→ 测试见 Task C-1（缺省解析）与 Task R-3（`GetClient` 返回 null 而非抛）。
2. **`DefaultModels` 写成裸模型 ID**：用户手写 `"embedding": "text-embedding-3-small"`，若被接受，同名多 provider 时解析到错误模型。→ 测试见 Task M-1（写入时抛 `ArgumentException`）。
3. **Speech 模型只标类型、不填 `Modalities`**：`Resolve<ISpeechSynthesisClient>` 必须给"声明不足"的可操作提示，而不是静默选错或发 400。→ 测试见 Task R-1（第 3 层校验）。
4. **ASR 客户端误 Dispose 调用方的 `Stream`**：接受 `Stream` 参数的方法不负责释放是 .NET 惯例，实现者容易顺势写 `using var`，导致调用方后续读流崩溃。→ 测试见 Task O-3（传入可检测二次 Dispose 的流）。
5. **配置页保存一次就抹掉 `capabilities`**：`ProviderConfig` 有四处独立的手写字段克隆清单，漏一处即静默丢配置。→ 测试见 Task R-6（round-trip）。

---

# Wave 0：契约层（单轨，必须最先完成）

**为什么必须串行**：其余六个轨道全部依赖这里的签名。契约漂移会让所有并行轨道白做。

### Task C-1: 能力枚举与映射表

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmCapabilityKind.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmCapabilityMap.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Llm/ProviderCapabilityDefaults.cs`
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Llm/ILlmClient.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Llm/ModelType.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmCapabilityMapTests.cs`

**Interfaces:**
- Produces:
  - `enum LlmCapabilityKind { None, Chat, Embedding, Rerank, Decision, SpeechSynthesis, SpeechTranscription, ImageGeneration, VideoGeneration }`（`[Flags]`，`None = 0`，其后 `1 << n`）
  - `static IReadOnlyList<LlmCapabilityKind> LlmCapabilityMap.FromModelType(ModelType type)`
  - `static IReadOnlyDictionary<LlmCapabilityKind, Type> LlmCapabilityMap.ClientTypes { get; }`
  - `static LlmCapabilityKind LlmCapabilityMap.DefaultCapabilities(string protocol)`
  - `static string LlmCapabilityMap.ToConfigKey(LlmCapabilityKind kind)`
  - `static string LlmCapabilityMap.ToHookValue(LlmCapabilityKind kind)`
  - `enum ModelType { Text, Embedding, Rerank, Decision, ImageGeneration, Video, Speech, Other }`（**`Image` 已删除**）

- [ ] **Step 1: 写失败测试**

```csharp
// LlmCapabilityMapTests.cs
[Fact]
public void ToConfigKey_And_ToHookValue_ShouldCoverEveryKind()
{
    foreach (var kind in Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None))
    {
        LlmCapabilityMap.ToConfigKey(kind).Should().NotBeNullOrWhiteSpace();
        LlmCapabilityMap.ToHookValue(kind).Should().NotBeNullOrWhiteSpace();
    }
    LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.SpeechSynthesis).Should().Be("speechSynthesis");
    LlmCapabilityMap.ToHookValue(LlmCapabilityKind.SpeechSynthesis).Should().Be("speech.synthesis");
}

[Fact]
public void ModelTypeImage_ShouldNoLongerExist()
    => Enum.GetNames<ModelType>().Should().NotContain("Image");

[Fact]
public void ClientTypes_ShouldMapEveryRoutableKind()
{
    LlmCapabilityMap.ClientTypes[LlmCapabilityKind.Chat].Should().Be(typeof(IChatCompletionClient));
    LlmCapabilityMap.ClientTypes.Should().HaveCount(8); // 8 个能力端口
}

[Fact]
public void FromModelType_Speech_ShouldYieldBothDirections()
    => LlmCapabilityMap.FromModelType(ModelType.Speech).Should()
        .BeEquivalentTo(new[] { LlmCapabilityKind.SpeechSynthesis, LlmCapabilityKind.SpeechTranscription });

[Fact]
public void DefaultCapabilities_OpenAi_ShouldBeChatOnly()
    => LlmCapabilityMap.DefaultCapabilities("openai").Should().Be(LlmCapabilityKind.Chat);

[Fact]
public void DefaultCapabilities_TypeSafe_ShouldBeDecision()
    => LlmCapabilityMap.DefaultCapabilities("typesafe").Should().Be(LlmCapabilityKind.Decision);
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~LlmCapabilityMapTests"`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现**

枚举按 §0 决议定义。`ClientTypes` 覆盖 8 个端口：`Chat→IChatCompletionClient`、`Embedding→IEmbeddingClient`、`Rerank→IRerankClient`、`Decision→IDecisionClient`、`SpeechSynthesis→ISpeechSynthesisClient`、`SpeechTranscription→ISpeechTranscriptionClient`、`ImageGeneration→IImageGenerationClient`、`VideoGeneration→IVideoGenerationClient`。`FromModelType` 严格按 spec §7.2.1 的表。`ToConfigKey` 用 `kind.ToString()` 首字母小写；`ToHookValue` 对 `SpeechSynthesis`/`SpeechTranscription`/`ImageGeneration`/`VideoGeneration` 输出点分（`speech.synthesis` 等），其余输出全小写单词。`ProviderCapabilityDefaults` 是一张 `Dictionary<string, LlmCapabilityKind>`：`openai`/`openai-responses`/`anthropic` → `Chat`，`typesafe` → `Decision`，未知名 → `Chat`。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(llm): add capability kind, map, and provider defaults"
```

### Task C-2: 能力端口与 DTO

**Files:**
- Create: `src/abstractions/.../Llm/ILlmCapabilityClient.cs`、`IChatCompletionClient.cs`、`IEmbeddingClient.cs`、`IRerankClient.cs`、`IDecisionClient.cs`、`ISpeechSynthesisClient.cs`、`ISpeechTranscriptionClient.cs`、`IImageGenerationClient.cs`、`IVideoGenerationClient.cs`、`IModelCatalogClient.cs`
- Create: `src/abstractions/.../Llm/Embedding/EmbeddingModels.cs`、`Decision/DecisionModels.cs`、`Speech/SpeechModels.cs`、`Image/ImageModels.cs`、`Video/VideoModels.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmCapabilityContractTests.cs`

**Interfaces:**
- Produces: spec §7.2 的全部接口与 §7.2.2 的全部 DTO，签名逐字照抄 spec。
- **关键约束**：`ILlmCapabilityClient` **不得**声明 `Capabilities` 属性（决议 15）；`TestConnectionAsync` 必带 `LlmCapabilityKind capability` 参数。
- `TranscriptionRequest.Audio` 的 XML 注释必须写明「由调用方拥有，实现不得 Dispose」（决议 17）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ILlmCapabilityClient_ShouldNotExposeCapabilitiesProperty()
    => typeof(ILlmCapabilityClient).GetProperty("Capabilities").Should().BeNull();

[Fact]
public void TestConnectionAsync_ShouldRequireCapabilityParameter()
{
    var p = typeof(ILlmCapabilityClient).GetMethod(nameof(ILlmCapabilityClient.TestConnectionAsync))!;
    p.GetParameters().Select(x => x.ParameterType).Should().Contain(typeof(LlmCapabilityKind));
}

[Fact]
public void DecisionAnswer_ShouldCarryAllThreePrimitives()
{
    var a = new DecisionAnswer { Noul = 0.9, Choice = "billing", Score = 2, Confidence = 0.8 };
    a.Noul.Should().Be(0.9); a.Choice.Should().Be("billing"); a.Score.Should().Be(2);
}
```

- [ ] **Step 2-4: 常规 TDD 循环**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(llm): add capability ports and DTOs"
```

### Task C-3: Provider / 工厂 / 装饰器 / Router / Invoker / 适配器契约

**Files:**
- Create: `src/abstractions/.../Llm/ILlmRouter.cs`、`ILlmInvoker.cs`、`LlmInvocationOptions.cs`、`LlmCapabilityMismatchException.cs`、`Hooks/ILlmHookPayloadAdapter.cs`、`Hooks/ILlmHookPayloadAdapterRegistry.cs`
- Modify: `src/abstractions/.../Llm/ILlmProvider.cs`、`ILlmClientFactory.cs`、`ILlmClientDecorator.cs`、`ProviderConfig.cs`、`ProviderInfo.cs`、`LlmClientFactoryResolver.cs`、`IModelConfigManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmProviderContractTests.cs`

**Interfaces:**
- Produces: spec §7.3（`ILlmProvider` / `ILlmClientFactory`）、§7.4（`ILlmRouter`）、§7.5（`ILlmInvoker` / `LlmInvocationOptions` / 适配器）。
- `ProviderConfig`：**新增** `List<string>? Capabilities`、`bool ListModels`；**删除** `DefaultModel`。`ProviderTypes` 加 `TypeSafe = "typesafe"`、`OpenAiResponses = "openai-responses"`，`Normalize` 同步。

> **`IModelConfigManager` 的六个默认模型成员在 Wave 0 只落「签名」**（关卡 1 审查要求：S 轨道要用 `GetDefaultModel(Decision)`，签名必须前置）。**实现与测试仍归 Task M-1**，M 轨道不得再改接口文件。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ProviderConfig_ShouldHaveCapabilitiesAndListModels_ButNoDefaultModel()
{
    typeof(ProviderConfig).GetProperty("Capabilities").Should().NotBeNull();
    typeof(ProviderConfig).GetProperty("ListModels").Should().NotBeNull();
    typeof(ProviderConfig).GetProperty("DefaultModel").Should().BeNull();
}

[Fact]
public void ProviderTypes_Normalize_ShouldHandleTypeSafeAndResponses()
{
    ProviderTypes.Normalize("TypeSafe").Should().Be("typesafe");
    ProviderTypes.Normalize("OpenAI-Responses").Should().Be("openai-responses");
}

[Fact]
public void ILlmProvider_ShouldTakeCapabilityOnGetClientAndTestConnection()
{
    typeof(ILlmProvider).GetMethod("GetClient")!.GetParameters()[0].ParameterType
        .Should().Be(typeof(LlmCapabilityKind));
    typeof(ILlmProvider).GetMethod("TestConnectionAsync")!.GetParameters()[1].ParameterType
        .Should().Be(typeof(LlmCapabilityKind));
}
```

- [ ] **Step 2: 运行测试确认失败**
- [ ] **Step 3: 实现**（签名逐字照抄 spec §7.3 / §7.4 / §7.5）
- [ ] **Step 4: 运行测试确认通过**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(llm): add provider/factory/router/invoker contracts"
```

> **Wave 0 的编译边界**：本任务之后 `ProviderConfig.DefaultModel` 已删但 `ConfiguredLlmProvider`/`ProviderManager` 仍引用它，`ILlmProvider.GetClient()` 已改签名但 5 个实现类未改。**Wave 0 结束时 `dotnet build Seeing.Agent.slnx` 必然失败，这是设计好的**——由 Task I-1 统一收口。不要在 Wave 0 试图让它编译通过，那会诱使你在契约层加兼容重载。

### Task C-4: 关卡 1 审查修复（已由主控预先落地，Wave 1 只需知晓）

**本节记录关卡 1 审查发现的四个阻塞项及其修复，属已完成状态。** Wave 1 的实现者需要知道这些 API 已经存在，不要重复实现。

| 阻塞项 | 修复 | 落地位置 |
|---|---|---|
| **B1** 缺「字符串 → 能力」反向解析，三轨会各写一份 `Enum.TryParse` 导致语义分叉 | 新增 `TryParseConfigKey` / `TryParseHookValue` / `TryGetKindFromClientType` / `ParseCapabilities(values, protocol)`。`ParseCapabilities` 遇未登记名字**抛 `ArgumentException`**，禁止静默忽略 | `LlmCapabilityMap.cs` |
| **B2** `LlmPayloadContext` 缺 `messageId` / `step`，H-2 无法产出 spec §7.6 要求的键 | 新增 `MessageId` / `Step` / `StreamedContent` / `StreamedReasoning` / `StreamedToolCalls` 五个字段 | `Hooks/ILlmHookPayloadAdapter.cs` |
| **B3** `ILlmInvoker.StreamAsync` 承诺「累计用量触发终态 Hook」但契约无承载 | 在 XML 注释里钉死累计规则：`StreamUpdate` 累计 Id/Content/Reasoning/ToolCalls/Usage 并填入上述字段；其余 chunk 类型**不承诺**累计语义 | `ILlmInvoker.cs` |
| **B4** S 轨道依赖 `GetDefaultModel(Decision)` 但签名在 M-1 | 六个成员**签名前置到 Wave 0**，实现仍留 M-1 | `IModelConfigManager.cs` |

**已裁定的待议项**：`Llm/Rerank/RerankModels.cs`（`RerankRequest` / `RerankHit` / `RerankResponse`）**保留**。它是 spec §7.2 的 `IRerankClient` 的必需依赖，且 spec §7.2.2 的 DTO 块漏列了 Rerank。形状与 Cohere/Voyage/Jina 的公共子集对齐，`RerankHit(Index, Score, Document)` 的 `Index` 指回原下标，与 `EmbeddingVector` 风格一致。

**Wave 1 必须知道的三条派发顺序（关卡 1 审查补充）**：
1. **H-1 必须先于 R-2 落地** —— R-2 消费 H-1 产出的 `llm.*` 常量。若同批启动，R-2 需按 Wave 0 已定的点分字符串自测，等 H-1 合入后再联调。
2. **S-3 依赖 M-1 的实现**（签名已在 Wave 0）—— S-3 若先完成，用 Moq 桩住 `IModelConfigManager` 即可，不阻塞。
3. **D-1 的装饰器靠 `is` 判断能力**，因决议 15 删了客户端自述。**前提：每个客户端实例恰好实现一个能力端口**——这是隐含契约，D-1 与 O-4 必须一致遵守。

---

## 审查关卡 1（Wave 0 结束后，唯一的契约审查）

**只审一次，审这一件事**：契约是否足以让 6 个轨道并行实现而不必互相询问。

检查清单：
- [ ] 8 个能力端口签名齐全，且 `ILlmCapabilityClient` 无 `Capabilities`
- [ ] `LlmCapabilityMap` **七个**方法齐全（`FromModelType` / `ClientTypes` / `DefaultCapabilities` / `ToConfigKey` / `ToHookValue` / `TryParseConfigKey` / `ParseCapabilities` / `TryGetKindFromClientType`）
- [ ] `ILlmRouter` / `ILlmInvoker` / `ILlmHookPayloadAdapter(Registry)` 齐全
- [ ] `LlmPayloadContext` 含 `MessageId` / `Step` / `StreamedContent` / `StreamedReasoning` / `StreamedToolCalls`
- [ ] `IModelConfigManager` 六个成员**签名**齐全（实现留 M-1）
- [ ] `ProviderConfig` 有 `Capabilities`/`ListModels`、无 `DefaultModel`
- [ ] `ModelType` 中无 `Image`，有 `Decision` / `ImageGeneration`
- [ ] `ILlmProvider.GetClient` 与 `TestConnectionAsync` 均带 `LlmCapabilityKind` 参数
- [ ] **`dotnet build Seeing.Agent.slnx` 失败符合预期**（只应有"实现类未跟上新签名"的错误，不应有契约自身的语法/类型错误）

**判定方法**：构建失败信息中，任何错误若指向 `src/abstractions/**` 内部，则是契约写错，必须修；若指向 `src/spine/**` 或 `src/capabilities/**` 的实现类，属预期。

审查不通过则只改契约层，不进入 Wave 1。

**关卡 1 已执行完毕**（审查者：`deepseek-v4.1-flash`）。结论 `PASS_WITH_FIXES`，4 个阻塞项已全部修复并记录在 Task C-4。**Wave 1 已可启动。**

---

# Wave 1：八轨道并行（每个子代理独占文件集）

**进入条件**：审查关卡 1 通过。

**并行规则**：
- **八条轨道同时启动**（R / D / O / A / H / S / M / E），各自只改「文件所有权矩阵」中自己名下的文件。
- **任何跨文件需求写进「跨轨道请求」栏，不自行修改他人文件。**
- **轨道内任务串行**（同一轨道的 Task N 依赖 Task N-1 的产物）；**轨道之间不等待**。
- Wave 1 期间**不要跑全量构建**，只看自己轨道的单元测试。累积的编译错误由 Task I-1 统一收口。

**轨道间已知依赖（只依赖 Wave 0 契约，不依赖彼此实现）：**

| 轨道 | 依赖 Wave 0 的什么 | 不依赖 |
|---|---|---|
| R | `ILlmRouter`/`ILlmInvoker` 契约、`ILlmProvider` 新签名 | 其余全部 |
| D | `ILlmCapabilityClient` 及 8 个端口 | 其余全部 |
| O | 8 个端口 + DTO | 其余全部 |
| A | 8 个端口 + 工厂契约 | 其余全部 |
| H | `HookRegistry` 待改名（H 自己改）、适配器契约 | R 的 Invoker 实现（只按契约对接） |
| S | `IDecisionClient` / `IModelCatalogClient` / 工厂契约 | R 的 Router 实现 |
| M | `IModelConfigManager` 六个成员（M 自己改接口+实现） | R 的 Router 实现（测试用 Moq 桩） |
| E | `IEmbeddingClient` / `ILlmInvoker` 契约 | O 的 Embedding 实现（只按契约调用） |

---

## 轨道 R：运行时（Router / Invoker / Provider 接线）

### Task R-1: LlmRouter 五层校验

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Llm/LlmRouter.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmRouterTests.cs`

**Interfaces:**
- Consumes: `IModelConfigManager`、`IProviderRegistry`、`LlmCapabilityMap`
- Produces: `sealed class LlmRouter : ILlmRouter`，构造函数 `(IModelConfigManager models, IProviderRegistry providers, ILogger<LlmRouter> logger)`

- [ ] **Step 1: 写失败测试（覆盖五层）**

```csharp
[Fact] // 第 1 层：模型不存在
public void Resolve_UnknownModel_ShouldThrow() { /* 断言消息含 modelId */ }

[Fact] // 第 2 层：模型类型不含所需能力
public void Resolve_EmbeddingModelAsChat_ShouldThrow() { /* 断言消息含"模型声明类型" */ }

[Fact] // 第 3 层：Speech 缺 Modalities
public void Resolve_SpeechWithoutModalities_ShouldThrowDeclarationInsufficient() { }

[Fact] // 第 4 层：Provider 未声明该能力
public void Resolve_ProviderLacksCapability_ShouldThrow() { }

[Fact] // 第 5 层：Provider 声明了但工厂返回 null
public void Resolve_ConfiguredCapabilityButNoClient_ShouldThrow() { }

[Fact] // 缓存键是 providerId，不是 modelId
public void Resolve_TwoModelsSameProvider_ShouldReturnSameClientInstance() { }

[Fact]
public void TryResolve_ShouldNotThrowAndGiveReason() { }
```

- [ ] **Step 2-4: TDD 循环**（校验顺序严格按 spec §7.4 的 1→5）
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(llm): add five-layer capability router"
```

### Task R-2: LlmInvoker 与 Hook 生命周期

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Llm/LlmInvoker.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmInvokerTests.cs`

**Interfaces:**
- Consumes: `ILlmRouter`、`IHookManager`、`ILlmHookPayloadAdapterRegistry`
- Produces: `sealed class LlmInvoker : ILlmInvoker`，构造函数 `(ILlmRouter router, IHookManager hooks, ILlmHookPayloadAdapterRegistry adapters, ILogger<LlmInvoker> logger)`

**跨轨道请求 → H 轨道**：`llm.params` / `llm.headers` / `llm.message` / `llm.after_complete` / `llm.on_error` 五个 `HookSpec` 常量与 `HookDataContract.LlmAfterComplete` 的 key 常量由 H 轨道提供；本轨道按名消费。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void Invoke_ShouldFireParamsHeadersAfterCompleteInOrder() { }
[Fact] public void Invoke_WhenEmitHooksFalse_ShouldFireNothing() { }
[Fact] public void Invoke_OnRetryExhaustion_ShouldFireOnErrorOnce() { }
[Fact] public void Stream_ShouldFireAfterCompleteWithAccumulatedUsage() { }
[Fact] public void Invoke_NonChatCapability_ShouldPassCapabilityKeyInPayload() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task R-3: ConfiguredLlmProvider 多能力化

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Llm/ConfiguredLlmProvider.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/ConfiguredLlmProviderTests.cs`（改造既有）

**Interfaces:**
- Produces: `ConfiguredLlmProvider` 持 `Lazy<ILlmCapabilityClient>[]`（按 `config.Capabilities` 或协议缺省构建）；`GetClient(kind)` 命中返回、未声明返回 `null`；`DisposeAsync` 释放**全部**已创建客户端；`GetModelsAsync` 在 `ListModels == true` 且客户端实现 `IModelCatalogClient` 时合并远端目录。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void GetClient_UndeclaredCapability_ShouldReturnNull() { }
[Fact] public void GetClient_DeclaredCapability_ShouldReturnSameInstanceOnRepeat() { }
[Fact] public void Dispose_ShouldDisposeEveryCreatedClient() { }
[Fact] public void GetModelsAsync_ListModelsTrue_ShouldUnionRemoteCatalog() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task R-4: ProviderManager / ProviderRegistry / LlmProviderBase 适配

**Files:**
- Modify: `ProviderManager.cs`、`ProviderRegistry.cs`、`LlmProviderBase.cs`、`IProviderManager.cs`、`LlmService.cs`
- Delete: `src/spine/Seeing.Agent.Core/Llm/OptionsProviderEndpointLookup.cs`
- Test: `ProviderManagerTests.cs`、`ProviderManagerCharacterizationTests.cs`、`ProviderManagerConcurrencyTests.cs`、`LlmAndExecutorTests.cs`、`MigrationTests.cs`（全部改造）

**Interfaces:**
- `ProviderManager.RequiresRebuild` 纳入 `Capabilities` / `ListModels`，移除 `DefaultModel` 比较。
- `ResolveFactory` 走 `SupportsProtocol`。
- `LlmService` 改走 `ILlmInvoker`，保留 chat 参数归一化（`MaxTokens` / `ThinkingEffort` / `Temperature`）。

- [ ] **Step 1-4: 改造既有测试断言到新签名，TDD 循环**
- [ ] **Step 5: Commit**

### Task R-5: DI 接线（唯一属主）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Invariants/DiWiringTests.cs`

**Interfaces:**
- 登记 `ILlmRouter` / `ILlmInvoker` / `ILlmHookPayloadAdapterRegistry` + 五个内置适配器；移除 `IProviderEndpointLookup` 登记。

- [ ] **Step 1: 写失败测试**（`ServiceCollection` 上 `AddSeeingCore` 后能解析 `ILlmRouter`、`ILlmInvoker`、`ILlmHookPayloadAdapterRegistry`，且适配器注册表能解析 `Chat`/`Embedding`/`Decision`/`SpeechSynthesis`/`SpeechTranscription`）
- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task R-6: 四处克隆清单同步（round-trip 防回归）

**Files:**
- Modify: 与 R-3/R-4 同（`ConfiguredLlmProvider.LoadConfigAsync`/`ApplyValues`/`CloneConfig`、`ProviderManager.CloneConfig`）
- Test: `tests/spine/Seeing.Agent.Tests/Llm/ProviderConfigRoundTripTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void SaveThenLoad_ShouldPreserveCapabilitiesAndListModels()
{
    // 构造 config { Capabilities = ["chat","embedding"], ListModels = true }
    // 走 LoadConfigAsync → ApplyValues 往返
    // 断言两项均未丢失
}
```

> 这条正是 Review Focus #5，缺了它"保存一次抹掉配置"会静默发生。

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task R-7: Llm.Support 推断修正与端点查询整组删除

**Files:**
- Modify: `src/support/Seeing.Agent.Llm.Support/Inference/ModelTypeInference.cs`
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Llm/IProviderEndpointLookup.cs`
- Modify: `src/spine/Seeing.Agent.Core/Llm/OptionsProviderEndpointLookup.cs`（删除，R-4 已列）
- Test: `tests/spine/Seeing.Agent.Tests/Llm/ModelTypeInferenceTests.cs`（若无则新建）

**Interfaces:**
- `ModelTypeInference.InferTypes` 对 `stable-diffusion` / `sdxl` / `flux` / `dall-e` / `-image` / `image-` **全部**返回 `[ModelType.ImageGeneration]`（含糊模式也归生成，因为 `Image` 值已删）。
- `IProviderEndpointLookup` / `ProviderEndpoint` / `IProviderEndpointInfo` 三类型整组删除。

> **这是 Wave 0 发现的两个属主空缺**。`ModelTypeInference.cs` 不改则 `Llm.Support` 永远编译不过；`IProviderEndpointLookup` 的唯一生产消费者是 E 轨道待删的两个 Memory 类，**删除时机必须是 E-1 完成之后**，否则会留下悬空引用。

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
[InlineData("dall-e-3", ModelType.ImageGeneration)]
[InlineData("flux-pro", ModelType.ImageGeneration)]
[InlineData("sdxl-turbo", ModelType.ImageGeneration)]
[InlineData("stable-diffusion-xl", ModelType.ImageGeneration)]
public void InferTypes_ImageModels_ShouldReturnImageGeneration(string id, ModelType expected)
    => ModelTypeInference.InferTypes(id).Should().ContainSingle().Which.Should().Be(expected);
```

- [ ] **Step 2-4: TDD 循环**（`IProviderEndpointLookup` 的删除须在 E-1 合入后执行；若 E-1 未完成，先只改 `ModelTypeInference.cs`，删除留给 Task I-1）
- [ ] **Step 5: Commit**

---

## 轨道 D：装饰器泛型化

### Task D-1: 泛型重试装饰器

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs`、`LlmClientDecoratorRegistry.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/RetryLlmClientDecoratorTests.cs`（改造）

**Interfaces:**
- `ILlmClientDecorator.Wrap(ILlmCapabilityClient, ProviderConfig)` 返回 `ILlmCapabilityClient`。
- 内部为 8 个能力各写一个 `Retrying<X>Client`；4 个带流式/轮询的另写 `StreamOnceAsync` 变体（chat 2 方法、speech×2 各 2 方法、video 4 方法）。

**工作量提示**：约 600–900 行，是本 Wave 最大的单点。逻辑照抄现有 `RetryingLlmClient`，只换类型与流元素类型。**流式边界不变：一旦产出过 chunk 绝不重试。**

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void EmbeddingRetry_ShouldRetryOnTransientFailure() { }
[Fact] public void DecisionRetry_ShouldRetryOnTransientFailure() { }
[Fact] public void SpeechStream_AfterFirstChunk_ShouldNotRetry() { }
[Fact] public void Retry_ShouldWriteAttemptItemsIntoCallContext() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

---

## 轨道 O：OpenAI 客户端

### Task O-1: Embedding 客户端

**Files:**
- Create: `src/capabilities/Seeing.Agent.Llm.OpenAI/Clients/OpenAiEmbeddingClient.cs`
- Test: `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests/OpenAiEmbeddingClientTests.cs`

**Interfaces:**
- Produces: `sealed class OpenAiEmbeddingClient : IEmbeddingClient`，`POST {baseUrl}/embeddings`，`Capability` 无属性（决议 15）。复用 `LlmHttpClientFactory` 与 `OutboundPipeline`。

- [ ] **Step 1-4: TDD 循环**（断言请求体 `model`/`input`，响应解析出 `Vectors` 与 `Dimensions`，`index` 乱序时按 `Index` 排序）
- [ ] **Step 5: Commit**

### Task O-2: TTS 客户端

**Files:**
- Create: `Clients/OpenAiSpeechSynthesisClient.cs`
- Test: `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests/OpenAiSpeechSynthesisClientTests.cs`

**Interfaces:** `sealed class OpenAiSpeechSynthesisClient : ISpeechSynthesisClient`，`POST /v1/audio/speech`；非流式返回完整字节；流式按响应体自然分块产出 `AudioChunk`。

- [ ] **Step 1-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task O-3: ASR 客户端

**Files:**
- Create: `Clients/OpenAiSpeechTranscriptionClient.cs`
- Test: `tests/capabilities/Seeing.Agent.Llm.OpenAI.Tests/OpenAiSpeechTranscriptionClientTests.cs`

- [ ] **Step 1: 写失败测试（含 Stream 所有权）**

```csharp
[Fact]
public async Task Transcribe_ShouldNotDisposeCallerStream()
{
    var stream = new DisposeTrackingStream(new MemoryStream(new byte[64]));
    await sut.TranscribeAsync(new TranscriptionRequest { Model = "whisper-1", Audio = stream });
    stream.Disposed.Should().BeFalse();   // Review Focus #4
}
```

- [ ] **Step 2-4: TDD 循环**（multipart 构造、`verbose_json` 段落解析、流式 SSE）
- [ ] **Step 5: Commit**

### Task O-4: Chat / Responses 客户端改造 + 工厂分派

**Files:**
- Modify: `Clients/OpenAiChatClient.cs`、`Clients/OpenAiResponsesClient.cs`、`OpenAiLlmClientFactory.cs`
- Test: `OpenAiLlmModuleTests.cs`（改造）

**Interfaces:** `Create(config, capability)` switch 分派 chat/embedding/speech×2；支持协议 `openai` 与 `openai-responses`；装饰器在 `Create` 返回前应用（唯一应用点）。

- [ ] **Step 1-4: TDD 循环**
- [ ] **Step 5: Commit**

---

## 轨道 A：Anthropic + 四个 plug provider

### Task A-1: Anthropic 客户端适配

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Llm.Anthropic/Clients/AnthropicClient.cs`、`AnthropicLlmClientFactory.cs`
- Test: `tests/capabilities/Seeing.Agent.Llm.Anthropic.Tests/AnthropicLlmModuleTests.cs`

**Interfaces:** 仅 `Chat`；`Create(config, Chat)` 返回客户端，其余能力返回 `null`。

- [ ] **Step 1-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task A-2: 四个 plug provider 适配

**Files:**
- Modify: `plugs/providers/Seeing.Provider.{DeepSeek,MiMo,OpenCodeZen,SiliconFlow}/*Provider.cs`、`*LlmModule.cs`
- Test: `tests/plugs/Seeing.Provider.{DeepSeek,MiMo,OpenCodeZen}.Tests/*ProviderTests.cs`、`*LlmModuleTests.cs`

**Interfaces:** `GetClient()` → `GetClient(LlmCapabilityKind)`，仅 `Chat`；`_client` 字段/`InvalidateClient`/`DisposeClientAsync` 随之改。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void GetClient_Chat_ShouldReturnClient() { }
[Fact] public void GetClient_Embedding_ShouldReturnNull() { }
[Fact] public void CreatedClient_ShouldStillBeWrappedByRetryDecorator() { }  // 防 B2 回归
```

> 最后一条是关键：装饰器若被错误上移到 `ConfiguredLlmProvider`，plug provider 会静默失去重试。

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

---

## 轨道 H：Hook 泛化（含 TokenBudget / MemoryHookHandler）

### Task H-1: Hook 点重命名与废弃告警

**Files:**
- Modify: `src/abstractions/.../Hooks/HookRegistry.cs`、`HookDataContract.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Hooks/HookManager.cs`（废弃点告警）
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:373`
- Test: `tests/spine/Seeing.Agent.Tests/Hooks/HookManagerTests.cs`、`LlmTransportOnlyTests.cs`

**Interfaces:** `HookRegistry.LlmParams/LlmHeaders/LlmMessage/LlmAfterComplete/LlmOnError`（点分字符串）；`HookDataContract.LlmAfterComplete` 的 key 常量。

- [ ] **Step 1-4: TDD 循环**

```csharp
[Fact]
public void AddHandler_LegacyChatPoint_ShouldLogWarningAndNotFire()
{
    // 注册 spec.Point = "chat.after_complete"
    // 触发 LlmAfterComplete → 该 handler 不应被调用，且日志含"已改名 llm.*"
}
```

- [ ] **Step 5: Commit**

### Task H-2: 载荷适配器与注册表

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Llm/Hooks/LlmHookPayloadAdapterRegistry.cs`、`BuiltIn{LlmChat,LlmEmbedding,LlmDecision,LlmSpeechSynthesis,LlmSpeechTranscription}PayloadAdapter.cs`、`FallbackLlmHookPayloadAdapter.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/LlmHookPayloadAdapterTests.cs`

**Interfaces:** 注册表后注册胜；无匹配返回通用兜底；兜底**绝不抛异常**。

- [ ] **Step 1-4: TDD 循环**

```csharp
[Fact] public void PluginAdapter_ShouldOverrideBuiltIn() { }
[Fact] public void UnimplementedCapability_ShouldUseFallbackWithoutThrowing() { }
[Fact] public void ChatAdapter_ShouldEmitContentReasoningAndToolCalls() { }
```

- [ ] **Step 5: Commit**

### Task H-3: 两个既有消费方改造

**Files:**
- Modify: `src/capabilities/Seeing.Agent.TokenBudget/Hooks/BudgetUpdateHook.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Integration/MemoryHookHandler.cs`
- Test: 对应两个包的测试

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void BudgetUpdate_NonChatCapability_ShouldNotRecordSessionBudget() { }
[Fact] public void BudgetUpdate_ChatWithoutSession_ShouldStillShortCircuit() { }
[Fact] public void MemoryHook_NonChatCapability_ShouldSkip() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

---

## 轨道 S：SystemOne 合流

### Task S-1: 包重命名与平行栈删除

**Files:**
- Rename: `src/capabilities/Seeing.Agent.SystemOne/` → `src/capabilities/Seeing.Agent.Llm.SystemOne/`（含 csproj、`RootNamespace`）
- Delete: `Configuration/SystemOneConfigStore.cs`、`SystemOneProviderManager.cs`、`SystemOneReloadHandler.cs`、`SystemOneEnvironmentConfig.cs`、`Runtime/ConfiguredSystemOneProvider.cs`、`Clients/SystemOneHttpClientFactory.cs`、`PredefinedSystemOneProviders.cs`
- Modify: `Clients/SystemOneHttpHelper.cs`（删重试/退避，保留序列化与错误映射）
- Modify: `Seeing.Agent.slnx:52,107`、`src/spine/.../CapabilitySets/BuiltInCapabilitySets.cs:110-111`、`tests/spine/Seeing.Agent.Invariants.Tests/Invariants/ModuleArchitectureInvariantTests.cs:33`

**Interfaces:** 模块 id `systemone` → `llm.systemone`；`systemone.tools` → `llm.systemone.tools`（含 `DependsOn`）。

- [ ] **Step 1-4: 改造/删除测试（13 删 / 3 改 / 4 留，按 spec §10.2 表）**
- [ ] **Step 5: Commit**

### Task S-2: TypeSafe 客户端实现 IDecisionClient

**Files:**
- Modify: `Clients/TypeSafeSystemOneClient.cs`、`TypeSafeSystemOneClientFactory.cs`
- Modify: `Runtime/SystemOneService.cs`
- Test: `TypeSafeSystemOneClientTests.cs`（改造）、`SystemOneSerializationTests.cs`（保留）

**Interfaces:** `IDecisionClient` + `IModelCatalogClient`；`ProviderType = "typesafe"`；`Create(config, Decision)` 返回本客户端。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void Factory_ShouldOnlyServeDecisionCapability() { }
[Fact] public void ListModels_ShouldMapToDecisionModelConfigs() { }
[Fact] public async Task Evaluate_ShouldParseNoulChoiceScore() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task S-3: SystemOneTool 走 Router + 默认模型

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.SystemOne/SystemOneTool.cs`、`SystemOneToolParser.cs`、`SystemOneToolsModule.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests/`（8 个文件）

**Interfaces:** 模型解析四步链（tool 参数 → `GetDefaultModel(Decision)` → 目录唯一 Decision → 报错列候选）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Tool_NoModelNoDefaultMultipleCandidates_ShouldErrorWithCandidates() { }
[Fact] public async Task Tool_UsesDefaultModelsDecision_WhenPresent() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

---

## 轨道 M：默认模型系统

### Task M-1: 默认模型契约与实现（接口+实现+测试一次完成）

**Files:**
- Modify: `src/abstractions/.../Llm/IModelConfigManager.cs`（六个新成员，删旧签名）
- Modify: `src/spine/.../Configuration/SeeingAgentOptions.cs:16`（删 `DefaultModel`，加 `DefaultModels`）
- Modify: `src/spine/.../Configuration/ConfigSectionRegistry.cs:42`
- Modify: `src/spine/.../Configuration/LegacyGatewayConfigMigrator.cs:26-34`
- Modify: `src/spine/.../Llm/ModelConfigManager.cs`、`ModelManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Llm/ModelManagerTests.cs`、`Configuration/SeeingAgentOptionsSplitTests.cs`、`ConfigSectionRegistryConsumptionTests.cs`

**Interfaces:** 实现 spec §7.14.2 六个成员：
- `string? GetDefaultModel(LlmCapabilityKind capability)`
- `IReadOnlyDictionary<LlmCapabilityKind, string> GetAllDefaultModels()`
- `bool TryResolveDefault<TClient>(LlmCapabilityKind, out TClient?, out string?) where TClient : class, ILlmCapabilityClient`
- `Task SetDefaultModelAsync(LlmCapabilityKind, string?, ConfigLevel, CancellationToken)`
- `bool CanSetAsDefaultModel(string modelId, LlmCapabilityKind capability)`
- `IReadOnlyDictionary<LlmCapabilityKind, string> ValidateDefaultModels()`

**旧的无参 `GetDefaultModel()` 与旧签名直接替换，不留重载。** `CanSetAsDefaultModel` 去掉 `ModelType.Text` 硬编码。`ResolveNativeModel` 末档改 `GetDefaultModel(Chat)`。

> `TryResolveDefault` 需要 `ILlmRouter`——它由 R 轨道同时在建。**本任务只依赖 `ILlmRouter` 的接口签名**（Wave 0 已定），实现类在 DI 里注入即可；单元测试用 Moq 桩住 Router。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void GetDefaultModel_Unconfigured_ShouldReturnNull() { }          // 严格查表
[Fact] public void SetDefaultModel_WithoutProviderPrefix_ShouldThrow() { }        // Review Focus #2
[Fact] public void SetDefaultModel_ModelLacksCapability_ShouldThrow() { }
[Fact] public void CanSetAsDefaultModel_EmbeddingModel_AsEmbedding_ShouldBeTrue() { }
[Fact] public void CanSetAsDefaultModel_EmbeddingModel_AsChat_ShouldBeFalse() { }
[Fact] public void ValidateDefaultModels_PointsToDeletedModel_ShouldReportAndKeep() { }
[Fact] public void ResolveNativeModel_ShouldFallBackToGetDefaultModelChat() { }
[Fact] public void GetAllDefaultModels_ShouldOnlyContainConfiguredEntries() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(llm): capability-aware global default models"
```

### Task M-2: TextCompletionService 与标题模型死配置

**Files:**
- Modify: `src/spine/.../Llm/TextCompletionService.cs:63,97`
- Modify: `src/spine/.../Llm/TextCompletionService.cs`（接上 `TitleGeneration.Model`，优先于 chat 默认）
- Test: `tests/spine/Seeing.Agent.Tests/Llm/TextCompletionServiceTests.cs`

- [ ] **Step 1-4: TDD 循环**

```csharp
[Fact] public void Complete_NoModelArg_ShouldUseChatDefault() { }
[Fact] public void Complete_TitleGenerationModelSet_ShouldPreferIt() { }
```

- [ ] **Step 5: Commit**

---

## 轨道 E：Memory embedding 迁移

### Task E-1: RouterEmbeddingService 与配置瘦身

**Files:**
- Create: `src/capabilities/Seeing.Agent.Memory/Core/Embedding/RouterEmbeddingService.cs`
- Delete: `Core/Embedding/ProviderEmbeddingService.cs`、`ProviderEmbeddingService` 的裸 HTTP、`EmbeddingConnectionTester` 的裸 HTTP 实现
- Modify: `Configuration/MemoryOptions.cs`（`MemoryEmbeddingOptions` → `Enabled`（默认 false）+ `ModelId`）
- Modify: `Extensions/MemoryServiceExtensions.cs:83-94`（始终注册 `RouterEmbeddingService`）
- Modify: `Core/Embedding/ConfigurableEmbeddingStatus.cs`（依赖 `IModelManager`）
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Embedding/*.cs`、`Configuration/MemoryOptionsTests.cs`

**Interfaces:** **不读 `DefaultModels.embedding`**（决议 10）；`IsEmbeddingConfigured = Enabled && ModelId 非空`；解析发生在调用期。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void EnabledTrueButNoModelId_ShouldRemainUnavailable() { }   // Review Focus #1 的配套
[Fact] public void DefaultModelsEmbeddingSetButMemoryModelIdEmpty_ShouldStillBeUnavailable() { }
[Fact] public async Task Resolve_UsesExplicitModelIdNotCatalogFirst() { }
```

- [ ] **Step 2-4: TDD 循环**
- [ ] **Step 5: Commit**

### Task E-2: 缓存键与维数容错

**Files:**
- Modify: `Core/Embedding/SqliteEmbeddingCache.cs`（主键加模型指纹）
- Modify: `Core/Index/VectorIndex.cs:294-297`（维数不等 → 跳过 + Warning，不抛）
- Modify: `Core/Models/EmbeddingResult.cs`（加 `ModelId`）
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Index/VectorIndexTests.cs`、`Embedding/SqliteEmbeddingCacheTests.cs`

- [ ] **Step 1-4: TDD 循环**

```csharp
[Fact] public void CosineSimilarity_DimensionMismatch_ShouldSkipNotThrow() { }
[Fact] public async Task Cache_SameTextDifferentModel_ShouldNotHit() { }
```

- [ ] **Step 5: Commit**

---

## 集成构建（Wave 1 收口，唯一属主）

### Task I-1: 集成构建与逐轨修复

**Files:** 全部（但由 **R** 轨道属主统一执行，其他轨道只提供诊断信息）

- [ ] **Step 1:** `dotnet build Seeing.Agent.slnx`
- [ ] **Step 2:** 按编译错误归属轨道，**通知对应子代理修自己的文件**；属主不代改
- [ ] **Step 3:** 构建通过后跑全部测试：`dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`（其余测试项目同理）
- [ ] **Step 4:** Commit

---

## 审查关卡 2（Wave 1 结束，唯一的实现审查）

**只审一次，审这三件事**（不做逐任务审查）：

1. **分层与反模式**：跑 spec §11.1 的全部 `rg` 复核命令；重点确认无能力包引用 Core、无 `ILlmClient` 残留、无 `chat.*` 残留。
2. **Review Focus 五条是否都有测试兜住**：逐条对照 §Review Focus，缺哪条补哪条。
3. **跨轨接口一致性**：`LlmCapabilityMap` 的五个方法与实际调用点是否一一对应（尤其 `ToConfigKey` / `ToHookValue` 无手拼字符串）。

审查不通过则回到对应轨道修复，通过后进入 Wave 2。

---

# Wave 2：WebUI（单轨，依赖 Wave 1 的接口）

### Task W-1: 默认模型按能力分组

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Settings.razor:97,345`
- Modify: `samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor:33,230,638,796-811,1145`
- Test: 手工验证（WebUI 无自动化测试基建）

- [ ] **Step 1:** 设置页单值下拉 → 按能力分组多下拉，`AllowClear` 留空 = 未配置
- [ ] **Step 2:** 模型页顶部提示改为按能力列出；"设为默认"改为可选能力；显示 `ValidateDefaultModels()` 失效告警
- [ ] **Step 3:** `CloneProviderConfig` 加 `capabilities` / `listModels`
- [ ] **Step 4:** 手工验证 §11.1 的 WebUI 相关条目
- [ ] **Step 5:** Commit

### Task W-2: 能力多选框与记忆设置卡

**Files:**
- Modify: Provider 编辑表单（`ModelsPage.razor` 内）
- Modify: `samples/Seeing.Agent.WebUI/Components/Settings/MemorySettingsCard.razor`（四处 + 占位文案）

- [ ] **Step 1:** Provider 表单加能力多选框（8 项）
- [ ] **Step 2:** 模型页展示「可路由能力」= `Provider.Capabilities ∩ FromModelType(Types)`，不一致给告警（不阻止保存）
- [ ] **Step 3:** 记忆设置卡：`ResolveEmbeddingKey` / `OnEmbeddingKeyChanged` 改成只写 `ModelId`；删"当前: provider/model"行；`Dimensions` 只显示不落盘；占位文案改"空则不启用向量检索"
- [ ] **Step 4:** 手工验证
- [ ] **Step 5:** Commit

---

# Wave 3：收尾

### Task Z-1: 架构文档同步与全量复核

**Files:**
- Modify: `docs/architecture/02-modules.md:65`、`README.md:241-246`、`06-compliance-audit.md:188-199`
- Create: `docs/architecture/28-llm-capability-abstraction-release-notes.md`

- [ ] **Step 1:** 更新 hook 表（`chat.*` → `llm.*`）与模块 id（`systemone` → `llm.systemone`）
- [ ] **Step 2:** `06` 的纯函数豁免清单加 `LlmCapabilityMap` / `ProviderCapabilityDefaults`
- [ ] **Step 3:** 写 release notes：破坏性清单（照抄 spec §9.5）、迁移说明（配置三处变更）、已知边界
- [ ] **Step 4:** 跑 spec §11.1 全部复核命令 + 全量测试
- [ ] **Step 5:** Commit

### Task Z-2: 手工验收清单

逐条执行 spec §11.1 的手工项，重点是：

- [ ] 默认 `memory.json` → 向量索引不启动、`IsAvailable == false`
- [ ] `Embedding.Enabled=true` 但 `ModelId` 空 → 仍不启用
- [ ] `Providers.typesafe` 不写 `capabilities` → 无 jev 模型、`Resolve<IDecisionClient>` 明确报错
- [ ] Provider 声明 `embedding` 但工厂未实现 → Router 第 5 层报错而非静默 null
- [ ] 模型只标 `Speech` 不填 `Modalities` → 抛"声明不足"而非 400
- [ ] 配置页保存 provider → `capabilities`/`listModels` 未丢失
- [ ] 插件注册自定义适配器 → 覆盖内置生效
- [ ] `DefaultModels` 写裸 ID → 报错
- [ ] embedding 模型设为 embedding 默认成功、设为 chat 默认被拒
- [ ] 删模型后 `ValidateDefaultModels` 报失效项且配置未被自动清除

---

## 审查关卡 3（唯一终审）

**只审一次**：全量测试 + 手工验收清单 + 破坏性变更清单是否与 spec §9.5 逐条对齐。

---

## Spec 章节 → 任务映射（自检用）

| Spec 章节 | 覆盖任务 |
|---|---|
| §7.1 协议 vs 能力 | C-1（枚举/缺省）、C-3（`ProviderConfig.Capabilities`） |
| §7.2 能力端口与 DTO | C-2 |
| §7.2.1 映射表三张 | C-1 |
| §7.3 Provider 与工厂 | C-3（契约）、R-3（`ConfiguredLlmProvider`）、O-4 / A-1 / A-2 / S-2（各工厂） |
| §7.4 Router 五层校验 | R-1 |
| §7.5 横切层 `ILlmInvoker` | C-3（契约）、R-2（实现）、H-2（载荷适配器） |
| §7.6 Hook 泛化 `llm.*` | H-1、H-2、H-3 |
| §7.6.1 计费范围 | H-3 |
| §7.7 装饰器泛型化 | D-1 |
| §7.8 Embedding 落地 | E-1、E-2、O-1 |
| §7.8.1 禁止隐式回落 | E-1（含 `Enabled` 默认 false、不读 `DefaultModels.embedding`） |
| §7.9 Decision 合流 | S-1、S-2、S-3 |
| §7.10 Speech 落地 | O-2、O-3 |
| §7.11 Rerank/Image/Video 只出契约 | C-2（契约）、W-2（能力多选框含这三项） |
| §7.12 `ModelType` 治理 | C-1 |
| §7.13 ProviderInfo、能力 UI、连接测试 | C-3（`ProviderInfo`）、W-2（能力 UI）、R-1（连接测试能力参数） |
| §7.14 按能力默认模型 | M-1（契约+实现）、M-2（消费方）、W-1（UI） |
| §9 影响面清单 | 文件所有权矩阵 |
| §10 测试计划 | 各任务的 Step 1 + I-1 + Z-1 |
| §11 实施顺序 | 本计划的 Wave 结构 |
| §12 留待实现者决定 | 全计划各任务按决议锁定，§12 七条属实现细节 |

---

## 并行度总结

| Wave | 并行轨道数 | 串行依赖 |
|---|---|---|
| 0 | 1（3 个任务串行） | 无 |
| 1 | **8**（R / D / O / A / H / S / M / E） | 全部依赖 Wave 0 契约 |
| 2 | 1（2 个任务） | 依赖 Wave 1 集成通过 |
| 3 | 1 | 依赖 Wave 2 |

**审查次数：3 次**（关卡 1 / 2 / 3），无逐任务审查。

**子代理调度建议**：
- Wave 0：1 个实现子代理（`qwen3.8-flash`，串行做完 C-1→C-3）。
- **审查关卡 1**：1 个审查子代理（`deepseek-v4.1-flash`，全新会话）。
- Wave 1：**8 个实现子代理同时启动**（全部 `qwen3.8-flash`），每个带完整的「文件所有权矩阵」行 + 自己轨道的任务清单 + Global Constraints。
- Wave 1 收口：1 个实现子代理（`qwen3.8-flash`）做 Task I-1 集成构建并按错误归属分派修复。
- **审查关卡 2**：1 个审查子代理（`deepseek-v4.1-flash`，全新会话）。
- Wave 2：1 个实现子代理（`qwen3.8-flash`，W-1→W-2 串行）。
- Wave 3：1 个实现子代理（`qwen3.8-flash`，Z-1→Z-2）。
- **审查关卡 3**：1 个审查子代理（`deepseek-v4.1-flash`，全新会话）。

**审查子代理的输入契约（每次一致）**：计划文件路径 + spec 文件路径 + 本次 Wave 的代码变更范围 + 该关卡的检查清单。**不给它实现过程、不给实现子代理的自我总结**——独立判断，避免被实现方的叙述带偏。

**审查不通过的处理**：审查子代理输出问题清单 → 原实现子代理（同 sessionID 续聊）修复 → 再审一次。**同一关卡最多再审一轮**，仍不过则升级到人工决策，不由子代理自行反复循环。

**风险最高的三处**（子代理须格外小心）：
1. Task D-1 泛型装饰器 —— 600–900 行机械改造，流式边界一旦写错就是数据损坏。
2. Task R-3 + R-6 —— 四处独立克隆清单，漏一处静默丢配置。
3. Task S-1 —— 包重命名牵连 `slnx`、能力集、守门测试三处，漏一处构建或守门失败。
