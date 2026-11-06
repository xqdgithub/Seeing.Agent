# WebUI PromptInput 语音输入 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 WebUI 的 prompt 输入框旁加一个麦克风按钮，点击开始录音、再点结束，音频经服务端已有 ASR 能力转成文字并插入光标处。

**Architecture:** 浏览器侧 `MediaRecorder` 录音（新增 `wwwroot/js/voice-input.js`），POST 到新增的 `/api/transcriptions` 端点；端点交给新增的 `SpeechTranscriptionService`，后者经 `ILlmInvoker` → `ISpeechTranscriptionClient` 调用既有 ASR 实现（重试/Hook/用量统计自动生效）。`PromptInput` 注入 `IModelConfigManager`，用 `TryResolveDefault` 判定按钮灰态并订阅 `ModelConfigChanged` 原位刷新。

**Tech Stack:** .NET 10、Blazor Server（Razor 组件 + JS interop）、Minimal API、xUnit v3 + FluentAssertions + Moq、`MediaRecorder` / `getUserMedia`

**Spec:** `docs/superpowers/specs/2026-11-06-webui-voice-input-design.md`

## Global Constraints

- 目标框架 `net10.0`；注释与用户可见文案一律中文（仓库 AGENTS.md 要求）。
- 结构化日志用 `{PropertyName}` 占位符，禁止字符串拼接。
- 测试**禁止等待型断言**（`Task.Delay`/`Thread.Sleep` 等状态就绪）；本计划所有测试都是纯函数或纯桩，不得引入延迟。
- 测试运行方式（仓库 AGENTS.md 已记录 MTP 无法发现测试）：
  先 `dotnet build <测试项目>`，再 `dotnet vstest <输出 dll> --TestCaseFilter:"FullyQualifiedName~<类名>"`。
- 探测音频格式、判定灰态都**不得硬编码** `audio/webm`；按 `blob.type` / `TryResolveDefault` 结果决定。
- 灰态按钮**不得设 `Disabled`**：`OnClick` 必须仍触发，否则触屏用户看到死图标。
- 不新增 `PromptInput` 的 `[Parameter]`；改动只发生在组件内部与新增文件。
- 依赖方向：`Tools.Support` 只允许引用 Abstractions；能力包禁止反向依赖 Core/WebUI。
- 所有新建文件用 UTF-8（无 BOM 亦可）；**不要用 PowerShell `Set-Content` 改这些文件**，它会按系统 ANSI 码页写坏中文，用 `write`/`edit` 工具。

## Review Focus

1. **麦克风不可用**（权限被拒 / 无设备 / 非 https 且非 localhost）——用户必须看到可读原因，绝不能点了没反应。（浏览器行为，Task 4 无自动化测试，靠 Task 5 Step 6 手工清单第 5 条）
2. **空转写结果**（没说话或服务端返回空串）——必须提示「未识别到语音」并保持输入框原样，不能插入空串。（Task 5 回调约束 3；手工清单第 7 条）
3. **录音中途切换会话**——音频结果不得落进新会话的输入框。（Task 5 约束 5、7；手工清单第 6 条）
4. **模型配置变更后按钮状态**——在模型页配好默认模型后回到会话页，按钮必须从灰变亮，不需要刷新浏览器。（Task 5 Step 2 事件订阅；手工清单第 4 条）
5. **音频容器格式不被服务端接受**（WKWebView 只能出 `audio/mp4`）——MIME 与文件名必须从 `blob.type` 派生。（Task 4 约束 2/6；手工清单第 1、8 条）

> 第 1–5 条里有 4 条落在浏览器侧（录音、权限、格式协商、会话切换时的 JS 取消），
> 本仓库没有前端测试基础设施，因此它们的验证方式是 Task 5 Step 6 的手工清单，
> 而不是伪造一层无意义的 mock。有自动化测试的是 Task 1（模型引用）、
> Task 2（插入纯函数）、Task 3（服务层模型解析与错误映射）。

---

### Task 1: `ModelReference.ToWireModelId` 共享 helper

**Files:**
- Create: `src/support/Seeing.Agent.Tools.Support/Llm/ModelReference.cs`
- Test: `tests/support/Seeing.Agent.Tools.Support.Tests/Llm/ModelReferenceTests.cs`

**Interfaces:**
- Consumes: `IModelConfigManager.GetModel(string)`（命名空间 `Seeing.Agent.Llm`，程序集 Abstractions）、`ModelConfig.Id`
- Produces: `public static string ModelReference.ToWireModelId(IModelConfigManager models, string modelRef)` —— 供 Task 3 的 `SpeechTranscriptionService` 调用

**说明**：与 `src/capabilities/Seeing.Agent.Memory/Core/Embedding/EmbeddingModelReference.cs` 同口径（先查目录 `ModelConfig.Id`，查不到则剥离 `provider/` 前缀）。**不要修改 Memory 包**——那里是 `internal`，上提会牵动三个调用点与既有 embedding 测试。

- [ ] **Step 1: 写失败的测试**

`tests/support/Seeing.Agent.Tools.Support.Tests/Llm/ModelReferenceTests.cs`：

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools.Support.Llm;

namespace Seeing.Agent.Tools.Support.Tests.Llm;

