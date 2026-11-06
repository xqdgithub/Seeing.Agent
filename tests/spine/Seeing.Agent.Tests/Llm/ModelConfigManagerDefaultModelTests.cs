using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 按能力配置的全局默认模型（spec §7.14 / 决议 11–14、21–23）。
/// <para>核心纪律：读端严格查表不回落（spec §13 原则 1），写端校验前缀与能力，失效默认值只报告不清除。</para>
/// </summary>
public class ModelConfigManagerDefaultModelTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "default-models-" + Guid.NewGuid().ToString("N"));

    #region 读：严格查表

    [Fact]
    public async Task GetDefaultModel_Unconfigured_ShouldReturnNull()
    {
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        // 未配置即 null：不回落 chat、不查目录猜（决议 13）
        sut.GetDefaultModel(LlmCapabilityKind.Embedding).Should().BeNull();
        sut.GetDefaultModel(LlmCapabilityKind.Chat).Should().BeNull();
        sut.GetDefaultModel(LlmCapabilityKind.Decision).Should().BeNull();
    }

    [Fact]
    public async Task GetDefaultModel_CamelCaseConfigKey_ShouldResolveSpeechSynthesis()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.SpeechSynthesis)] = "openai/tts-1"
            }
        };
        using var sut = await CreateSutAsync(options);

        sut.GetDefaultModel(LlmCapabilityKind.SpeechSynthesis).Should().Be("openai/tts-1");
        // 合成与转写是两个独立槽位，互不回落
        sut.GetDefaultModel(LlmCapabilityKind.SpeechTranscription).Should().BeNull();
    }

    [Fact]
    public async Task GetAllDefaultModels_ShouldOnlyContainConfiguredEntries()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o",
                ["embedding"] = "openai/text-embedding-3-small",
                ["decision"] = "   "
            }
        };
        using var sut = await CreateSutAsync(options);

        var all = sut.GetAllDefaultModels();

        all.Keys.Should().BeEquivalentTo(new[] { LlmCapabilityKind.Chat, LlmCapabilityKind.Embedding });
        all[LlmCapabilityKind.Chat].Should().Be("openai/gpt-4o");
        all.Should().NotContainKey(LlmCapabilityKind.SpeechSynthesis, "未配置的能力不得出现在结果里");
    }

    [Fact]
    public async Task ValidateDefaultModels_PointsToDeletedModel_ShouldReportAndKeep()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o",
                ["embedding"] = "openai/deleted-embedding"
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = CreateSut(configManager, options);

        var problems = sut.ValidateDefaultModels();

        problems.Keys.Should().BeEquivalentTo(new[] { LlmCapabilityKind.Embedding });
        problems[LlmCapabilityKind.Embedding].Should().Contain("openai/deleted-embedding")
            .And.Contain("不在模型目录");
        // 决议 23：只报告，不自动清除
        configManager.SeeingAgent.DefaultModels.Should().ContainKey("embedding");
        sut.GetDefaultModel(LlmCapabilityKind.Embedding).Should().Be("openai/deleted-embedding");
    }

    [Fact]
    public async Task ValidateDefaultModels_LostCapability_ShouldReportWithoutClearing()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        configManager.SetSectionInMemory("DefaultModels", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat"] = "openai/text-embedding-3-small"
        });
        using var sut = CreateSut(configManager);

        var problems = sut.ValidateDefaultModels();

        problems.Keys.Should().BeEquivalentTo(new[] { LlmCapabilityKind.Chat });
        problems[LlmCapabilityKind.Chat].Should().Contain("不具备该能力");
        configManager.SeeingAgent.DefaultModels.ContainsKey("chat").Should().BeTrue();
    }

    [Fact]
    public async Task ValidateDefaultModels_AllConfigured_ShouldReportNothing()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o",
                ["embedding"] = "openai/text-embedding-3-small"
            }
        };
        using var sut = await CreateSutAsync(options);

        sut.ValidateDefaultModels().Should().BeEmpty();
    }

    #endregion

    #region 写：写时校验

    [Fact]
    public async Task SetDefaultModel_WithoutProviderPrefix_ShouldThrow()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        var act = () => sut.SetDefaultModelAsync(
            LlmCapabilityKind.Embedding,
            "text-embedding-3-small",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        // Review Focus #2：裸 ID 必须拒绝
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*provider/模型*");
        configManager.SeeingAgent.DefaultModels.Should().NotContainKey("embedding");
    }

    [Fact]
    public async Task SetDefaultModel_ModelLacksCapability_ShouldThrow()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        var act = () => sut.SetDefaultModelAsync(
            LlmCapabilityKind.Chat,
            "openai/text-embedding-3-small",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*不具备*");
        configManager.SeeingAgent.DefaultModels.Should().NotContainKey("chat");
    }

    [Fact]
    public async Task SetDefaultModel_UnknownModel_ShouldThrow()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        var act = () => sut.SetDefaultModelAsync(
            LlmCapabilityKind.Chat,
            "openai/not-in-catalog",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*不在模型目录*");
    }

    [Fact]
    public async Task SetDefaultModel_EmbeddingModelAsEmbedding_ShouldPersistAndReadBack()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        await sut.SetDefaultModelAsync(
            LlmCapabilityKind.Embedding,
            "openai/text-embedding-3-small",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        sut.GetDefaultModel(LlmCapabilityKind.Embedding).Should().Be("openai/text-embedding-3-small");

        // 落盘形状：SeeingAgent.DefaultModels.embedding = "provider/model"
        var raw = await ReadUserSeeingJsonAsync();
        raw.Should().Contain("\"DefaultModels\"")
            .And.Contain("\"embedding\"")
            .And.Contain("openai/text-embedding-3-small");
        configManager.GetSeeingAgentOptions().DefaultModels["embedding"]
            .Should().Be("openai/text-embedding-3-small");
    }

    [Fact]
    public async Task SetDefaultModel_Should_Raise_ModelConfigChanged()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        var raised = 0;
        sut.ModelConfigChanged += (_, _) => raised++;

        await sut.SetDefaultModelAsync(
            LlmCapabilityKind.SpeechTranscription,
            "openai/whisper-1",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        // 语音输入等 UI 依赖此事件原位刷新「是否已配置默认模型」；
        // 不触发就只能靠页面重建，跨标签页/多窗口场景会一直显示灰态。
        // 断言 >= 1 而非 == 1：SaveSectionAsync 自身也可能触发一次配置变更事件，
        // 重复通知只是多一次刷新，漏通知才是 bug。
        raised.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ClearDefaultModel_Should_Raise_ModelConfigChanged()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["speechTranscription"] = "openai/whisper-1"
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = CreateSut(configManager, options);

        var raised = 0;
        sut.ModelConfigChanged += (_, _) => raised++;

        await sut.SetDefaultModelAsync(
            LlmCapabilityKind.SpeechTranscription,
            null,
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        raised.Should().BeGreaterThanOrEqualTo(1);
        sut.GetDefaultModel(LlmCapabilityKind.SpeechTranscription).Should().BeNull();
    }

    [Fact]
    public async Task SetDefaultModel_ChangedCapability_ShouldKeepOtherSlots()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o"
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = CreateSut(configManager, options);

        await sut.SetDefaultModelAsync(
            LlmCapabilityKind.Embedding,
            "openai/text-embedding-3-small",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        sut.GetAllDefaultModels().Keys.Should().BeEquivalentTo(
            new[] { LlmCapabilityKind.Chat, LlmCapabilityKind.Embedding });
    }

    [Fact]
    public async Task SetDefaultModel_NullModelId_ShouldClearSlotOnly()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o",
                ["embedding"] = "openai/text-embedding-3-small"
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = CreateSut(configManager, options);

        await sut.SetDefaultModelAsync(
            LlmCapabilityKind.Chat,
            null,
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        sut.GetDefaultModel(LlmCapabilityKind.Chat).Should().BeNull();
        sut.GetDefaultModel(LlmCapabilityKind.Embedding).Should().Be("openai/text-embedding-3-small");
    }

    [Fact]
    public async Task SetDefaultModel_CompositeCapability_ShouldThrow()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var sut = CreateSut(configManager);

        var act = () => sut.SetDefaultModelAsync(
            LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding,
            "openai/gpt-4o",
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*单一*");
    }

    #endregion

    #region 能力判定（打破 ModelType.Text 硬编码）

    [Fact]
    public async Task CanSetAsDefaultModel_EmbeddingModel_AsEmbedding_ShouldBeTrue()
    {
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        sut.CanSetAsDefaultModel("openai/text-embedding-3-small", LlmCapabilityKind.Embedding)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CanSetAsDefaultModel_EmbeddingModel_AsChat_ShouldBeFalse()
    {
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        sut.CanSetAsDefaultModel("openai/text-embedding-3-small", LlmCapabilityKind.Chat)
            .Should().BeFalse();
    }

    [Fact]
    public async Task CanSetAsDefaultModel_UntypedModel_AsChat_ShouldBeTrue()
    {
        // 无 Types 的模型有效类型为 Text → 投影为 chat 能力
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        sut.CanSetAsDefaultModel("openai/gpt-4o", LlmCapabilityKind.Chat).Should().BeTrue();
    }

    [Fact]
    public async Task CanSetAsDefaultModel_MultiTypeModel_AsDecision_ShouldBeFalse()
    {
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        sut.CanSetAsDefaultModel("openai/gpt-4o", LlmCapabilityKind.Decision).Should().BeFalse();
        sut.CanSetAsDefaultModel("missing/model", LlmCapabilityKind.Chat).Should().BeFalse();
    }

    #endregion

    #region TryResolveDefault：三类失败 + 成功

    [Fact]
    public async Task TryResolveDefault_Unconfigured_ShouldGiveReason()
    {
        using var sut = await CreateSutAsync(new SeeingAgentOptions());

        var ok = sut.TryResolveDefault<IEmbeddingClient>(
            LlmCapabilityKind.Embedding, out var client, out var reason);

        ok.Should().BeFalse();
        client.Should().BeNull();
        reason.Should().Contain("未配置").And.Contain("embedding");
    }

    [Fact]
    public async Task TryResolveDefault_RouterMissing_ShouldGiveReason()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["embedding"] = "openai/text-embedding-3-small"
            }
        };
        // 未提供 IServiceProvider ⇒ Router 不可用
        using var sut = await CreateSutAsync(options, services: null);

        var ok = sut.TryResolveDefault<IEmbeddingClient>(
            LlmCapabilityKind.Embedding, out var client, out var reason);

        ok.Should().BeFalse();
        client.Should().BeNull();
        reason.Should().Contain(nameof(ILlmRouter));
    }

    [Fact]
    public async Task TryResolveDefault_RouterFails_ShouldPropagateRouterReason()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "openai/gpt-4o"
            }
        };
        var router = new Mock<ILlmRouter>();
        IChatCompletionClient? nullClient = null;
        string? failReason = "Provider 'openai' 未声明 'chat' 能力";
        router.Setup(r => r.TryResolve("openai/gpt-4o", out nullClient, out failReason))
            .Returns(false);

        using var sut = await CreateSutAsync(options, new SingleRouterProvider(router.Object));

        var ok = sut.TryResolveDefault<IChatCompletionClient>(
            LlmCapabilityKind.Chat, out var client, out var reason);

        ok.Should().BeFalse();
        client.Should().BeNull();
        reason.Should().Contain("解析失败").And.Contain("未声明");
    }

    [Fact]
    public async Task TryResolveDefault_RouterSucceeds_ShouldReturnClient()
    {
        var options = new SeeingAgentOptions
        {
            DefaultModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["embedding"] = "openai/text-embedding-3-small"
            }
        };
        var client = new Mock<IEmbeddingClient>().Object;
        IEmbeddingClient? resolved = client;
        string? noReason = null;
        var router = new Mock<ILlmRouter>();
        router.Setup(r => r.TryResolve("openai/text-embedding-3-small", out resolved, out noReason))
            .Returns(true);

        using var sut = await CreateSutAsync(options, new SingleRouterProvider(router.Object));

        var ok = sut.TryResolveDefault<IEmbeddingClient>(
            LlmCapabilityKind.Embedding, out var result, out var reason);

        ok.Should().BeTrue();
        result.Should().BeSameAs(client);
        reason.Should().BeNull();
        router.VerifyAll();
    }

    #endregion

    #region 构造辅助

    private sealed class SingleRouterProvider : IServiceProvider
    {
        private readonly ILlmRouter? _router;

        public SingleRouterProvider(ILlmRouter? router) => _router = router;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILlmRouter) ? _router : null;
    }

    private static Dictionary<string, ProviderConfig> CreateProviders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = new ProviderConfig
        {
            Id = "openai",
            Type = ProviderTypes.OpenAi,
            Models = new Dictionary<string, ModelConfig>
            {
                ["gpt-4o"] = new ModelConfig { Id = "gpt-4o", Provider = "openai" },
                ["text-embedding-3-small"] = new ModelConfig
                {
                    Id = "text-embedding-3-small",
                    Provider = "openai",
                    Types = [ModelType.Embedding]
                },
                ["whisper-1"] = new ModelConfig
                {
                    Id = "whisper-1",
                    Provider = "openai",
                    Types = [ModelType.Speech],
                    Modalities = new ModelModalities
                    {
                        Input = ["audio"],
                        Output = ["text"]
                    }
                }
            }
        }
    };

    private async Task<ModelConfigManager> CreateSutAsync(
        SeeingAgentOptions options,
        IServiceProvider? services = null)
    {
        var configManager = await CreateConfigManagerAsync(options);
        return CreateSut(configManager, options, services);
    }

    private ModelConfigManager CreateSut(
        UnifiedConfigManager configManager,
        SeeingAgentOptions? options = null,
        IServiceProvider? services = null)
        => new(
            configManager,
            CreateRegistry(CreateProviders()),
            NullLogger<ModelConfigManager>.Instance,
            services);

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(SeeingAgentOptions options)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        var payload = new
        {
            SeeingAgent = options,
            Providers = CreateProviders()
        };
        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "providers.json"),
            JsonSerializer.Serialize(payload.Providers, JsonOptions),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = options }, JsonOptions),
            TestContext.Current.CancellationToken);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync(TestContext.Current.CancellationToken);
        return manager;
    }

    private static IProviderRegistry CreateRegistry(Dictionary<string, ProviderConfig> providers)
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        foreach (var providerId in providers.Keys)
        {
            var provider = new Mock<ILlmProvider>();
            provider.SetupGet(candidate => candidate.Id).Returns(providerId);
            registry.Register(new ProviderRegistration(provider.Object, ProviderOrigin.Configured));
        }

        return registry;
    }

    private async Task<string> ReadUserSeeingJsonAsync()
    {
        var path = Path.Combine(_tempDirectory, "user", ".seeing", "seeing.json");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
            : string.Empty;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    #endregion
}
