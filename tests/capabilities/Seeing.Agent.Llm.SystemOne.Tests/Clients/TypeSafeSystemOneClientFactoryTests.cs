using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests.Clients;

/// <summary>
/// <see cref="TypeSafeSystemOneClientFactory"/> 的「协议 + 能力」分派
/// （spec §7.9：只供 Decision；其余组合返回 null，由 Router 第 5 层报错——决议 19）。
/// </summary>
public class TypeSafeSystemOneClientFactoryTests
{
    [Theory]
    [InlineData("typesafe")]
    [InlineData("TypeSafe")]
    [InlineData("TYPESAFE")]
    public void SupportsProtocol_IsCaseInsensitive(string protocol)
    {
        CreateFactory().SupportsProtocol(protocol).Should().BeTrue();
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    [InlineData("   ")]
    public void SupportsProtocol_WithOtherOrBlankProtocol_ReturnsFalse(string protocol)
    {
        CreateFactory().SupportsProtocol(protocol).Should().BeFalse();
    }

    [Fact]
    public void SupportedProtocols_ContainsOnlyTypeSafe()
    {
        var factory = CreateFactory();

        factory.SupportedProtocols.Should().Equal(ProviderTypes.TypeSafe);
        factory.SupportedProtocols.Should().NotContain(ProviderTypes.OpenAi);
    }

    [Fact]
    public void Create_DecisionCapability_ReturnsTypeSafeClient()
    {
        var factory = CreateFactory();

        var client = factory.Create(Config(), LlmCapabilityKind.Decision);

        client.Should().BeOfType<TypeSafeSystemOneClient>();
        client!.ProviderId.Should().Be("typesafe");
        client.ProviderType.Should().Be(ProviderTypes.TypeSafe);
    }

    [Fact]
    public void Factory_ShouldOnlyServeDecisionCapability()
    {
        var factory = CreateFactory();

        foreach (var capability in new[]
                 {
                     LlmCapabilityKind.Chat,
                     LlmCapabilityKind.Embedding,
                     LlmCapabilityKind.SpeechSynthesis,
                     LlmCapabilityKind.SpeechTranscription,
                     LlmCapabilityKind.Rerank,
                     LlmCapabilityKind.ImageGeneration,
                     LlmCapabilityKind.VideoGeneration
                 })
        {
            factory.Create(Config(), capability).Should().BeNull(capability.ToString());
        }
    }

    [Fact]
    public void Create_WithOtherProtocol_ReturnsNull()
    {
        var factory = CreateFactory();
        var config = Config();
        config.Type = ProviderTypes.OpenAi;

        factory.Create(config, LlmCapabilityKind.Decision).Should().BeNull();
    }

    [Fact]
    public void Create_AppliesDecoratorRegistry_BeforeReturning()
    {
        var decorators = new RecordingDecoratorRegistry();
        var factory = new TypeSafeSystemOneClientFactory(NullLoggerFactory.Instance, decorators);

        var client = factory.Create(Config(), LlmCapabilityKind.Decision);

        decorators.ApplyCount.Should().Be(1);
        client.Should().BeSameAs(decorators.LastResult);
    }

    private static TypeSafeSystemOneClientFactory CreateFactory()
        => new(NullLoggerFactory.Instance);

    private static ProviderConfig Config()
        => new()
        {
            Id = "typesafe",
            Type = ProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Timeout = 10000
        };

    /// <summary>记录装饰器是否被应用（重试等横切的唯一应用点在工厂内——spec §7.3）。</summary>
    private sealed class RecordingDecoratorRegistry : ILlmClientDecoratorRegistry
    {
        public int ApplyCount { get; private set; }
        public ILlmCapabilityClient? LastResult { get; private set; }

        public void Register(ILlmClientDecorator decorator) { }

        public bool Unregister(ILlmClientDecorator decorator) => false;

        public ILlmCapabilityClient Apply(ILlmCapabilityClient client, ProviderConfig config)
        {
            ApplyCount++;
            LastResult = client;
            return client;
        }
    }
}
