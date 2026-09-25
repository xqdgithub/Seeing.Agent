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
/// OpenAiResponsesClient 输入侧映射测试：ChatRole.Tool 二轮工具调用往返
/// （assistant 的 ToolCalls → function_call item；tool 消息 → function_call_output item）。
/// </summary>
public class OpenAiResponsesClientTests
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public string LastRequestBody = "";

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond();
        }
    }

    private static ChatRequest CreateToolRoundTripRequest() => new()
    {
        Model = "gpt-test",
        Messages =
        [
            new() { Role = ChatRole.User, Content = "北京天气怎么样" },
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_1",
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = "{\"city\":\"北京\"}"
                        }
                    }
                ]
            },
            new()
            {
                Role = ChatRole.Tool,
                ToolCallId = "call_1",
                Content = "{\"temp\":20}"
            }
        ]
    };

    private static async Task<JsonElement> CaptureRequestBodyAsync(StubHandler handler)
    {
        // 非流式请求捕获请求体；Responses API 假响应为空 output
        var response = """
            {"type":"response","response":{"id":"resp_1","status":"completed","model":"gpt-test","output":[]}}
            """;
        handler.LastRequestBody.Should().NotBeEmpty("请求体应已被捕获");
        return JsonDocument.Parse(handler.LastRequestBody).RootElement;
    }

    [Fact]
    public async Task CompleteAsync_ToolRoundTrip_ShouldMapFunctionCallItemsInInput()
    {
        // Arrange：二轮工具调用消息序列
        var handler = new StubHandler(() => JsonResponse(
            "{\"type\":\"response\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[]}}"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiResponsesClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        await client.CompleteAsync(CreateToolRoundTripRequest());

        // Assert：输入侧映射 function_call / function_call_output items
        var root = JsonDocument.Parse(handler.LastRequestBody).RootElement;
        var input = root.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.Array);

        var functionCall = FindItem(input, "function_call");
        functionCall.HasValue.Should().BeTrue("assistant 的 ToolCalls 应映射为 function_call input item");
        functionCall!.Value.GetProperty("call_id").GetString().Should().Be("call_1");
        functionCall.Value.GetProperty("name").GetString().Should().Be("get_weather");
        functionCall.Value.GetProperty("arguments").GetString().Should().Contain("北京");

        var functionOutput = FindItem(input, "function_call_output");
        functionOutput.HasValue.Should().BeTrue("tool 角色消息应映射为 function_call_output input item");
        functionOutput!.Value.GetProperty("call_id").GetString().Should().Be("call_1");
        functionOutput.Value.GetProperty("output").GetString().Should().Contain("temp");

        // 不应再把 tool 结果映射为 assistant 文本角色
        var hasStrayAssistantText = false;
        foreach (var item in input.EnumerateArray())
        {
            if (item.TryGetProperty("role", out var r) && r.GetString() == "assistant"
                && !item.TryGetProperty("type", out _))
            {
                hasStrayAssistantText = true;
            }
        }
        hasStrayAssistantText.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_ToolRoundTrip_AssistantTextShouldRemainMessageItem()
    {
        // Arrange：assistant 消息同时携带文本与工具调用
        var request = CreateToolRoundTripRequest();
        request.Messages[1].Content = "我来查询一下天气";

        var handler = new StubHandler(() => JsonResponse(
            "{\"type\":\"response\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[]}}"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var client = new OpenAiResponsesClient(CreateConfig(), httpClient, NullLogger.Instance);

        // Act
        await client.CompleteAsync(request);

        // Assert：assistant 文本保留为普通 message item，工具调用单独映射
        var root = JsonDocument.Parse(handler.LastRequestBody).RootElement;
        var input = root.GetProperty("input");

        var hasAssistantText = false;
        foreach (var item in input.EnumerateArray())
        {
            if (item.TryGetProperty("role", out var r) && r.GetString() == "assistant"
                && item.TryGetProperty("content", out var c) && c.GetString() == "我来查询一下天气")
            {
                hasAssistantText = true;
            }
        }
        hasAssistantText.Should().BeTrue("assistant 文本内容应保留为普通 message item");
    }

    /// <summary>在 input 数组中查找指定 type 的 item。</summary>
    private static JsonElement? FindItem(JsonElement input, string itemType)
    {
        foreach (var item in input.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t) && t.GetString() == itemType)
                return item;
        }
        return null;
    }
}
