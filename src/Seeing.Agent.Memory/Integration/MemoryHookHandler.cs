using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Chat Hook 处理器 - 捕获对话响应为 Episodic 记忆
/// </summary>
public sealed class ChatMemoryHandler : IHookHandler
{
    private readonly MemoryWriteQueue _writeQueue;
    private readonly ILogger<ChatMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ChatAfterComplete;
    public int Priority => 10;

    public ChatMemoryHandler(
        MemoryWriteQueue writeQueue,
        ILogger<ChatMemoryHandler>? logger = null)
    {
        _writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        _logger = logger;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var content = HookDataContract.ChatAfterComplete.Content.GetFrom(payload.Result);
            if (string.IsNullOrEmpty(content))
            {
                return HookResult.Success;
            }

            _logger?.LogInformation("Chat Hook 捕获对话: Session={SessionId}", payload.SessionId);

            var entry = CreateMemoryEntry(
                payload.SessionId,
                content,
                MemoryType.Episodic,
                "chat.after_complete");

            await _writeQueue.EnqueueWriteAsync(entry);
            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ChatMemoryHandler 处理失败");
            return HookResult.FromError(ex);
        }
    }

    private static MemoryEntry CreateMemoryEntry(
        string sessionId,
        string content,
        MemoryType type,
        string source)
    {
        var memoryId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var metadata = new MemoryMetadata(
            SessionId: sessionId,
            AgentId: "memory.hook",
            Source: source,
            Tags: new[] { "hook", type.ToString().ToLower() },
            Confidence: 1.0,
            Importance: type == MemoryType.Episodic ? 0.8 : 0.5
        );

        return new MemoryEntry(
            Id: memoryId,
            Type: type,
            Content: content,
            Metadata: metadata,
            CreatedAt: now,
            ValidAt: now,
            InvalidAt: null
        );
    }
}

/// <summary>
/// Tool Hook 处理器 - 捕获工具执行结果为 Procedural 记忆
/// </summary>
public sealed class ToolMemoryHandler : IHookHandler
{
    private readonly MemoryWriteQueue _writeQueue;
    private readonly MemoryOrchestrator _orchestrator;
    private readonly ILogger<ToolMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ToolExecuteAfter;
    public int Priority => 10;

    public ToolMemoryHandler(
        MemoryWriteQueue writeQueue,
        MemoryOrchestrator orchestrator,
        ILogger<ToolMemoryHandler>? logger = null)
    {
        _writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var toolId = HookDataContract.ToolExecuteAfter.ToolId.GetFrom(payload.Input);
            var output = HookDataContract.ToolExecuteAfter.Output.GetFrom(payload.Result);

            if (string.IsNullOrEmpty(output))
            {
                return HookResult.Success;
            }

            _logger?.LogInformation("Tool Hook 捕获工具结果: Tool={ToolId}, Session={SessionId}",
                toolId, payload.SessionId);

            var entry = CreateMemoryEntry(
                payload.SessionId,
                $"Tool: {toolId}\nOutput: {output}",
                MemoryType.Procedural,
                "tool.execute.after");

            await _writeQueue.EnqueueWriteAsync(entry);
            await _orchestrator.QueueForConsolidationAsync(payload.SessionId);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ToolMemoryHandler 处理失败");
            return HookResult.FromError(ex);
        }
    }

    private static MemoryEntry CreateMemoryEntry(
        string sessionId,
        string content,
        MemoryType type,
        string source)
    {
        var memoryId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var metadata = new MemoryMetadata(
            SessionId: sessionId,
            AgentId: "memory.hook",
            Source: source,
            Tags: new[] { "hook", type.ToString().ToLower() },
            Confidence: 1.0,
            Importance: type == MemoryType.Episodic ? 0.8 : 0.5
        );

        return new MemoryEntry(
            Id: memoryId,
            Type: type,
            Content: content,
            Metadata: metadata,
            CreatedAt: now,
            ValidAt: now,
            InvalidAt: null
        );
    }
}