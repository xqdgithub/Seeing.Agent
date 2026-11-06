# WebUI PromptInput 语音输入（点击切换录音 → 整段转写）

- 日期：2026-11-06
- 状态：待批准（批准后进入实现计划）
- 范围：`samples/Seeing.Agent.WebUI`（`Components/PromptInput.razor` + 新端点 + `wwwroot/js/voice-input.js`
  + `wwwroot/css/prompt-input.css` + `_Host.cshtml` 脚本引用），
  `src/support/Seeing.Agent.Tools.Support`（新增 `ModelReference` helper），
  以及 `tests/apps/Seeing.Agent.WebUI.Tests`
- 前序：`2026-11-04-llm-capability-client-abstraction-design.md`（本方案消费其 ASR 端口）

## 1. 意图与为什么

用户希望在 prompt 输入框里直接说话输入，不必手打。

现状：后端 ASR 已经完整（`ISpeechTranscriptionClient` 契约、OpenAI 兼容实现、重试装饰器、
`llm.*` Hook、用量统计、`DefaultModels.speechTranscription` 配置位），**但整个 WebUI 没有任何
调用方**——`CapabilityLabels` 里只有一句「语音转写」的展示文案。

因此这次要做的不是「实现语音转写能力」，而是**给已有能力接一个 UI 入口**：
浏览器录音 → 服务端转写 → 文本落进输入框。

选定路线（用户已选 A）：**整段转写**。点击麦克风开始录音，**再点一次**结束，
整段音频一次 POST 到服务端转写。不做边说边出字，也不做按住说话（评审提出的点击/按住
二选一，用户已定为**点击切换**）。

## 2. 目标 / 非目标

目标：

- prompt 输入框旁多一个麦克风按钮，**点击开始、再点结束**并转写；
- **没配好语音转写模型时按钮呈灰态**（`available=false`），但**仍可点击**——点一下给出
  「未配置语音转写模型」的提示与「去模型页配置」按钮，**不请求麦克风、不进入录音**。
  这样鼠标用户 hover 就能知道原因，触屏用户也不会撞上一个点了没反应的死图标；
- 转写文本**插入到光标处**，不覆盖用户已经输入的内容；
- 录音中按钮有明确的进行中状态（红点 + 计时），转写中有 loading 态，且**不阻塞打字**；
- 失败必须给出可读原因，并区分四类：没配模型 / 麦克风未授权 / 浏览器不支持录音 / 转写服务报错；
- API key 不出服务端；复用已有重试、Hook、用量统计；- 桌面宿主（Photino，Windows）与浏览器宿主行为一致；macOS 记为已知边界（见 §4.7）。

非目标：

- **不做实时/流式字幕**（`TranscribeStreamAsync` 本次不接线）；
- 不做语音唤醒、快捷键（如按住 Ctrl 说话）、自动断句静音检测；
- 不把录音落盘为附件、不进会话历史；
- 不改 `ModelType.Speech` 的路由语义，不加新能力位；
- 不做多语言 UI 切换（语言提示固定跟随输入框现有文案语境，默认不显式传 `language`）。

反目标（做到也不算成功）：

- 转写结果替换整个输入框——用户在写长 prompt 时会丢内容；
- 录音按钮点下去没反应（权限被拒、非 https/localhost、浏览器不支持），用户不知道发生了什么；
- 把音频存到磁盘再清理，平白引入临时文件生命周期问题。

## 3. 方案选择（Approaches）

三条路线在对话中已比较并选定：

| 方案 | 取舍 |
|---|---|
| **A. 服务端转写端点（已选）** | 复用已有 ASR 端口 + 重试 + Hook + 用量；API key 留在服务端；代价是一次录音一次往返 |
| B. 浏览器 `SpeechRecognition` | 零后端、实时出字；但 Chrome/Edge 会把音频送到 Google，Firefox 不支持，且与本仓库 ASR 能力脱钩，等于养两套 |
| C. 分片流式 + 服务端 SSE | 体验最好；但要处理分片边界、断流重连、部分结果回滚，复杂度约 A 的 3–4 倍，且流式 ASR 至今没有调用方验证过 |

选 A：用户要的是「能说话输入」，不是「边说边看字」；A 的端点与组件结构在升级到 C 时可原样复用。

## 4. 设计

### 4.1 分层与职责

