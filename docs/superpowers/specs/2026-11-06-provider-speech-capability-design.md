# Provider 语音能力接入（插件侧 + 配置侧）

- 日期：2026-11-06
- 状态：待批准
- 范围：`plugs/providers/Seeing.Provider.SiliconFlow`、`plugs/providers/Seeing.Provider.MiMo`、
  `src/support/Seeing.Agent.Llm.Support`、`src/spine/Seeing.Agent.Core/Llm`、
  `samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor`
- 不在范围：WebUI 语音输入 UI（录音按钮 / 转写端点 / `PromptInput`），见
  `2026-11-06-webui-voice-input-design.md`，由另一任务交付

## 1. 实测事实（本文档的前提，均为真调用验证）

用本机 `.seeing/providers/siliconflow.json` 的 key 实打：

| 项 | SiliconFlow | MiMo |
|---|---|---|
| ASR 端点 | `POST /v1/audio/transcriptions`（multipart） | `POST /v1/chat/completions`（JSON + base64） |
| 可用模型 | `FunAudioLLM/SenseVoiceSmall`、`TeleAI/TeleSpeechASR`、`XingChenAGI/XingChenASR-V3.2`、`XingChenAGI/XingChenASR-V3.2-Ultra` | `mimo-v2.5-asr`（单一） |
| `response_format=verbose_json` | 接受。`SenseVoiceSmall` 忽略它只回 `{text,language,usage}`；`TeleSpeechASR` / `XingChenASR-*` 回 `segments` | 不适用 |
| 真实中文语音 | 全部正确转写 | — |
| 与我们 `OpenAiSpeechTranscriptionClient` | **形状匹配，零改动** | **形状不匹配** |
| 音频输入 | 文件上传，任意常见格式 | 仅 `mp3` / `wav`，base64 data URL |

MiMo 的响应把文本放在 `choices[0].message.content`，音频走 `messages[].content[].input_audio`，
语种走顶层 `asr_options.language`（`auto`/`zh`/`en`）。这是一个**多模态 chat 模型**，不是转写端点。

旁证：`OpenAiChatClient.cs:360` 当前对 `input_audio` 是 `LogWarning` 后丢弃，所以连「借 chat 通路」
都走不通——这正是需要专有客户端的直接原因。

## 2. 目标 / 非目标

目标：

- SiliconFlow 声明 `speechTranscription`（作者默认能力），装完即可路由；
- MiMo 声明 `speechTranscription`，并配一个真正能跑的专有客户端；
- 模型自动刷新时补齐 `Modalities`，否则 Router 第 3 层必然拒（`LlmRouter.cs:146-153`）；
- 模型编辑弹窗能配**输出模态**（当前只有输入模态），否则 TTS 无法配置；
- 设置默认模型时提前暴露「模型缺 modalities」问题，不等到运行时才炸。

非目标：

- 不加 `speechSynthesis`（TTS）。虽然 `OpenAiSpeechSynthesisClient` 已存在且 SiliconFlow 有
  `CosyVoice2-0.5B`，但本任务是「语音输入」，TTS 无消费方，声明了只是占位。
- 不改 `ISpeechTranscriptionClient` 契约（沿用 `TranscriptionRequest` / `TranscriptionResult`）。
- 不改 chat 客户端的 `input_audio` 支持（方案 C 已排除，见 §4）。
- 不做 WebUI 录音 UI。

## 3. 约束

- **能力包只依赖 Abstractions + support**：MiMo 专有客户端若引用 `Seeing.Agent.Llm.OpenAI` 的
  `LlmHttpClientFactory`（internal）就越界了。必须自建 HttpClient 或用 Abstractions 里已有的东西。
- **装饰器唯一应用点**：`ILlmClientDecoratorRegistry.Apply` 只在工厂 `Create` 返回前调用。
  MiMo 自建客户端时必须自己调一次，否则静默失去重试。
- **`TestConnectionAsync` 必须真探针**：决议 25 要求真实极短音频，不接受「构造成功即通过」。
  MiMo 只收 mp3/wav，现有 OpenAI 客户端的 8000Hz/20ms WAV 探针正好合规。
- **音频流所有权**：`TranscriptionRequest.Audio` 归调用方，实现只读不 Dispose（决议 17）。
  MiMo 走 base64，需要先整体读进内存，但仍不得 Dispose 调用方的流。

## 4. 方案与取舍

| | 做法 | 取舍 |
|---|---|---|
| A | 只声明 SiliconFlow，MiMo 不声明 | 最小；但 MiMo 有 ASR 却不可用，且用户配置里写了 `speechTranscription` 会得到误导性的第 5 层报错 |
| B | SiliconFlow 零改动声明 + MiMo 专有客户端（**已选**） | 两家都能真跑；代价是 MiMo 多一个客户端类与其测试 |
| C | 改造 chat 客户端支持 `input_audio`，让 MiMo 走 chat 能力 | 会把 ASR 伪装成 chat，`DefaultModels.speechTranscription` 语义崩塌；且要动共享 chat 出站路径，影响面大 |

