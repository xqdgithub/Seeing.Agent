using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// OpenAiSpeechTranscriptionClient multipart 构造 / verbose_json 段落解析 / 流式 SSE /
/// 真实探针连接测试（spec §7.10 + 决议 25）。
/// 重点：<b>Stream 所有权——客户端不得 Dispose 调用方的 Audio 流</b>（决议 17 / Review Focus #4）。
/// </summary>
public class OpenAiSpeechTranscriptionClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "test-provider",
        Type = ProviderTypes.OpenAi,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "test-key"
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>记录 Dispose 是否发生的装饰流（决议 17 的探针）。</summary>
    private sealed class DisposeTrackingStream : Stream
    {
        private readonly Stream _inner;
        public bool Disposed { get; private set; }

        public DisposeTrackingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<(HttpRequestMessage Request, byte[] Body)> Requests = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request, body));
            return _respond(request);
        }
    }

    private static (OpenAiSpeechTranscriptionClient Client, StubHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHandler(respond ?? (_ => JsonResponse("{\"text\":\"hello\"}")));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new OpenAiSpeechTranscriptionClient(CreateConfig(), httpClient, NullLogger.Instance), handler);
    }

    /// <summary>multipart 文本字段用 Latin1 解码保证字节保真（音频字节可能非 UTF-8）。</summary>
    private static string Decode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append((char)b);
        return sb.ToString();
    }

    // ---- 决议 17 / Review Focus #4：不得 Dispose 调用方的流 ----

    [Fact]
    public async Task Transcribe_ShouldNotDisposeCallerStream()
    {
        var (sut, _) = CreateSut();
        var stream = new DisposeTrackingStream(new MemoryStream(new byte[64]));

        await sut.TranscribeAsync(new TranscriptionRequest { Model = "whisper-1", Audio = stream });

        stream.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Transcribe_CallerStream_ShouldRemainReadableAfterCall()
    {
        // 上传只读消耗流；调用后所有权仍归调用方——可回卷重读，且未被释放
        var (sut, _) = CreateSut();
        using var inner = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var stream = new DisposeTrackingStream(inner);

        await sut.TranscribeAsync(new TranscriptionRequest { Model = "whisper-1", Audio = stream });

        stream.Disposed.Should().BeFalse();
        stream.CanRead.Should().BeTrue();
        stream.Seek(0, SeekOrigin.Begin);
        var replay = new byte[4];
        stream.ReadExactly(replay);
        replay.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task TranscribeStream_ShouldNotDisposeCallerStream()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"delta\":\"hi\"}\n\ndata: {\"finish_reason\":\"stop\"}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream")
        });
        var stream = new DisposeTrackingStream(new MemoryStream(new byte[64]));

        await foreach (var _ in sut.TranscribeStreamAsync(
            new TranscriptionRequest { Model = "whisper-1", Audio = stream }))
        {
        }

        stream.Disposed.Should().BeFalse();
    }

    // ---- multipart 构造 ----

    [Fact]
    public async Task TranscribeAsync_ShouldPostMultipartWithModelFileAndVerboseJson()
    {
        var (sut, handler) = CreateSut();

        await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[64]),
            Language = "zh"
        });

        handler.Requests.Should().HaveCount(1);
        var (request, body) = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/audio/transcriptions");
        request.Content!.Headers.ContentType!.MediaType.Should().Be("multipart/form-data");

        var text = Decode(body);
        // 注：.NET MultipartFormDataContent 对纯 ASCII 字段名不加引号（name=model 而非 name="model"）
        text.Should().Contain("name=model");
        text.Should().Contain("whisper-1");
        text.Should().Contain("name=language");
        text.Should().Contain("zh");
        text.Should().Contain("name=response_format");
        text.Should().Contain("verbose_json");
        text.Should().Contain("name=file");
        // 无显式文件名时按 MIME 推断扩展名；MimeType 未给 → 兜底 mp3
        text.Should().Contain("filename=audio.mp3");
        text.Should().NotContain("name=stream");
    }

    [Fact]
    public async Task TranscribeAsync_ExplicitFileNameAndMimeType_ShouldPassThrough()
    {
        var (sut, handler) = CreateSut();

        await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[8]),
            MimeType = "audio/wav",
            FileName = "meeting.wav"
        });

        var text = Decode(handler.Requests[0].Body);
        text.Should().Contain("filename=meeting.wav");
        text.Should().Contain("Content-Type: audio/wav");
    }

    [Fact]
    public async Task TranscribeAsync_StringSegmentId_ShouldParseWithoutThrowing()
    {
        // SiliconFlow（XingChenASR）把 segment.id 返回成字符串 "0"，不是数字。
        // 该字段从未被读取，但类型过窄会让整条转写失败——
        // 用户看到的是「转写失败」而后端其实已经成功识别出文字。
        var (sut, _) = CreateSut(_ => JsonResponse("""
            {"duration":2.58,"text":"你是谁呀？",
             "segments":[{"type":"transcript.text.segment","id":"0","start":0.92,"end":2.56,"text":"你是谁呀？"}],
             "usage":{"type":"duration","seconds":3}}
            """));

        var result = await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "XingChenASR-V3.2-Ultra",
            Audio = new MemoryStream(new byte[64])
        });

        result.Text.Should().Be("你是谁呀？");
        result.Segments.Should().HaveCount(1);
        result.Segments![0].Text.Should().Be("你是谁呀？");
        result.Segments[0].Start.Should().Be(TimeSpan.FromSeconds(0.92));
        result.Segments[0].End.Should().Be(TimeSpan.FromSeconds(2.56));
    }

    [Fact]
    public async Task TranscribeAsync_NonTokenUsageShape_ShouldNotBreakDeserialization()
    {
        // SiliconFlow 的 usage 是 {"type":"duration","seconds":3}，没有 input_tokens；
        // 只需不抛异常，用量按「无 token 计量」处理。
        var (sut, _) = CreateSut(_ => JsonResponse("""
            {"text":"你好","usage":{"type":"duration","seconds":3}}
            """));

        var result = await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "XingChenASR-V3.2-Ultra",
            Audio = new MemoryStream(new byte[64])
        });

        result.Text.Should().Be("你好");
    }

    // ---- 响应解析 ----

    [Fact]
    public async Task TranscribeAsync_VerboseJson_ShouldParseTextSegmentsAndUsage()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("""
            {"text":"你好 世界","language":"zh","duration":2.5,
             "segments":[
               {"id":0,"start":0.0,"end":1.0,"text":"你好"},
               {"id":1,"start":1.0,"end":2.5,"text":"世界"}],
             "usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14}}
            """));

        var result = await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[64])
        });

        result.Text.Should().Be("你好 世界");
        result.Segments.Should().HaveCount(2);
        result.Segments![0].Text.Should().Be("你好");
        result.Segments[0].Start.Should().Be(TimeSpan.FromSeconds(0));
        result.Segments[0].End.Should().Be(TimeSpan.FromSeconds(1));
        result.Segments[1].Text.Should().Be("世界");
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(10);
        result.Usage.OutputTokens.Should().Be(4);
    }

    [Fact]
    public async Task TranscribeAsync_PlainJsonWithoutSegments_ShouldReturnNullSegments()
    {
        var (sut, _) = CreateSut(_ => JsonResponse("{\"text\":\"hi\"}"));

        var result = await sut.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[64])
        });

        result.Text.Should().Be("hi");
        result.Segments.Should().BeNull("服务端未返回细粒度时段时契约要求 null");
        result.Usage.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeStreamAsync_SseDeltas_ShouldYieldSegmentsInOrder()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n",
                "data: {\"delta\":\"你好\"",  // 非法 JSON → 跳过不炸
                "",
                "data: {\"delta\":\"你好\"}",
                "",
                "data: {\"delta\":\"世界\"}",
                "",
                "data: {\"finish_reason\":\"stop\"}",
                "",
                "data: [DONE]",
                ""), Encoding.UTF8, "text/event-stream")
        });

        var segments = new List<TranscriptSegment>();
        await foreach (var s in sut.TranscribeStreamAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[64])
        }))
        {
            segments.Add(s);
        }

        segments.Select(s => s.Text).Should().Equal("你好", "世界");
    }

    // ---- 决议 25 连接测试探针 ----

    [Fact]
    public async Task TestConnectionAsync_TranscriptionProbe_ShouldSendSilentAudioAndReturnTrue()
    {
        // 传极短静音音频，校验结构合法（能解析即通过）
        var (sut, handler) = CreateSut();

        var ok = await sut.TestConnectionAsync("whisper-1", LlmCapabilityKind.SpeechTranscription);

        ok.Should().BeTrue();
        handler.Requests.Should().HaveCount(1);
        var text = Decode(handler.Requests[0].Body);
        text.Should().Contain("name=file");
        text.Should().Contain("filename=connection-probe.wav");
    }

    [Fact]
    public async Task TestConnectionAsync_HttpFailure_ShouldReturnFalseNotThrow()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        (await sut.TestConnectionAsync("whisper-1", LlmCapabilityKind.SpeechTranscription)).Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_NonTranscriptionCapability_ShouldReturnFalse()
    {
        var (sut, handler) = CreateSut();

        (await sut.TestConnectionAsync("whisper-1", LlmCapabilityKind.Chat)).Should().BeFalse();
        handler.Requests.Should().BeEmpty("能力不匹配时不应发探针请求");
    }

    [Fact]
    public void Identity_ShouldComeFromConfig()
    {
        var (sut, _) = CreateSut();

        sut.ProviderId.Should().Be("test-provider");
        sut.ProviderType.Should().Be(ProviderTypes.OpenAi);
    }
}