| 层 | 位置 | 职责 |
|---|---|---|
| 浏览器录音 | `wwwroot/js/voice-input.js`（新文件） | `getUserMedia` + `MediaRecorder`，产出 Blob |
| 上传/转写调用 | 同上 | POST multipart 到 `/api/transcriptions`，把结果回传 .NET |
| HTTP 端点 | `Endpoints/TranscriptionEndpoints.cs`（新） | 校验、读音频、调服务、返回 JSON |
| 服务层 | `Services/SpeechTranscriptionService.cs`（新，Scoped） | 解析默认模型 → `ILlmInvoker` → `ISpeechTranscriptionClient` |
| 组件 | `Components/PromptInput.razor` | 按钮、状态、光标处插入 |
| 样式 | `wwwroot/css/prompt-input.css` | 录音/转写态 |

新 JS 单独成文件而不是塞进 `app.js`：`app.js` 已经 25KB 且混杂了 Monaco/终端的胶水，
录音是一整块自洽的媒体逻辑（含状态机与清理），单独文件更好审阅，也便于 `DisposeAsync` 成对释放。

### 4.2 端点契约

```
POST /api/transcriptions?sessionId={id}
Header: X-Seeing-Voice: 1
Body: multipart/form-data, field "audio" = 录音 Blob, filename = voice.webm 或 voice.mp4
```

音频格式**按 `blob.type` 决定，不硬编码**：前端用 `MediaRecorder.isTypeSupported` 依次探测
`audio/webm;codecs=opus` → `audio/webm` → `audio/mp4` → `audio/ogg`，取第一个支持者；
filename 的扩展名与 MIME 都从实际选中的类型派生。原因是 WKWebView（macOS 桌面宿主）不支持
webm，只能出 `audio/mp4`；写死 webm 会让桌面版在 Mac 上必然失败。

响应：

```json
// 200
{ "text": "转写文本", "segments": [{ "start": 0.0, "end": 1.2, "text": "…" }] }
// 4xx/5xx
{ "error": "可读原因", "code": "modelNotConfigured | unsupportedBrowser | transcriptionFailed | invalidRequest" }
```

- 与 `UploadEndpoints` 同构：`sessionId` 必须存在于 `ISessionManager`，否则 404；
  必须有 `X-Seeing-Voice: 1` 头，否则 400（防跨站表单直接打进来）。
- `segments` 本次前端不使用，但照常返回——它已在契约里，删掉反而制造信息损失。
- 音频大小上限 `VoiceInputLayout.MaxAudioBytes = 25MB`（约 10 分钟 opus），
  用 `RequestSizeLimit` 元数据挡住超大请求。
- **空结果**：服务端返回空文本时，端点照常 200 并带 `text: ""`；前端特判为空 →
  提示「未识别到语音」并保持输入框不变（不插入空串，否则用户只看到「什么也没发生」）。

**不设可用性预检端点**：是否能录由组件直读配置判定（见 §6b），少一个端点、少一次请求，
也不存在「预检说能用、实际不能用」的漂移。

### 4.3 服务层

```
SpeechTranscriptionService.TranscribeAsync(sessionId, audio, mimeType, fileName, ct)
  → _models.GetDefaultModel(LlmCapabilityKind.SpeechTranscription)
      为空 → 抛 ModelNotConfiguredException（带「请在模型页为『语音转写』选一个默认模型」）
  → _invoker.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
        modelRef,
        (client, call, token) => client.TranscribeAsync(new TranscriptionRequest {
            Model = WireModelId(modelRef), Audio = audio, MimeType = mimeType, FileName = fileName
        }, call, token),
        options: new LlmInvocationOptions { SessionId = sessionId })
```

三个关键点：

1. **`Audio` 流由调用方拥有**（契约决议 17）。服务层 `using` 包装 `MemoryStream`，
   端点不负责释放；`LlmInvoker` 的重试装饰器会 `Seek` 回起点，因此必须是可 seek 流
   （端点先把请求体读进 `MemoryStream`，天然满足）。
2. **模型引用 → 线上模型 ID**：`DefaultModels` 存的是 `provider/model` 引用，
   而 OpenAI 端点要裸模型名。仓库里已有同一口径的实现
   （`Memory` 的 `EmbeddingModelReference.ToWireModelId`：先查目录 `ModelConfig.Id`，
   查不到再剥 `provider/` 前缀）。**决定：不动 `Seeing.Agent.Memory`**——它是 `internal`，
   上提会牵动该包三个调用点与既有 embedding 测试。改为在 `Tools.Support` 新增一个
   公共 helper（`ModelReference.ToWireModelId`），WebUI 用它；Memory 保持原状。
   代价是两份逻辑同构，收益是零回归面。
