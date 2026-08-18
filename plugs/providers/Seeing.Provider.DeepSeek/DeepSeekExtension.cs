using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekExtension : IExtension
{
    public string? Id => DeepSeekProvider.ExtensionId;
    public string Version => "1.0.0";
    public string Name => "DeepSeek Provider";
    public string Description => "DeepSeek OpenAI-compatible LLM provider";
    public string Target => "server";

    private DeepSeekProvider? _provider;

    public Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        _provider = context.Services.GetService<DeepSeekProvider>();
        return Task.CompletedTask;
    }

    public IEnumerable<ILlmProvider> GetProviders()
    {
        if (_provider is not null)
            yield return _provider;
    }
}
