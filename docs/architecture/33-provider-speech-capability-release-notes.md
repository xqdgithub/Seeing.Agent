# Provider 语音能力接入 Release Notes

**日期：** 2026-11-06
**范围：** `plugs/providers`（SiliconFlow / MiMo）、`src/support/Seeing.Agent.Llm.Support`、
`src/spine/Seeing.Agent.Core/Llm`、`samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor`
**设计：** [2026-11-06-provider-speech-capability-design.md](../superpowers/specs/2026-11-06-provider-speech-capability-design.md)

---

## 1. 背景

此前 `ISpeechTranscriptionClient` 契约、OpenAI 兼容实现、重试装饰器、Hook、用量统计全部就位，
但**没有任何 Provider 声明 `speechTranscription`**，也没有任何调用方。结果是能力存在却不可达：
模型页看不到语音能力，Router 第 4 层直接拒。

本次补齐**插件侧与配置侧**；WebUI 录音 UI 由另一任务交付
（见 `docs/superpowers/specs/2026-11-06-webui-voice-input-design.md`）。

## 2. 两个 Provider 的协议差异（实测）

用真实 API Key 验证：

| | SiliconFlow | MiMo |
|---|---|---|
| 端点 | `POST /v1/audio/transcriptions`（multipart） | `POST /v1/chat/completions`（JSON + base64） |
| 模型 | `FunAudioLLM/SenseVoiceSmall`、`TeleAI/TeleSpeechASR`、`XingChenAGI/XingChenASR-V3.2(-Ultra)` | `mimo-v2.5-asr` |
| 响应 | `{text, language?, segments?, usage}` | `{choices[0].message.content, usage}` |
| 音频格式 | 常见格式 | 仅 mp3 / wav |
| 能否复用 OpenAI 客户端 | **能，零改动** | **不能** |

SiliconFlow 实测结果（真实中文语音）：

```
FunAudioLLM/SenseVoiceSmall  → {"text":"今天天气很好，我们一起去公园散步吧。","language":"Chinese","usage":{...}}
TeleAI/TeleSpeechASR         → {"text":"...","segments":[{"start":0.0,"end":4.44,...}],"usage":{...}}
```

## 3. 改动清单

### 3.1 SiliconFlow 声明语音转写

`SiliconFlowProvider.DefaultCapabilities`：

```csharp
LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding | LlmCapabilityKind.SpeechTranscription
```

**不声明 `speechSynthesis`**：端点虽有 `/audio/speech`，但当前无消费方，
声明了只是制造一个「可路由但没人用」的假标记。

### 3.2 MiMo 专有语音转写客户端

新增 `plugs/providers/Seeing.Provider.MiMo/MiMoSpeechTranscriptionClient.cs`：

- 请求：`messages[].content[].input_audio` + `data:<mime>;base64,...`
- 响应：取 `choices[0].message.content`
- `asr_options.language` 只透传 `zh`/`en`，其余回落 `auto`（官方仅支持 auto/zh/en）
- 非 mp3/wav 在**发请求前**抛 `NotSupportedException`（不静默改扩展名，否则服务端 400 更难懂）
- 流式：解析 SSE 的 `choices[].delta.content`

`MiMoProvider.CreateClient` 对 `SpeechTranscription` 分支自建客户端，其余仍走工厂；
自建路径**显式调用 `ILlmClientDecoratorRegistry.Apply`**，否则静默失去重试。

### 3.3 代理统一上提（顺带修复）

原先 `LlmHttpClientFactory` 是 `Seeing.Agent.Llm.OpenAI` 包内的 `internal`，
`Seeing.Agent.Llm.Anthropic` 有一份**逐行重复**的副本。第三个消费者（MiMo）出现时必须收敛。

新增 `src/support/Seeing.Agent.Llm.Support/Http/LlmHttpClientFactory.cs`（public），
两个协议包改为转发壳——**现有调用点与既有代理测试零改动**。

代理语义（由测试锁定）：

| 配置 | 行为 |
|---|---|
| `UseProxy=true`（默认）+ `Proxy=null` | 使用操作系统默认代理 |
| `UseProxy=true` + 显式 `Proxy` | 覆盖系统代理；userinfo 拆成 `NetworkCredential`，不留在 URL |
| `UseProxy=false` | 完全不走代理，即使 `Proxy` 有值 |
| `socks5://...` | 抛 `ArgumentException`（**不静默直连**） |

### 3.4 探针时长 20ms → 200ms（修复既有缺陷）

`SilentWavProbe` 上提到 Support 层共用，并把时长改为 **200ms**。

原因：实测 SiliconFlow 对 20ms 音频返回 **HTTP 500**，200ms 起正常。
原实现让「端点其实可用」被误判成连接失败：

```
duration=20ms   => HTTP 500
duration=200ms  => HTTP 200
duration=500ms  => HTTP 200
duration=1000ms => HTTP 200
```

### 3.5 模型目录刷新补 `Modalities`

新增 `src/support/Seeing.Agent.Llm.Support/Speech/ModalityInference.cs`。

