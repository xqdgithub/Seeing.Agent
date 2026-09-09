using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 验证客户端每次发送前从 registry Resolve，而非 Create 时快照。
/// </summary>
public class PerRequestInterceptorResolveTests
{
    private sealed class SessionHeaderInterceptor : ILlmCallInterceptor
    {
        public int Order => 0;
        public bool AppliesTo(string providerId, string providerType) => providerId == "opencode-zen";
        public void OnSending(LlmOutboundRequest request)
            => request.Headers["x-opencode-session"] = request.Call?.SessionId ?? "fallback";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var json = """
                {"id":"1","object":"chat.completion","created":1,"model":"m",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task CompleteAsync_UsesLiveRegistry_AfterLateRegister()
    {
        var registry = new LlmCallInterceptorRegistry();
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var config = new ProviderConfig
        {
            Id = "opencode-zen",
            Type = ProviderTypes.OpenAi,
            BaseUrl = "https://example.test/",
            ApiKey = "k"
        };
        var client = new OpenAiChatClient(config, http, NullLogger.Instance, registry);

        // Create 时尚未注册 —— 首请求无 session 头
        await client.CompleteAsync(
            new ChatRequest
            {
                Model = "m",
                Messages = [new ChatMessage { Role = "user", Content = "hi" }]
            },
            new LlmCallContext { SessionId = "s1" });

        handler.Requests[0].Headers.Contains("x-opencode-session").Should().BeFalse();

        // 晚注册后，已缓存 client 仍能带上拦截器
        registry.Register(new SessionHeaderInterceptor());
        await client.CompleteAsync(
            new ChatRequest
            {
                Model = "m",
                Messages = [new ChatMessage { Role = "user", Content = "hi" }]
            },
            new LlmCallContext { SessionId = "s2" });

        handler.Requests[1].Headers.GetValues("x-opencode-session").Should().ContainSingle().Which.Should().Be("s2");
    }
}
