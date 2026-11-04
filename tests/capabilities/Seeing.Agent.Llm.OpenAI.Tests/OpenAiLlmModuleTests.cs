using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

public class OpenAiLlmModuleTests
{
    [Fact]
    public void Module_Id_IsLlmOpenAi()
    {
        var module = new OpenAiLlmModule();
        module.Id.Should().Be("llm.openai");
    }

    [Fact]
    public void ProvidedSeams_ContainsLlm()
    {
        var module = new OpenAiLlmModule();
        module.ProvidedSeams.Should().Equal("llm");
    }

    [Fact]
    public void ProvidedTools_IsEmpty()
    {
        var module = new OpenAiLlmModule();
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new OpenAiLlmModule();
        module.DependsOn.Should().BeEmpty();
    }
}

/// <summary>
/// 工厂测试：协议集合（openai + openai-responses，决议 7）、按能力 switch 分派、
/// 装饰器在 Create 返回前应用（唯一应用点，spec §7.3），
/// 以及「每个客户端恰好实现一个能力端口」隐含契约（关卡 1 派发顺序 #3，D-1 靠 `is` 判能力）。
/// </summary>
public class OpenAiLlmClientFactoryTests
{
    private static ProviderConfig CreateConfig(string type = ProviderTypes.OpenAi) => new()
    {
        Id = "test-provider",
        Type = type,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "test-key"
    };

    /// <summary>创建客户端、执行断言并确保释放（工厂路径客户端拥有自建 HttpClient）。</summary>
    private static void WithClient(
        OpenAiLlmClientFactory factory,
        ProviderConfig config,
        LlmCapabilityKind capability,
        Action<ILlmCapabilityClient?> assert)
    {
        var client = factory.Create(config, capability);
        try
        {
            assert(client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>异步断言版本：避免把 async lambda 交给 Action（async void）。</summary>
    private static async Task WithClientAsync(
        OpenAiLlmClientFactory factory,
        ProviderConfig config,
        LlmCapabilityKind capability,
        Func<ILlmCapabilityClient?, Task> assert)
    {
        var client = factory.Create(config, capability);
        try
        {
            await assert(client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>记录 Apply 调用并可返回替换客户端的测试注册表。</summary>
    private sealed class RecordingDecoratorRegistry : ILlmClientDecoratorRegistry
    {
        public readonly List<ILlmCapabilityClient> Applied = new();
        public Func<ILlmCapabilityClient, ILlmCapabilityClient>? Replace;

        public void Register(ILlmClientDecorator decorator) { }
        public bool Unregister(ILlmClientDecorator decorator) => false;

        public ILlmCapabilityClient Apply(ILlmCapabilityClient client, ProviderConfig config)
        {
            Applied.Add(client);
            var replaced = Replace?.Invoke(client) ?? client;
            if (!ReferenceEquals(replaced, client))
                (client as IDisposable)?.Dispose();
            return replaced;
        }
    }

    [Fact]
    public void SupportsProtocol_ShouldBeCaseInsensitive_AndCoverBothOpenAiProtocols()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        factory.SupportsProtocol("openai").Should().BeTrue();
        factory.SupportsProtocol("OpenAI").Should().BeTrue();
        factory.SupportsProtocol(ProviderTypes.OpenAi).Should().BeTrue();
        factory.SupportsProtocol("openai-responses").Should().BeTrue();
        factory.SupportsProtocol("OpenAI-Responses").Should().BeTrue();
        factory.SupportsProtocol("anthropic").Should().BeFalse();
        factory.SupportsProtocol("").Should().BeFalse();
    }

    [Fact]
    public void SupportedProtocols_ShouldContainOpenAiAndOpenAiResponses()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        factory.SupportedProtocols.Should().Contain(new[] { ProviderTypes.OpenAi, ProviderTypes.OpenAiResponses });
        factory.SupportedProtocols.Should().HaveCount(2);
    }

    [Fact]
    public void Create_Chat_OpenAiProtocol_ShouldReturnChatCompletionsClient()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        WithClient(factory, CreateConfig(), LlmCapabilityKind.Chat, client =>
        {
            client.Should().BeOfType<OpenAiChatClient>();
            client.Should().BeAssignableTo<IChatCompletionClient>();
            client!.ProviderType.Should().Be(ProviderTypes.OpenAi);
        });
    }

    [Fact]
    public void Create_Chat_ResponsesProtocol_ShouldReturnResponsesClient()
    {
        // 决议 7：openai-responses 独立协议接入，消除悬空死代码
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        WithClient(factory, CreateConfig(ProviderTypes.OpenAiResponses), LlmCapabilityKind.Chat, client =>
        {
            client.Should().BeOfType<OpenAiResponsesClient>();
            client.Should().BeAssignableTo<IChatCompletionClient>();
            client!.ProviderType.Should().Be(ProviderTypes.OpenAiResponses);
        });
    }

    [Theory]
    [InlineData(LlmCapabilityKind.Embedding, typeof(OpenAiEmbeddingClient))]
    [InlineData(LlmCapabilityKind.SpeechSynthesis, typeof(OpenAiSpeechSynthesisClient))]
    [InlineData(LlmCapabilityKind.SpeechTranscription, typeof(OpenAiSpeechTranscriptionClient))]
    public void Create_SupportedCapabilities_ShouldDispatchToDedicatedClient(LlmCapabilityKind capability, Type expected)
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        WithClient(factory, CreateConfig(), capability, client =>
            client.Should().NotBeNull().And.BeOfType(expected));
    }

    [Theory]
    [InlineData(LlmCapabilityKind.None)]
    [InlineData(LlmCapabilityKind.Rerank)]
    [InlineData(LlmCapabilityKind.Decision)]
    [InlineData(LlmCapabilityKind.ImageGeneration)]
    [InlineData(LlmCapabilityKind.VideoGeneration)]
    public void Create_UnsupportedCapabilities_ShouldReturnNullWithoutThrowing(LlmCapabilityKind capability)
    {
        // spec §7.11：Rerank/Image/Video 本次只出契约，工厂返回 null（Router 第 5 层负责报错）
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        var client = factory.Create(CreateConfig(), capability);

        client.Should().BeNull();
    }

    [Fact]
    public void Create_UnsupportedProtocol_ShouldReturnNull()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        factory.Create(CreateConfig("anthropic"), LlmCapabilityKind.Chat).Should().BeNull();
    }

    [Fact]
    public void Create_ShouldApplyDecorators_BeforeReturning_AndOnlyHere()
    {
        // 装饰器唯一应用点在 Create 返回前（spec §7.3）：上移会让直接调工厂的 plug provider 静默失去重试
        var registry = new RecordingDecoratorRegistry();
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance, decorators: registry);

        WithClient(factory, CreateConfig(), LlmCapabilityKind.Chat, client =>
        {
            registry.Applied.Should().ContainSingle();
            registry.Applied[0].Should().BeSameAs(client);
        });
    }

