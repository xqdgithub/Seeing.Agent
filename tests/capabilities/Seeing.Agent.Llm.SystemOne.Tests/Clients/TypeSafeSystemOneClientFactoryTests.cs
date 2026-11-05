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
    /// <summary>
    /// 协议绑定已从工厂移到注册表（协议是服务键）：工厂只按能力分派，
    /// 「typesafe 协议 → 本工厂」由 <see cref="LlmClientFactoryResolver"/> 保证。
    /// </summary>
    [Theory]
    [InlineData("typesafe")]
    [InlineData("TypeSafe")]
    [InlineData("TYPESAFE")]
    public void Resolver_ResolvesTypeSafeCaseInsensitively(string protocol)
    {
        var factory = CreateFactory();
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, factory)]);

        resolver.Find(protocol).Should().BeSameAs(factory);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolver_WithOtherOrBlankProtocol_ReturnsNull(string protocol)
    {
        var factory = CreateFactory();
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, factory)]);

        resolver.Find(protocol).Should().BeNull();
    }

    [Fact]
    public void Resolver_RegistersOnlyTypeSafe()
    {
        var factory = CreateFactory();
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, factory)]);

        resolver.Protocols.Should().Equal(ProviderTypes.TypeSafe);
        resolver.Protocols.Should().NotContain(ProviderTypes.OpenAi);
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

    /// <summary>
    /// 协议校验已从工厂移到注册表：工厂按能力分派，不再自检 <c>config.Type</c>。
    /// 「openai 配置不会被 TypeSafe 工厂处理」由注册表的协议绑定保证。
    /// </summary>
    [Fact]
    public void Resolver_WithOtherProtocol_ReturnsNull()
    {
        var factory = CreateFactory();
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, factory)]);

        resolver.Find(ProviderTypes.OpenAi).Should().BeNull();
        resolver.Require(ProviderTypes.TypeSafe).Should().BeSameAs(factory);
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
