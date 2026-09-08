using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Extensions;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Spec §8：LLM 并列；门面执行器；ProviderConfig.Type 为 string + SupportedTypes。
/// </summary>
public class LlmAndExecutorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "seeing-inv-llm-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Disabling_OpenAi_Does_Not_Affect_Enabled_Anthropic()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "user", ".seeing"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "project", ".seeing"));

        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderTypes.OpenAi,
                ApiKey = "sk-openai"
            },
            ["claude"] = new ProviderConfig
            {
                Id = "claude",
                Type = ProviderTypes.Anthropic,
                ApiKey = "sk-anthropic"
            }
        };

        var userSeeing = Path.Combine(_tempDirectory, "user", ".seeing");
        await File.WriteAllTextAsync(
            Path.Combine(userSeeing, "providers.json"),
            System.Text.Json.JsonSerializer.Serialize(providers));

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory)
            .Returns(Path.Combine(_tempDirectory, "project", ".seeing"));

        var configManager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        await configManager.LoadAsync();

        var openAiClient = Mock.Of<ILlmClient>(c => c.ProviderType == ProviderTypes.OpenAi);
        var anthropicClient = Mock.Of<ILlmClient>(c => c.ProviderType == ProviderTypes.Anthropic);

        var openAi = new Mock<ILlmClientFactory>();
        openAi.SetupGet(f => f.SupportedTypes)
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.OpenAi });
        openAi.Setup(f => f.SupportsType(It.IsAny<string>()))
            .Returns((string t) => string.Equals(t, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase));
        openAi.Setup(f => f.Create(It.IsAny<ProviderConfig>())).Returns(openAiClient);

        var anthropic = new Mock<ILlmClientFactory>();
        anthropic.SetupGet(f => f.SupportedTypes)
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.Anthropic });
        anthropic.Setup(f => f.SupportsType(It.IsAny<string>()))
            .Returns((string t) => string.Equals(t, ProviderTypes.Anthropic, StringComparison.OrdinalIgnoreCase));
        anthropic.Setup(f => f.Create(It.IsAny<ProviderConfig>())).Returns(anthropicClient);

        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        using var sut = new ProviderManager(
            configManager,
            [openAi.Object, anthropic.Object],
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        sut.GetClient("claude").Should().BeSameAs(anthropicClient);
        sut.GetClient("openai").Should().BeSameAs(openAiClient);

        // 「关 openai」= 仅 anthropic 工厂仍可解析 anthropic 路径（并列独立）
        using var anthropicOnly = new ProviderManager(
            configManager,
            [anthropic.Object],
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        anthropicOnly.GetClient("claude").Should().BeSameAs(anthropicClient);
        anthropicOnly.ResolveFactory(ProviderTypes.OpenAi).Should().BeNull();
        anthropicOnly.ResolveFactory(ProviderTypes.Anthropic).Should().BeSameAs(anthropic.Object);
    }

    [Fact]
    public void Container_Has_One_IAgentExecutor_Router_And_Multiple_Implementations_No_Replace()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingAcp(registry);
        services.AddSeeingCore(registry);

        var executors = services.Where(d => d.ServiceType == typeof(IAgentExecutor)).ToList();
        executors.Should().HaveCount(1);
        executors[0].ImplementationType.Should().Be(typeof(AgentExecutorRouter));

        var implementations = services
            .Where(d => d.ServiceType == typeof(IAgentExecutorImplementation))
            .Select(d => d.ImplementationType)
            .ToList();

        implementations.Should().HaveCount(2);
        implementations.Should().Contain(typeof(NativeAgentExecutor));
        implementations.Should().Contain(typeof(AcpAgentExecutor));
        executors.Should().NotContain(d => d.ImplementationType == typeof(AcpAgentExecutor));

        typeof(AcpServiceCollectionExtensions).GetMethod(
                "ReplaceExecutionRouter",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().BeNull();
    }

    [Fact]
    public void Router_Dispatches_By_Runtime()
    {
        var nativeCalled = false;
        var acpCalled = false;

        var native = new Mock<IAgentExecutorImplementation>();
        native.SetupGet(i => i.SupportedRuntime).Returns(AgentRuntime.Native);
        native.Setup(i => i.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream)
            .Callback(() => nativeCalled = true);

        var acp = new Mock<IAgentExecutorImplementation>();
        acp.SetupGet(i => i.SupportedRuntime).Returns(AgentRuntime.AcpPassthrough);
        acp.Setup(i => i.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream)
            .Callback(() => acpCalled = true);

        var router = new AgentExecutorRouter([native.Object, acp.Object]);

        _ = router.ExecuteAsync(
            new AgentDefinition { Name = "n", Runtime = AgentRuntime.Native },
            [],
            new AgentContext(),
            CancellationToken.None);
        nativeCalled.Should().BeTrue();
        acpCalled.Should().BeFalse();

        nativeCalled = false;
        _ = router.ExecuteAsync(
            new AgentDefinition { Name = "a", Runtime = AgentRuntime.AcpPassthrough },
            [],
            new AgentContext(),
            CancellationToken.None);
        acpCalled.Should().BeTrue();
        nativeCalled.Should().BeFalse();
    }

    [Fact]
    public void ProviderConfig_Type_Is_String_And_ThirdParty_Factory_Uses_SupportedTypes()
    {
        typeof(ProviderConfig).GetProperty(nameof(ProviderConfig.Type))!
            .PropertyType.Should().Be(typeof(string));

        // 路由键是 string，不是 ProviderType 枚举
        typeof(ProviderConfig).Assembly.GetType("Seeing.Agent.Abstractions.Llm.ProviderType")
            .Should().BeNull("第三方经 SupportedTypes 声明，不改 Abstractions ProviderType 枚举");

        ILlmClientFactory factory = new ThirdPartyFactory();
        factory.SupportedTypes.Should().Contain("custom-vendor");
        factory.SupportsType("Custom-Vendor").Should().BeTrue();
        factory.SupportsType(ProviderTypes.OpenAi).Should().BeFalse();
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class ThirdPartyFactory : ILlmClientFactory
    {
        public IReadOnlySet<string> SupportedTypes { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom-vendor" };

        public bool SupportsType(string type) =>
            !string.IsNullOrWhiteSpace(type) && SupportedTypes.Contains(type);

        public ILlmClient Create(ProviderConfig config) =>
            throw new NotSupportedException();
    }
}
