using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Memory Hook 处理器，捕获对话和工具执行结果。
/// 实现 IHookHandler 接口，集成到框架 HookManager。
/// </summary>
public class MemoryHookHandler : IHookHandler
{
    private readonly MemoryWriteQueue _writeQueue;
    private readonly MemoryOrchestrator _orchestrator;
    private readonly ILogger<MemoryHookHandler>? _logger;

    /// <summary>
    /// Hook 点 - ChatAfterComplete（对话完成后）
    /// </summary>
    public string HookPoint => HookPoints.ChatAfterComplete;

    /// <summary>
    /// 优先级 - 中等
    /// </summary>
    public int Priority => 10;

    /// <summary>
    /// 创建 MemoryHookHandler 实例
    /// </summary>
    public MemoryHookHandler(
        MemoryWriteQueue writeQueue,
        MemoryOrchestrator orchestrator,
        ILogger<MemoryHookHandler>? logger = null)
    {
        _writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger;
    }

    /// <summary>
    /// 执行 Hook 逻辑
    /// </summary>
    public async Task<HookResult> ExecuteAsync(HookContext context)
    {
        try
        {
            // 提取 sessionId
            var sessionId = context.Data.TryGetValue("sessionId", out var idObj)
                ? idObj?.ToString()
                : null;

            if (string.IsNullOrEmpty(sessionId))
            {
                _logger?.LogDebug("Hook 数据缺少 sessionId，跳过记忆捕获");
                return new HookResult { Continue = true };
            }

            // ChatAfterComplete: 捕获对话响应
            if (context.HookPoint == HookPoints.ChatAfterComplete)
            {
                await ProcessChatHookAsync(sessionId, context);
            }

            // ToolExecuteAfter: 捕获工具结果
            if (context.HookPoint == HookPoints.ToolExecuteAfter)
            {
                await ProcessToolHookAsync(sessionId, context);
            }

            return new HookResult { Continue = true };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Memory Hook 处理失败");
            return new HookResult { Continue = true, Error = ex };
        }
    }

    /// <summary>
    /// 处理 Chat Hook - 捕获对话响应为 Episodic 记忆
    /// </summary>
    private async Task ProcessChatHookAsync(string sessionId, HookContext context)
    {
        // 提取响应内容
        var response = context.Output.TryGetValue("response", out var respObj)
            ? respObj?.ToString()
            : null;

        if (string.IsNullOrEmpty(response))
        {
            return;
        }

        _logger?.LogInformation("Chat Hook 捕获对话: Session={SessionId}", sessionId);

        // 创建 Episodic 记忆条目
        var entry = CreateMemoryEntry(
            sessionId,
            response,
            MemoryType.Episodic,
            "chat.after_complete");

        // 异步写入（使用 MemoryWriteQueue，不阻塞）
        await _writeQueue.EnqueueWriteAsync(entry);
    }

    /// <summary>
    /// 处理 Tool Hook - 捕获工具结果为 Procedural 记忆
    /// </summary>
    private async Task ProcessToolHookAsync(string sessionId, HookContext context)
    {
        // 提取工具 ID
        var toolId = context.Data.TryGetValue("toolId", out var toolObj)
            ? toolObj?.ToString()
            : "unknown";

        // 提取工具输出
        var output = context.Output.TryGetValue("output", out var outObj)
            ? outObj?.ToString()
            : null;

        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        _logger?.LogInformation("Tool Hook 捕获工具结果: Tool={ToolId}, Session={SessionId}",
            toolId, sessionId);

        // 创建 Procedural 记忆条目（工具执行流程）
        var entry = CreateMemoryEntry(
            sessionId,
            $"Tool: {toolId}\nOutput: {output}",
            MemoryType.Procedural,
            "tool.execute.after");

        // 工具结果加入写入队列
        await _writeQueue.EnqueueWriteAsync(entry);

        // 加入批量合并队列
        await _orchestrator.QueueForConsolidationAsync(sessionId);
    }

    /// <summary>
    /// 创建 MemoryEntry 辅助方法
    /// </summary>
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