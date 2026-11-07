using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// OpenAiSpeechSynthesisClient 请求映射 / 非流式完整字节 / 流式自然分块 / 真实探针连接测试
/// （spec §7.10 + 决议 25）。
/// </summary>
public class OpenAiSpeechSynthesisClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "test-provider",
        Type = ProviderTypes.OpenAi,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "test-key"
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public readonly List<(HttpRequestMessage Request, string Body)> Requests = new();

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return _respond();
        }
    }

    /// <summary>按预设块序供给字节的流：每次读取恰好返回一个预设块（模拟响应体自然分块）。</summary>
    private sealed class ScriptedChunkStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();

        public ScriptedChunkStream(params byte[][] chunks)
        {
            foreach (var chunk in chunks)
                _chunks.Enqueue(chunk);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_chunks.Count == 0) return 0;
            var chunk = _chunks.Peek();
            var n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer);
            if (n == chunk.Length) _chunks.Dequeue();
            else _chunks.Enqueue(chunk[n..]);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromResult(Read(buffer.Span));
    }

    private static HttpResponseMessage AudioResponse(byte[] audio, string? contentType = "audio/mpeg")
    {
        var content = new ByteArrayContent(audio);
        if (contentType != null)
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static (OpenAiSpeechSynthesisClient Client, StubHandler Handler) CreateSut(
        Func<HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new OpenAiSpeechSynthesisClient(CreateConfig(), httpClient, NullLogger.Instance), handler);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldPostToAudioSpeech_WithMappedBody()
    {
        var (client, handler) = CreateSut(() => AudioResponse(new byte[] { 1, 2, 3 }));

        var result = await client.SynthesizeAsync(new SpeechSynthesisRequest
        {
            Model = "tts-1",
            Text = "你好世界",
            Voice = "nova",
            Format = "flac",
            Speed = 1.25
        });

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Request.RequestUri!.AbsolutePath.Should().Be("/audio/speech");

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("tts-1");
        root.GetProperty("input").GetString().Should().Be("你好世界");
        root.GetProperty("voice").GetString().Should().Be("nova");
        root.GetProperty("response_format").GetString().Should().Be("flac");
        root.GetProperty("speed").GetDouble().Should().Be(1.25);

        result.Audio.ToArray().Should().Equal(1, 2, 3);
        result.MimeType.Should().Be("audio/mpeg"); // 优先响应 Content-Type
    }

    [Fact]
    public async Task SynthesizeAsync_VoiceAndFormatUnset_ShouldDefaultToAlloyMp3()
    {
        var (client, handler) = CreateSut(() => AudioResponse(new byte[] { 9 }, contentType: null));

        var result = await client.SynthesizeAsync(new SpeechSynthesisRequest
        {
            Model = "tts-1",
            Text = "hi"
        });

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.GetProperty("voice").GetString().Should().Be("alloy");
        doc.RootElement.GetProperty("response_format").GetString().Should().Be("mp3");

        // 无 Content-Type 时按请求格式推断 MIME
        result.MimeType.Should().Be("audio/mpeg");
    }

    [Fact]
    public async Task SynthesizeStreamAsync_ShouldYieldNaturalBodyChunksNotFixedFraming()
    {
        // Arrange：响应体以 13 / 100 / 7 字节自然分块到达（不是固定 8KB 帧）
        var (client, _) = CreateSut(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ScriptedChunkStream(
                new byte[13], new byte[100], new byte[7]))
        });

        var chunks = new List<AudioChunk>();
        await foreach (var chunk in client.SynthesizeStreamAsync(new SpeechSynthesisRequest
        {
            Model = "tts-1",
            Text = "stream me"
        }))
        {
            chunks.Add(chunk);
        }

        // Assert：分块数与每块大小忠实于响应体自然供给
        chunks.Select(c => c.Data.Length).Should().Equal(13, 100, 7);
        chunks.Should().AllSatisfy(c => c.MimeType.Should().Be("audio/mpeg"));
    }

    [Fact]
    public async Task SynthesizeStreamAsync_EmptyBody_ShouldYieldNoChunks()
    {
        var (client, _) = CreateSut(() => AudioResponse(Array.Empty<byte>()));

        var chunks = new List<AudioChunk>();
        await foreach (var chunk in client.SynthesizeStreamAsync(new SpeechSynthesisRequest
        {
            Model = "tts-1",
            Text = "x"
        }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnectionAsync_SpeechSynthesisProbe_ShouldRequireNonEmptyAudioBytes()
    {
        // 决议 25：合成一句短句，校验返回非空字节
        var (okClient, okHandler) = CreateSut(() => AudioResponse(new byte[] { 1, 2, 3 }));
        (await okClient.TestConnectionAsync("tts-1", LlmCapabilityKind.SpeechSynthesis)).Should().BeTrue();
        okHandler.Requests.Should().HaveCount(1);

        // 空音频 → 判失败
        var (emptyClient, _) = CreateSut(() => AudioResponse(Array.Empty<byte>()));
        (await emptyClient.TestConnectionAsync("tts-1", LlmCapabilityKind.SpeechSynthesis)).Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_HttpFailure_ShouldReturnFalseNotThrow()
    {
        var (client, _) = CreateSut(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        (await client.TestConnectionAsync("tts-1", LlmCapabilityKind.SpeechSynthesis)).Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_NonSynthesisCapability_ShouldReturnFalse()
    {
        var (client, handler) = CreateSut(() => AudioResponse(new byte[] { 1 }));

        (await client.TestConnectionAsync("tts-1", LlmCapabilityKind.Chat)).Should().BeFalse();
        handler.Requests.Should().BeEmpty("能力不匹配时不应发探针请求");
    }

    [Fact]
    public void Identity_ShouldComeFromConfig()
    {
        var (client, _) = CreateSut(() => AudioResponse(Array.Empty<byte>()));

        client.ProviderId.Should().Be("test-provider");
        client.ProviderType.Should().Be(ProviderTypes.OpenAi);
    }
}
