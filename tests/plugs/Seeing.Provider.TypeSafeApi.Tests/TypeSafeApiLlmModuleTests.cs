using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Provider.TypeSafeApi.Tests;

/// <summary>
/// TypeSafe 端点插件的注册与开箱可用性守门。
/// <para>背景：这段 Provider 逻辑原本在能力包 <c>llm.typesafe</c> 里以 <see cref="ProviderOrigin.Builtin"/>
/// 注册，导致设置页把它当成「配置型」渲染。下沉为插件后，来源必须是
/// <see cref="ProviderOrigin.Extension"/>，且生命周期仍归 DI 容器。</para>
/// </summary>
public class TypeSafeApiLlmModuleTests
{
    [Fact]
    public void 模块元数据_应声明稳定Id并依赖协议包()
    {
        var module = new TypeSafeApiLlmModule();

        module.Id.Should().Be("provider.typesafe-api");
        module.DependsOn.Should().Equal("llm.typesafe");
        module.ProvidedTools.Should().BeEmpty();
        module.ProvidedSeams.Should().BeEmpty();
    }

    [Fact]
    public async Task Activate_ShouldRegisterAsExtension_WithContainerOwnedLifetime()
    {
        var registry = new FakeProviderRegistry();
        var (module, sp) = Build(registry);

        await using (sp)
        {
            await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

            var registration = registry.GetRegistration(TypeSafeApiProvider.ProviderId);
            registration.Should().NotBeNull("Activate 必须把 Provider 挂进注册表，否则装完不可用");
            registration!.Origin.Should().Be(ProviderOrigin.Extension,
                "插件注册的来源必须是 Extension，设置页据此标「扩展」并走 schema 表单");
            registration.OwnerId.Should().Be(TypeSafeApiProvider.ExtensionId);
            registration.DisposeByRegistry.Should().BeFalse(
                "实例来自 DI 单例，注册表释放它会让热重载后再 Activate 撞 ObjectDisposedException");
        }
    }

    [Fact]
    public async Task Deactivate_ShouldUnregisterProvider()
    {
        var registry = new FakeProviderRegistry();
        var (module, sp) = Build(registry);

        await using (sp)
        {
            await module.ActivateAsync(sp, TestContext.Current.CancellationToken);
            await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);

            registry.GetProvider(TypeSafeApiProvider.ProviderId).Should().BeNull();
        }
    }

    [Fact]
    public async Task Deactivate_ThenActivate_ShouldNotThrow_WhenRegistryDoesNotOwnLifetime()
    {
        var registry = new FakeProviderRegistry();
        var (module, sp) = Build(registry);

        await using (sp)
        {
            await module.ActivateAsync(sp, TestContext.Current.CancellationToken);
            await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);

            // 注册表不释放 DI 单例 → 二次激活后仍可取客户端
            var act = async () => await module.ActivateAsync(sp, TestContext.Current.CancellationToken);
            await act.Should().NotThrowAsync();
        }
    }

    private static (TypeSafeApiLlmModule Module, ServiceProvider Provider) Build(FakeProviderRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkspaceProvider>(new TempWorkspaceProvider());
        services.AddSingleton<IProviderRegistry>(registry);
        services.AddSingleton(Mock.Of<IModelCapabilityManager>());
        services.AddSingleton<ILlmClientFactoryResolver>(sp => new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.TypeSafe, new StubTypeSafeFactory())]));

        var module = new TypeSafeApiLlmModule();
        module.ConfigureServices(services);

        return (module, services.BuildServiceProvider());
    }

    /// <summary>不触网的最小工厂：只为让 Provider 能建出客户端壳。</summary>
    private sealed class StubTypeSafeFactory : ILlmClientFactory
    {
        public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
            => null;
    }

    private sealed class FakeProviderRegistry : IProviderRegistry
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

    /// <summary>测试独占的用户级目录，避免污染真实 <c>~/.seeing</c>。</summary>
    internal sealed class TempWorkspaceProvider : IWorkspaceProvider
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "typesafe-api-" + Guid.NewGuid().ToString("N"));

        public TempWorkspaceProvider() => Directory.CreateDirectory(_root);

        public string WorkspaceRoot => _root;

        public string UserSeeingDirectory => Path.Combine(_root, "user", ".seeing");

        public string ProjectSeeingDirectory => Path.Combine(_root, ".seeing");

        public string GetSeeingDirectory(ConfigLevel level) => level switch
        {
            ConfigLevel.User => UserSeeingDirectory,
            ConfigLevel.Project => ProjectSeeingDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

        public string StartupDirectory => _root;

        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.StartupDirectory;

        public string? GlobalWorkspaceRoot => null;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged
        {
            add { }
            remove { }
        }

        public void SetWorkspaceRoot(string workspaceRoot) { }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
