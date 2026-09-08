using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Recall;

public sealed class MemoryRecallService : IMemoryRecallService
{
    private readonly IMemoryIndex _index;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<MemoryRecallService>? _logger;

    public MemoryRecallService(
        IMemoryIndex index,
        IOptions<MemoryOptions> options,
        ILogger<MemoryRecallService>? logger = null)
    {
        _index = index;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchHit>> RecallAsync(string query, CancellationToken ct = default)
    {
        var opts = _options.Value.Retrieval;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(10, opts.InjectTimeoutMs)));

        try
        {
            var hits = await _index.SearchAsync(new SearchQuery(
                Text: query,
                Mode: SearchMode.Hybrid,
                Limit: opts.TopK), cts.Token);

            var allowed = opts.SearchTypes
                .Select(t => t.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return hits
                .Where(h =>
                {
                    var path = h.Node.Path.Replace('\\', '/');
                    var type = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    return allowed.Contains(type) && !path.StartsWith("session/", StringComparison.OrdinalIgnoreCase);
                })
                .Take(opts.TopK)
                .ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogDebug("Memory recall timed out for query length {Len}", query.Length);
            return Array.Empty<SearchHit>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Memory recall failed");
            return Array.Empty<SearchHit>();
        }
    }
}
