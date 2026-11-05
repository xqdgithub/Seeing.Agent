using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using Xunit;

namespace Seeing.Provider.TypeSafeApi.Tests;

/// <summary>
/// Provider 的行为：协议/能力投影、schema 表单字段、种子模型与客户端构造。
/// <para>不需要真实网络：客户端构造不发起请求。</para>
/// </summary>
public class TypeSafeApiProviderTests
{
    [Fact]
    public void Protocol_ShouldBeTypeSafe()
    {
        CreateSut().Protocol.Should().Be(ProviderTypes.TypeSafe);
    }

    [Fact]
    public void Capabilities_ShouldDeclareDecisionOnly()
    {
        // TypeSafe 协议只实现 decision；声明其它能力会在 Router 第 5 层报错
        CreateSut().Capabilities.Should().Be(LlmCapabilityKind.Decision);
    }

    [Fact]
    public void Id_ShouldBeStable()
    {
        CreateSut().Id.Should().Be("typesafe-api");
    }

    /// <summary>
    /// 设置页只暴露 ApiKey：这是一个预置 client，端点/能力/超时/代理都是插件自身的事实，
    /// 让用户可改只会制造必然失败的配置（例如给 TypeSafe 声明 chat 能力会在 Router 第 5 层报错）。
    /// </summary>
    [Fact]
    public void GetConfigSchema_ShouldExposeOnlyApiKey()
    {
        var schema = CreateSut().GetConfigSchema();

        schema.Should().NotBeNull();
        schema!.Select(f => f.Name).Should().BeEquivalentTo("ApiKey");
    }

    /// <summary>
    /// 端点、超时、目录行为都固定在插件里，不读配置——即使配置文件里写了也不生效。
    /// 这条断言防止有人后来「顺手」把它们接回 Options。
    /// <para><b>UseProxy 例外</b>：它必须留在框架默认值（<c>true</c> = 跟随系统代理），
    /// 与 openai / anthropic / 配置驱动 Provider 一致。写死 <c>false</c> 会让请求直连，
    /// 对按来源 IP 判定地域的服务会直接拿到 451。</para>
    /// </summary>
    [Fact]
    public void ProviderConfig_ShouldUseFixedEndpointSettings_IgnoringOptions()
    {
        var provider = CreateSut();
        var config = (ProviderConfig)typeof(TypeSafeApiProvider)
            .GetMethod("BuildProviderConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(provider, null)!;

        config.BaseUrl.Should().Be(TypeSafeApiProvider.DefaultBaseUrl);
        config.Timeout.Should().Be(TypeSafeApiProvider.DefaultTimeoutMs);
        config.ListModels.Should().BeTrue();
        config.Capabilities.Should().BeNull("能力是协议事实，不由配置覆盖");
        config.UseProxy.Should().BeTrue(
            "必须跟随系统代理（框架默认），与其他 Provider 一致；写死 false 会导致直连");
    }

    /// <summary>
    /// ApiKey 刻意<b>不是必填</b>：本插件的设计前提是「无凭据也能用种子模型」，
    /// 必填会让表单拦住保存、也无法清空已保存的 key 回到无凭据状态。
    /// </summary>
    [Fact]
    public void GetConfigSchema_ApiKey_ShouldNotBeRequired()
    {
        var apiKey = CreateSut().GetConfigSchema()!
            .Single(f => f.Name == "ApiKey");

        apiKey.Type.Should().Be(ConfigFieldType.Secret);
        apiKey.Required.Should().BeFalse();
    }

    /// <summary>
    /// 无任何配置时目录也非空：远端目录需要凭据，
    /// 静态种子模型（jev-latest）是「装完即用」的必要条件。
    /// </summary>
    [Fact]
    public async Task GetModelsAsync_WithoutConfig_ShouldExposeSeedDecisionModel()
    {
        var models = await CreateSut().GetModelsAsync(TestContext.Current.CancellationToken);

        models.Should().Contain(m => m.Id == "jev-latest");
        models.Single(m => m.Id == "jev-latest").Types.Should().Contain(ModelType.Decision);
    }

    [Fact]
    public void GetClient_Decision_ShouldReturnFactoryProduct()
    {
        var client = Mock.Of<IDecisionClient>();
        var sut = CreateSut(factory: client);

        sut.GetClient(LlmCapabilityKind.Decision).Should().BeSameAs(client);
    }

    [Fact]
    public void GetClient_Chat_ShouldReturnNull()
    {
        CreateSut().GetClient(LlmCapabilityKind.Chat).Should().BeNull(
            "TypeSafe 工厂不实现 chat；Provider 不得虚报能力");
    }

    private static TypeSafeApiProvider CreateSut(
        ILlmCapabilityClient? factory = null,
        string? root = null)
    {
        var store = new TypeSafeApiConfigStore(
            root ?? Path.Combine(Path.GetTempPath(), "typesafe-provider-" + Guid.NewGuid().ToString("N")),
            NullLogger<TypeSafeApiConfigStore>.Instance);

        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(
                ProviderTypes.TypeSafe,
                new StubFactory(factory ?? Mock.Of<IDecisionClient>()))]);

        return new TypeSafeApiProvider(
            store,
            resolver,
            new FakeRegistry(),
            NullLogger<TypeSafeApiProvider>.Instance);
    }

    private sealed class StubFactory(ILlmCapabilityClient client) : ILlmClientFactory
    {
        public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
            => capability.HasFlag(LlmCapabilityKind.Decision) ? client : null;
    }

    private sealed class FakeRegistry : IProviderRegistry
    {
        private readonly Dictionary<string, ProviderRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

        public IReadOnlyDictionary<string, ILlmProvider> GetProviders()
            => _registrations.ToDictionary(p => p.Key, p => p.Value.Provider);

        public IReadOnlyDictionary<string, ProviderRegistration> GetRegistrations() => _registrations;

        public ILlmProvider? GetProvider(string id)
            => _registrations.TryGetValue(id, out var r) ? r.Provider : null;

        public ProviderRegistration? GetRegistration(string id)
            => _registrations.TryGetValue(id, out var r) ? r : null;

        public ProviderOrigin? GetOrigin(string id)
            => _registrations.TryGetValue(id, out var r) ? r.Origin : null;

        public string? GetOwnerId(string id)
            => _registrations.TryGetValue(id, out var r) ? r.OwnerId : null;

        public bool IsConfigured(string id) => GetOrigin(id) == ProviderOrigin.Configured;

        public void Register(ProviderRegistration registration)
        {
            _registrations[registration.Provider.Id] = registration;
            ProvidersChanged?.Invoke(this, new ProvidersChangedEventArgs(
                GetProviders(), [registration.Provider.Id], null));
        }

        public bool Unregister(string id) => _registrations.Remove(id);

        public int UnregisterByOwner(string ownerId)
        {
            var ids = _registrations.Where(p => p.Value.OwnerId == ownerId).Select(p => p.Key).ToArray();
            foreach (var id in ids) _registrations.Remove(id);
            return ids.Length;
        }
    }
}
