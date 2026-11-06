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
