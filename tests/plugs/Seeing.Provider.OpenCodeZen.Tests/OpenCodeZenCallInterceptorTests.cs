using Seeing.Agent.Abstractions.Llm;
using FluentAssertions;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenCallInterceptorTests
{
    [Fact]
    public void OnSending_WithSessionId_UsesSessionId()
    {
        var sut = new OpenCodeZenCallInterceptor();
        var request = new LlmOutboundRequest
        {
            ProviderId = "opencode-zen",
            ProviderType = ProviderTypes.OpenAi,
            Call = new LlmCallContext { SessionId = "sess-abc" }
        };

        sut.OnSending(request);

        request.Headers["x-opencode-session"].Should().Be("sess-abc");
    }

    [Fact]
    public void OnSending_WithoutSessionId_UsesFallback()
    {
        var sut = new OpenCodeZenCallInterceptor();
        var request = new LlmOutboundRequest
        {
            ProviderId = "opencode-zen",
            ProviderType = ProviderTypes.OpenAi,
            Call = null
        };

        sut.OnSending(request);

        request.Headers["x-opencode-session"].Should().NotBeNullOrWhiteSpace();
        request.Headers["x-opencode-session"].Should().StartWith("ses_");
    }

    [Fact]
    public void AppliesTo_OnlyOpenCodeZen()
    {
        var sut = new OpenCodeZenCallInterceptor();
        sut.AppliesTo("opencode-zen", ProviderTypes.OpenAi).Should().BeTrue();
        sut.AppliesTo("deepseek", ProviderTypes.OpenAi).Should().BeFalse();
    }
}
