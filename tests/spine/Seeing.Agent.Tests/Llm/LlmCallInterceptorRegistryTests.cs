using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class LlmCallInterceptorRegistryTests
{
    private sealed class StubInterceptor : ILlmCallInterceptor
    {
        public required int Order { get; init; }
        public required string Id { get; init; }
        public required Func<string, string, bool> Filter { get; init; }
        public bool AppliesTo(string providerId, string providerType) => Filter(providerId, providerType);
        public void OnSending(LlmOutboundRequest request) { }
    }

    [Fact]
    public void Resolve_FiltersByAppliesTo_AndOrdersByOrder()
    {
        var registry = new LlmCallInterceptorRegistry();
        var late = new StubInterceptor
        {
            Order = 20,
            Id = "late",
            Filter = (id, _) => id == "opencode-zen"
        };
        var early = new StubInterceptor
        {
            Order = 5,
            Id = "early",
            Filter = (id, _) => id == "opencode-zen"
        };
        var other = new StubInterceptor
        {
            Order = 1,
            Id = "other",
            Filter = (id, _) => id == "deepseek"
        };

        registry.Register(late);
        registry.Register(early);
        registry.Register(other);

        var resolved = registry.Resolve("opencode-zen", ProviderTypes.OpenAi);
        resolved.Should().HaveCount(2);
        resolved[0].Should().BeSameAs(early);
        resolved[1].Should().BeSameAs(late);
    }

    [Fact]
    public void Unregister_RemovesFromSubsequentResolve()
    {
        var registry = new LlmCallInterceptorRegistry();
        var interceptor = new StubInterceptor
        {
            Order = 1,
            Id = "x",
            Filter = (_, _) => true
        };
        registry.Register(interceptor);
        registry.Unregister(interceptor).Should().BeTrue();
        registry.Resolve("any", ProviderTypes.OpenAi).Should().BeEmpty();
    }
}
