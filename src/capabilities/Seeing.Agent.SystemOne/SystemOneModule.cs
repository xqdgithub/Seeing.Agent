using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Runtime;

namespace Seeing.Agent.SystemOne;

/// <summary>SystemOne 能力模块 — id=<c>systemone</c>；provider 由 Activate/Deactivate 自管。</summary>
public sealed class SystemOneModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "systemone";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SystemOneConfigStore>();
        services.AddSingleton<ISystemOneClientFactory, TypeSafeSystemOneClientFactory>();
        services.AddSingleton<SystemOneProviderManager>();
        services.AddSingleton<ISystemOneProviderRegistry>(sp => sp.GetRequiredService<SystemOneProviderManager>());
        services.AddSingleton<ISystemOneService, SystemOneService>();
        // 以具体类型登记，不登记为 IReloadHandler：避免宿主启动期全量挂载，
        // 改由本模块 Activate/Deactivate 动态挂接/撤销（停用后热重载不得复活 provider）。
        services.AddSingleton<SystemOneReloadHandler>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        services.GetRequiredService<SystemOneProviderManager>().Reload();
        AttachReloadHandler(services);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DetachReloadHandler(services);
        services.GetRequiredService<ISystemOneProviderRegistry>().UnregisterByOwner("systemone");
        return Task.CompletedTask;
    }

    private static void AttachReloadHandler(IServiceProvider services)
    {
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<SystemOneReloadHandler>() is { } handler)
        {
            registry.RegisterHandler(handler);
        }
    }

    private static void DetachReloadHandler(IServiceProvider services)
    {
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<SystemOneReloadHandler>() is { } handler)
        {
            registry.UnregisterHandler(handler);
        }
    }
}
