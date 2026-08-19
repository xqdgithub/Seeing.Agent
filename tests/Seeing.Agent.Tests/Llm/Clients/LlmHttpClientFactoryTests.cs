using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Clients;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

public class LlmHttpClientFactoryTests
{
    [Fact]
    public void CreateHandler_WithProviderProxy_UsesExplicitWebProxy()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "http://proxy.example:8080"
        });

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeOfType<WebProxy>();
        ((WebProxy)handler.Proxy!).Address.Should().Be(new Uri("http://proxy.example:8080"));
    }

    [Fact]
    public void CreateHandler_WithProxyCredentials_StoresCredentialsWithoutUserInfoInAddress()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "https://proxy-user:p%40ssword@proxy.example:8443"
        });

        var proxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
        proxy.Address.Should().Be(new Uri("https://proxy.example:8443"));
        proxy.Credentials.Should().BeOfType<NetworkCredential>();
        var credentials = (NetworkCredential)proxy.Credentials!;
        credentials.UserName.Should().Be("proxy-user");
        credentials.Password.Should().Be("p@ssword");
    }

    [Fact]
    public void CreateHandler_WithoutProviderProxy_UsesSystemProxyByDefault()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig());

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void CreateHandler_WhenProxyDisabled_DoesNotUseAnyProxy()
    {
        using var handler = LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            UseProxy = false,
            Proxy = "http://proxy.example:8080"
        });

        handler.UseProxy.Should().BeFalse();
        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public void CreateHandler_WithSocksProxy_RejectsUnsupportedScheme()
    {
        var act = () => LlmHttpClientFactory.CreateHandler(new ProviderConfig
        {
            Proxy = "socks5://127.0.0.1:1080"
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*absolute http or https URL*");
    }

    [Fact]
    public async Task OpenAiChatClient_WithFactoryHandler_ReusesHttpClientWhenBaseAddressIsUnset()
    {
        var handler = new RecordingHandler("""
            {"id":"chatcmpl-test","model":"test","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiChatClient(
            new ProviderConfig
            {
                Id = "openai",
                ApiKey = null,
                BaseUrl = "http://llm.test/v1",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer custom-token",
                    ["X-Provider-Header"] = "custom-value"
                }
            },
            httpClient,
            NullLogger.Instance);

        var response = await client.CompleteAsync(new ChatRequest
        {
            Model = "test",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }]
        });

        response.Message.Content.Should().Be("ok");
        handler.RequestCount.Should().Be(1);
        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://llm.test/v1/chat/completions"));
        handler.LastRequest.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer custom-token");
        handler.LastRequest.Headers.GetValues("X-Provider-Header").Should().ContainSingle().Which.Should().Be("custom-value");
    }

    [Fact]
    public async Task AnthropicClient_CustomHeadersAreForwardedAndOverrideBuiltInHeaders()
    {
        var handler = new RecordingHandler("""
            {"id":"msg-test","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"model":"test","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(
            new ProviderConfig
            {
                Id = "anthropic",
                ApiKey = null,
                BaseUrl = "http://llm.test",
                Headers = new Dictionary<string, string>
                {
                    ["x-api-key"] = "sk-custom",
                    ["X-Provider-Header"] = "custom-value"
                }
            },
            httpClient,
            NullLogger.Instance);

        await client.CompleteAsync(new ChatRequest
        {
            Model = "test",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "hi" }]
        });

        handler.LastRequest!.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-custom");
        handler.LastRequest.Headers.GetValues("X-Provider-Header").Should().ContainSingle().Which.Should().Be("custom-value");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public RecordingHandler(string body) => _body = body;

        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