public class ModelReferenceTests
{
    [Fact]
    public void ToWireModelId_Should_Prefer_Catalog_Id_Over_Reference()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel("openai/whisper-1"))
              .Returns(new ModelConfig { Id = "whisper-1-2024" });

        ModelReference.ToWireModelId(models.Object, "openai/whisper-1")
            .Should().Be("whisper-1-2024");
    }

    [Fact]
    public void ToWireModelId_Should_Strip_Provider_Prefix_When_Not_In_Catalog()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);

        ModelReference.ToWireModelId(models.Object, "siliconflow/whisper-large-v3")
            .Should().Be("whisper-large-v3");
    }

    [Fact]
    public void ToWireModelId_Should_Return_Input_Unchanged_When_No_Slash()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);

        ModelReference.ToWireModelId(models.Object, "whisper-1").Should().Be("whisper-1");
    }

    [Fact]
    public void ToWireModelId_Should_Ignore_Blank_Catalog_Id()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel("openai/whisper-1"))
              .Returns(new ModelConfig { Id = "  " });

        ModelReference.ToWireModelId(models.Object, "openai/whisper-1")
            .Should().Be("whisper-1");
    }
}
```

`ModelConfig.Id` 是 `public string Id { get; set; } = string.Empty;`（`src/abstractions/Seeing.Agent.Abstractions/Llm/ModelConfig.cs:12`），可直接用对象初始化器赋值。

- [ ] **Step 2: 运行测试确认失败**

Run:
```
dotnet build tests/support/Seeing.Agent.Tools.Support.Tests
```
Expected: 编译失败，`命名空间 Seeing.Agent.Tools.Support.Llm 不存在`（或 `ModelReference` 未定义）。

- [ ] **Step 3: 实现 helper**

`src/support/Seeing.Agent.Tools.Support/Llm/ModelReference.cs`：

```csharp
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Tools.Support.Llm;

/// <summary>
/// 配置里的模型引用（<c>provider/model</c>）→ 能力端口实际发送的裸模型 ID。
/// </summary>
/// <remarks>
/// 与 <c>Seeing.Agent.Memory</c> 的 <c>EmbeddingModelReference</c> 同口径：
/// 目录里有该模型时以 <c>ModelConfig.Id</c> 为准（可能与目录键不同）；
/// 查不到时剥离 <c>provider/</c> 前缀。本方法不做任何「挑模型」的工作。
/// </remarks>
public static class ModelReference
{
    /// <summary>引用 → 出站模型 ID。</summary>
    public static string ToWireModelId(IModelConfigManager models, string modelRef)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRef);

        var id = models.GetModel(modelRef)?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        var slash = modelRef.LastIndexOf('/');
        return slash >= 0 && slash < modelRef.Length - 1
            ? modelRef[(slash + 1)..]
            : modelRef;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```
dotnet build tests/support/Seeing.Agent.Tools.Support.Tests
dotnet vstest tests/support/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll --TestCaseFilter:"FullyQualifiedName~ModelReferenceTests"
```
Expected: 4 个测试全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add src/support/Seeing.Agent.Tools.Support/Llm/ModelReference.cs tests/support/Seeing.Agent.Tools.Support.Tests/Llm/ModelReferenceTests.cs
git commit -m "feat(support): 新增模型引用转出站模型 ID 的共享 helper"
```

---

### Task 2: 插入文本纯函数与语音常量

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Models/VoiceInputLayout.cs`
- Create: `samples/Seeing.Agent.WebUI/Models/VoiceTextInsertion.cs`
- Modify: `tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj`（加 `Moq`，供 Task 3 用）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Voice/VoiceTextInsertionTests.cs`

**Interfaces:**
- Produces:
  - `public static class VoiceInputLayout`：`MaxAudioBytes`（`long`）、`MaxRecordingSeconds`（`int`）、`ContainerFieldName`（`string`，`"audio"`）、`RequestHeaderName`（`string`，`"X-Seeing-Voice"`）、`RequestHeaderValue`（`string`，`"1"`）
  - `public sealed class VoiceInsertAnchor { public int Start { get; set; } public int End { get; set; } }`
  - `public static (string Text, int CaretIndex) VoiceTextInsertion.Insert(string original, int selectionStart, int selectionEnd, string insert)`

- [ ] **Step 1: 写失败的测试**

`tests/apps/Seeing.Agent.WebUI.Tests/Voice/VoiceTextInsertionTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Tests.Voice;

public class VoiceTextInsertionTests
{
    [Fact]
    public void Insert_Should_Replace_Selection_And_Place_Caret_After_Text()
    {
        var (text, caret) = VoiceTextInsertion.Insert("你好世界", 2, 4, "语音");

        text.Should().Be("你好语音");
        caret.Should().Be(4);
    }

    [Fact]
    public void Insert_Should_Add_Spaces_When_Surrounded_By_Word_Characters()
    {
        var (text, _) = VoiceTextInsertion.Insert("abcdef", 3, 3, "语音");

        text.Should().Be("abc 语音 def");
    }

    [Fact]
    public void Insert_Should_Not_Add_Spaces_Next_To_Whitespace()
    {
        var (text, _) = VoiceTextInsertion.Insert("abc def", 3, 3, "语音");

        text.Should().Be("abc 语音def");
    }

