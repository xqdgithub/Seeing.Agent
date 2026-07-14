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
    /// Note: Hooks must be singletons because they are registered once at startup
    /// and need to persist for the lifetime of the application.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTokenBudgetHooks(
        this IServiceCollection services)
    {
        // Register hooks as singletons
        // This is required because hooks are registered once at startup
        services.AddSingleton<BudgetCheckHook>();
        services.AddSingleton<BudgetUpdateHook>();

        return services;
    }

    /// <summary>
    /// Registers token budget hooks with the hook manager.
    /// Call this after building the service provider.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>The service provider for chaining.</returns>
    public static IServiceProvider UseTokenBudgetHooks(
        this IServiceProvider services)
    {
        var hookManager = services.GetRequiredService<IHookManager>();

        var checkHook = services.GetRequiredService<BudgetCheckHook>();
        var updateHook = services.GetRequiredService<BudgetUpdateHook>();

        hookManager.Register(checkHook);
        hookManager.Register(updateHook);

        return services;
    }
}