    [Fact]
    public void Create_DecoratorReplacement_ShouldBeWhatCallerReceives()
    {
        var decorated = new MarkerClient();
        var registry = new RecordingDecoratorRegistry { Replace = _ => decorated };
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance, decorators: registry);

        var client = factory.Create(CreateConfig(), LlmCapabilityKind.Embedding);

        try
        {
            client.Should().BeSameAs(decorated);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Create_UnsupportedCapability_ShouldNotApplyDecorators()
    {
        var registry = new RecordingDecoratorRegistry();
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance, decorators: registry);

        factory.Create(CreateConfig(), LlmCapabilityKind.Rerank).Should().BeNull();
        registry.Applied.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LlmCapabilityKind.Chat)]
    [InlineData(LlmCapabilityKind.Embedding)]
    [InlineData(LlmCapabilityKind.SpeechSynthesis)]
    [InlineData(LlmCapabilityKind.SpeechTranscription)]
    public void CreatedClient_ShouldImplementExactlyOneCapabilityPort(LlmCapabilityKind capability)
    {
        // 关卡 1 审查派发顺序 #3 的隐含契约：D-1 装饰器靠 `is` 判能力，
        // 前提是每客户端实例恰好实现一个能力端口。
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        WithClient(factory, CreateConfig(), capability, client =>
        {
            client.Should().NotBeNull();
            var implemented = PortTypes.Where(client!.GetType().GetInterfaces().Contains).ToArray();
            implemented.Should().ContainSingle("客户端实例恰好实现一个能力端口（决议 15 / D-1 `is` 判定前提）");
        });
    }

    [Fact]
    public async Task TestConnection_ChatClient_NonChatCapability_ShouldReturnFalseWithoutNetwork()
    {
        // O-4：TestConnectionAsync 新增 capability 参数；能力不匹配时直接判 false，不发请求
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        await WithClientAsync(factory, CreateConfig(), LlmCapabilityKind.Chat, async client =>
        {
            var chat = (OpenAiChatClient)client!;
            (await chat.TestConnectionAsync("gpt-test", LlmCapabilityKind.Embedding)).Should().BeFalse();
        });
    }

    private static readonly Type[] PortTypes =
    {
        typeof(IChatCompletionClient),
        typeof(IEmbeddingClient),
        typeof(IRerankClient),
        typeof(IDecisionClient),
        typeof(ISpeechSynthesisClient),
        typeof(ISpeechTranscriptionClient),
        typeof(IImageGenerationClient),
        typeof(IVideoGenerationClient),
    };

    /// <summary>装饰器替换返回值用的最小桩客户端。</summary>
    private sealed class MarkerClient : IEmbeddingClient
    {
        public string ProviderId => "marker";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, LlmCallContext? call = null, CancellationToken ct = default)
            => Task.FromResult(new EmbeddingResponse { Vectors = Array.Empty<EmbeddingVector>() });

        public Task<bool> TestConnectionAsync(string modelId, LlmCapabilityKind capability, LlmCallContext? call = null, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