    [Fact]
    public void Insert_Should_Not_Add_Left_Space_After_Opening_Bracket()
    {
        var (text, _) = VoiceTextInsertion.Insert("（", 1, 1, "语音");

        text.Should().Be("（语音");
    }

    [Fact]
    public void Insert_Should_Not_Add_Right_Space_Before_Closing_Bracket()
    {
        var (text, _) = VoiceTextInsertion.Insert(")", 0, 0, "语音");

        text.Should().Be("语音)");
    }

    [Fact]
    public void Insert_Should_Return_Original_When_Insert_Is_Whitespace()
    {
        var (text, caret) = VoiceTextInsertion.Insert("abc", 1, 1, "   ");

        text.Should().Be("abc");
        caret.Should().Be(1);
    }

    [Fact]
    public void Insert_Should_Clamp_Out_Of_Range_Anchors()
    {
        var (text, caret) = VoiceTextInsertion.Insert("ab", 99, 100, "x");

        text.Should().Be("abx");
        caret.Should().Be(3);
    }
}
```

同时改 `tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj`，在 PackageReference 组内加：

```xml
<PackageReference Include="Moq" Version="4.20.72" />
```

（该项目 `ManagePackageVersionsCentrally=false`，必须写显式版本，与 `Directory.Packages.props` 的 `4.20.72` 一致。）

- [ ] **Step 2: 运行测试确认失败**

Run:
```
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
```
Expected: 编译失败，`VoiceTextInsertion` 未定义。

- [ ] **Step 3: 实现三个文件**

`VoiceInputLayout.cs`：

```csharp
namespace Seeing.Agent.WebUI.Models;

/// <summary>语音输入的传输布局与上限。</summary>
public static class VoiceInputLayout
{
    /// <summary>单段录音上限（约 10 分钟 opus）。</summary>
    public const long MaxAudioBytes = 25L * 1024 * 1024;

    /// <summary>单段录音最长时长（秒），到时自动停止。</summary>
    public const int MaxRecordingSeconds = 10 * 60;

    /// <summary>multipart 表单里的音频字段名。</summary>
    public const string ContainerFieldName = "audio";

    /// <summary>防空表单/CORS 简单请求的头名。</summary>
    public const string RequestHeaderName = "X-Seeing-Voice";

    /// <summary>上述头的期望值。</summary>
    public const string RequestHeaderValue = "1";
}

/// <summary>JS 返回的 textarea 选区锚点。</summary>
public sealed class VoiceInsertAnchor
{
    /// <summary>选区起点。</summary>
    public int Start { get; set; }

    /// <summary>选区终点。</summary>
    public int End { get; set; }
}
```

`VoiceTextInsertion.cs`：

```csharp
using System.Text;

namespace Seeing.Agent.WebUI.Models;

/// <summary>语音转写文本插入输入框的纯函数（不依赖组件状态）。</summary>
public static class VoiceTextInsertion
{
    /// <summary>
    /// 在 <paramref name="selectionStart"/>..<paramref name="selectionEnd"/> 之间插入文本。
    /// 边界越界自动夹紧；插入内容为空白时原样返回。
    /// </summary>
    /// <returns>替换后的全文与建议光标位置（紧随插入内容之后）。</returns>
    public static (string Text, int CaretIndex) Insert(
        string original,
        int selectionStart,
        int selectionEnd,
        string insert)
    {
        original ??= string.Empty;
        insert ??= string.Empty;

        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, original.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, original.Length);

        if (string.IsNullOrWhiteSpace(insert))
            return (original, start);

        var needLeft = start > 0
            && !char.IsWhiteSpace(original[start - 1])
            && !IsOpeningBracket(original[start - 1]);
        var needRight = end < original.Length
            && !char.IsWhiteSpace(original[end])
            && !IsClosingBracket(original[end]);

        var builder = new StringBuilder(original.Length + insert.Length + 2);
        builder.Append(original, 0, start);
        if (needLeft)
            builder.Append(' ');
        builder.Append(insert);
        if (needRight)
            builder.Append(' ');
        builder.Append(original, end, original.Length - end);

        var caret = start + (needLeft ? 1 : 0) + insert.Length;
        return (builder.ToString(), caret);
    }

    private static bool IsOpeningBracket(char ch)
        => ch is '(' or '[' or '{' or '（' or '【' or '《';

    private static bool IsClosingBracket(char ch)
        => ch is ')' or ']' or '}' or '）' or '】' or '》';
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~VoiceTextInsertionTests"
```
Expected: 7 个测试全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Models/VoiceInputLayout.cs samples/Seeing.Agent.WebUI/Models/VoiceTextInsertion.cs tests/apps/Seeing.Agent.WebUI.Tests/Voice/VoiceTextInsertionTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj
git commit -m "feat(webui): 语音插入文本纯函数与传输常量"
```

---