选 B。A 的「不可用但不报错」比 B 的多一个类更坏——用户会以为配好了。

## 5. 设计

### 5.0 实现期发现的偏差（已按实测修正）

1. **探针时长必须是 200ms，不是 20ms**。实测 SiliconFlow 对 20ms 音频直接返回 HTTP 500
   （200ms/500ms/1000ms 均 200）。原 OpenAI 客户端的 20ms 探针会让「端点其实可用」被误判成连接失败——
   这是既有缺陷，真实联调才暴露。`SilentWavProbe.DurationMs = 200`。

2. **代理必须上提到 Support 层**。原 `LlmHttpClientFactory` 是 OpenAI 包内的 `internal`，
   且 Anthropic 包有一份逐行重复的副本。MiMo 插件要支持代理只能再抄第三份。
   现上提到 `src/support/Seeing.Agent.Llm.Support/Http/LlmHttpClientFactory.cs`，
   两个协议包改为转发壳（调用点与既有测试零改动），MiMo 直接使用。

3. **`Segments` 的可用性比预期好**。`TeleAI/TeleSpeechASR` 与 `XingChenAGI/XingChenASR-*`
   都返回 `segments`，只有 `FunAudioLLM/SenseVoiceSmall` 不返回。三者都能被现有客户端解析。

### 5.1 SiliconFlow：只改一行

`SiliconFlowProvider.DefaultCapabilities` 追加 `LlmCapabilityKind.SpeechTranscription`：

```csharp
protected override LlmCapabilityKind DefaultCapabilities =>
    LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding | LlmCapabilityKind.SpeechTranscription;
```

不声明 `speechSynthesis`（非目标）。`CreateClient` 已走 `CreateClientViaFactory`，无需改动。

### 5.2 MiMo：专有客户端

新增 `plugs/providers/Seeing.Provider.MiMo/MiMoSpeechTranscriptionClient.cs`，
实现 `ISpeechTranscriptionClient`：

- `TranscribeAsync`：读流 → base64 → `POST {base}/chat/completions`，body：
  ```json
  { "model": "<wire id>", "messages": [{ "role": "user", "content": [
      { "type": "input_audio", "input_audio": { "data": "data:<mime>;base64,<b64>" } } ] }],
    "asr_options": { "language": "auto" } }
  ```
  解析 `choices[0].message.content` → `TranscriptionResult(text, segments: null, usage)`。
  `usage` 映射 `prompt_tokens` / `completion_tokens`；`seconds` 不属 `TokenUsage`，丢弃。
- `TranscribeStreamAsync`：MiMo 支持 `stream: true`（SSE delta 在 `choices[].delta.content`），
  按行解析产出 `TranscriptSegment(Zero, Zero, delta)`。**空流不抛**（与 OpenAI 客户端一致）。
- `TestConnectionAsync`：仅在 `capability.HasFlag(SpeechTranscription)` 时执行；
  探针用 20ms/8kHz/16bit 单声道静音 WAV（复用 OpenAI 客户端的构造逻辑，**注意不能直接引用它
  ——internal 且跨包**，所以在 MiMo 包内自带一份，或上提到 support 层由两家共用）。
- 音频格式门闸：MiMo 只收 mp3/wav。若 `MimeType` 不在白名单，返回前抛
  `NotSupportedException`（可读原因），**不静默改扩展名**——那是制造 400。

`MiMoProvider.CreateClient` 对 `SpeechTranscription` 分支自建客户端，其余仍走工厂：

```csharp
protected override ILlmCapabilityClient? CreateClient(ProviderConfig config, LlmCapabilityKind capability)
    => capability == LlmCapabilityKind.SpeechTranscription
        ? CreateTranscriptionClient(config)
        : CreateClientViaFactory(config, capability);
```

`CreateTranscriptionClient` 内部构造 `MiMoSpeechTranscriptionClient`，再经
`ILlmClientDecoratorRegistry.Apply` 包装（构造报错/未注册时为 null 则原样返回）。

`DefaultCapabilities` 追加 `SpeechTranscription`。

### 5.3 静音 WAV 探针上提到 Support

当前 `BuildSilentWavBytes` 是 `OpenAiSpeechTranscriptionClient` 的 private 方法。MiMo 也要用。
在 `src/support/Seeing.Agent.Llm.Support/Speech/SilentWavProbe.cs` 新增公共实现，
两家共用一份；OpenAI 客户端改为调用它（消除重复，不改行为）。

### 5.4 配置侧：自动刷新补 `Modalities`

