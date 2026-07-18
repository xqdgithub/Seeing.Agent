using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Chat Hook 处理器 - 捕获对话响应为记忆文件
/// </summary>
public sealed class ChatMemoryHandler : IHookHandler
{
    private readonly IMemoryService _memoryService;
    private readonly ILogger<ChatMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ChatAfterComplete;
    public int Priority => 10;

    public ChatMemoryHandler(
        IMemoryService memoryService,
        ILogger<ChatMemoryHandler>? logger = null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
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

            var path = $"session/{payload.SessionId}/{Guid.NewGuid():N}.md";
            await _memoryService.SaveAsync(path, content);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ChatMemoryHandler 处理失败");
            return HookResult.FromError(ex);
        }
    }
}

/// <summary>
/// Tool Hook 处理器 - 捕获工具执行结果为记忆文件
/// </summary>
public sealed class ToolMemoryHandler : IHookHandler
{
    private readonly IMemoryService _memoryService;
    private readonly ILogger<ToolMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ToolExecuteAfter;
    public int Priority => 10;

    public ToolMemoryHandler(
        IMemoryService memoryService,
        ILogger<ToolMemoryHandler>? logger = null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
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

            var content = $"Tool: {toolId}\nOutput: {output}";
            var path = $"session/{payload.SessionId}/tool-{Guid.NewGuid():N}.md";
            await _memoryService.SaveAsync(path, content);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ToolMemoryHandler 处理失败");
            return HookResult.FromError(ex);
        }
    }
}