问题：远端 `/models` 不返回模态，而 Router 第 3 层要求语音模型
`modalities.input`（转写）或 `modalities.output`（合成）明确含 `audio`，缺了就拒。
不补的话，刷新出来的 `SenseVoiceSmall` 永远无法设为语音转写默认模型。

推断规则（**只在能确定方向时填**）：

| 条件 | 推断结果 |
|---|---|
| `Speech` + 名字含 `asr`/`transcri`/`whisper`/`sensevoice`/`stt` | `input:[audio]`, `output:[text]` |
| `Speech` + 名字含 `tts`/`cosyvoice` | `input:[text]`, `output:[audio]` |
| `Speech` 但看不出方向 | **不填**（猜错会让合成模型被当转写模型路由通过） |
| `Embedding` / `Rerank` / `ImageGeneration` / `Video` | 对应模态 |
| 纯文本 | 不填 |

`ModelTypeInference` 同时补上 `asr` / `-stt` 关键词，否则 `mimo-v2.5-asr` 推不出 `Speech`。

### 3.6 MiMo 模型目录补 `Types`

`MiMoModelsClient` 此前完全不填 `Types`，刷新出来的 `mimo-v2.5-asr` 隐式是 `Text`，
Router 第 2 层与 `CanSetAsDefaultModel` 都会拒。现与 SiliconFlow 一致：
`Types = ModelTypeInference.InferTypes(id) ?? []`，再经 `ModalityInference.Apply`。

### 3.7 设置默认模型时前置校验语音方向

`ModelConfigManager.ModelCoversCapability` 此前只查 `Types`，不查 `Modalities`，
允许把「标了 Speech 但没写 audio」的模型设成默认，运行时才炸（Router 第 3 层）。

现追加 `CoversSpeechModality`：转写看 `modalities.input`、合成看 `modalities.output`，
与 Router 第 3 层同口径。非语音能力不受影响。

**只作用于「设为默认」的判定**；已失效的默认值仍走 `ValidateDefaultModels` 的
「只报告不清除」语义（决议 23），不自动清空。

### 3.8 模型编辑补「输出模态」

`ModelsPage.razor` 编辑弹窗此前只有输入模态，TTS 无法配置。
后端零改动——`ModelOverride.OutputModalities` 与 `ModelOverrideApplier` 早已存在。

## 4. 上场前需要做的配置

1. 模型页 → SiliconFlow/MiMo 卡片确认「生效能力」含语音转写；
2. 刷新模型（让 `Modalities` 被推断补上）；
3. 设置页 → 默认模型（按能力）→ **语音转写** 选一个模型，如
   `siliconflow/TeleAI/TeleSpeechASR` 或 `mimo/mimo-v2.5-asr`；
4. 模型页「测试连接」选语音转写验证。

> 若模型被判定为「不具备该能力」，去编辑弹窗手动补 `输入模态 = audio`。

## 4b. SiliconFlow 语音模型选型（实测）

用 Windows TTS 造 4 个样本（纯中文 / 代码术语 / 中英混说 / 数字金额）真打端点，
每个模型预热后跑 3 次取中位延迟：

| 模型 | 延迟(中位) | `verbose_json` | 价格 | 结论 |
|---|---|---|---|---|
| `TeleAI/TeleSpeechASR` | 1.18s | ✅ | 免费 | **推荐默认** |
| `XingChenAGI/XingChenASR-V3.2` | **1.06s** | ✅ | 免费 | 最快，表现同上 |
| `XingChenAGI/XingChenASR-V3.2-Ultra` | 1.91s | ✅ | 免费 | 中英混说最准 |
| `XingChenAGI/XingChenGSR-V1.0` | 1.50s | ✅ | 免费 | 数字/书面语最准，但**会改写原话** |
| `FunAudioLLM/SenseVoiceSmall` | 1.21s | ✅ | 免费 | 中英混说会掉字（`API` → `AP`） |
| `Qwen/Qwen3-ASR-1.7B` | — | ❌ **HTTP 400** | ¥0.00022/秒 | **不可用**，见下 |
| `XingChenAGI/XingChenASR-Diarize-V3.0` | 0.86s | ✅ | 免费 | 单人音频会被误拆成两个说话人，不适配语音输入 |

不是语音识别的两个（图里也打「语音」标签，但是 TTS）：
`fnlp/MOSS-TTSD-v0.5`、`FunAudioLLM/CosyVoice2-0.5B`。

**`Qwen3-ASR` 不可用是有意的边界**：它只接受 `response_format=json` 或 `text`，
传 `verbose_json` 直接 **HTTP 400**。我们的 `OpenAiSpeechTranscriptionClient` 是
**标准 OpenAI 协议实现**，固定发 `verbose_json` 以便拿到 `segments`。
为单一厂商的私有取值在协议层加回退会污染协议实现，故**不做兼容**——
要用这个模型需在 Provider 侧另写客户端（与 MiMo 同路数）。

**中英混说实测输出**（原句：`这个 API 的 timeout 设成 30 秒，如果 retry 三次还是失败就报警`）：

