using Seeing.Agent.Abstractions.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ConfiguredLlmProviderTests
{
    [Fact]
    public void GetConfigSchema_ReturnsNull()
    {
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object);
        ((IConfigurableLlmProvider)sut).GetConfigSchema().Should().BeNull();
    }

    [Fact]
    public async Task LoadConfigAsync_MapsConnectionFields()
    {
        var config = new ProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            Timeout = 1000,
            MaxRetries = 2,
            DefaultModel = "gpt-4o",
            Headers = new Dictionary<string, string>
            {
                ["X-Provider-Header"] = "configured"
            },
            Models = new Dictionary<string, ModelConfig> { ["gpt-4o"] = new() { Id = "gpt-4o" } }
        };
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object, config);

        var values = await ((IConfigurableLlmProvider)sut).LoadConfigAsync(TestContext.Current.CancellationToken);

        values.Keys.Should().NotContain("Models");
        values["Name"].Should().Be("OpenAI");
        values["Type"].Should().Be(nameof(ProviderType.OpenAI));
        values["BaseUrl"].Should().Be(config.BaseUrl);
        values["ApiKey"].Should().Be("sk-test");
        values["Headers"].Should().BeEquivalentTo(config.Headers);
        values["Timeout"].Should().Be(1000);
        values["MaxRetries"].Should().Be(2);
        values["DefaultModel"].Should().Be("gpt-4o");
    }

    [Fact]
    public async Task SaveConfigAsync_PerservesModelsAndInvokesSaveCallback()
    {
        ProviderConfig? saved = null;
        ConfigLevel? savedLevel = null;
        var config = new ProviderConfig
        {
            Id = "openai",
            Type = ProviderType.OpenAI,
            Models = new Dictionary<string, ModelConfig> { ["m"] = new() { Id = "m" } }
        };
        var sut = CreateProvider(
            CreateFactory(Mock.Of<ILlmClient>()).Object,
            config,
            saveAsync: (cfg, level, _) =>
            {
                saved = cfg;
                savedLevel = level;
                return Task.CompletedTask;
            });

        await ((IConfigurableLlmProvider)sut).SaveConfigAsync(
            new Dictionary<string, object?>
            {
                ["Name"] = "Renamed",
                ["Type"] = nameof(ProviderType.Anthropic),
                ["BaseUrl"] = "https://example.com",
                ["ApiKey"] = "k",
                ["Headers"] = new Dictionary<string, string>
                {
                    ["X-Saved-Header"] = "saved"
                },
                ["Timeout"] = 5000,
                ["MaxRetries"] = 9,
                ["DefaultModel"] = "x"
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Renamed");
        saved.Type.Should().Be(ProviderType.Anthropic);
        saved.Headers.Should().Contain(new KeyValuePair<string, string>("X-Saved-Header", "saved"));
        saved.Models.Should().ContainKey("m");
        savedLevel.Should().Be(ConfigLevel.User);
    }

    [Fact]
    public void GetClient_CalledTwice_ReturnsSameInstance()
    {
        var client = Mock.Of<ILlmClient>();
        var factory = CreateFactory(client);
        var sut = CreateProvider(factory.Object);

        var first = sut.GetClient();
        var second = sut.GetClient();

        first.Should().BeSameAs(client);
        second.Should().BeSameAs(client);
        factory.Verify(candidate => candidate.Create(It.IsAny<ProviderConfig>()), Times.Once);
    }

    [Fact]
    public async Task GetModelsAsync_ReturnsConfigModels()
    {
        var first = new ModelConfig { Id = "first" };
        var second = new ModelConfig { Id = "second" };
        var config = new ProviderConfig
        {
            Id = "configured",
            Models = new Dictionary<string, ModelConfig>
            {
                [first.Id] = first,
                [second.Id] = second
            }
        };
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object, config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Select(model => model.Id).Should().Equal(first.Id, second.Id);
        models.Should().OnlyContain(model => model.Provider == config.Id);
        models.Should().NotContain(model => ReferenceEquals(model, first) || ReferenceEquals(model, second));
    }

    [Fact]
    public async Task GetModelsAsync_EmptyModelId_UsesDictionaryKeyWithoutMutatingConfig()
    {
        var model = new ModelConfig { Name = "Model" };
        var config = new ProviderConfig
        {
            Id = "configured",
            Models = new Dictionary<string, ModelConfig> { ["model-key"] = model }
        };
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object, config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ModelConfig { Id = "model-key", Name = "Model", Provider = "configured" });
        models[0].Should().NotBeSameAs(model);
        model.Id.Should().BeEmpty();
        model.Provider.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModelsAsync_NoModels_ReturnsEmpty()
    {
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    [Fact]
    public void MaxRetries_ReturnsConfigValue()
    {
        var config = new ProviderConfig { Id = "configured", MaxRetries = 7 };
        var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object, config);

        sut.MaxRetries.Should().Be(7);
    }

    [Fact]
    public async Task TestConnectionAsync_DelegatesToClient()
    {
        using var cts = new CancellationTokenSource();
        var client = new Mock<ILlmClient>();
        client.Setup(candidate => candidate.TestConnectionAsync("test-model", cts.Token))
            .ReturnsAsync(true);
        var sut = CreateProvider(CreateFactory(client.Object).Object);

        var result = await sut.TestConnectionAsync("test-model", cts.Token);

        result.Should().BeTrue();
        client.Verify(
            candidate => candidate.TestConnectionAsync("test-model", cts.Token),
            Times.Once);
    }

    private static ConfiguredLlmProvider CreateProvider(
        ILlmClientFactory factory,
        ProviderConfig? config = null,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task>? saveAsync = null)
        => new(
            config ?? new ProviderConfig { Id = "configured" },
            factory,
            NullLogger.Instance,
            saveAsync ?? ((_, _, _) => Task.CompletedTask));

    private static Mock<ILlmClientFactory> CreateFactory(ILlmClient client)
    {
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(client);
        return factory;
    }
}
