using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Seeing.Provider.MiMo.Tests;

/// <summary>
/// MiMo 语音转写客户端：协议形状与 OpenAI 完全不同（chat/completions + base64 音频），
/// 这些用例锁定它「按 MiMo 的形状发、按 MiMo 的形状收」。
/// </summary>
public class MiMoSpeechTranscriptionClientTests
{
    [Fact]
    public async Task TranscribeAsync_ShouldPostToChatCompletions_WithBase64AudioContentPart()
    {
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":"你好世界"}}],
             "usage":{"prompt_tokens":46,"completion_tokens":20}}
            """);
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([1, 2, 3, 4]);

        var result = await sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = "mimo-v2.5-asr",
                Audio = audio,
                MimeType = "audio/wav",
                FileName = "voice.wav",
            },
            ct: TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/chat/completions",
            "MiMo 的 ASR 走 chat 端点，不是 /audio/transcriptions");

        var body = handler.LastBody!;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be("mimo-v2.5-asr");

        var part = root.GetProperty("messages")[0].GetProperty("content")[0];
        part.GetProperty("type").GetString().Should().Be("input_audio");
        part.GetProperty("input_audio").GetProperty("data").GetString()
            .Should().StartWith("data:audio/wav;base64,");
        part.GetProperty("input_audio").GetProperty("data").GetString()
            .Should().EndWith(Convert.ToBase64String([1, 2, 3, 4]));

        root.GetProperty("asr_options").GetProperty("language").GetString().Should().Be("auto");

        result.Text.Should().Be("你好世界");
        result.Segments.Should().BeNull("MiMo 不返回段落级时间区间");
        result.Usage!.InputTokens.Should().Be(46);
        result.Usage.OutputTokens.Should().Be(20);
    }

    [Fact]
    public async Task TranscribeAsync_WithLanguage_ShouldPassThroughSupportedValue()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        await sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = "mimo-v2.5-asr",
                Audio = audio,
                MimeType = "audio/mpeg",
                Language = "zh",
            },
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("asr_options").GetProperty("language").GetString()
            .Should().Be("zh", "zh/en 是官方支持的取值，应原样透传");
    }

    [Fact]
    public async Task TranscribeAsync_WithUnsupportedLanguage_ShouldFallBackToAuto()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        await sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = "mimo-v2.5-asr",
                Audio = audio,
                MimeType = "audio/mpeg",
                Language = "ja",
            },
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("asr_options").GetProperty("language").GetString()
            .Should().Be("auto", "官方只支持 auto/zh/en，未知值回落而不是报错");
    }

    [Theory]
    [InlineData("audio/webm")]
    [InlineData("audio/ogg")]
    [InlineData("audio/flac")]
    public async Task TranscribeAsync_WithUnsupportedFormat_ShouldThrowReadableError(string mime)
    {
        var handler = new RecordingHandler("""{"choices":[]}""");
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        var act = () => sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = "mimo-v2.5-asr",
                Audio = audio,
                MimeType = mime,
            },
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*仅支持 mp3 / wav*");
        handler.RequestCount.Should().Be(0, "不支持的格式应在发请求前就拒绝");
    }

    [Fact]
    public async Task TranscribeAsync_ShouldNotDisposeCallerAudioStream()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        var sut = CreateSut(handler);
        var audio = new MemoryStream([1, 2, 3]);

        await sut.TranscribeAsync(
            new TranscriptionRequest { Model = "m", Audio = audio, MimeType = "audio/wav" },
            ct: TestContext.Current.CancellationToken);

        audio.CanRead.Should().BeTrue("决议 17：音频流归调用方所有，实现不得释放");
        audio.Dispose();
    }

    [Fact]
    public async Task TranscribeAsync_WhenServerReturnsError_ShouldThrowWithBody()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"model not found"}}""", HttpStatusCode.BadRequest);
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        var act = () => sut.TranscribeAsync(
            new TranscriptionRequest { Model = "bad", Audio = audio, MimeType = "audio/wav" },
            ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*400*model not found*");
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldUseSilentWavProbe_AndSucceedOnEmptyTranscript()
    {
        // 静音探针会转写出空文本；空文本仍是「模型可用」的合法证据
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":""}}]}""");
        var sut = CreateSut(handler);

        var ok = await sut.TestConnectionAsync(
            "mimo-v2.5-asr",
            LlmCapabilityKind.SpeechTranscription,
            ct: TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("input_audio").GetProperty("data").GetString()
            .Should().StartWith("data:audio/wav;base64,", "探针必须是 MiMo 接受的真实 WAV");
    }

    [Fact]
    public async Task TestConnectionAsync_ForNonSpeechCapability_ShouldFailWithoutRequest()
    {
        var handler = new RecordingHandler("{}");
        var sut = CreateSut(handler);

        var ok = await sut.TestConnectionAsync(
            "mimo-v2.5-asr", LlmCapabilityKind.Chat, ct: TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
        handler.RequestCount.Should().Be(0);
    }

    /// <summary>
    /// 流式转写：MiMo 的 SSE 增量在 <c>choices[].delta.content</c>（与转写端点的
    /// <c>delta</c> 字段不同），必须按 MiMo 的形状解析。
    /// </summary>
    [Fact]
    public async Task TranscribeStreamAsync_ShouldParseSseDeltasFromChoices()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"今天\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"天气很好\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new StreamingHandler(sse);
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in sut.TranscribeStreamAsync(
            new TranscriptionRequest { Model = "mimo-v2.5-asr", Audio = audio, MimeType = "audio/wav" },
            call: null,
            TestContext.Current.CancellationToken))
        {
            segments.Add(segment);
        }

        segments.Select(s => s.Text).Should().Equal("今天", "天气很好");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue(
            "流式必须显式带 stream=true");
    }

    [Fact]
    public async Task TranscribeStreamAsync_ShouldSkipMalformedChunksWithoutAborting()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"第一段\"}}]}",
            "",
            "data: {not valid json",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"第二段\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new StreamingHandler(sse);
        var sut = CreateSut(handler);
        using var audio = new MemoryStream([0]);

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in sut.TranscribeStreamAsync(
            new TranscriptionRequest { Model = "mimo-v2.5-asr", Audio = audio, MimeType = "audio/wav" },
            call: null,
            TestContext.Current.CancellationToken))
        {
            segments.Add(segment);
        }

        segments.Select(s => s.Text).Should().Equal(["第一段", "第二段"]);
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly string _sse;

        public StreamingHandler(string sse) => _sse = sse;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static MiMoSpeechTranscriptionClient CreateSut(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.xiaomimimo.com/v1/")
        };

        return new MiMoSpeechTranscriptionClient(
            new ProviderConfig { Id = "mimo", Type = ProviderTypes.OpenAi },
            http,
            NullLogger.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