问题：`SiliconFlowModelsClient` 只推 `Types`，`Modalities` 保持空 → Router 第 3 层拒。
MiMo 的 `MiMoModelsClient` 更彻底，连 `Types` 都不推。

改法：在 `ModelTypeInference` 旁边新增纯函数 `ModalityInference`（support 层），
按模型 ID 强约定与已推断出的 `ModelType` 给出模态；两个 ModelsClient 调用它填充。

```
ModelType.Speech + 名字含 asr/transcribe/whisper/sensevoice → input:[audio], output:[text]
ModelType.Speech + 名字含 tts/voice/cosyvoice/speech     → input:[text], output:[audio]
ModelType.Embedding                                      → input:[text], output:[embedding]
ModelType.ImageGeneration                                → input:[text], output:[image]
ModelType.Text（或无强约定）                              → 不动（保持空，由内建目录/用户覆盖补）
```

要点：**只在能确定方向时填**，Speech 双向歧义（合成 vs 转写）时按名字判定，判不出就不填——
与 Router「不猜」的立场一致（否则猜错会让合成模型被当转写模型路由通过）。

### 5.5 配置侧：模型编辑补输出模态

`ModelsPage.razor` 编辑弹窗当前只有「输入模态」(`_editInputModalities`)。补「输出模态」：

- `_editOutputModalities` 字段 + `OnEditOutputModalitiesChanged`
- `ModelOverride.OutputModalities` 契约已存在，`ModelOverrideApplier` 已应用，**后端零改动**
- `ShowEditModel` 读 `config.Modalities?.Output`，`SaveEditModelAsync` 写 `OutputModalities`

### 5.6 配置侧：默认模型前置校验

现状：`CanSetAsDefaultModel` 只查 `Types`（`ModelConfigManager.cs:249-256`），
不查 `Modalities` → 允许把缺 `audio` 的 Speech 模型设为 `speechTranscription` 默认，运行时才炸。

改为复用 Router 的第 2/3 层判定（不复制规则）：
`ModelCoversCapability` 对 `SpeechSynthesis` / `SpeechTranscription` 追加模态检查。
失败时 `ArgumentException` 的文案直接说明「模型标为 Speech 但未在 modalities.input 写明 audio」。

**只影响 UI 的「设为默认」下拉与保存路径**；已配置的失效默认值仍走
`ValidateDefaultModels` 的「只报告不清除」语义（决议 23），不自动清空。

### 5.7 配置侧：MiMo 的模型目录补 `Types`

`MiMoModelsClient` 目前完全不填 `Types`，刷新出来的 `mimo-v2.5-asr` 是隐式 `Text`，
`CanSetAsDefaultModel` 与 Router 第 2 层都会拒。改为与 SiliconFlow 一致：
`Types = ModelTypeInference.InferTypes(id) ?? []`，再由 §5.4 的 `ModalityInference` 补模态。

前提：`ModelTypeInference` 的 Speech 关键词（`whisper`/`-tts`/`tts-`/`speech`/`cosyvoice`/`sensevoice`）
需补 `asr`，否则 `mimo-v2.5-asr` 推断不出来。

## 6. 手工验收

1. 重启 WebUI → 模型页 → SiliconFlow：生效能力块出现「语音转写」。
2. SiliconFlow 刷新模型 → `FunAudioLLM/SenseVoiceSmall` 的「类型」显示 `Speech`、
   输入模态含 `audio` → 可设为 `speechTranscription` 默认。
3. 设置页 → 默认模型 → 语音转写 → 选 `siliconflow/FunAudioLLM/SenseVoiceSmall`。
4. 连接测试（模型页「测试连接」选语音转写）→ 成功。
5. MiMo 同样流程：刷新后 `mimo-v2.5-asr` 类型为 `Speech`、输入模态 `audio`；
   设为默认后可解析客户端。
6. 把 SiliconFlow 的 ASR 模型手动改成「输入模态为空」→ 设为默认被拒，错误文案含
   `modalities.input`。
7. 编辑模型弹窗能看到并能保存「输出模态」。

## 7. 留给实现者的决定

- `SilentWavProbe` 放 `src/support/Seeing.Agent.Llm.Support/Speech/` 还是与 `Inference/` 同级
  的 `Speech/` 子目录——建议后者，与 `Inference/` 并列，语义都是「启发式/工具性实现」。
- `ModalityInference` 的返回是否复用 `ModelModalities`（Abstractions 类型，support 已引用）——
  建议是，避免再造一个形状。
- MiMo 客户端的 HttpClient 用 `new HttpClient()` 自建（`MiMoModelsClient` 已是这个模式、
  自行管理生命周期）还是在插件 DI 里注册单例——建议自建 + `Dispose`，与同包既有类保持一致。
- `asr_options.language` 是否透传 `TranscriptionRequest.Language`（`zh`/`en` → 直传，
  其他值回落 `auto`）——建议透传，未知值回落 `auto` 而不报错。
