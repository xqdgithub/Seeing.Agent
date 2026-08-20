using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Clients;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

/// <summary>
/// OpenAiChatClient.CompleteStreamAsync：推迟 IsComplete、工具累加、trailing usage。
/// </summary>
public class OpenAiChatClientStreamTests
{
    [Fact]
    public async Task CompleteStreamAsync_tool_calls_应推迟IsComplete并聚合工具()
    {
        var sse = """
            data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"search","arguments":""}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"x\"}"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var client = CreateClient(sse);
        var updates = await CollectAsync(client);

        updates.Should().NotBeEmpty();
        updates.Where(u => u.IsComplete).Should().HaveCount(1);
        updates.Take(updates.Count - 1).Should().OnlyContain(u => !u.IsComplete);

        var complete = updates[^1];
        complete.IsComplete.Should().BeTrue();
        complete.FinishReason.Should().Be("tool_calls");
        complete.ToolCallDeltas.Should().NotBeNull().And.HaveCount(1);
        complete.ToolCallDeltas![0].Id.Should().Be("call_abc");
        complete.ToolCallDeltas[0].Function!.Name.Should().Be("search");
        complete.ToolCallDeltas[0].Function!.Arguments.Should().Be("{\"q\":\"x\"}");
    }

    [Fact]
    public async Task CompleteStreamAsync_finish后trailing_usage_应出现在最终Complete()
    {
        var sse = """
            data: {"id":"chatcmpl-2","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":null}]}

            data: {"id":"chatcmpl-2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"chatcmpl-2","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

            data: [DONE]

            """;

        var client = CreateClient(sse);
        var updates = await CollectAsync(client);

        updates.Where(u => u.IsComplete).Should().HaveCount(1);
        var complete = updates[^1];
        complete.FinishReason.Should().Be("stop");
        complete.Usage.Should().NotBeNull();
        complete.Usage!.InputTokens.Should().Be(10);
        complete.Usage.OutputTokens.Should().Be(2);

        var content = string.Concat(updates.Where(u => u.ContentDelta != null).Select(u => u.ContentDelta));
        content.Should().Be("hi");
    }

    [Fact]
    public async Task CompleteStreamAsync_stop_content_最终一次Complete()
    {
        var sse = """
            data: {"id":"chatcmpl-3","choices":[{"index":0,"delta":{"role":"assistant","content":"hel"},"finish_reason":null}]}

            data: {"id":"chatcmpl-3","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}

            data: {"id":"chatcmpl-3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var client = CreateClient(sse);
        var updates = await CollectAsync(client);

        updates.Count(u => !string.IsNullOrEmpty(u.ContentDelta)).Should().Be(2);
        updates.Where(u => u.IsComplete).Should().HaveCount(1);

        var complete = updates[^1];
        complete.IsComplete.Should().BeTrue();
        complete.FinishReason.Should().Be("stop");
        complete.ToolCallDeltas.Should().BeNull();

        string.Concat(updates.Select(u => u.ContentDelta ?? "")).Should().Be("hello");
    }

    private static OpenAiChatClient CreateClient(string sseBody)
    {
        var handler = new SseHttpHandler(sseBody);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/")
        };

        var config = new ProviderConfig
        {
            Id = "test-openai",
            Type = ProviderType.OpenAI,
            ApiKey = "sk-test",
            BaseUrl = "http://localhost/v1"
        };

        return new OpenAiChatClient(config, http, NullLogger.Instance);
    }

    [Fact]
    public void Constructor_WithoutApiKeyOrAuthorizationHeader_AllowsAnonymousGateway()
    {
        // OpenCode Zen 等免费匿名网关无需认证：无 ApiKey 且无 Authorization 头也应可构造
        using var http = new HttpClient(new SseHttpHandler("data: [DONE]\n\n"))
        {
            BaseAddress = new Uri("http://localhost/v1/")
        };

        var client = new OpenAiChatClient(
            new ProviderConfig
            {
                Id = "opencode-zen",
                Type = ProviderType.OpenAI,
                BaseUrl = "http://localhost/v1"
            },
            http,
            NullLogger.Instance);

        client.ProviderId.Should().Be("opencode-zen");
    }

    private static async Task<List<StreamUpdate>> CollectAsync(OpenAiChatClient client)
    {
        var request = new ChatRequest
        {
            Model = "gpt-test",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }]
        };

        var list = new List<StreamUpdate>();
        await foreach (var update in client.CompleteStreamAsync(request))
            list.Add(update);
        return list;
    }

    private sealed class SseHttpHandler : HttpMessageHandler
    {
        private readonly string _sseBody;

        public SseHttpHandler(string sseBody) => _sseBody = sseBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sseBody, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }
}
