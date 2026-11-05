using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using System.Reflection;
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
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());

        using var sut = new ProviderManager(
            configManager,
            new LlmClientFactoryResolver([new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory.Object)]),
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
            new LlmClientFactoryResolver([new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory.Object)]),
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
        // 结构性守门（Review Focus #5）：Provider 表单「导出 → 写回」链路
        // （ConfiguredLlmProvider.LoadConfigAsync ↔ ApplyValues）漏一处字段，
        // 就会出现「保存一次配置就静默抹掉该字段」。
        var config = new ProviderConfig { Id = "p", Type = ProviderTypes.OpenAi };
        var probe = BuildProbeSession(config);

        probe.Import(probe.NonDefaultValues);

        foreach (var (name, expected) in probe.NonDefaultValues)
        {
            var actual = probe.Read(name);
            actual.Should().BeEquivalentTo(expected,
                $"{name} 未在 Provider 表单往返链路中保留——检查 ConfiguredLlmProvider 的 " +
                "LoadConfigAsync / ApplyValues / CloneConfig 与 ProviderManager.CloneConfig");
        }
    }

    [Fact]
    public async Task ProviderConfig_LoadConfig_ShouldExportEveryFormManagedField()
    {
        // 导出方向的单独守门：LoadConfigAsync 少导出一个键，UI 里就看不到该字段、
        // 编辑后也写不回（"静默丢失"的另一半）。上一个测试只覆盖写回方向。
        var config = new ProviderConfig { Id = "p", Type = ProviderTypes.OpenAi };
        var provider = CreateProvider(config);

        var exported = await provider.LoadConfigAsync(TestContext.Current.CancellationToken);

        foreach (var field in FormManagedFields)
            exported.Keys.Should().Contain(field,
                $"LoadConfigAsync 必须导出表单字段 '{field}'，否则 UI 无法回填它");
    }

    [Fact]
    public void ProviderConfig_NewWritableProperty_ShouldBeRegisteredOrExplicitlyExcluded()
    {
        // 守门元测试：白名单必须覆盖「除显式排除项外的全部可写属性」。
        // 新增 ProviderConfig 属性却忘了改 ApplyValues/LoadConfigAsync 时，这里会红。
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ProviderConfig.Id),        // 身份字段，按配置键固定
            nameof(ProviderConfig.Models),    // 走模型 CRUD（SaveModelsAsync）
            nameof(ProviderConfig.Options),   // Provider 私有扩展，不走表单往返
        };

        var writable = typeof(ProviderConfig)
            .GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !excluded.Contains(name))
            .ToHashSet(StringComparer.Ordinal);

        writable.Should().BeSubsetOf(FormManagedFields,
            "每个可写属性要么进 Provider 表单往返链路（加到 FormManagedFields + ApplyValues + LoadConfigAsync），" +
            "要么显式列入本测试的 excluded 并说明它走哪条专门通道");
    }

    [Fact]
    public void ProviderConfig_ShouldNotHaveProviderLevelDefaultModel()
    {
        // 决议 22：默认模型只在全局 DefaultModels 一处，Provider 级已删除。
        typeof(ProviderConfig).GetProperty("DefaultModel").Should().BeNull();
    }

    private static ConfiguredLlmProvider CreateProvider(
        ProviderConfig config,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task>? saveAsync = null)
    {
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());

        return new ConfiguredLlmProvider(
            config,
            factory.Object,
            NullLogger.Instance,
            saveAsync ?? ((_, _, _) => Task.CompletedTask),
            NullModelCapabilityManager.Instance);
    }

    /// <summary>
    /// 克隆链路探针：验证 <c>ConfiguredLlmProvider</c> 的表单「导出 → 写回」链路
    /// 是否覆盖 <see cref="ProviderConfig"/> 的全部<b>表单可写字段</b>。
    /// <para>用反射遍历属性，但以 <see cref="FormManagedFields"/> 白名单为口径——
    /// 白名单必须与 <c>ConfiguredLlmProvider.ApplyValues</c> 实际处理的字段一一对应；
    /// 新增字段若只加属性不加白名单，本测试会因「白名单缺字段」而失败（见下方断言）。</para>
    /// </summary>
    private static CloneProbeSession BuildProbeSession(ProviderConfig config)
    {
        var provider = CreateProvider(config);

        var configField = typeof(ConfiguredLlmProvider)
            .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance);
        if (configField is null)
            throw new InvalidOperationException("ConfiguredLlmProvider._config 字段缺失，克隆守门测试已失效");

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in typeof(ProviderConfig).GetProperties().Where(p => p.CanWrite))
        {
            if (!FormManagedFields.Contains(property.Name))
                continue;

            values[property.Name] = BuildNonDefaultValue(property.Name, property.PropertyType);
        }

        return new CloneProbeSession(
            Import: v => provider.SaveConfigAsync(v, ConfigLevel.User).GetAwaiter().GetResult(),
            Read: name => ((ProviderConfig)configField.GetValue(provider)!).GetType()
                .GetProperty(name)!.GetValue(configField.GetValue(provider)),
            NonDefaultValues: values);
    }

    /// <summary>
    /// Provider 表单链路（<c>LoadConfigAsync</c> ↔ <c>ApplyValues</c>）管理的字段集。
    /// <para><b>不含</b> <c>Id</c>（身份，按配置键固定）、<c>Models</c>（走模型 CRUD）、
    /// <c>Options</c>（Provider 私有扩展，不走表单往返）——它们各有专门通道。</para>
    /// </summary>
    private static readonly HashSet<string> FormManagedFields = new(StringComparer.Ordinal)
    {
        nameof(ProviderConfig.Name),
        nameof(ProviderConfig.Type),
        nameof(ProviderConfig.BaseUrl),
        nameof(ProviderConfig.ApiKey),
        nameof(ProviderConfig.Proxy),
        nameof(ProviderConfig.UseProxy),
        nameof(ProviderConfig.Headers),
        nameof(ProviderConfig.Timeout),
        nameof(ProviderConfig.MaxRetries),
        nameof(ProviderConfig.RetryBaseDelayMs),
        nameof(ProviderConfig.RetryMaxDelayMs),
        nameof(ProviderConfig.RetryTotalBudgetMs),
        nameof(ProviderConfig.Capabilities),
        nameof(ProviderConfig.ListModels),
    };

    /// <summary>给定属性造一个「明显不同于默认值」的样本，用于往返比对。</summary>
    /// <remarks>
    /// <c>Capabilities</c> 必须是合法能力名——写入会经 <c>ParseCapabilities</c> 校验，
    /// 用 "probe" 会被正确地拒绝（这本身说明守门有效，但会让本测试测不到克隆链）。
    /// </remarks>
    private static object? BuildNonDefaultValue(string propertyName, Type type)
    {
        if (propertyName == nameof(ProviderConfig.Capabilities))
            return new List<string>
            {
                LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Chat),
                LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Embedding)
            };

        if (type == typeof(string))
            return "probe-value";

        if (type == typeof(bool))
            return true;

        if (type == typeof(int))
            return 42;

        if (type == typeof(int?))
            return 42;

        if (type == typeof(List<string>))
            return new List<string> { "probe" };

        if (type == typeof(Dictionary<string, string>))
            return new Dictionary<string, string> { ["probe"] = "1" };

        if (type == typeof(Dictionary<string, object>))
            return new Dictionary<string, object> { ["probe"] = 1 };

        if (type == typeof(Dictionary<string, ModelConfig>))
            return new Dictionary<string, ModelConfig>
            {
                ["probe-model"] = new() { Id = "probe-model" }
            };

        return null;
    }

    private sealed record CloneProbeSession(
        Action<IReadOnlyDictionary<string, object?>> Import,
        Func<string, object?> Read,
        IReadOnlyDictionary<string, object?> NonDefaultValues);

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
