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

    /// <summary>
    /// 协议解析大小写不敏感、空白拒绝 —— 由注册表统一承担（工厂不再自述协议）。
    /// </summary>
    [Fact]
    public void Resolver_ProtocolLookup_IsCaseInsensitive()
    {
        ILlmClientFactory factory = new FakeLlmClientFactory();
        var resolver = new LlmClientFactoryResolver(
        [
            new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory),
            new LlmClientFactoryRegistration(ProviderTypes.Anthropic, factory),
        ]);

        resolver.Find("OpenAI").Should().BeSameAs(factory);
        resolver.Find("openai").Should().BeSameAs(factory);
        resolver.Find("OPENAI").Should().BeSameAs(factory);
        resolver.Find("anthropic").Should().BeSameAs(factory);
        resolver.Find("Anthropic").Should().BeSameAs(factory);
        resolver.Find("unknown").Should().BeNull();
        resolver.Find("").Should().BeNull();
        resolver.Find("   ").Should().BeNull();
    }

    private sealed class FakeLlmClientFactory : ILlmClientFactory
    {
        public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability) =>
            throw new NotSupportedException();
    }
}
