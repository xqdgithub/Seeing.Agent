using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.TokenBudget;

namespace Seeing.Agent.Extensions;

/// <summary>
/// Extension methods for registering token budget hooks.
/// </summary>
public static class TokenBudgetHookExtensions
{
    /// <summary>
    /// Adds token budget hooks to the service collection.
    /// These hooks integrate with the Seeing.Agent hook system.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTokenBudgetHooks(
        this IServiceCollection services)
    {
        // Register hooks as scoped since they depend on scoped services
        services.AddScoped<BudgetCheckHook>();
        services.AddScoped<BudgetUpdateHook>();

        return services;
    }

    /// <summary>
    /// Registers token budget hooks with the hook manager.
    /// Call this after building the service provider, within a scope.
    /// Note: Since hooks are scoped, they will be resolved per-request.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>The service provider for chaining.</returns>
    public static IServiceProvider UseTokenBudgetHooks(
        this IServiceProvider services)
    {
        // Create a scope to resolve scoped hooks
        using var scope = services.CreateScope();
        var hookManager = services.GetRequiredService<IHookManager>();

        var checkHook = scope.ServiceProvider.GetRequiredService<BudgetCheckHook>();
        var updateHook = scope.ServiceProvider.GetRequiredService<BudgetUpdateHook>();

        hookManager.Register(checkHook);
        hookManager.Register(updateHook);

        return services;
    }
}
