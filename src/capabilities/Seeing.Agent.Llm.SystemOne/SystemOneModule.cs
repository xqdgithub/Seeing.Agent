using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm.SystemOne.Clients;
using Seeing.Agent.Llm.SystemOne.Configuration;

namespace Seeing.Agent.Llm.SystemOne;

/// <summary>
/// SystemOne（TypeSafe / Jev）能力模块 —— id=<c>llm.systemone</c>。
/// <para>原平行栈（<c>systemone.json</c> 配置节、<c>SYSTEMONE_*</c> 环境变量、
/// 自有 provider 注册表与热重载）已删除；现在拆成两件事：</para>
/// <list type="bullet">
/// <item><description>登记 typesafe 协议的客户端工厂（协议→工厂唯一映射）；</description></item>
/// <item><description>登记内置 <see cref="SystemOneLlmProvider"/>，Activate 时挂进 Provider 注册表，
/// 使 Decision 能力**装完即用**，无需用户手写 <c>Providers.typesafe</c>。</description></item>
/// </list>
/// <para>用户若在 <c>Providers</c> 节显式写了同 id 配置，用户配置优先级更高（注册表后注册者覆盖）。</para>
/// </summary>
public sealed class SystemOneModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "llm.systemone";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton(sp => new SystemOneConfigStore(
            sp.GetService<IWorkspaceProvider>(),
            sp.GetRequiredService<ILogger<SystemOneConfigStore>>()));
        services.TryAddSingleton<SystemOneLlmProvider>();

        // 协议→工厂唯一映射：typesafe 协议绑本工厂（重复登记在构造期即抛）。
        // 必须按具体类型解析——ILlmClientFactory 有多个实现，按接口取会拿到最后登记的那个。
        services.AddSingleton<TypeSafeSystemOneClientFactory>();
        services.AddSingleton<ILlmClientFactory>(sp => sp.GetRequiredService<TypeSafeSystemOneClientFactory>());
        services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
            ProviderTypes.TypeSafe, sp.GetRequiredService<TypeSafeSystemOneClientFactory>()));
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var provider = services.GetRequiredService<SystemOneLlmProvider>();
        await provider.WarmupAsync(cancellationToken).ConfigureAwait(false);

        var registry = services.GetRequiredService<IProviderRegistry>();

        // 用户若在 Providers 节显式配置了同 id 的配置驱动 Provider，以用户为准：
        // 内置 Provider 只负责「没有用户配置时也能用」。
        // （ProviderRegistry 是后注册者覆盖，所以这里必须主动让位，不能只靠注释。）
        if (registry.GetProvider(provider.Id) is not null)
            return;

        registry.Register(provider, ownerExtensionId: null);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var provider = services.GetRequiredService<SystemOneLlmProvider>();
        var registry = services.GetRequiredService<IProviderRegistry>();

        // 只注销「仍是自己」的那个实例：Activate 可能因用户/扩展优先而没注册本 Provider，
        // 无条件 Unregister 会把别人的 Provider 删掉。
        if (!ReferenceEquals(registry.GetProvider(provider.Id), provider))
            return Task.CompletedTask;

        registry.Unregister(provider.Id);
        return Task.CompletedTask;
    }
}
