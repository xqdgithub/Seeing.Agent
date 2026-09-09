using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class OutboundPipelineTests
{
    private sealed class CapturingInterceptor : ILlmCallInterceptor
    {
        public int Order { get; init; }
        public string HeaderName { get; init; } = "X-Test";
        public string HeaderValue { get; init; } = "v";
        public bool AppliesTo(string providerId, string providerType) => true;
        public void OnSending(LlmOutboundRequest request) => request.Headers[HeaderName] = HeaderValue;
    }

    [Fact]
    public void Apply_MergesStaticExtraAndInterceptor_InterceptorWinsOnConflict()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://example.test/x");
        var call = new LlmCallContext();
        call.ExtraHeaders["X-A"] = "from-call";
        call.ExtraHeaders["X-Shared"] = "from-call";

        var interceptors = new ILlmCallInterceptor[]
        {
            new CapturingInterceptor { HeaderName = "X-Shared", HeaderValue = "from-interceptor" },
            new CapturingInterceptor { HeaderName = "X-B", HeaderValue = "from-interceptor" }
        };

        OutboundPipeline.Apply(
            message,
            staticHeaders: new Dictionary<string, string> { ["X-Static"] = "s" },
            call,
            interceptors,
            providerId: "p",
            providerType: ProviderTypes.OpenAi);

        message.Headers.GetValues("X-Static").Should().ContainSingle().Which.Should().Be("s");
        message.Headers.GetValues("X-A").Should().ContainSingle().Which.Should().Be("from-call");
        message.Headers.GetValues("X-Shared").Should().ContainSingle().Which.Should().Be("from-interceptor");
        message.Headers.GetValues("X-B").Should().ContainSingle().Which.Should().Be("from-interceptor");
    }
}
