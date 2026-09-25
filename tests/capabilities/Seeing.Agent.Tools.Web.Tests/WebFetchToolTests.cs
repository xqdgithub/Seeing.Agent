using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Core.Tools.Web.Tests;

public class WebFetchToolTests
{
    private const long MaxResponseSize = 5L * 1024 * 1024;

    [Fact]
    public async Task ExecuteAsync_RedirectToLinkLocalAddress_ShouldReject()
    {
        // 初始请求返回 302，重定向目标为云元数据私网地址
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == "/start"
                ? Redirect("http://169.254.169.254/latest/meta-data")
                : Ok("should-not-reach"));

        var tool = CreateTool(handler);
        var result = await tool.ExecuteAsync(Args("http://1.1.1.1/start"), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("安全策略拒绝");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RedirectToPublicAddress_ShouldFollow()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == "/start"
                ? Redirect("http://1.1.1.1/final")
                : Ok("hello-public"));

        var tool = CreateTool(handler);
        var result = await tool.ExecuteAsync(Args("http://1.1.1.1/start"), new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello-public");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_RedirectChainExceedsLimit_ShouldFail()
    {
        // 每次请求都返回 302，超过 5 跳上限
        var handler = new StubHttpMessageHandler(_ => Redirect("http://1.1.1.1/loop"));

        var tool = CreateTool(handler);
        var result = await tool.ExecuteAsync(Args("http://1.1.1.1/start"), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("重定向次数超限");
        handler.RequestCount.Should().Be(6);
    }

    [Fact]
    public async Task ExecuteAsync_ChunkedOversizedStream_ShouldFailWithoutReadingAll()
    {
        // 无 Content-Length 的超大流：应限长读取并失败，且不得读完全部数据
        var stream = new GeneratedStream(MaxResponseSize + 1024 * 1024, (byte)'a');
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new RawContent(stream)
        });

        var tool = CreateTool(handler);
        var result = await tool.ExecuteAsync(Args("http://1.1.1.1/big"), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("响应过大");
        stream.BytesRead.Should().BeLessThan(stream.Total);
    }

    private static WebFetchTool CreateTool(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new WebFetchTool(NullLogger<WebFetchTool>.Instance, client);
    }

    private static JsonElement Args(string url) => JsonSerializer.SerializeToElement(new { url });

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) }
    };

    private static HttpResponseMessage Ok(string body, string mediaType = "text/plain") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>
    /// 不设置 Content-Length 的内容，直接暴露底层流（模拟 chunked 响应）。
    /// </summary>
    private sealed class RawContent(Stream stream) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);

        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// 惰性生成的只读流，用于模拟超大响应体而不一次性分配全部内存。
    /// </summary>
    private sealed class GeneratedStream(long total, byte value) : Stream
    {
        private long _position;

        public long Total => total;
        public long BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = total - _position;
            if (remaining <= 0)
                return 0;

            var toRead = (int)Math.Min(count, remaining);
            Array.Fill(buffer, value, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = total - _position;
            if (remaining <= 0)
                return ValueTask.FromResult(0);

            var toRead = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..toRead].Fill(value);
            _position += toRead;
            return ValueTask.FromResult(toRead);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
