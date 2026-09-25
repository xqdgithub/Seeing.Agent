using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// OpenAiChatClient stream_options 声明 / 流式工具参数 JSON 校验 / 释放行为测试。
/// </summary>
public class OpenAiChatClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "test-provider",
        Type = ProviderTypes.OpenAi,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "test-key"
    };

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public readonly List<(HttpRequestMessage Request, string Body)> Requests = new();
        public bool Disposed;

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

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task CompleteStreamAsync_RequestBody_ShouldDeclareStreamOptionsIncludeUsage()
    {
        // Arrange：流式请求应携带 stream_options.include_usage=true，以获得 trailing usage chunk
        var sse = string.Join("\n",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"}}]}",
            "",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}",
            "",
            "data: [DONE]",
            "");
        var handler = new StubHandler(() => Sse(sse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiChatClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        await foreach (var _ in client.CompleteStreamAsync(new ChatRequest
        {
            Model = "gpt-test",
            Messages = [new() { Role = ChatRole.User, Content = "Hi" }]
        }))
        {
        }

        // Assert：请求体包含 stream_options.include_usage
        handler.Requests.Should().HaveCount(1);
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.TryGetProperty("stream_options", out var options).Should().BeTrue();
        options.TryGetProperty("include_usage", out var includeUsage).Should().BeTrue();
        includeUsage.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CompleteStreamAsync_InvalidToolArgumentsJson_ShouldFallbackToEmptyObject()
    {
        // Arrange：流式工具参数分片拼出非法 JSON——对齐 Anthropic 的 Parse 校验回退 "{}"
        var sse = string.Join("\n",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"ci\"}}]}}]}",
            "",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new StubHandler(() => Sse(sse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiChatClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        StreamUpdate? complete = null;
        await foreach (var update in client.CompleteStreamAsync(new ChatRequest
        {
            Model = "gpt-test",
            Messages = [new() { Role = ChatRole.User, Content = "Hi" }]
        }))
        {
            if (update.IsComplete)
                complete = update;
        }

        // Assert：非法 JSON 参数回退为 "{}"
        complete.Should().NotBeNull();
        complete!.ToolCallDeltas.Should().ContainSingle();
        complete.ToolCallDeltas![0].Function!.Arguments.Should().Be("{}");
    }

    [Fact]
    public void Dispose_SharedHttpClient_ShouldNotDisposeHandler()
    {
        // Arrange：外部已配置 BaseAddress 的共享 HttpClient——客户端不拥有
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiChatClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        client.Dispose();

        // Assert：共享 HttpClient 不被释放
        handler.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_OwnedHttpClient_ShouldDisposeHandler()
    {
        // Arrange：工厂路径——未配置 BaseAddress 的 HttpClient，客户端配置并拥有
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler);
        var client = new OpenAiChatClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        client.Dispose();

        // Assert：自建 HttpClient 级联释放底层 handler
        handler.Disposed.Should().BeTrue();
    }
}