3. **不吞异常**：`LlmCapabilityMismatchException` 与 HTTP 失败都转成
   `code=transcriptionFailed` + 原始 message（已含模型 ID 与五层校验原因），
   这正是仓库既有的「给可操作报错」风格。

### 4.4 前端状态机

按钮四态，单一 `_voiceState` 字段驱动渲染。**交互契约是点击切换（click-toggle），不是按住说话**——
评审提出的二选一，用户已定为点击：

| 状态 | 视觉 | 行为 |
|---|---|---|
| `Idle`（就绪） | 正常麦克风图标 | 点击 → 请求麦克风 → `Recording` |
| `Idle`（未配模型） | 灰调麦克风，tooltip 给原因 | 点击 → **不进录音**，展开错误条 +「去模型页配置」按钮 |
| `Recording` | 红色实心圆 + 计时 `0:07`，按钮变危险色 | **再点一次** → 停止 → 上传 → `Transcribing`；`Escape` 或超过 10 分钟自动停止 |
| `Transcribing` | loading 图标 + 「转写中」 | 完成后 `Idle` 并插入文本；失败后 `Idle` + 错误条 |

不做「最短录音时长」门槛：点击语义下用户是主动结束的，短录音照常上传，
空结果由 §4.2 的空文本规则兜底。也不做长按=按住说话的双模交互（用户已排除）。

权限与能力检测在**首次点击时**做（不在渲染时做，避免每次挂载都触发权限提示）。
`VoiceReady == false` 时点击不走到这一步——直接在错误条里给配置引导（见 §6b）：

- `!navigator.mediaDevices?.getUserMedia` → `unsupportedBrowser`
- `!window.MediaRecorder` → `unsupportedBrowser`
- `getUserMedia` reject 且 `err.name === 'NotAllowedError'` → 「麦克风权限被拒绝」
- `NotFoundError` → 「未找到麦克风设备」
- 非安全上下文（`window.isSecureContext === false` 且 host 不是 localhost）→ 明确提示需要 https 或 localhost

停止录音后立即 `track.stop()`，麦克风指示灯必须在转写开始前熄灭。

### 4.5 光标处插入

现有 `PromptInput` 只有 `InputText` 字符串，没有光标位置。插入按两步：

1. JS 侧 `getVoiceInsertAnchor(textareaId)` 返回 `selectionStart`/`selectionEnd`；
2. .NET 侧在 `InputText` 上做替换，再渲染后调 JS `restoreCursor(textareaId, pos)`。

找不到 textarea（组件未挂载）时退化为「追加到末尾」。
文本两侧按需补空格：锚点左侧非空白且非 `(`/`[` 且插入内容非空白时补一个空格；同理右侧。

### 4.6 可见性

按钮**始终显示，从不隐藏**——隐藏会让用户以为「这产品没有语音输入」，而实际只差一次配置。
未配好模型时呈灰态但可点，点击给配置引导（§6b）。
指引文案必须指对地方：**模型页 `/models` 的「默认模型（按能力）」区块，键 `speechTranscription`**
（仓库里没有叫「设置」的默认模型入口，指向设置页会让用户找不到）。

### 4.7 桌面宿主（Photino）

**在本次范围内**，但只做 Windows 直通验证：

- PhotinoX 的 `MediaStreamEnabled` / 浏览器权限授予默认开启，Windows WebView2 走 Chromium，
  预期零改动；若实测被拦，再评估是否需要在 `samples/Seeing.Agent.Desktop/Program.cs` 显式放开。
- **macOS 记为已知边界**：WKWebView 需要 Info.plist 的麦克风用途声明，且只能出 `audio/mp4`。
  §4.2 的「按 `blob.type` 派生格式」已让前端不至于硬失败，但打包声明与真机验证不在本期。
  这条要写进实现计划的任务清单，避免「Mac 上点了没反应」变成隐形故障。

## 5. 约束

- Blazor Server：麦克风只能在浏览器侧，必须 JS interop；`IJSObjectReference` 或全局脚本二选一，
  本设计用**全局脚本 + 现有 `DotNetObjectReference` 回传**，与 `setupDropZone`/`setupPasteHandler` 一致。
- `PromptInput` 已实现 `IAsyncDisposable`：录音器移除监听、停止 track、清计时器都挂在这里。
- 测试禁等待型断言：前端态与网络不可测；可测的是**服务层的模型解析与错误映射**、
  **灰态判定 `VoiceReady` 的四类原因**，以及**插入文本的纯函数**（锚点合并、空格补齐）。JS 行为靠手工清单。