### Task 3: 转写服务与 HTTP 端点

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/SpeechTranscriptionService.cs`
- Create: `samples/Seeing.Agent.WebUI/Endpoints/TranscriptionEndpoints.cs`
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`（注册服务 + 映射端点）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SpeechTranscriptionServiceTests.cs`

**Interfaces:**
- Consumes: `ModelReference.ToWireModelId`（Task 1）、`VoiceInputLayout`（Task 2）、`ILlmInvoker`、`IModelConfigManager`、`ISpeechTranscriptionClient`、`TranscriptionRequest`/`TranscriptionResult`
- Produces:
  - `public sealed class SpeechTranscriptionUnavailableException : Exception { public string Reason { get; } }`
  - `public sealed class SpeechTranscriptionService { public SpeechTranscriptionService(ILlmInvoker invoker, IModelConfigManager models, ILogger<SpeechTranscriptionService> logger); public Task<string> TranscribeAsync(string sessionId, Stream audio, string? mimeType, string? fileName, CancellationToken ct = default); }`
  - `public static class TranscriptionEndpoints { public const string Route = "/api/transcriptions"; public static IEndpointRouteBuilder MapTranscriptionEndpoints(this IEndpointRouteBuilder endpoints); }`

- [ ] **Step 1: 写失败的测试**

`tests/apps/Seeing.Agent.WebUI.Tests/Services/SpeechTranscriptionServiceTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SpeechTranscriptionServiceTests
{
    private static SpeechTranscriptionService Create(IModelConfigManager models, ILlmInvoker invoker)
        => new(invoker, models, NullLogger<SpeechTranscriptionService>.Instance);

    [Fact]
    public async Task TranscribeAsync_Should_Throw_Unavailable_When_No_Default_Model()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns((string?)null);
        var invoker = new Mock<ILlmInvoker>();

        var act = () => Create(models.Object, invoker.Object)
            .TranscribeAsync("s1", new MemoryStream([1, 2, 3]), "audio/webm", "voice.webm");

        var ex = await act.Should().ThrowAsync<SpeechTranscriptionUnavailableException>();
        ex.Which.Reason.Should().Contain("speechTranscription");
        invoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TranscribeAsync_Should_Invoke_Speech_Client_With_Wire_Model_And_Return_Text()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns("openai/whisper-1");
        models.Setup(m => m.GetModel("openai/whisper-1"))
              .Returns(new ModelConfig { Id = "whisper-1" });

        TranscriptionRequest? captured = null;
        var client = new Mock<ISpeechTranscriptionClient>();
        client.Setup(c => c.TranscribeAsync(
                  It.IsAny<TranscriptionRequest>(),
                  It.IsAny<LlmCallContext?>(),
                  It.IsAny<CancellationToken>()))
              .Callback<TranscriptionRequest, LlmCallContext?, CancellationToken>((r, _, _) => captured = r)
              .ReturnsAsync(new TranscriptionResult("你好", null, null));

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .Returns<string, Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>, LlmInvocationOptions?, CancellationToken>(
                   (_, call, _, ct) => call(client.Object, new LlmCallContext(), ct));

        var text = await Create(models.Object, invoker.Object)
            .TranscribeAsync("s1", new MemoryStream([1, 2, 3]), "audio/webm", "voice.webm");

        text.Should().Be("你好");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("whisper-1");
        captured.MimeType.Should().Be("audio/webm");
        captured.FileName.Should().Be("voice.webm");
    }

    [Fact]
    public async Task TranscribeAsync_Should_Pass_SessionId_To_Invocation_Options()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns("openai/whisper-1");
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);

        LlmInvocationOptions? seen = null;
        var client = new Mock<ISpeechTranscriptionClient>();
        client.Setup(c => c.TranscribeAsync(
                  It.IsAny<TranscriptionRequest>(),
                  It.IsAny<LlmCallContext?>(),
                  It.IsAny<CancellationToken>()))
              .ReturnsAsync(new TranscriptionResult("ok", null, null));

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .Returns<string, Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>, LlmInvocationOptions?, CancellationToken>(
                   (_, call, options, ct) =>
                   {
                       seen = options;
                       return call(client.Object, new LlmCallContext(), ct);
                   });

        await Create(models.Object, invoker.Object)
            .TranscribeAsync("session-42", new MemoryStream([1]), "audio/webm", "voice.webm");

        seen.Should().NotBeNull();
        seen!.SessionId.Should().Be("session-42");
    }
}
```

`LlmCallContext` 有无参构造（`src/abstractions/Seeing.Agent.Abstractions/Llm/LlmCallContext.cs`）。

- [ ] **Step 2: 运行测试确认失败**

Run:
```
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
```
Expected: 编译失败，`SpeechTranscriptionService` / `SpeechTranscriptionUnavailableException` 未定义。

- [ ] **Step 3: 实现服务**

`SpeechTranscriptionService.cs` 对外签名见本任务 Interfaces。实现约束：

1. `ArgumentNullException.ThrowIfNull(audio)`；`ArgumentException.ThrowIfNullOrWhiteSpace(sessionId)`。
2. `_models.GetDefaultModel(LlmCapabilityKind.SpeechTranscription)` 为空白 → 抛 `SpeechTranscriptionUnavailableException`，
   `Reason` 必须含 `speechTranscription` 字样与「模型页 / 默认模型」指引（测试断言包含 `speechTranscription`）。
3. `var wireModel = ModelReference.ToWireModelId(_models, modelRef);`
4. `_invoker.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(modelRef, (client, call, token) => client.TranscribeAsync(new TranscriptionRequest { Model = wireModel, Audio = audio, MimeType = mimeType, FileName = fileName }, call, token), options: new LlmInvocationOptions { SessionId = sessionId }, cancellationToken: ct)`
5. 调用前后各一条 `LogDebug`，形如 `_logger.LogDebug("语音转写: Session={SessionId}, Model={Model}", sessionId, modelRef)`。
6. 返回 `result.Text`；**不要** `Dispose` `audio`（契约决议 17，由调用方负责）。

`SpeechTranscriptionUnavailableException` 可与服务同文件，`public string Reason { get; }` 由构造函数赋值。

- [ ] **Step 4: 运行测试确认通过**

Run:
```
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SpeechTranscriptionServiceTests"
```
Expected: 3 个测试全部 PASS。

- [ ] **Step 5: 实现端点**

`TranscriptionEndpoints.cs`，与 `Endpoints/UploadEndpoints.cs` 同构；公开签名见本任务 Interfaces。

`HandleAsync` 判定顺序与响应（严格按此）：

| 条件 | 响应 |
|---|---|
| `sessionId` 空白或 `sessions.Get(sessionId) is null` | `404` `{ error = "session not found" }` |
| 缺 `X-Seeing-Voice: 1` 头 | `400` `{ error = "invalid voice request", code = "invalidRequest" }` |
| 非 multipart 或没有 `audio` 文件 | `400` `{ error = "audio required", code = "invalidRequest" }` |
| 文件长度 `<= 0` 或 `> VoiceInputLayout.MaxAudioBytes` | `400` `{ error = "invalid audio size", code = "invalidRequest" }` |
| 成功 | `200` `{ text, segments }` |
| `SpeechTranscriptionUnavailableException` | `503` `{ error = ex.Reason, code = "modelNotConfigured" }` |
| 其它异常 | `502` `{ error = ex.Message, code = "transcriptionFailed" }` |

- 用 `RequestSizeLimitAttribute` 限制请求体：`VoiceInputLayout.MaxAudioBytes + 1024 * 1024`。
- 把文件读进 `MemoryStream`（可 seek，重试装饰器需要 `Seek` 回起点），在 `await service.TranscribeAsync(...)` 期间保持存活，方法结束一并释放。
- `segments` 投影为 `{ start, end, text }`，时间用 `TotalSeconds`（`double`）；为 null 时输出 `null`。
- MIME 取 `file.ContentType`，空则 `application/octet-stream`；文件名取 `file.FileName`，空则 `"voice.webm"`。
- 只在「其它异常」分支 `LogError`，不吞细节。

- [ ] **Step 6: 在宿主里接线**

`samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`：
- `builder.Services.AddScoped<UploadedFileService>();` 附近加 `builder.Services.AddScoped<SpeechTranscriptionService>();`
- `app.MapUploadEndpoints();` 后加 `app.MapTranscriptionEndpoints();`

- [ ] **Step 7: 构建整体确认无编译错误**

Run:
```
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 构建成功。

