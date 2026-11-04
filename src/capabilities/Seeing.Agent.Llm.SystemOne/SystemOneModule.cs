using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Llm.SystemOne.Clients;

namespace Seeing.Agent.Llm.SystemOne;

/// <summary>
/// SystemOne（TypeSafe / Jev）能力模块 — id=<c>llm.systemone</c>。
/// <para>原平行栈（<c>systemone.json</c> 配置节、<c>SYSTEMONE_*</c> 环境变量、自有 provider 注册表与热重载）
/// 已删除：provider 改由框架 <c>Providers.typesafe</c> 配置驱动，本模块只登记协议客户端工厂（spec §7.9）。</para>
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
        services.AddSingleton<ISystemOneClientFactory, TypeSafeSystemOneClientFactory>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
