using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ProviderConfigStringTests
{
    [Fact]
    public void ProviderConfig_Type_IsString()
    {
        var config = new ProviderConfig { Type = ProviderTypes.OpenAi };

        config.Type.Should().BeOfType<string>();
        config.Type.Should().Be("openai");
    }

    [Fact]
    public void FakeFactory_SupportsType_IsCaseInsensitive()
    {
        ILlmClientFactory factory = new FakeLlmClientFactory();

        factory.SupportsType("OpenAI").Should().BeTrue();
        factory.SupportsType("openai").Should().BeTrue();
        factory.SupportsType("OPENAI").Should().BeTrue();
        factory.SupportsType("anthropic").Should().BeTrue();
        factory.SupportsType("Anthropic").Should().BeTrue();
        factory.SupportsType("unknown").Should().BeFalse();
        factory.SupportsType("").Should().BeFalse();
        factory.SupportsType("   ").Should().BeFalse();
    }

    private sealed class FakeLlmClientFactory : ILlmClientFactory
    {
        public IReadOnlySet<string> SupportedTypes { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ProviderTypes.OpenAi,
                ProviderTypes.Anthropic
            };

        public bool SupportsType(string type) =>
            !string.IsNullOrWhiteSpace(type) && SupportedTypes.Contains(type);

        public ILlmClient Create(ProviderConfig config) =>
            throw new NotSupportedException();
    }
}