- [ ] **Step 8: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SpeechTranscriptionService.cs samples/Seeing.Agent.WebUI/Endpoints/TranscriptionEndpoints.cs samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/SpeechTranscriptionServiceTests.cs
git commit -m "feat(webui): 语音转写服务与转写端点"
```

---

### Task 4: 浏览器录音脚本与样式

**Files:**
- Create: `samples/Seeing.Agent.WebUI/wwwroot/js/voice-input.js`
- Modify: `samples/Seeing.Agent.WebUI/Pages/_Host.cshtml`（`js/app.js` 之后加脚本引用）
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/prompt-input.css`（追加样式）

**Interfaces:**
- Produces（全局函数，供 Task 5 调用）：
  - `startVoiceRecording(containerId, textareaId, dotNetRef, maxSeconds)` → `Promise<{ ok, code?, message?, mimeType? }>`
  - `stopVoiceRecording()` → `Promise<void>`（停止并上传；**上传完成/失败后才 resolve**）
  - `cancelVoiceRecording()` → `void`（停止并丢弃，不上传）
  - `disposeVoiceInput()` → `void`
  - `getVoiceInsertAnchor(textareaId)` → `{ start, end }`
  - `setVoiceCaret(textareaId, position)` → `void`
- 回调 .NET 方法名（Task 5 用同名 `[JSInvokable]`）：
  - `HandleVoiceTick(seconds)`
  - `HandleVoiceTranscribed(text, error, code)`
  - `HandleVoiceInterrupted(reason)`

**关键状态机约束**：
- 同一时刻只能有一段录音；`startVoiceRecording` 在已录音时返回 `{ ok:false, code:'alreadyRecording' }`。
- 模块级持有 `recorder`、`stream`、`chunks`、`timerId`、`seconds`、`mimeType`、`cancelled`、`dotNetRef`、`containerId`。
- `stopVoiceRecording()` 的 Promise 只在**上传完成或失败后** resolve —— 这是 .NET 侧能安全改状态的前提。

**本任务无自动化测试**，脚本按 `app.js` 既有风格写（普通函数 + 顶层声明即全局，不用 ES module）。

- [ ] **Step 1: 写脚本**

`voice-input.js` 逐条实现：

