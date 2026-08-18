using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Tools;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Memory.Integration.Hosting;

/// <summary>
/// 宿主启动时幂等注册 Memory Hook / Tool（不依赖 WebUI / Extension Initialize）。
/// </summary>
internal sealed class MemoryBootstrapHostedService : IHostedService
{
    private readonly IHookManager _hookManager;
    private readonly ToolManager _toolInvoker;
    private readonly MemorySearchTool _searchTool;
    private readonly MemoryWriteTool _writeTool;
    private readonly MemoryReadTool _readTool;
    private readonly ChatMemoryHandler _chat;
    private readonly ToolMemoryHandler _tool;
    private readonly AgentTurnMemoryHandler _agentTurn;
    private readonly MemoryRecallHandler _recall;
    private readonly ILogger<MemoryBootstrapHostedService> _logger;

    public MemoryBootstrapHostedService(
        IHookManager hookManager,
        ToolManager toolInvoker,
        MemorySearchTool searchTool,
        MemoryWriteTool writeTool,
        MemoryReadTool readTool,
        ChatMemoryHandler chat,
        ToolMemoryHandler tool,
        AgentTurnMemoryHandler agentTurn,
        MemoryRecallHandler recall,
        ILogger<MemoryBootstrapHostedService> logger)
    {
        _hookManager = hookManager;
        _toolInvoker = toolInvoker;
        _searchTool = searchTool;
        _writeTool = writeTool;
        _readTool = readTool;
        _chat = chat;
        _tool = tool;
        _agentTurn = agentTurn;
        _recall = recall;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterToolIfMissing(_searchTool);
        RegisterToolIfMissing(_writeTool);
        RegisterToolIfMissing(_readTool);

        if (!MemoryHookRegistrationGate.TryClaim())
        {
            _logger.LogDebug("Memory hooks already registered; skipping Bootstrap");
            return Task.CompletedTask;
        }

        _hookManager.Register(_chat);
        _hookManager.Register(_tool);
        _hookManager.Register(_agentTurn);
        _hookManager.Register(_recall);
        _logger.LogInformation("Memory hooks registered (chat/tool/agent-turn/recall)");
        return Task.CompletedTask;
    }

    private void RegisterToolIfMissing(ITool tool)
    {
        if (_toolInvoker.HasTool(tool.Id))
            return;

        _toolInvoker.RegisterTool(tool);
        _logger.LogInformation("Memory tool registered: {ToolId}", tool.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
