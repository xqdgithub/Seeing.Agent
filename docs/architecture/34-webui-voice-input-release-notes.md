# WebUI 语音输入 Release Notes

- 日期：2026-11-06
- 分支：`feat/webui-voice-input`
- 设计规格：`docs/superpowers/specs/2026-11-06-webui-voice-input-design.md`
- 实现计划：`docs/superpowers/plans/2026-11-06-webui-voice-input.md`

## 1. 背景

后端 ASR 能力早已完整（`ISpeechTranscriptionClient` 契约、OpenAI 兼容实现、
重试装饰器、`llm.*` Hook、用量统计、`DefaultModels.speechTranscription` 配置位），
但**整个 WebUI 没有任何调用方**——`CapabilityLabels` 里只有一句「语音转写」的展示文案。

本次不是「实现语音转写」，而是**给已有能力接一个 UI 入口**：
浏览器录音 → 服务端转写 → 文本落进输入框。

## 2. 分层

```
PromptInput.razor（按钮 + 四态机 + 光标插入）
  ↓ JSRuntime
voice-input.js（getUserMedia + MediaRecorder + fetch）
  ↓ POST /api/transcriptions（multipart）
TranscriptionEndpoints（校验 / 内存缓冲 / 响应映射）
  ↓
SpeechTranscriptionService（解析默认模型 → 调横切层）
  ↓ ILlmInvoker
ISpeechTranscriptionClient（既有 OpenAI 兼容实现）
```

重试、`llm.*` Hook、用量统计因为走 `ILlmInvoker` 而自动生效，
不需要在 WebUI 里重复实现任何 LLM 调用细节。

## 3. 关键决策

### 3.1 点击切换录音，不是按住说话

点一次开始，再点一次结束并转写。

按住说话在触屏上与滚动/滑动手势冲突，且长句子手要一直按着。
计划阶段两种说法都写过，最终定为点击切换；JS 只绑 `click`，不绑 `pointerdown`/`pointerup`。

### 3.2 灰态可点，不是 `Disabled`

未配置 `speechTranscription` 默认模型时按钮呈灰调，但**保持可点击**：

- 鼠标用户 hover 即可看到原因（tooltip）；
- 触屏用户点一下会就地展开「未配置语音转写模型」+「去模型页配置」按钮。

若真设 `Disabled`，`OnClick` 不触发、tooltip 在触屏上不可达，就退化成死图标。

### 3.3 组件直读配置，不设预检端点

`PromptInput` 注入 `IModelConfigManager`（`MainLayout`/`ModelsPage`/`ModelSelectBadge`
三处已有同样用法），初始化时调一次：

```csharp
_voiceReady = ModelManager.TryResolveDefault<ISpeechTranscriptionClient>(
    LlmCapabilityKind.SpeechTranscription, out _, out _voiceUnavailableReason);
```

用 `TryResolveDefault` 而不是 `GetDefaultModel(...) is null`：前者内部走 `ILlmRouter` 的
五层校验，能区分「没配」「模型已不在目录」「声明类型不含 Speech」「Provider 未声明能力」，
并把原因措辞直接复用。零出站请求、不产生 LLM 调用。

早期设计曾计划加一个 `GET /api/transcriptions/availability` 预检端点，
后取消——少一个端点、少一次请求，也没有「预检说能用、实际不能用」的漂移面。

订阅 `ModelManager.ModelConfigChanged`：用户在模型页配好默认模型后回到会话页，
按钮自动从灰变亮，**不需要刷新浏览器**。

### 3.4 `ModelReference.ToWireModelId` 放 `Tools.Support`

`DefaultModels` 存的是 `provider/model` 引用，而 OpenAI 端点要裸模型名。
仓库里 `Seeing.Agent.Memory` 已有同口径实现（`EmbeddingModelReference`），但它是 `internal`，
上提会牵动该包三个调用点与既有 embedding 测试。

因此**不改 Memory 包**，在 `src/support/Seeing.Agent.Tools.Support/Llm/ModelReference.cs`
新增公共 helper，WebUI 用它。代价是两份同构逻辑，收益是零回归面。

### 3.5 音频格式按 `blob.type` 派生，不硬编码 webm

`MediaRecorder.isTypeSupported` 依次探测
`audio/webm;codecs=opus` → `audio/webm` → `audio/mp4` → `audio/ogg;codecs=opus`，
filename 的扩展名与 MIME 都从实际选中的类型派生。

WKWebView（macOS 桌面宿主）不支持 webm，只能出 `audio/mp4`；写死 webm 会让桌面版在 Mac 上必然失败。

### 3.6 音频不落盘

端点把请求体读进 `MemoryStream`（必须可 seek——重试装饰器会 `Seek` 回起点），
转写完随方法结束释放。不进上传目录、不进会话历史，因此没有临时文件生命周期问题。

### 3.7 会话切换的双重兜底

- **录音中切会话**：`OnParametersSet` 的 SessionKey 变更分支调 `cancelVoiceRecording()`，
  置 `cancelled` 标并停止录音器，不上传。
- **转写中切会话**（上传已在途，无法取消）：进入 `Transcribing` 时快照 `_voiceBoundSessionKey`，
  回调里若与当前 `SessionKey` 不一致则**只清状态、不插入文本**。

## 4. 端点契约

```
POST /api/transcriptions?sessionId={id}
Header: X-Seeing-Voice: 1
Body: multipart/form-data, field "audio" = 录音 Blob
```

