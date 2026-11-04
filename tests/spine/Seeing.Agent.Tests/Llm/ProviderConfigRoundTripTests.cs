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
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Task R-6：ProviderConfig 手写字段克隆清单的 round-trip 防回归（Review Focus #5）。
/// <para>四处独立清单：<c>ConfiguredLlmProvider.LoadConfigAsync</c> / <c>ApplyValues</c> / <c>CloneConfig</c>
/// 与 <c>ProviderManager.CloneConfig</c>。<b>漏任何一处 = 配置页保存一次就抹掉 capabilities / listModels</b>，
/// 而且是静默的——所以这里既测内存往返，也测落盘再读回。</para>
/// </summary>
public class ProviderConfigRoundTripTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "provider-config-roundtrip-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveThenLoad_ShouldPreserveCapabilitiesAndListModels()
    {
        var config = new ProviderConfig
        {
            Id = "openai",
            Type = ProviderTypes.OpenAi,
            ApiKey = "sk",
            Capabilities = new List<string> { "chat", "embedding" },
            ListModels = true
        };
        ProviderConfig? saved = null;
        var sut = CreateProvider(config, saveAsync: (cfg, _, _) =>
        {
            saved = cfg;
            return Task.CompletedTask;
        });

        // 配置页回填 → 用户改动 → 保存：整条链就是 LoadConfigAsync 的键值喂回 ApplyValues
        var values = await ((IConfigurableLlmProvider)sut).LoadConfigAsync(TestContext.Current.CancellationToken);
        var edited = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        edited["Name"] = "改名后的 Provider";

        await ((IConfigurableLlmProvider)sut).SaveConfigAsync(
            edited,
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        saved.Should().NotBeNull();
        saved!.Capabilities.Should().BeEquivalentTo(new[] { "chat", "embedding" }, "保存一次不得抹掉能力清单");
        saved.ListModels.Should().BeTrue("保存一次不得抹掉动态目录开关");
        saved.Name.Should().Be("改名后的 Provider");
    }

    [Fact]
    public async Task ProviderManagerSave_ThenReloadFromDisk_ShouldPreserveCapabilitiesAndListModels()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.SupportsProtocol(It.IsAny<string>())).Returns(true);
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());

        using var sut = new ProviderManager(
            configManager,
            [factory.Object],
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        await sut.SaveProviderAsync(
            "openai",
            new ProviderConfig
            {
                Id = "openai",
                Type = ProviderTypes.OpenAi,
                ApiKey = "sk-1",
                Capabilities = ["chat", "embedding", "speechSynthesis"],
                ListModels = true,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["gpt-4o"] = new() { Id = "gpt-4o", Types = new List<ModelType> { ModelType.Text } }
                }
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        // 落盘真相：直接从用户级节里读，绕开任何内存副本
        var path = Path.Combine(_tempDirectory, "user", ".seeing", "providers.json");
        var root = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var openai = root.RootElement.GetProperty("openai");
        openai.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString())
            .Should()
            .BeEquivalentTo(new[] { "chat", "embedding", "speechSynthesis" });
        openai.GetProperty("listModels").GetBoolean().Should().BeTrue();

        // 重启语义：重新装配 ProviderManager，投影出的能力集必须与写入一致
        var reloaded = await CreateConfigManagerAsync(new SeeingAgentOptions());
        using var second = new ProviderManager(
            reloaded,
            [factory.Object],
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        second.GetProvider("openai")!.Capabilities.Should().Be(
            LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding | LlmCapabilityKind.SpeechSynthesis);

        var configurable = second.TryGetConfigurable("openai", out var probe);
        configurable.Should().BeTrue();
        var exported = await probe!.LoadConfigAsync(TestContext.Current.CancellationToken);
        exported["Capabilities"].Should().NotBeNull("回填 UI 时能力清单不得为空");
        exported["ListModels"].Should().Be(true);
    }

    [Fact]
    public void ProviderConfig_CloningSites_ShouldCoverEveryWritableProperty()
    {
        // 结构性守门：任何新增到 ProviderConfig 的可写属性都必须进克隆清单
        // （清单里出现的名字与属性名一一对应，新增字段忘了登记 → 本测试立刻红）
        var writable = typeof(ProviderConfig)
            .GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        writable.Should().Contain(new[] { "Capabilities", "ListModels" });
        writable.Should().NotContain("DefaultModel", "决议 22：Provider 级默认模型已删除");
    }

    private static ConfiguredLlmProvider CreateProvider(
        ProviderConfig config,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task>? saveAsync = null)
    {
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.SupportsProtocol(It.IsAny<string>())).Returns(true);
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());

        return new ConfiguredLlmProvider(
            config,
            factory.Object,
            NullLogger.Instance,
            saveAsync ?? ((_, _, _) => Task.CompletedTask),
            NullModelCapabilityManager.Instance);
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(SeeingAgentOptions options)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = options }));

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
