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

    [Fact]
    public void SupportsProtocol_IsCaseInsensitive()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);

        factory.SupportsProtocol("anthropic").Should().BeTrue();
        factory.SupportsProtocol("Anthropic").Should().BeTrue();
        factory.SupportsProtocol("ANTHROPIC").Should().BeTrue();
        factory.SupportsProtocol(ProviderTypes.Anthropic).Should().BeTrue();
        factory.SupportsProtocol("openai").Should().BeFalse();
    }

    [Fact]
    public void SupportedProtocols_ContainsOnlyAnthropic()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        factory.SupportedProtocols.Should().Equal(ProviderTypes.Anthropic);
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

    [Fact]
    public void Create_OtherProtocol_ShouldReturnNull()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        var config = CreateAnthropicConfig();
        config.Type = ProviderTypes.OpenAi;

        var client = factory.Create(config, LlmCapabilityKind.Chat);

        client.Should().BeNull();
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
