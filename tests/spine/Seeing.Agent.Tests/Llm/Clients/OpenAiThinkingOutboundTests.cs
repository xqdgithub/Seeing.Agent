using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

public class OpenAiThinkingOutboundTests
{
    [Fact]
    public async Task CompleteAsync_Enabled_WritesThinkingAndReasoningEffort()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkCompletion();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "deepseek-chat",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            ThinkingEffort = "high"
        }, ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        root.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task CompleteAsync_Disabled_WritesThinkingDisabled_NoEffort()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkCompletion();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "deepseek-chat",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            ThinkingEffort = "disabled"
        }, ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        root.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_EchoReasoning_WritesReasoningContentOnAssistant()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkCompletion();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "deepseek-chat",
            EchoReasoningContent = true,
            Messages =
            [
                new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = "answer",
                    ReasoningContent = "secret-thought",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_1",
                            Type = "function",
                            Function = new FunctionCall { Name = "x", Arguments = "{}" }
                        }
                    ]
                },
                new ChatMessage { Role = ChatRole.Tool, ToolCallId = "call_1", Content = "ok" }
            ]
        }, ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var assistant = doc.RootElement.GetProperty("messages")[0];
        assistant.GetProperty("reasoning_content").GetString().Should().Be("secret-thought");
    }

    [Fact]
    public async Task CompleteAsync_NoEcho_OmitsReasoningContent()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkCompletion();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "gpt-4o",
            EchoReasoningContent = false,
            Messages =
            [
                new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = "answer",
                    ReasoningContent = "secret-thought"
                }
            ]
        }, ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var assistant = doc.RootElement.GetProperty("messages")[0];
        assistant.TryGetProperty("reasoning_content", out _).Should().BeFalse();
    }

    private static OpenAiChatClient CreateClient(Func<string, HttpResponseMessage> respond)
    {
        var handler = new CaptureHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/") };
        return new OpenAiChatClient(
            new ProviderConfig
            {
                Id = "test-openai",
                Type = ProviderTypes.OpenAi,
                ApiKey = "sk-test",
                BaseUrl = "http://localhost/v1"
            },
            http,
            NullLogger.Instance);
    }

    private static HttpResponseMessage OkCompletion() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"1","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""",
                Encoding.UTF8,
                "application/json")
        };

    private sealed class CaptureHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(body);
        }
    }
}
