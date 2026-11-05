using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;
using Xunit;

namespace Seeing.Agent.Llm.Anthropic.Tests;

public class AnthropicLlmModuleTests
{
    [Fact]
    public void Module_Id_IsLlmAnthropic()
    {
        var module = new AnthropicLlmModule();
        module.Id.Should().Be("llm.anthropic");
    }

    [Fact]
    public void ProvidedSeams_ContainsLlm()
    {
        var module = new AnthropicLlmModule();
        module.ProvidedSeams.Should().Equal("llm");
    }

    [Fact]
    public void ProvidedTools_IsEmpty()
    {
        var module = new AnthropicLlmModule();
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new AnthropicLlmModule();
        module.DependsOn.Should().BeEmpty();
    }
}

public class AnthropicLlmClientFactoryTests
{
    private static ProviderConfig CreateAnthropicConfig() => new()
    {
        Id = "anthropic",
        Type = ProviderTypes.Anthropic,
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "test-key"
    };

    /// <summary>
    /// 协议绑定已从工厂移到注册表（协议是服务键）：工厂不再自述协议，
    /// 「重复协议 → 构造期抛」是新的守门点。本用例锁定注册表的解析行为。
    /// </summary>
    [Fact]
    public void Resolver_IsCaseInsensitive_AndRejectsUnknownProtocol()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.Anthropic, factory)]);

        resolver.Find("anthropic").Should().BeSameAs(factory);
        resolver.Find("Anthropic").Should().BeSameAs(factory);
        resolver.Find("ANTHROPIC").Should().BeSameAs(factory);
        resolver.Find("openai").Should().BeNull();

        var act = () => resolver.Require("openai");
        act.Should().Throw<NotSupportedException>().WithMessage("*anthropic*");
    }

    /// <summary>同一协议登记两个不同工厂 → 构造期即抛（从前是 first-wins 静默覆盖）。</summary>
    [Fact]
    public void Resolver_DuplicateProtocol_ShouldThrow()
    {
        var first = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        var second = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);

        var act = () => new LlmClientFactoryResolver(
        [
            new LlmClientFactoryRegistration(ProviderTypes.Anthropic, first),
            new LlmClientFactoryRegistration(ProviderTypes.Anthropic, second),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*重复登记*");
    }

    /// <summary>同一工厂登记多个协议（OpenAI 工厂同时服务 openai 与 openai-responses）。</summary>
    [Fact]
    public void Resolver_SameFactoryMultipleProtocols_IsAllowed()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        var resolver = new LlmClientFactoryResolver(
        [
            new LlmClientFactoryRegistration(ProviderTypes.Anthropic, factory),
            new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory),
        ]);

        resolver.Protocols.Should().BeEquivalentTo(ProviderTypes.Anthropic, ProviderTypes.OpenAi);
        resolver.Find(ProviderTypes.OpenAi).Should().BeSameAs(factory);
    }

    [Fact]
    public void Create_Chat_ShouldReturnChatCompletionClient()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);

        var client = factory.Create(CreateAnthropicConfig(), LlmCapabilityKind.Chat);

        client.Should().NotBeNull();
        client.Should().BeAssignableTo<IChatCompletionClient>();
        client!.ProviderId.Should().Be("anthropic");
        client.ProviderType.Should().Be(ProviderTypes.Anthropic);
    }

    /// <summary>Anthropic 只供 chat：其余能力必须返回 null（决议 19，不抛、不回退）。</summary>
    [Theory]
    [InlineData(LlmCapabilityKind.None)]
    [InlineData(LlmCapabilityKind.Embedding)]
    [InlineData(LlmCapabilityKind.Rerank)]
    [InlineData(LlmCapabilityKind.Decision)]
    [InlineData(LlmCapabilityKind.SpeechSynthesis)]
    [InlineData(LlmCapabilityKind.SpeechTranscription)]
    [InlineData(LlmCapabilityKind.ImageGeneration)]
    [InlineData(LlmCapabilityKind.VideoGeneration)]
    public void Create_NonChatCapability_ShouldReturnNull(LlmCapabilityKind capability)
    {
        var decorators = new RecordingDecoratorRegistry();
        var factory = new AnthropicLlmClientFactory(
            NullLoggerFactory.Instance, decorators: decorators);

        var client = factory.Create(CreateAnthropicConfig(), capability);

        client.Should().BeNull();
        // 未产出客户端时不得空跑装饰器链
        decorators.ApplyCalls.Should().Be(0);
    }

    /// <summary>
    /// 协议校验已从工厂移到注册表：工厂不再自检 <c>config.Type</c>，
    /// 「openai 配置不会被 Anthropic 工厂处理」由注册表的协议绑定保证。
    /// </summary>
    [Fact]
    public void Resolver_OpenAiProtocol_ShouldNotReachAnthropicFactory()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.Anthropic, factory)]);

        resolver.Find(ProviderTypes.OpenAi).Should().BeNull(
            "openai 协议未登记到 Anthropic 工厂，解析阶段即失败");
        resolver.Require(ProviderTypes.Anthropic).Should().BeSameAs(factory);
    }

    /// <summary>
    /// 装饰器唯一应用点在工厂 <c>Create</c> 返回之前（spec §7.3）。
    /// 上移到 Provider 层会让直接调工厂的 plug provider 静默失去重试。
    /// </summary>
    [Fact]
    public void Create_Chat_ShouldApplyDecoratorsBeforeReturning()
    {
        var decorators = new RecordingDecoratorRegistry
        {
            Wrapper = inner => new WrappingChatClient(inner)
        };
        var factory = new AnthropicLlmClientFactory(
            NullLoggerFactory.Instance, decorators: decorators);

        var client = factory.Create(CreateAnthropicConfig(), LlmCapabilityKind.Chat);

        decorators.ApplyCalls.Should().Be(1);
        decorators.LastConfig.Should().NotBeNull();
        decorators.LastConfig!.Id.Should().Be("anthropic");
        // 交给调用方的必须是装饰后的最外层客户端，且仍是 chat 端口
        client.Should().BeOfType<WrappingChatClient>();
        ((WrappingChatClient)client!).Inner.Should().BeOfType<AnthropicClient>();
    }

    [Fact]
    public void Create_NullConfig_ShouldThrow()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);

        var act = () => factory.Create(null!, LlmCapabilityKind.Chat);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>装饰器注册表桩：记录 Apply 次数与入参，可选把入参换成包装物。</summary>
    private sealed class RecordingDecoratorRegistry : ILlmClientDecoratorRegistry
    {
        public int ApplyCalls { get; private set; }
        public ProviderConfig? LastConfig { get; private set; }
        public Func<ILlmCapabilityClient, ILlmCapabilityClient>? Wrapper { get; set; }

        public void Register(ILlmClientDecorator decorator)
        {
        }

        public bool Unregister(ILlmClientDecorator decorator) => false;

        public ILlmCapabilityClient Apply(ILlmCapabilityClient client, ProviderConfig config)
        {
            ApplyCalls++;
            LastConfig = config;
            return Wrapper?.Invoke(client) ?? client;
        }
    }

    /// <summary>模拟装饰器产出的最外层客户端（保持 chat 端口不变）。</summary>
    private sealed class WrappingChatClient : IChatCompletionClient
    {
        public WrappingChatClient(ILlmCapabilityClient inner) => Inner = inner;

        public ILlmCapabilityClient Inner { get; }

        public string ProviderId => Inner.ProviderId;

        public string ProviderType => Inner.ProviderType;

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Inner.TestConnectionAsync(modelId, capability, call, cancellationToken);

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("仅用于断言装饰链结构。");

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("仅用于断言装饰链结构。");
    }
}
