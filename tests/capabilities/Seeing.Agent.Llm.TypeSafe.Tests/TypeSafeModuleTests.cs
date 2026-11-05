using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Llm.TypeSafe.Tests;

public class TypeSafeModuleTests
{
    [Fact]
    public void 模块元数据_应声明稳定Id且无工具与依赖()
    {
        var module = new TypeSafeModule();

        module.Id.Should().Be("llm.typesafe");
        module.ProvidedTools.Should().BeEmpty();
        module.ProvidedSeams.Should().BeEmpty();
        module.DependsOn.Should().BeEmpty();
    }

    /// <summary>
    /// 反向守门：协议包<b>只登记工厂</b>，不得注册任何 Provider。
    /// <para>早先这里注册了一个 <see cref="ProviderOrigin.Builtin"/> 的 Provider，
    /// 使设置页把一个非用户配置的 Provider 当成「配置型」渲染，还给了它一份用户看不见的私有配置文件。
    /// 该缺陷单元测试看不见——只有这条断言能钉住「能力包不造 Provider」。</para>
    /// </summary>
    [Fact]
    public async Task Activate_ShouldNotRegisterAnyProvider()
    {
        var registry = new FakeProviderRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<IProviderRegistry>(registry);

        var module = new TypeSafeModule();
        module.ConfigureServices(services);

        await using var sp = services.BuildServiceProvider();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registry.GetProviders().Should().BeEmpty(
            "llm.typesafe 只管协议；Provider 由插件 provider.typesafe-api 或用户配置提供");
    }

    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        private readonly Dictionary<string, ProviderRegistration> _registrations
            = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

        public IReadOnlyDictionary<string, ILlmProvider> GetProviders()
            => _registrations.ToDictionary(p => p.Key, p => p.Value.Provider);

        public IReadOnlyDictionary<string, ProviderRegistration> GetRegistrations()
            => _registrations;

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