1. **能力探测**：`window.isSecureContext === false` 且 host 不是 `localhost`/`127.0.0.1` → `{ ok:false, code:'insecureContext', message:'需要 https 或 localhost 才能使用麦克风' }`；`!navigator.mediaDevices?.getUserMedia` 或 `!window.MediaRecorder` → `{ ok:false, code:'unsupportedBrowser', message:'当前浏览器不支持录音' }`。
2. **格式协商**（不硬编码）：按序试 `['audio/webm;codecs=opus','audio/webm','audio/mp4','audio/ogg;codecs=opus']`，用 `MediaRecorder.isTypeSupported` 取第一个支持的；全不支持则用 `new MediaRecorder(stream)` 默认类型。
3. **取流**：`await navigator.mediaDevices.getUserMedia({ audio: true })`；`NotAllowedError` → `{ ok:false, code:'permissionDenied', message:'麦克风权限被拒绝' }`；`NotFoundError` → `code:'noDevice'`。
4. **计时**：`setInterval(1000)` 调 `dotNetRef.invokeMethodAsync('HandleVoiceTick', seconds)`；到 `maxSeconds`（第 4 参数）自动 `stopVoiceRecording()`，**不硬编码 600**。
5. **停止**：`recorder.stop()`，`onstop` 里立刻 `stream.getTracks().forEach(t => t.stop())`（麦克风指示灯先熄灭），再清计时器。
6. **上传**：`dataavailable` 收集 chunks → `new Blob(chunks, { type: mimeType })`；扩展名由 mimeType 派生（含 `webm`→`webm`、`mp4`→`mp4`、`ogg`→`ogg`，否则 `bin`）；`FormData` 字段名 `'audio'`，`append(blob, 'voice.' + ext)`。
7. **请求**：`fetch('/api/transcriptions?sessionId=' + encodeURIComponent(sessionId), { method:'POST', headers:{ 'X-Seeing-Voice':'1' }, body: form })`；`sessionId` 来自 `document.getElementById(containerId).dataset.sessionId`。
8. **回传**：成功 `dotNetRef.invokeMethodAsync('HandleVoiceTranscribed', json.text || '', null, null)`；失败优先取响应体 `code`/`error`，取不到用 `'transcriptionFailed'` + `HTTP {status}`；网络异常用 `'networkError'`。
9. **取消**：`cancelVoiceRecording` 先置 `cancelled = true` 再 `stop()`；`onstop` 见 `cancelled` 直接返回，不上传。
10. **清理**：`disposeVoiceInput` 清计时器、停所有 track、置空引用；所有 `dotNetRef` 调用包 `try/catch`（circuit 可能已断）。

- [ ] **Step 2: 加脚本引用**

`Pages/_Host.cshtml` 中在 `<script src="js/app.js" ...></script>` 之后追加一行，保持既有 `asp-append-version="true"` 约定。

- [ ] **Step 3: 加样式**

`wwwroot/css/prompt-input.css` 追加：

```css
/* ========== 语音输入按钮 ========== */
.prompt-voice-btn {
    display: inline-flex;
    align-items: center;
    gap: 4px;
}

/* 未配置模型：灰调但仍可点（不能设 Disabled，否则触屏无反馈） */
.prompt-voice-btn--unavailable {
    color: var(--color-text-quaternary) !important;
    opacity: 0.65;
}

.prompt-voice-btn--unavailable:hover {
    opacity: 0.85;
    color: var(--color-text-tertiary) !important;
}

/* 录音中：红色 + 呼吸圆点 */
.prompt-voice-btn--recording {
    color: var(--color-error, #ff4d4f) !important;
}

.prompt-voice-recording-dot {
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: currentColor;
    animation: prompt-voice-pulse 1.2s ease-in-out infinite;
}

@keyframes prompt-voice-pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.35; }
}

.prompt-voice-error {
    padding: 4px 8px;
    color: var(--color-error);
    font-size: 12px;
    display: flex;
    align-items: center;
    gap: 8px;
}

.prompt-voice-error-action {
    color: var(--color-primary);
    cursor: pointer;
    text-decoration: underline;
    background: none;
    border: none;
    padding: 0;
    font-size: 12px;
}
```

- [ ] **Step 4: 构建确认**

Run:
```
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 构建成功。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/wwwroot/js/voice-input.js samples/Seeing.Agent.WebUI/wwwroot/css/prompt-input.css samples/Seeing.Agent.WebUI/Pages/_Host.cshtml
git commit -m "feat(webui): 浏览器录音脚本与语音按钮样式"
```

---

### Task 5: PromptInput 组件接线

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/PromptInput.razor`（模板、`@code`、`OnInitialized`、`OnParametersSet`、`DisposeAsync`）

**Interfaces:**
- Consumes: Task 4 的全部 JS 全局函数与回调方法名；Task 2 的 `VoiceTextInsertion.Insert`、`VoiceInputLayout.MaxRecordingSeconds`、`VoiceInsertAnchor`
- Produces: 无对外新接口（不新增 `[Parameter]`）

- [ ] **Step 1: 加注入与状态字段**

组件顶部 `@using` 区补 `@using Seeing.Agent.Llm`，并加：

```razor
@inject IModelConfigManager ModelManager
@inject NavigationManager Navigation
```

`@code` 内新增：

```csharp
/// <summary>语音按钮状态。</summary>
private enum VoiceState { Idle, Recording, Transcribing }

private VoiceState _voiceState = VoiceState.Idle;
private bool _voiceReady;
private string? _voiceUnavailableReason;
private string? _voiceError;
private int _voiceSeconds;
private string? _voiceMimeType;