| 条件 | 响应 |
|---|---|
| 会话不存在 | `404` `{ error = "session not found" }` |
| 缺 `X-Seeing-Voice: 1` | `400` `{ error, code = "invalidRequest" }` |
| 非 multipart / 无 audio | `400` `{ error = "audio required", code = "invalidRequest" }` |
| 文件空或超 25MB | `400` `{ error = "invalid audio size", code = "invalidRequest" }` |
| 未配默认模型 | `503` `{ error = <可读原因>, code = "modelNotConfigured" }` |
| 转写失败 | `502` `{ error = <原始 message>, code = "transcriptionFailed" }` |
| 成功 | `200` `{ text, segments }` |

超过 25MB 的请求会被 Kestrel 的 `RequestSizeLimit` 在端点外直接 `413`，
前端对非 JSON 响应降级为「转写失败（HTTP 413）」，属预期行为。

`segments` 目前恒为 `[]`：`TranscribeAsync` 只回文本，端点手上没有分段。
接流式字幕时才需要填。

## 5. 已知边界

1. **macOS 桌面宿主未验证**：WKWebView 需要 Info.plist 的麦克风用途声明，
   且只能出 `audio/mp4`。格式协商已让前端不至于硬失败，但打包声明与真机验证不在本期。
2. **流式字幕未接线**：`TranscribeStreamAsync` 至今没有调用方；本期只做整段转写。
3. **Photino Windows 未实测**：PhotinoX 的 `MediaStreamEnabled` 默认开启、
   Windows WebView2 走 Chromium，预期零改动，但未在桌面宿主里跑过。
4. **真音频→真文字未端到端验证**：验证环境没有配置 ASR 服务商模型，
   实测到「真录音 → 真上传 → 503 modelNotConfigured」为止。
5. **ASR 端口不表达说话人分离（speaker diarization）**。
   业界主流服务（Deepgram / AssemblyAI / Google / Azure / ElevenLabs）都支持，
   但**没有统一的请求/响应协议**——OpenAI 要 `response_format=diarized_json`
   配专用模型，Deepgram 是查询参数 `diarize=true` 且响应结构完全不同，
   AssemblyAI 走异步 job 轮询，Azure 是另一个专有接口。
   因此 `TranscriptSegment` 只有 `Start` / `End` / `Text`，`TranscriptionRequest`
   也没有 `ResponseFormat` 字段，客户端固定请求 `verbose_json`（业界最小公约数）。
   将来若需要，正确做法是新增独立能力位 + 专用端口（如 `IDiarizationClient`），
   **而不是往 `TranscriptSegment` 加字段**——共用 `Segments` 数组装不下各家差异。

   > 相关实测：SiliconFlow 把 `verbose_json` 的 `segments[].id` 序列化成字符串 `"0"`
   > （规范是整数）。`TranscriptionApiSegment.Id` 用
   > `[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]` 兼容两种形状；
   > 不去把类型放宽成 `object`，也不兼容 `"seg_001"`——我们只发 `verbose_json`，
   > 读不进来即说明误用了别的响应格式，报错才对。

## 6. 手工验收清单

1. 浏览器宿主 + 已配 `speechTranscription` 默认模型 → 点麦克风 → 说一句 → **再点一次** → 文本出现在光标处。
2. 输入框已有内容、光标在中间 → 文本插入中间，前后空格正确，原文未丢。
3. 未配模型 → 按钮呈灰态、hover 显示原因；**点下去**给「未配置语音转写模型」+「去模型页配置」，
   且**不请求麦克风权限**；从该按钮能走到 `/models`。
4. 在 `/models` 配好 `speechTranscription` → 回到会话页 → **按钮自动变亮，无需刷新浏览器**。
5. 浏览器拒绝麦克风权限 → 提示「权限被拒绝」，不出现转写请求。
6. 录音中途切到别的会话 → 录音停止，结果不落进新会话。
7. 点了开始又立刻点结束（几乎没说话）→ 提示「未识别到语音」，输入框不变。
8. 桌面宿主（Photino，Windows）下重复第 1 条。
9. macOS 桌面宿主未验证（见 §5.1）。

## 7. 已完成的验证

自动化（`dotnet vstest`）：

- `ModelReferenceTests` 4/4 —— 目录 ID 优先、剥前缀、无斜杠原样、空 ID 回落
- `VoiceTextInsertionTests` 7/7 —— 选区替换、ASCII/中文交界补空格、空白插入、越界夹紧
- `SpeechTranscriptionServiceTests` 3/3 —— 未配模型抛可读异常、wire model 与 MIME/文件名透传、SessionId 进 InvocationOptions
- 整仓 `Seeing.Agent.WebUI.Tests` 222/222、`Seeing.Agent.Tools.Support.Tests` 21/21

手工（Playwright + msedge，fake media device）：

- 六个 JS 全局函数均暴露
- 未配模型时按钮 `prompt-voice-btn--unavailable` 且 `disabled=false`
- 点击给出 `speechTranscription` 键名与「去模型页配置」，跳转 `/models` 正确
- 真实录音起停：协商到 `audio/webm;codecs=opus`，`stopVoiceRecording` 的 Promise 正常 resolve
- 真实上传：multipart 到达端点，返回 503 `modelNotConfigured` + 可读中文原因
- 光标：`getVoiceInsertAnchor` 读 (4,4)、`setVoiceCaret` 复位均正确；元素不存在时不抛异常
- 端点分支：无头 400、坏会话 404、超 25MB 413 均符合预期