| 模型 | 输出 |
|---|---|
| Ultra | `这个API的time out设成30秒，如果retry三次还是失败，就报警。` |
| TeleSpeechASR | `这个api的time out设成30秒，如果retry3次还是失败就报警。` |
| SenseVoiceSmall | `这个AP的time out设成30秒...`（掉了 I） |
| GSR-V1.0 | `将这个API的时间限制设为三十秒，若三次尝试仍失败则触发报警。`（**改写了**） |

## 5. 测试

| 项目 | 用例 | 覆盖 |
|---|---|---|
| `Seeing.Provider.MiMo.Tests` | `MiMoSpeechTranscriptionClientTests`（10） | 请求形状、base64 data URL、语言透传/回落、格式拒绝、不释放调用方流、错误体透出、探针、非语音能力 |
| `Seeing.Agent.Tests` | `ModalityInferenceTests`（15） | 转写/合成方向、不猜、既有声明优先 |
| `Seeing.Agent.Tests` | `SupportLlmHttpClientFactoryTests`（7） | 系统代理、显式代理、凭据剥离、关闭代理、socks 拒绝、转发壳一致性 |
| `Seeing.Agent.Tests` | `ModelConfigManagerDefaultModelTests`（+2） | 语音方向性前置校验 |
| `Seeing.Provider.SiliconFlow.Tests` | `DefaultCapabilities_ShouldIncludeSpeechTranscription...` | 声明转写但不声明合成 |
| `Seeing.Agent.Llm.OpenAI.Tests` | `SiliconFlowSpeechTranscriptionLiveTests`（2） | **真实端点**：连接测试 + 响应解析（需 `SEEING_SILICONFLOW_KEY`，无则跳过） |
| `Seeing.Agent.Llm.OpenAI.Tests` | `SiliconFlowSpeechStreamingLiveTests`（2） | **真实端点**：锁定「SiliconFlow 不支持 SSE，流式产出空序列」 |
| `Seeing.Provider.MiMo.Tests` | `MiMoSpeechTranscriptionClientTests` 的流式用例（2） | SSE delta 解析 + 坏块跳过 |

回归：`Seeing.Agent.Tests` 1625、OpenAI 62、Anthropic 26、MiMo 31、SiliconFlow 6、
DeepSeek 25、TypeSafeApi 18、OpenCodeZen 46、WebUI 222，全绿；
`solution` 构建 0 错误。

## 6. 破坏性变更

| 变更 | 影响 |
|---|---|
| `LlmHttpClientFactory` 从 OpenAI/Anthropic 包内 `internal` 上提为 Support 层 `public` | 包内旧类保留为转发壳，源码级无破坏；新增的是 Support 包对 OpenAI/Anthropic 的项目引用 |
| `SilentWavProbe.DurationMs` = 200 | 连接测试流量从 20ms 音频变为 200ms，可忽略 |
| `MiMoProvider` 构造函数新增可选参数 `ILlmClientDecoratorRegistry?` | 可选参数，既有 `new` 调用点不破 |
| MiMo 默认能力 `Chat` → `Chat \| SpeechTranscription` | 模型页会显示语音转写；旧配置写了 `capabilities` 的仍以配置为准 |

## 7. 已知边界

- **只做转写（ASR），不做合成（TTS）**。`OpenAiSpeechSynthesisClient` 已存在，
  SiliconFlow 也有 `CosyVoice2-0.5B`，但无消费方，故未声明。接 TTS 时改 `DefaultCapabilities` 即可。
- **MiMo 流式转写未经真实端点验证**。实现按官方 SSE delta 形状编写，
  但本次只在非流式路径做了真调用（连接测试与转写解析）。UI 任务采用整段转写，不消费流式。
- **SiliconFlow 不支持流式转写（实测结论）**。请求带 `stream=true` 时它**忽略该参数**，
  返回普通 JSON（`Content-Type: application/json` + `Content-Length`），不是 SSE。
  因此 `TranscribeStreamAsync` 对 SiliconFlow 会**静默产出空序列**——
  它不报错，只是永远没有增量。已用 `SiliconFlowSpeechStreamingLiveTests` 锁定该行为：
  若该测试将来失败，说明厂商开始支持 SSE，可考虑启用实时字幕。
  **消费方因此必须用整段转写 `TranscribeAsync`，不要依赖流式出字。**
- **`SenseVoiceSmall` 不返回 `segments`**，`TranscriptionResult.Segments` 为 `null`。
  需要段落级时间轴时选 `TeleAI/TeleSpeechASR` 或 `XingChenAGI/XingChenASR-*`。
- **DeepSeek / OpenCodeZen / TypeSafe 未声明语音能力**：官方端点确实没有 ASR，不虚报。
- **`Qwen/Qwen3-ASR-1.7B` 当前不可用**：它拒绝 `response_format=verbose_json`（HTTP 400），
  而标准 OpenAI 协议实现固定发该取值。**刻意不在协议层加厂商回退**——
  要用它需在 Provider 侧写专有客户端。详见 §4b。
- **WebUI 仍无录音入口**：本期只让能力「可路由」，真正的说话输入由另一任务交付。