/// <summary>进入 Transcribing 时的会话快照：回调若发现会话已变，只清状态不插文本。</summary>
private string? _voiceBoundSessionKey;
```

- [ ] **Step 2: 灰态判定与订阅**

```csharp
protected override void OnInitialized()
{
    ModelManager.ModelConfigChanged += OnModelConfigChanged;
    RefreshVoiceAvailability();
}

/// <summary>重算语音可用性：走 Router 五层校验，能区分「没配」「模型不在目录」等原因。</summary>
private void RefreshVoiceAvailability()
{
    _voiceReady = ModelManager.TryResolveDefault<ISpeechTranscriptionClient>(
        LlmCapabilityKind.SpeechTranscription,
        out _,
        out _voiceUnavailableReason);
}

private void OnModelConfigChanged(object? sender, ModelConfigChangedEventArgs e)
    => _ = InvokeAsync(() =>
    {
        RefreshVoiceAvailability();
        StateHasChanged();
    });
```

`DisposeAsync` 末尾（既有 `try/catch` **之外**，确保一定执行）加 `ModelManager.ModelConfigChanged -= OnModelConfigChanged;`。

- [ ] **Step 3: 加按钮与错误条**

在 `.prompt-actions` 的 `<Space>` 内、「上传文件」`SpaceItem` **之前**插入：

```razor
<SpaceItem>
    <Tooltip Title="@VoiceButtonTooltip">
        <Button Type="@ButtonType.Text"
                Size="@ButtonSize.Small"
                Class="@VoiceButtonClass"
                OnClick="@OnVoiceClick"
                Disabled="@(_voiceState == VoiceState.Transcribing)">
            @if (_voiceState == VoiceState.Recording)
            {
                <span class="prompt-voice-recording-dot"></span>
                <span style="font-size: 12px;">@VoiceElapsedText</span>
            }
            else
            {
                <Icon Type="@IconType.Outline.Sound" />
            }
        </Button>
    </Tooltip>
</SpaceItem>
```

错误条加在既有 `_uploadError` 提示块之后：

```razor
@if (!string.IsNullOrEmpty(_voiceError))
{
    <div class="prompt-voice-error">
        <span>@_voiceError</span>
        @if (!_voiceReady)
        {
            <button class="prompt-voice-error-action" @onclick="@(() => Navigation.NavigateTo("/models"))">去模型页配置</button>
        }
    </div>
}
```

配套属性：

```csharp
private string VoiceButtonTooltip => _voiceState switch
{
    VoiceState.Recording => "点击结束并转写",
    VoiceState.Transcribing => "正在转写…",
    _ => _voiceReady ? "语音输入" : _voiceUnavailableReason ?? "未配置语音转写模型"
};

private string VoiceButtonClass
{
    get
    {
        var classes = "prompt-voice-btn";
        if (!_voiceReady) classes += " prompt-voice-btn--unavailable";
        if (_voiceState == VoiceState.Recording) classes += " prompt-voice-btn--recording";
        return classes;
    }
}

private string VoiceElapsedText => $"{_voiceSeconds / 60}:{_voiceSeconds % 60:D2}";
```

**`Disabled` 只在 `Transcribing` 时置位**；`!VoiceReady` 时只加灰调 class，保持可点击。

- [ ] **Step 4: 点击与三个 `[JSInvokable]` 回调**

签名：

```csharp
private async Task OnVoiceClick();

[JSInvokable]
public Task HandleVoiceTick(int seconds);

[JSInvokable]
public Task HandleVoiceTranscribed(string? text, string? error, string? code);

