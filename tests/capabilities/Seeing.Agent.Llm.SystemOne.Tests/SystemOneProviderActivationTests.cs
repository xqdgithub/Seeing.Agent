using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm.SystemOne.Clients;
using Seeing.Agent.Llm.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests;

/// <summary>
/// SystemOne 作为<b>内置 Provider</b> 的启动链守门：Activate 必须把 Provider 挂进注册表，
/// 且未做任何配置时也能被解析出 decision 客户端与模型。
/// <para>背景：此前 <c>llm.systemone</c> 只注册工厂、不注册 Provider，
/// 等于这个能力包装上后完全不可用（用户必须手写 <c>Providers.typesafe</c>）。
/// 而删掉 Activate 里的注册调用后原有 55 个测试全绿——说明这条链此前零守护。</para>
/// </summary>
public class SystemOneProviderActivationTests
{
    [Fact]
    public async Task Activate_ShouldRegisterProviderIntoRegistry()
    {
        var services = CreateServices(out var registry);
        var module = new SystemOneModule();
        module.ConfigureServices(services);

        await using var sp = services.BuildServiceProvider();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registry.Verify(
            r => r.Register(It.IsAny<SystemOneLlmProvider>(), null),
            Times.Once,
            "内置 Provider 必须在 Activate 时挂进注册表，否则用户不开箱可用");
    }

    /// <summary>
    /// 未配置 apiKey 时也要有可路由的模型：远端目录需要凭据，
    /// 因此静态种子模型（jev-latest）是「装完即用」的必要条件。
    /// </summary>
    [Fact]
    public async Task Provider_WithoutAnyConfig_ShouldStillExposeDecisionModel()
    {
        var provider = CreateProvider(out _);
        await provider.WarmupAsync(TestContext.Current.CancellationToken);

        provider.Capabilities.Should().HaveFlag(LlmCapabilityKind.Decision);

        var models = await provider.GetModelsAsync(TestContext.Current.CancellationToken);
        models.Should().Contain(
            m => m.Id == SystemOneOptions.SeedModelId,
            "无凭据时远端目录为空，必须靠静态种子模型保证可路由");
        models.Single(m => m.Id == SystemOneOptions.SeedModelId).Types
            .Should().Contain(ModelType.Decision);
    }

    [Fact]
    public async Task Provider_WithoutAnyConfig_ShouldServeDecisionClient()
    {
        var provider = CreateProvider(out _);
        await provider.WarmupAsync(TestContext.Current.CancellationToken);

        var client = provider.GetClient(LlmCapabilityKind.Decision);
        client.Should().NotBeNull(
            "无 apiKey 也应能建出客户端（失败发生在请求期，不是构造期）");
        if (client is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        provider.GetClient(LlmCapabilityKind.Chat).Should().BeNull(
            "TypeSafe 工厂不实现 chat；Provider 不得虚报能力");
    }

    /// <summary>
    /// 用户在 <c>Providers</c> 节显式配置了同 id 的 Provider 时，内置 Provider 必须让位。
    /// <para><see cref="IProviderRegistry"/> 是后注册者覆盖，所以 Activate 必须主动检查，
    /// 光靠注释说「用户优先」是不成立的。</para>
    /// </summary>
    [Fact]
    public async Task Activate_WhenUserProviderWithSameIdExists_ShouldNotOverride()
    {
        var registry = new FakeProviderRegistry();
        var provider = CreateProvider(out _);

        // 先放一个用户配置驱动的 Provider（同一个 id）
        var userProvider = new Mock<ILlmProvider>();
        userProvider.SetupGet(p => p.Id).Returns(SystemOneOptions.DefaultId);
        registry.Register(userProvider.Object, ownerExtensionId: null);

        var services = new ServiceCollection();
        services.AddSingleton<IProviderRegistry>(registry);
        services.AddSingleton(provider);
        var module = new SystemOneModule();

        await using var sp = services.BuildServiceProvider();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registry.GetProvider(SystemOneOptions.DefaultId).Should().BeSameAs(userProvider.Object,
            "用户显式配置的 Provider 优先级高于内置默认");
    }

    /// <summary>轻量注册表替身（测试项目不引用 Core，不能用真实 ProviderRegistry）。</summary>
    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        private readonly Dictionary<string, ILlmProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

        public IReadOnlyDictionary<string, ILlmProvider> GetProviders() => _providers;

        public ILlmProvider? GetProvider(string id)
            => _providers.TryGetValue(id, out var provider) ? provider : null;

        public string? GetOwnerExtensionId(string id) => null;

        public void Register(ILlmProvider provider, string? ownerExtensionId = null)
        {
            _providers[provider.Id] = provider;
            ProvidersChanged?.Invoke(this, new ProvidersChangedEventArgs(
                _providers, [provider.Id], null));
        }

        public bool Unregister(string id) => _providers.Remove(id);

        public int UnregisterByOwner(string ownerExtensionId) => 0;
    }

    /// <summary>Provider 的 id 固定，不随配置变化（可变 id 会让注册表旧键永久残留）。</summary>
    [Fact]
    public void ProviderId_ShouldBeStable_NotDerivedFromOptions()
    {
        var provider = CreateProvider(out _);

        provider.Id.Should().Be(SystemOneOptions.DefaultId);

        // 类型上没有 Id 属性可改：配置无法重命名内置 Provider
        typeof(SystemOneOptions).GetProperty("Id").Should().BeNull(
            "注册表按 id 索引，内建 Provider 的 id 不可配置");
    }

    private static ServiceCollection CreateServices(out Mock<IProviderRegistry> registry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IWorkspaceProvider>());
        services.AddSingleton(Mock.Of<IModelCapabilityManager>());

        var mock = new Mock<IProviderRegistry>();
        registry = mock;
        services.AddSingleton(mock.Object);

        // 协议→工厂：typesafe 绑真实工厂（不依赖 Core）
        services.AddSingleton<ILlmClientFactory, TypeSafeSystemOneClientFactory>();
        services.AddSingleton<ILlmClientFactoryResolver>(sp => new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, sp.GetRequiredService<ILlmClientFactory>())]));

        return services;
    }

    private static SystemOneLlmProvider CreateProvider(out Mock<IProviderRegistry> registry)
    {
        var services = CreateServices(out registry);
        var store = new SystemOneConfigStore(
            Path.Combine(Path.GetTempPath(), "systemone-activation-" + Guid.NewGuid().ToString("N")),
            NullLogger<SystemOneConfigStore>.Instance);

        // 不能 using：resolver 会在 Provider 生命周期内被调用（构建客户端）。
        var sp = services.BuildServiceProvider();
        return new SystemOneLlmProvider(
            store,
            sp.GetRequiredService<ILlmClientFactoryResolver>(),
            Mock.Of<IProviderRegistry>(),
            NullLogger<SystemOneLlmProvider>.Instance);
    }
}
