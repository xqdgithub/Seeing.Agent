using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.OpenAI;

/// <summary>
/// OpenAI LLM 模块 — 注册 <see cref="OpenAiLlmClientFactory"/>。
/// </summary>
public sealed class OpenAiLlmModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["llm"];

    /// <inheritdoc />
    public string Id => "llm.openai";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 协议→工厂唯一映射：本模块登记 openai 与 openai-responses 两个协议键，
        // 共享同一个工厂实例（同一协议只能绑一个工厂，重复登记在构造期即抛）。
        services.AddSingleton<ILlmClientFactory, OpenAiLlmClientFactory>();
        services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
            ProviderTypes.OpenAi, sp.GetRequiredService<ILlmClientFactory>()));
        services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
            ProviderTypes.OpenAiResponses, sp.GetRequiredService<ILlmClientFactory>()));
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
