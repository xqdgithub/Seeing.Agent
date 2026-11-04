using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Seeing.Agent.Memory.Extensions;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

/// <summary>
/// DI 注册形态（spec §7.8 第 5 条）：不再在注册期二选一，始终注册
/// <see cref="RouterEmbeddingService"/>，未启用的判定与解析失败都推迟到调用期。
/// </summary>
public class MemoryEmbeddingDiTests
{
    [Fact]
    public void AddMemoryServices_ShouldAlways_Register_RouterEmbeddingService()
    {
        var services = CreateServices(new MemoryOptions());

        services.Should().Contain(d => d.ServiceType == typeof(RouterEmbeddingService),
            "向量服务必须常驻注册，DI 期不再按 IsEmbeddingConfigured 挑选实现");
        services.Should().Contain(d => d.ServiceType == typeof(IEmbeddingConnectionTester));
    }

    [Fact]
    public void AddMemoryServices_ShouldNot_Default_To_NullEmbeddingService()
    {
        // NullEmbeddingService 类型仍保留（供显式替身/测试用），但不再是默认注册项。
        var services = CreateServices(new MemoryOptions());

        services.Where(d => d.ServiceType == typeof(NullEmbeddingService)).Should().BeEmpty();
    }

    [Fact]
    public void UnconfiguredOptions_ShouldStill_Resolve_EmbeddingService_WithoutThrowing()
    {
        // 旧的「未配置就注册 NullEmbeddingService」在 DI 期定死实现；
        // 现在服务本体总能解析（它是常驻的 Router 包装），未启用的判定在调用期。
        using var provider = Build(new MemoryOptions());

        var service = provider.GetRequiredService<IEmbeddingService>();

        service.Should().BeOfType<EmbeddingService>();
        service.ProviderName.Should().Be("unconfigured");
        service.Dimensions.Should().Be(0);
    }

    [Fact]
    public void UnconfiguredOptions_Should_Make_Status_Unavailable()
    {
        // 可解析 ≠ 可用：是否建向量索引由 IEmbeddingStatus 决定。
        using var provider = Build(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = "openai/text-embedding-3-small" }
        });

        // 替身目录里没有这个模型 → 仍不可用（不猜、不回落）
        provider.GetRequiredService<IEmbeddingStatus>().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingStatus_ShouldDepend_On_ModelCatalog()
    {
        // 依赖边必须是 Abstractions 接口 IModelManager（spec §7.8 第 7 条）；能力包不得引 Core。
        var ctor = typeof(ConfigurableEmbeddingStatus).GetConstructors().Single();

        ctor.GetParameters().Select(p => p.ParameterType).Should().BeEquivalentTo(new[]
        {
            typeof(IOptionsMonitor<MemoryOptions>),
            typeof(IModelManager),
        });
    }

    [Fact]
    public void RouterEmbeddingService_ShouldDepend_On_Invoker_Not_HttpOrEndpointLookup()
    {
        var ctor = typeof(RouterEmbeddingService).GetConstructors().Single();

        ctor.GetParameters().Select(p => p.ParameterType).Should().BeEquivalentTo(new[]
        {
            typeof(Seeing.Agent.Abstractions.Llm.ILlmInvoker),
            typeof(IModelConfigManager),
            typeof(IOptionsMonitor<MemoryOptions>),
            typeof(Microsoft.Extensions.Logging.ILogger<RouterEmbeddingService>),
        });
    }

    private static ServiceProvider Build(MemoryOptions options) =>
        CreateServices(options).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static ServiceCollection CreateServices(MemoryOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfigSectionStore>(new StubSectionStore(options));
        services.AddSingleton(Mock.Of<IFileStore>());
        services.AddLlmInfraDoubles();
        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");
        return services;
    }

    /// <summary>返回指定 <see cref="MemoryOptions"/> 的配置存储替身。</summary>
    private sealed class StubSectionStore : IConfigSectionStore
    {
        private readonly MemoryOptions _options;
        public StubSectionStore(MemoryOptions options) => _options = options;

        public T GetSection<T>(string sectionName) where T : class, new() =>
            typeof(T) == typeof(MemoryOptions) ? (T)(object)_options : new T();

        public Task SaveSectionAsync<T>(
            string sectionName,
            T value,
            ConfigLevel level = ConfigLevel.Project,
            CancellationToken ct = default) where T : class
            => Task.CompletedTask;

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }
    }
}
