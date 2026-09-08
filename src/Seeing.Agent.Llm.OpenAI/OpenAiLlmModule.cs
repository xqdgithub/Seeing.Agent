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
        services.AddSingleton<ILlmClientFactory, OpenAiLlmClientFactory>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
