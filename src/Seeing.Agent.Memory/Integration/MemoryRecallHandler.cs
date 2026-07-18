using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// 在 chat.before_start 将召回记忆追加到 system prompt（受 Retrieval.Mode 控制）。
/// </summary>
public sealed class MemoryRecallHandler : IHookHandler
{
    private readonly IMemoryRecallService _recall;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<MemoryRecallHandler>? _logger;

    public HookSpec Spec => HookRegistry.ChatBeforeStart;
    public int Priority => 20;

    public MemoryRecallHandler(
        IMemoryRecallService recall,
        IOptions<MemoryOptions> options,
        ILogger<MemoryRecallHandler>? logger = null)
    {
        _recall = recall;
        _options = options;
        _logger = logger;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.Value;
            if (!opts.Enabled)
                return HookResult.Success;

            if (opts.Retrieval.Mode == MemoryRetrievalMode.ToolsOnly)
                return HookResult.Success;

            var userText = payload.GetInput<string>("userMessage")
                ?? payload.GetInput<string>("message")
                ?? payload.GetInput<string>("content");
            if (string.IsNullOrWhiteSpace(userText))
                return HookResult.Success;

            var hits = await _recall.RecallAsync(userText, payload.CancellationToken);
            if (hits.Count == 0)
                return HookResult.Success;

            var sb = new StringBuilder();
            sb.AppendLine("## Relevant memories");
            var budget = opts.Retrieval.MaxInjectTokens * 4; // rough chars
            foreach (var hit in hits)
            {
                var line = $"- ({hit.Score:0.00}) {hit.Node.Metadata.Title ?? hit.Node.Path}: {Truncate(hit.Node.Content, 240)}";
                if (sb.Length + line.Length > budget)
                    break;
                sb.AppendLine(line);
            }

            var existing = payload.Mutable.TryGetValue("systemPrompt", out var spObj)
                ? spObj as string
                : payload.GetInput<string>("systemPrompt") ?? "";
            payload.SetMutable("systemPrompt", string.IsNullOrEmpty(existing)
                ? sb.ToString()
                : existing + "\n\n" + sb);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Memory recall inject failed");
            return HookResult.Success;
        }
    }

    private static string Truncate(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
