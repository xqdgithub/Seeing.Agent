using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools.Support.Speech;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.Speech.Tests;

public class TranscribeAudioToolTests
{
    private sealed class AllowAllPathGate : IWorkspacePathGate
    {
        public string? EnsureAllowed(string sessionId, string path) => null;
        public string? EnsureWithinWorkspace(string sessionId, string path) => null;
    }

    private sealed class DenyAllPathGate : IWorkspacePathGate
    {
        public string? EnsureAllowed(string sessionId, string path) => "路径不在工作区允许集内";
        public string? EnsureWithinWorkspace(string sessionId, string path) => "路径不在工作区允许集内";
    }

    private static TranscribeAudioTool CreateTool(IWorkspacePathGate? gate = null) =>
        new(NullLogger<TranscribeAudioTool>.Instance,
            new Seeing.IO.Local.LocalFileSystem(),
            gate ?? new AllowAllPathGate());

    private static string TempAudio(long bytes = 32, string ext = ".wav")
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing_asr_" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static ToolContext Context(
        IModelConfigManager? models = null,
        ILlmInvoker? invoker = null)
    {
        var services = new ServiceCollection();
        if (models is not null)
            services.AddSingleton(models);
        if (invoker is not null)
            services.AddSingleton(invoker);

        return new ToolContext
        {
            SessionId = "s1",
            Services = services.BuildServiceProvider(),
        };
    }

    private static Mock<IModelConfigManager> Models(string? defaultModel = "siliconflow/m-asr")
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.SpeechTranscription))
              .Returns(defaultModel);
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);
        return models;
    }

    private static Mock<ILlmInvoker> Invoker(TranscriptionResult result)
    {
        var client = new Mock<ISpeechTranscriptionClient>();
        client.Setup(c => c.TranscribeAsync(
                  It.IsAny<TranscriptionRequest>(),
                  It.IsAny<LlmCallContext?>(),
                  It.IsAny<CancellationToken>()))
              .ReturnsAsync(result);

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .Returns<string, Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>, LlmInvocationOptions?, CancellationToken>(
                   (_, call, _, ct) => call(client.Object, new LlmCallContext(), ct));
        return invoker;
    }

    [Fact]
    public async Task ExecuteAsync_MissingPath_ShouldFail()
    {
        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task ExecuteAsync_MissingFile_ShouldFail()
    {
        var path = Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N") + ".wav");

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("文件不存在");
    }

    [Fact]
    public async Task ExecuteAsync_NonAudioExtension_ShouldFailBeforeCallingModel()
    {
        var path = TempAudio(ext: ".txt");
        var invoker = Invoker(new TranscriptionResult("x", null, null));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不是支持的音频格式");
        invoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFile_ShouldFail()
    {
        var path = TempAudio(bytes: 0);

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("为空");
    }

    [Fact]
    public async Task ExecuteAsync_TooLarge_ShouldFailBeforeCallingModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing_big_" + Guid.NewGuid().ToString("N") + ".wav");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            fs.SetLength(AudioFormatSupport.MaxAudioBytes + 1);

        var invoker = Invoker(new TranscriptionResult("x", null, null));
        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        File.Delete(path);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("超过上限");
        invoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_NoDefaultModel_ShouldFailWithReadableReason()
    {
        var path = TempAudio();

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }),
            Context(Models(defaultModel: null).Object, Invoker(new TranscriptionResult("x", null, null)).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("speechTranscription");
        result.Error.Should().Contain("未配置");
    }

    [Fact]
    public async Task ExecuteAsync_NoDefaultModel_ShouldNotGuessFromCatalog()
    {
        // 用户明确要求：未配置时直接报错，不自动挑目录里唯一的 ASR 模型。
        var path = TempAudio();
        var models = Models(defaultModel: null);
        models.Setup(m => m.GetModelsByType(It.IsAny<ModelType>(), It.IsAny<string?>()))
              .Returns(new Dictionary<string, ModelConfig>
              {
                  ["siliconflow/only-asr"] = new() { Id = "only-asr" },
              });

        var invoker = Invoker(new TranscriptionResult("x", null, null));
        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(models.Object, invoker.Object));

        result.Success.Should().BeFalse();
        invoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitModel_ShouldOverrideDefault()
    {
        var path = TempAudio();
        string? usedModel = null;

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
                   (model, call, _, ct) =>
                   {
                       usedModel = model;
                       return call(client.Object, new LlmCallContext(), ct);
                   });

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path, model = "mimo/mimo-v2.5-asr" }),
            Context(Models().Object, invoker.Object));

        result.Success.Should().BeTrue();
        usedModel.Should().Be("mimo/mimo-v2.5-asr");
        result.Metadata["model"].Should().Be("mimo/mimo-v2.5-asr");
    }

    [Fact]
    public async Task ExecuteAsync_Success_ShouldReturnTextAndMetadata()
    {
        var path = TempAudio(bytes: 512);
        var invoker = Invoker(new TranscriptionResult("你好，世界。", null, null));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("你好，世界。");
        result.Output.Should().Contain("该模型未返回分段");
        result.Metadata["mimeType"].Should().Be("audio/wav");
        result.Metadata["size"].Should().Be(512L);
        result.Metadata["segmentCount"].Should().Be(0);
        result.Metadata["path"].Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public async Task ExecuteAsync_WithSegments_ShouldIncludeTimestamps()
    {
        var path = TempAudio();
        var invoker = Invoker(new TranscriptionResult("第一句。第二句。",
        [
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "第一句。"),
            new TranscriptSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), "第二句。"),
        ], null));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("[00:00 - 00:02] 第一句。");
        result.Metadata["segmentCount"].Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTranscript_ShouldStillSucceed()
    {
        var path = TempAudio();
        var invoker = Invoker(new TranscriptionResult("", null, null));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        result.Success.Should().BeTrue("静音不是工具失败，重试没有意义");
        result.Output.Should().Contain("未识别到语音");
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedProviderFailure_ShouldSurfaceExceptionMessage()
    {
        // 兜底分支必须带上 ex.Message：HTTP 状态码与服务端错误体是唯一能定位问题的线索，
        // 只回「语音转写失败」会让用户无从判断是格式、鉴权还是服务端问题。
        var path = TempAudio();
        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                  It.IsAny<string>(),
                  It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                  It.IsAny<LlmInvocationOptions?>(),
                  It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("HTTP 400 Bad Request: (无 body)"));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object, invoker.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("语音转写失败");
        result.Error.Should().Contain("HTTP 400", "错误文本必须带状态码，否则用户无从定位");
    }

    [Fact]
    public async Task ExecuteAsync_PathDenied_ShouldFail()
    {
        var path = TempAudio();

        var result = await CreateTool(new DenyAllPathGate()).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), Context(Models().Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("路径不在工作区允许集内");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassSeekableStreamAndLanguage()
    {
        var path = TempAudio();
        TranscriptionRequest? captured = null;

        var client = new Mock<ISpeechTranscriptionClient>();
        client.Setup(c => c.TranscribeAsync(
                  It.IsAny<TranscriptionRequest>(),
                  It.IsAny<LlmCallContext?>(),
                  It.IsAny<CancellationToken>()))
              .Returns<TranscriptionRequest, LlmCallContext?, CancellationToken>((req, _, _) =>
              {
                  captured = req;
                  req.Audio.CanSeek.Should().BeTrue();
                  req.Audio.Position.Should().Be(0);
                  return Task.FromResult(new TranscriptionResult("ok", null, null));
              });

        var invoker = new Mock<ILlmInvoker>();
        invoker.Setup(i => i.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                   It.IsAny<string>(),
                   It.IsAny<Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>>(),
                   It.IsAny<LlmInvocationOptions?>(),
                   It.IsAny<CancellationToken>()))
               .Returns<string, Func<ISpeechTranscriptionClient, LlmCallContext, CancellationToken, Task<TranscriptionResult>>, LlmInvocationOptions?, CancellationToken>(
                   (_, call, _, ct) => call(client.Object, new LlmCallContext(), ct));

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path, language = "zh" }),
            Context(Models().Object, invoker.Object));

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Language.Should().Be("zh");
        captured.FileName.Should().Be(Path.GetFileName(path));
        captured.MimeType.Should().Be("audio/wav");
        captured.Model.Should().Be("m-asr", "配置里的 provider/ 前缀要剥掉");
    }

    [Fact]
    public async Task ExecuteAsync_NoServices_ShouldFail()
    {
        var path = TempAudio();

        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未接入");
    }
}
