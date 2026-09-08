using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Core.Embedding;

public sealed class ConfigurableEmbeddingStatus : IEmbeddingStatus
{
    private readonly IOptionsMonitor<MemoryOptions> _options;

    public ConfigurableEmbeddingStatus(IOptionsMonitor<MemoryOptions> options)
    {
        _options = options;
    }

    public bool IsAvailable => _options.CurrentValue.IsEmbeddingConfigured;

    public string? Reason =>
        IsAvailable ? null : "Embedding provider/model not configured";
}