- 不要给 `PromptInput` 增加新参数来「外部注入录音服务」——组件已很深，加端口会牵动
  `SessionWindow` 的每个调用点。录音逻辑留在组件内部 + JS 文件。
  注入 `IModelConfigManager` 是例外（既有惯例，见 §6b），不新增自定义端口。
- **不做长按/点击双模**：用户已排除，JS 只绑 `click`，不绑 `pointerdown`/`pointerup`。

## 6. 留给实现者的决定

- `_voiceState` 计时用 `System.Threading.Timer` 还是 JS 侧 `setInterval` 回推——两者都行；
  倾向 JS 侧（省掉每 100ms 一次的 circuit 往返，Blazor Server 下这很贵）。
- 错误条复用现有的 `.prompt-upload-error` 样式，还是新增 `.prompt-voice-error`——新增一个更清楚。
- 按钮放在「上传文件」左侧还是右侧——左侧（与「清空」分开，避免误触）。
- `ModelReference.ToWireModelId` 放 `Tools.Support` 的具体文件位置（建议与
  `FilePaste/` 同级新建 `Llm/` 目录，避免与文件工具混在一起）。

## 6b. 灰态可用性判定（组件内直读配置，不设预检端点）

`PromptInput` 注入 `IModelConfigManager`（已在 `MainLayout`/`ModelsPage`/`ModelSelectBadge`
三处使用，属于既有惯例），在 `OnInitialized` 时判定一次：

```
VoiceReady = ModelManager.TryResolveDefault<ISpeechTranscriptionClient>(
    LlmCapabilityKind.SpeechTranscription, out var client, out VoiceUnavailableReason)
```

用 `TryResolveDefault` 而不是 `GetDefaultModel(...) is null`：前者内部走 `ILlmRouter` 的
五层校验，能区分「没配」「模型已不在目录」「声明类型不含 Speech」「Provider 未声明能力」，
并把原因措辞直接复用（`ModelConfigManager.cs:486`）。零出站请求、不产生 LLM 调用。

**原位刷新**：订阅 `ModelManager.ModelConfigChanged`，事件到达后重算 `VoiceReady` 并
`InvokeAsync(StateHasChanged)`；`DisposeAsync` 里取消订阅。用户在模型页配好默认模型后回到
会话页，按钮自动从灰变亮，不需要刷新浏览器。

**按钮呈现**：

| `VoiceReady` | 视觉 | 点击行为 |
|---|---|---|
| `true` | 正常麦克风图标 | 走正常录音流程 |
| `false` | 灰调（`opacity`/`--color-text-quaternary`），`Tooltip` 显示 `VoiceUnavailableReason` | 不请求麦克风，就地展开错误条：「未配置语音转写模型」+「去模型页配置」按钮（`Navigation.NavigateTo("/models")`） |

**注意**：灰态**不是** `Disabled`。若设 `Disabled`，`OnClick` 不触发、tooltip 在触屏上不可达，
就退回成「死图标」。用灰调样式 + 可点击，是唯一同时满足鼠标与触屏的做法。

这样 §4.2.1 的 `GET /api/transcriptions/availability` 端点**取消**——少一个端点、少一次请求，
也就没有「预检说能用、实际不能用」的漂移面。服务层只需保留 `TranscribeAsync`。

## 7. 手工验收清单

1. 浏览器宿主 + 已配 `speechTranscription` 默认模型 → 点麦克风 → 说一句 → **再点一次** → 文本出现在光标处。
2. 输入框已有内容、光标在中间 → 文本插入中间，前后空格正确，原文未丢。
3. 未配模型 → 按钮呈灰态、hover 显示原因；**点下去**给「未配置语音转写模型」+「去模型页配置」，
   且**不请求麦克风权限**；从该按钮能走到 `/models`。
4. 在 `/models` 配好 `speechTranscription` → 回到会话页 → **按钮自动变亮，无需刷新浏览器**。
5. 浏览器拒绝麦克风权限 → 提示「权限被拒绝」，不出现转写请求。
6. 录音中途切到别的会话 → 录音停止，结果不落进新会话。
7. 点了开始又立刻点结束（几乎没说话）→ 提示「未识别到语音」，输入框不变。
8. 桌面宿主（Photino，Windows）下重复第 1 条。
9. macOS 桌面宿主未验证（已知边界，见 §4.7）。
