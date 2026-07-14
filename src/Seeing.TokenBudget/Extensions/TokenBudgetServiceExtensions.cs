using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Session.Compression.Strategies;
using Seeing.TokenBudget.Api;
using Seeing.TokenBudget.Configuration;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget.Extensions;

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
        
        // Budget management
        services.AddSingleton<ITokenBudgetConfigResolver, TokenBudgetConfigResolver>();
        services.AddScoped<ITokenBudgetManager, TokenBudgetManager>();
        
        // Compression
        services.AddScoped<ICompressionTrigger, DefaultCompressionTrigger>();
        services.AddScoped<SlidingWindowTokenStrategy>();
        
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

        // Register compression strategies (singletons for stateless strategies)
        services.AddSingleton<SlidingWindowTokenStrategy>();
        services.AddSingleton<SummarizingStrategy>();
        services.AddSingleton<HybridStrategy>();

        // Register compression infrastructure
        services.AddSingleton<ICompressionStrategyFactory, CompressionStrategyFactory>();
        services.AddScoped<ICompressionService, CompressionService>();

        return services;
    }
}
