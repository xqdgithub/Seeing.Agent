using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Filter;

public sealed class HeuristicMemoryFilter : IMemoryHeuristicFilter
{
    private readonly IOptions<MemoryOptions> _options;
    private readonly ConcurrentQueue<ulong> _recentHashes = new();
    private readonly object _hashLock = new();

    public HeuristicMemoryFilter(IOptions<MemoryOptions> options)
    {
        _options = options;
    }

    public FilterDecision Evaluate(MemoryCandidate candidate)
    {
        var filter = _options.Value.Filter;
        var capture = _options.Value.Capture;
        var snippet = candidate.Snippet?.Trim() ?? string.Empty;

        if (snippet.Length < filter.MinChars)
            return new FilterDecision(false, "too_short");

        foreach (var pattern in filter.AckPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            if (Regex.IsMatch(snippet, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
                return new FilterDecision(false, "ack");
        }

        if (candidate.Source == MemorySource.Tool && !string.IsNullOrEmpty(candidate.ToolId))
        {
            var toolId = candidate.ToolId;
            if (capture.ToolAllowlist.Count > 0
                && !capture.ToolAllowlist.Contains(toolId, StringComparer.OrdinalIgnoreCase))
                return new FilterDecision(false, "tool_blocked");

            if (capture.ToolBlocklist.Contains(toolId, StringComparer.OrdinalIgnoreCase))
                return new FilterDecision(false, "tool_blocked");
        }

        var hash = Fnv1a64(snippet);
        lock (_hashLock)
        {
            if (_recentHashes.Contains(hash))
                return new FilterDecision(false, "near_duplicate");

            _recentHashes.Enqueue(hash);
            while (_recentHashes.Count > Math.Max(1, filter.NearDuplicateWindow))
                _recentHashes.TryDequeue(out _);
        }

        return new FilterDecision(true, null);
    }

    private static ulong Fnv1a64(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
