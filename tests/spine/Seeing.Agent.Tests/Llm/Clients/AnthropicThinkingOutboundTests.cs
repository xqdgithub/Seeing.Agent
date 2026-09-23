using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

public class AnthropicThinkingOutboundTests
{
    [Fact]
    public async Task CompleteAsync_Budget_WritesEnabledBudget_AndOmitsSampling()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkMessage();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude-sonnet",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            ThinkingEffort = "high",
            ThinkingBudgetTokens = 500,
            Temperature = 0.2,
            TopP = 0.9,
            MaxTokens = 100
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(1024);
        root.GetProperty("max_tokens").GetInt32().Should().Be(2048);
        root.TryGetProperty("output_config", out _).Should().BeFalse();
        root.TryGetProperty("temperature", out _).Should().BeFalse();
        root.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_Adaptive_WritesEffort()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkMessage();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            ThinkingEffort = "high"
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        root.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task CompleteAsync_Off_OmitsThinking_KeepsTemperature()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkMessage();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }],
            ThinkingEffort = "disabled",
            Temperature = 0.3
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.TryGetProperty("thinking", out _).Should().BeFalse();
        root.GetProperty("temperature").GetDouble().Should().Be(0.3);
    }

    [Fact]
    public async Task CompleteAsync_ToolUse_PrependsThinkingWithSignature()
    {
        string? body = null;
        var client = CreateClient(req =>
        {
            body = req;
            return OkMessage();
        });

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages =
            [
                new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = "call tool",
                    ReasoningContent = "why",
                    ReasoningSignature = "sig-abc",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "toolu_1",
                            Function = new FunctionCall { Name = "search", Arguments = "{\"q\":1}" }
                        }
                    ]
                },
                new ChatMessage { Role = ChatRole.Tool, ToolCallId = "toolu_1", Content = "result" }
            ]
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("thinking");
        content[0].GetProperty("thinking").GetString().Should().Be("why");
        content[0].GetProperty("signature").GetString().Should().Be("sig-abc");
        content.EnumerateArray().Any(b => b.GetProperty("type").GetString() == "tool_use").Should().BeTrue();
    }

    private static AnthropicClient CreateClient(Func<string, HttpResponseMessage> respond)
    {
        var handler = new CaptureHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new AnthropicClient(
            new ProviderConfig
            {
                Id = "test-anthropic",
                Type = ProviderTypes.Anthropic,
                ApiKey = "sk-test",
                BaseUrl = "http://localhost/"
            },
            http,
            NullLogger.Instance);
    }

    private static HttpResponseMessage OkMessage() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"model":"claude","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""",
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
