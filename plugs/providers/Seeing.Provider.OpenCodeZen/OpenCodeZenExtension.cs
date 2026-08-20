using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;

using Seeing.Agent.Abstractions.Extensions;
using Seeing.Agent.Abstractions.Agents;
namespace Seeing.Provider.OpenCodeZen;

public sealed class OpenCodeZenExtension : IExtension, IProviderExtension
{
    public string? Id => OpenCodeZenProvider.ExtensionId;
    public string Version => "1.0.0";
    public string Name => "OpenCode Zen Provider";
    public string Description => "OpenCode Zen OpenAI-compatible LLM provider（含免费模型）";
    public string Target => "server";

    private OpenCodeZenProvider? _provider;

    public Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        _provider = context.Services.GetService<OpenCodeZenProvider>();
        return Task.CompletedTask;
    }

    public IEnumerable<ILlmProvider> GetProviders()
    {
        if (_provider is not null)
            yield return _provider;
    }
}
