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
    public async Task TranscribeAsync_Should_Not_Dispose_Caller_Audio_Stream()
    {
        // 契约决议 17：Audio 流由调用方拥有，实现不得 Dispose。
        // 重试装饰器还要 Seek 回起点，提前关流会让重试路径直接炸。
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns("openai/whisper-1");
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)(null));

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
                   (_, call, _, ct) => call(client.Object, new LlmCallContext(), ct));

        var audio = new MemoryStream([1, 2, 3]);
        await Create(models.Object, invoker.Object)
            .TranscribeAsync("s1", audio, "audio/webm", "voice.webm");

        audio.CanRead.Should().BeTrue("服务层不得释放调用方的音频流");
        audio.Position.Should().Be(0, "服务层不得改动调用方持有的流位置");
    }

    [Fact]
    public async Task TranscribeAsync_Should_Return_Empty_String_Verbatim()
    {
        // 空结果不能变成 null：端点据此输出 text:"" ，前端才能落到「未识别到语音」分支。
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns("openai/whisper-1");
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)(null));

        var client = new Mock<ISpeechTranscriptionClient>();
        client.Setup(c => c.TranscribeAsync(
                  It.IsAny<TranscriptionRequest>(),
                  It.IsAny<LlmCallContext?>(),
                  It.IsAny<CancellationToken>()))
              .ReturnsAsync(new TranscriptionResult("", null, null));

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .Returns<string, Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>, LlmInvocationOptions?, CancellationToken>(
                   (_, call, _, ct) => call(client.Object, new LlmCallContext(), ct));

        var text = await Create(models.Object, invoker.Object)
            .TranscribeAsync("s1", new MemoryStream([1]), "audio/webm", "voice.webm");

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_Should_Propagate_Provider_Failure_Unchanged()
    {
        // 端点靠 catch 把它映射成 502 + 原始 message；服务层不得吞掉或包装。
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns("openai/whisper-1");
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)(null));

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("upstream 400: unsupported audio format"));

        var act = () => Create(models.Object, invoker.Object)
            .TranscribeAsync("s1", new MemoryStream([1]), "audio/webm", "voice.webm");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().Contain("unsupported audio format");
    }

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
