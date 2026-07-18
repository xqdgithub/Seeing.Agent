using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Chat Hook：仅入队 MemoryCandidate，不写盘。
/// </summary>
public sealed class ChatMemoryHandler : IHookHandler
{
    private readonly IMemoryWorkQueue _queue;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ISessionActivityTracker _activity;
    private readonly ILogger<ChatMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ChatAfterComplete;
    public int Priority => 10;

    public ChatMemoryHandler(
        IMemoryWorkQueue queue,
        IOptions<MemoryOptions> options,
        ISessionActivityTracker activity,
        ILogger<ChatMemoryHandler>? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.Value;
            if (!opts.Enabled || !opts.Capture.AutoCapture || !opts.Capture.CaptureChat)
                return Task.FromResult(HookResult.Success);

            var content = HookDataContract.ChatAfterComplete.Content.GetFrom(payload.Result);
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(HookResult.Success);

            var max = Math.Max(1, opts.Capture.MaxSnippetChars);
            var snippet = content.Length <= max ? content : content[..max];

            var candidate = new MemoryCandidate(
                Guid.NewGuid().ToString("N"),
                string.IsNullOrEmpty(payload.SessionId) ? "unknown" : payload.SessionId,
                AgentId: null,
                MemorySource.Chat,
                ToolId: null,
                snippet,
                DateTimeOffset.UtcNow);

            if (!_queue.TryEnqueue(candidate))
                _logger?.LogWarning("Memory queue full, dropping chat candidate Session={SessionId}", payload.SessionId);
            else
                _activity.Touch(candidate.SessionId);

            return Task.FromResult(HookResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ChatMemoryHandler failed");
            return Task.FromResult(HookResult.Success);
        }
    }
}

/// <summary>
/// Tool Hook：仅入队 MemoryCandidate，不写盘。
/// </summary>
public sealed class ToolMemoryHandler : IHookHandler
{
    private readonly IMemoryWorkQueue _queue;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ISessionActivityTracker _activity;
    private readonly ILogger<ToolMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ToolExecuteAfter;
    public int Priority => 10;

    public ToolMemoryHandler(
        IMemoryWorkQueue queue,
        IOptions<MemoryOptions> options,
        ISessionActivityTracker activity,
        ILogger<ToolMemoryHandler>? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.Value;
            if (!opts.Enabled || !opts.Capture.AutoCapture || !opts.Capture.CaptureTools)
                return Task.FromResult(HookResult.Success);

            var toolId = HookDataContract.ToolExecuteAfter.ToolId.GetFrom(payload.Input) ?? "";
            var output = HookDataContract.ToolExecuteAfter.Output.GetFrom(payload.Result);
            if (string.IsNullOrWhiteSpace(output))
                return Task.FromResult(HookResult.Success);

            if (opts.Capture.ToolAllowlist.Count > 0
                && !opts.Capture.ToolAllowlist.Contains(toolId, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(HookResult.Success);

            if (opts.Capture.ToolBlocklist.Contains(toolId, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(HookResult.Success);

            var raw = $"Tool: {toolId}\nOutput: {output}";
            var max = Math.Max(1, opts.Capture.MaxSnippetChars);
            var snippet = raw.Length <= max ? raw : raw[..max];

            var candidate = new MemoryCandidate(
                Guid.NewGuid().ToString("N"),
                string.IsNullOrEmpty(payload.SessionId) ? "unknown" : payload.SessionId,
                AgentId: null,
                MemorySource.Tool,
                toolId,
                snippet,
                DateTimeOffset.UtcNow);

            if (!_queue.TryEnqueue(candidate))
                _logger?.LogWarning("Memory queue full, dropping tool candidate Tool={ToolId}", toolId);
            else
                _activity.Touch(candidate.SessionId);

            return Task.FromResult(HookResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ToolMemoryHandler failed");
            return Task.FromResult(HookResult.Success);
        }
    }
}
