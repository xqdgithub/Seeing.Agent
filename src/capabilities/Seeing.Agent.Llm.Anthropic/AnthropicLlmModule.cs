using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.Anthropic;

/// <summary>
/// Anthropic LLM 模块 — 注册 <see cref="AnthropicLlmClientFactory"/>。
/// </summary>
public sealed class AnthropicLlmModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["llm"];

    /// <inheritdoc />
    public string Id => "llm.anthropic";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 协议→工厂唯一映射：anthropic 协议绑本工厂（重复登记在构造期即抛）。
        // 必须按具体类型解析——ILlmClientFactory 有多个实现，按接口取会拿到最后登记的那个。
        services.AddSingleton<AnthropicLlmClientFactory>();
        services.AddSingleton<ILlmClientFactory>(sp => sp.GetRequiredService<AnthropicLlmClientFactory>());
        services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
            ProviderTypes.Anthropic, sp.GetRequiredService<AnthropicLlmClientFactory>()));
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
