using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Tools;

namespace Seeing.Agent.Memory.Integration.Hosting;

/// <summary>
/// 宿主启动时幂等注册 Memory Hook / Tool（不依赖 WebUI / Extension Initialize）。
/// </summary>
internal sealed class MemoryBootstrapHostedService : IHostedService
{
    public const string ModuleId = "memory";

    private readonly IHookManager _hookManager;
    private readonly IToolManager _toolManager;
    private readonly MemorySearchTool _searchTool;
    private readonly MemoryWriteTool _writeTool;
    private readonly MemoryReadTool _readTool;
    private readonly ChatMemoryHandler _chat;
    private readonly ToolMemoryHandler _tool;
    private readonly AgentTurnMemoryHandler _agentTurn;
    private readonly MemoryRecallHandler _recall;
    private readonly MemoryModuleActivity _activity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<MemoryBootstrapHostedService> _logger;

    public MemoryBootstrapHostedService(
        IHookManager hookManager,
        IToolManager toolManager,
        MemorySearchTool searchTool,
        MemoryWriteTool writeTool,
        MemoryReadTool readTool,
        ChatMemoryHandler chat,
        ToolMemoryHandler tool,
        AgentTurnMemoryHandler agentTurn,
        MemoryRecallHandler recall,
        MemoryModuleActivity activity,
        ILogger<MemoryBootstrapHostedService> logger,
        IModuleCatalog? catalog = null)
    {
        _hookManager = hookManager;
        _toolManager = toolManager;
        _searchTool = searchTool;
        _writeTool = writeTool;
        _readTool = readTool;
        _chat = chat;
        _tool = tool;
        _agentTurn = agentTurn;
        _recall = recall;
        _activity = activity;
        _logger = logger;
        _catalog = catalog;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger.LogDebug("Memory module disabled; Bootstrap HostedService no-op");
            return Task.CompletedTask;
        }

        if (!_activity.IsActive && _catalog is not null)
            return Task.CompletedTask;

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
        if (_toolManager.GetTool(tool.Id) is not null)
            return;

        _toolManager.RegisterTool(tool);
        _logger.LogInformation("Memory tool registered: {ToolId}", tool.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
