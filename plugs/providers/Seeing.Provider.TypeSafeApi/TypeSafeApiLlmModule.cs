using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.TypeSafeApi;

/// <summary>
/// TypeSafe 端点 Provider 模块 — id=<c>provider.typesafe-api</c>；依赖 <c>llm.typesafe</c> 工厂。
/// Activate 登记到 <see cref="IProviderRegistry"/>，Deactivate 注销。
/// </summary>
public sealed class TypeSafeApiLlmModule : ISeeingModule
{
    public const string ModuleIdValue = "provider.typesafe-api";

    private static readonly IReadOnlyList<string> s_dependsOn = ["llm.typesafe"];

    /// <inheritdoc />
    public string Id => ModuleIdValue;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            new TypeSafeApiConfigStore(
                sp.GetService<IWorkspaceProvider>(),
                sp.GetRequiredService<ILogger<TypeSafeApiConfigStore>>()));
        services.TryAddSingleton<TypeSafeApiProvider>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var provider = services.GetRequiredService<TypeSafeApiProvider>();
        await provider.WarmupAsync(cancellationToken).ConfigureAwait(false);

        // DisposeByRegistry=false：DI 单例的释放归容器，注册表擅自释放会让热重载后再 Activate 撞
        // ObjectDisposedException（见 TypeSafeApiProvider.RegisterSelf）。
        provider.RegisterSelf();
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var provider = services.GetRequiredService<TypeSafeApiProvider>();
        services.GetRequiredService<IProviderRegistry>().Unregister(provider.Id);
        return Task.CompletedTask;
    }
}
