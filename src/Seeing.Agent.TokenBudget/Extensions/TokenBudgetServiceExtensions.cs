using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Llm;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Agent.TokenBudget.Api;
using Seeing.Agent.TokenBudget.Configuration;
using Seeing.TokenEstimation;

namespace Seeing.Agent.TokenBudget.Extensions;

/// <summary>
/// Extension methods for registering token budget management services.
/// </summary>
public static class TokenBudgetServiceExtensions
{
    /// <summary>
    /// Adds token budget management services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration containing token budget settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTokenBudgetManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configuration
        services.Configure<GlobalTokenBudgetOptions>(
            configuration.GetSection(GlobalTokenBudgetOptions.SectionName));
        
        // Token estimation
        services.AddSingleton<ITokenCounter, CharBasedTokenCounter>();
        
        // Budget management - 需要 ILlmService 和 SeeingAgentOptions
        services.AddSingleton<ITokenBudgetConfigResolver, TokenBudgetConfigResolver>();
        services.AddSingleton<ITokenBudgetManager>(sp =>
        {
            var llmService = sp.GetRequiredService<ILlmService>();
            var options = sp.GetRequiredService<IOptions<SeeingAgentOptions>>();
            var tokenCounter = sp.GetService<ITokenCounter>();
            return new TokenBudgetManager(llmService, options, tokenCounter);
        });
        
        // Compression - register SlidingWindowTokenStrategy as both itself and ICompressionStrategy
        var slidingWindowDescriptor = ServiceDescriptor.Singleton<SlidingWindowTokenStrategy, SlidingWindowTokenStrategy>();
        services.Add(slidingWindowDescriptor);
        services.AddSingleton<ICompressionStrategy>(sp => sp.GetRequiredService<SlidingWindowTokenStrategy>());
        
        services.AddScoped<ICompressionTrigger, DefaultCompressionTrigger>();
        
        // API
        services.AddScoped<ITokenBudgetApi, TokenBudgetApi>();
        
        return services;
    }

    /// <summary>
    /// Adds token budget integration services including compression strategies and services.
    /// This includes all services from AddTokenBudgetManagement plus compression infrastructure.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration containing token budget settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTokenBudgetIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register base token budget services
        services.AddTokenBudgetManagement(configuration);

        // Register compression infrastructure
        services.AddSingleton<ICompressionStrategyFactory, CompressionStrategyFactory>();
        services.AddScoped<ICompressionService, CompressionService>();

        return services;
    }

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
        services.AddSingleton<BudgetCheckHook>();
        services.AddSingleton<BudgetUpdateHook>();
        services.AddSingleton<BudgetModelLimitHandler>();

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
        var hookManager = services.GetRequiredService<HookManager>();

        var checkHook = services.GetRequiredService<BudgetCheckHook>();
        var updateHook = services.GetRequiredService<BudgetUpdateHook>();
        var modelLimitHandler = services.GetRequiredService<BudgetModelLimitHandler>();

        hookManager.Register(checkHook);
        hookManager.Register(updateHook);
        hookManager.RegisterMulti(modelLimitHandler);

        return services;
    }
}