[JSInvokable]
public Task HandleVoiceInterrupted(string? reason);
```

实现约束：

1. `OnVoiceClick`：先 `_voiceError = null`。
   - `_voiceState == Recording` → **先**置 `_voiceBoundSessionKey = SessionKey` 与 `_voiceState = Transcribing`，**再** `await JSRuntime.InvokeVoidAsync("stopVoiceRecording")`
     （顺序不能反：上传可能很快，回调先到而状态还是 `Recording` 会打乱判断）。
   - `_voiceState == Idle` 且 `!_voiceReady` → `_voiceError = _voiceUnavailableReason ?? "未配置语音转写模型"`，`StateHasChanged()`，**直接 return，不调 JS**。
   - `_voiceReady` → `await JSRuntime.InvokeAsync<VoiceStartResult>("startVoiceRecording", ContainerId, TextareaId, _pasteRef, VoiceInputLayout.MaxRecordingSeconds)`；
     `ok == true` → `_voiceState = Recording`、`_voiceSeconds = 0`、`_voiceMimeType = mimeType`；
     否则 `_voiceError = message`（`message` 空时按 `code` 映射中文）。
   - 开头先 `_pasteRef ??= DotNetObjectReference.Create(this);`（`OnAfterRender` 首次渲染才创建）。
2. `HandleVoiceTick`：`_voiceSeconds = seconds; StateHasChanged();`
3. `HandleVoiceTranscribed`：
   - `_voiceState = Idle; _voiceSeconds = 0;`
   - **会话已变**（`!string.Equals(_voiceBoundSessionKey, SessionKey, StringComparison.Ordinal)`）→ 清 `_voiceBoundSessionKey`，`StateHasChanged()`，**直接结束**（不插入、不报错）。
   - `error` 非空 → `_voiceError = error`，`StateHasChanged()`，结束。
   - `text` 空白 → `_voiceError = "未识别到语音"`（**不插入空串**），`_voiceBoundSessionKey = null`，结束。
   - 否则取锚点 `await JSRuntime.InvokeAsync<VoiceInsertAnchor>("getVoiceInsertAnchor", TextareaId)`，
     调 `VoiceTextInsertion.Insert(InputText, anchor.Start, anchor.End, text)` 写回 `InputText`，
     `StateHasChanged()`，再 `await JSRuntime.InvokeVoidAsync("setVoiceCaret", TextareaId, caret)`。
     JS 调用抛异常时退化为「追加到末尾」并把光标置于文末（`InputText.Length`）。
   - 最后清 `_voiceError` 与 `_voiceBoundSessionKey`。
4. `HandleVoiceInterrupted`：重置 `Idle`、`_voiceSeconds = 0`，`reason` 非空写入 `_voiceError`。
5. **会话切换**：`OnParametersSet` 既有 SessionKey 变更分支里追加
   `if (_voiceState != VoiceState.Idle) { _ = JSRuntime.InvokeVoidAsync("cancelVoiceRecording"); _voiceState = VoiceState.Idle; _voiceSeconds = 0; _voiceMimeType = null; }`。
6. `DisposeAsync` 既有 `try/catch` 内加 `await JSRuntime.InvokeVoidAsync("disposeVoiceInput");`。
7. **`Transcribing` 期间切会话**：约束 5 已取消上传；但 JS 的 `cancelVoiceRecording` 必须同时置 `cancelled = true`，
   让迟到的上传结果被丢弃 —— 配合约束 3 的会话快照双重兜底。

若 `VoiceStartResult` 在 Task 2 未定义，在本步骤补到 `Models/VoiceInputLayout.cs`：

```csharp
/// <summary>JS 启动录音的返回值。</summary>
public sealed class VoiceStartResult
{
    /// <summary>是否成功开始录音。</summary>
    public bool Ok { get; set; }

    /// <summary>失败代码（insecureContext / unsupportedBrowser / permissionDenied / noDevice / alreadyRecording）。</summary>
    public string? Code { get; set; }

    /// <summary>失败时的可读原因。</summary>
    public string? Message { get; set; }

    /// <summary>实际协商到的音频 MIME 类型。</summary>
    public string? MimeType { get; set; }
}
```

- [ ] **Step 5: 构建并修复编译问题**

Run:
```
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 构建成功。

- [ ] **Step 6: 手工验收**

逐条执行 `docs/superpowers/specs/2026-11-06-webui-voice-input-design.md` §7 的 9 条。
前 7 条必须全部通过；第 8 条（Photino Windows）本机可跑则验，跑不了记为未验；
第 9 条（macOS）**不验**，在提交信息里注明已知边界。

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/PromptInput.razor samples/Seeing.Agent.WebUI/Models/VoiceInputLayout.cs
git commit -m "feat(webui): prompt 输入框语音按钮与转写结果插入"
```

---

### Task 6: 架构文档记录

**Files:**
- Create: `docs/architecture/33-webui-voice-input-release-notes.md`
- Modify: `docs/architecture/README.md`（索引加一行）
- Modify: `AGENTS.md`（`WHERE TO LOOK` 加一行）

**Interfaces:** 无代码接口。

- [ ] **Step 1: 写 release notes**

至少包含：

- 背景：ASR 能力早已存在但 WebUI 无调用方，本次只补 UI 入口；
- 分层：`voice-input.js` → `/api/transcriptions` → `SpeechTranscriptionService` → `ILlmInvoker` → `ISpeechTranscriptionClient`；
- 关键决策：点击切换而非按住说话；灰态可点而非 `Disabled`（触屏可达性）；组件直读 `IModelConfigManager` 而非另设预检端点；`ModelReference.ToWireModelId` 放 `Tools.Support` 且**不改 Memory 包**；
- 已知边界：macOS 桌面宿主（WKWebView）需 Info.plist 麦克风声明且只能出 `audio/mp4`，本期未验；流式字幕（`TranscribeStreamAsync`）未接线；
- 手工验收清单（与设计文档 §7 同源）。

- [ ] **Step 2: 更新索引**

`docs/architecture/README.md` 按编号顺序加：

```markdown
- [WebUI 语音输入 Release Notes](33-webui-voice-input-release-notes.md)
```

`AGENTS.md` 的 `WHERE TO LOOK` 表加一行：

```markdown
| 语音输入（PromptInput 麦克风） | 脚本 `samples/Seeing.Agent.WebUI/wwwroot/js/voice-input.js`；端点 `samples/Seeing.Agent.WebUI/Endpoints/TranscriptionEndpoints.cs`；服务 `samples/Seeing.Agent.WebUI/Services/SpeechTranscriptionService.cs` | 点击切换录音（非按住说话）；灰态可点非 Disabled；ASR 走既有 `ISpeechTranscriptionClient` + `DefaultModels.speechTranscription`；见 `docs/architecture/33` |
```

- [ ] **Step 3: 提交**

```bash
git add docs/architecture/33-webui-voice-input-release-notes.md docs/architecture/README.md AGENTS.md
git commit -m "docs(architecture): 记录 WebUI 语音输入设计与已知边界"
```
