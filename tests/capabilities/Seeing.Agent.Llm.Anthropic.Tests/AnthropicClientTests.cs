using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;
using Xunit;

namespace Seeing.Agent.Llm.Anthropic.Tests;

/// <summary>
/// AnthropicClient 流式 usage / HttpClient 所有权 / 释放行为测试。
/// </summary>
public class AnthropicClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "test-provider",
        Type = ProviderTypes.Anthropic,
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "test-key"
    };

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    /// <summary>可捕获请求并可控响应的 stub handler；记录 Dispose 以断言所有权。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public readonly List<HttpRequestMessage> Requests = new();
        public bool Disposed;

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond());
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task CompleteStreamAsync_MessageStartUsage_ShouldMergeIntoFinalTokenUsage()
    {
        // Arrange：SSE 假流——message_start 携带 input_tokens=25，message_delta 携带 output_tokens=100
        var sse = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":25,\"output_tokens\":1}}}",
            "",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}",
            "",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":100}}",
            "",
            "data: {\"type\":\"message_stop\"}",
            "");
        var handler = new StubHandler(() => Sse(sse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new AnthropicClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        StreamUpdate? complete = null;
        await foreach (var update in client.CompleteStreamAsync(new ChatRequest
        {
            Model = "claude-test",
            Messages = [new() { Role = ChatRole.User, Content = "Hi" }]
        }))
        {
            if (update.IsComplete)
                complete = update;
        }

        // Assert：最终 usage 合并 message_start 的 input_tokens 与 message_delta 的 output_tokens
        complete.Should().NotBeNull();
        complete!.Usage.Should().NotBeNull();
        complete.Usage!.InputTokens.Should().Be(25);
        complete.Usage.OutputTokens.Should().Be(100);
    }

    [Fact]
    public async Task CompleteStreamAsync_OnlyMessageStartUsage_ShouldStillReportInputTokens()
    {
        // Arrange：异常流缺少 message_delta——input_tokens 仍应在终态 usage 中上报
        var sse = string.Join("\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":30,\"output_tokens\":2}}}",
            "",
            "data: {\"type\":\"message_stop\"}",
            "");
        var handler = new StubHandler(() => Sse(sse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new AnthropicClient(CreateConfig(), httpClient, NullLogger.Instance);

        StreamUpdate? complete = null;
        await foreach (var update in client.CompleteStreamAsync(new ChatRequest
        {
            Model = "claude-test",
            Messages = [new() { Role = ChatRole.User, Content = "Hi" }]
        }))
        {
            if (update.IsComplete)
                complete = update;
        }

        complete.Should().NotBeNull();
        complete!.Usage.Should().NotBeNull();
        complete.Usage!.InputTokens.Should().Be(30);
    }

    [Fact]
    public void Constructor_PreConfiguredHttpClient_ShouldNotMutateBaseAddressOrTimeout()
    {
        // Arrange：外部已配置 BaseAddress/Timeout 的 HttpClient（共享场景）
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:9999/"),
            Timeout = TimeSpan.FromSeconds(42)
        };

        // Act
        using var client = new AnthropicClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Assert：构造函数不得篡改共享 HttpClient 的 BaseAddress / Timeout
        httpClient.BaseAddress.Should().Be(new Uri("http://localhost:9999/"));
        httpClient.Timeout.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Dispose_OwnedHttpClient_ShouldDisposeHandler()
    {
        // Arrange：工厂路径——传入未配置 BaseAddress 的 HttpClient，客户端应拥有并在 Dispose 时释放
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        client.Dispose();

        // Assert：自建 HttpClient 被释放，底层 handler 级联释放
        handler.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_SharedHttpClient_ShouldNotDisposeHandler()
    {
        // Arrange：共享路径——外部已配置 BaseAddress 的 HttpClient，客户端不拥有
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new AnthropicClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        client.Dispose();

        // Assert：共享 HttpClient 不被释放（仍可继续使用）
        handler.Disposed.Should().BeFalse();
        httpClient.Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }
}
