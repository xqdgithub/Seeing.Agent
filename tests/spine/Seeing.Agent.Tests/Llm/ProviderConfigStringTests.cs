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
    public void FakeFactory_SupportsProtocol_IsCaseInsensitive()
    {
        ILlmClientFactory factory = new FakeLlmClientFactory();

        factory.SupportsProtocol("OpenAI").Should().BeTrue();
        factory.SupportsProtocol("openai").Should().BeTrue();
        factory.SupportsProtocol("OPENAI").Should().BeTrue();
        factory.SupportsProtocol("anthropic").Should().BeTrue();
        factory.SupportsProtocol("Anthropic").Should().BeTrue();
        factory.SupportsProtocol("unknown").Should().BeFalse();
        factory.SupportsProtocol("").Should().BeFalse();
        factory.SupportsProtocol("   ").Should().BeFalse();
    }

    private sealed class FakeLlmClientFactory : ILlmClientFactory
    {
        public IReadOnlySet<string> SupportedProtocols { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ProviderTypes.OpenAi,
                ProviderTypes.Anthropic
            };

        public bool SupportsProtocol(string protocol) =>
            !string.IsNullOrWhiteSpace(protocol) && SupportedProtocols.Contains(protocol);

        public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability) =>
            throw new NotSupportedException();
    }
}
