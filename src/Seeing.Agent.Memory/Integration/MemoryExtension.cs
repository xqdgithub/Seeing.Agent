using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration.Tools;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Memory 插件入口。服务由 <see cref="MemoryServiceExtensions.AddMemoryServices"/> 注册；
/// 本扩展导出 Tools/Hooks，供 Plugins 加载路径使用。
/// </summary>
public class MemoryExtension : IExtension
{
    public string? Id => "seeing.agent.memory";
    public string Version => "2.1.0";
    public string Name => "Seeing.Agent Memory";
    public string Description => "基于文件的记忆系统，支持混合检索和知识图谱";
    public string Target => "server";

    private MemorySearchTool? _searchTool;
    private MemoryWriteTool? _writeTool;
    private MemoryReadTool? _readTool;
    private ChatMemoryHandler? _chat;
    private ToolMemoryHandler? _tool;
    private MemoryRecallHandler? _recall;
    private ILogger? _logger;

    public void ConfigureServices(IServiceCollection services) => services.AddMemoryServices();

    public Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<MemoryExtension>();

        _searchTool = context.Services.GetService<MemorySearchTool>();
        _writeTool = context.Services.GetService<MemoryWriteTool>();
        _readTool = context.Services.GetService<MemoryReadTool>();
        _chat = context.Services.GetService<ChatMemoryHandler>();
        _tool = context.Services.GetService<ToolMemoryHandler>();
        _recall = context.Services.GetService<MemoryRecallHandler>();

        _logger.LogInformation("初始化 {Name} v{Version} (state: {State})", Name, Version, meta.State);
        return Task.CompletedTask;
    }

    public IEnumerable<IHookHandler> GetHookHandlers()
    {
        var handlers = new List<IHookHandler>(3);
        if (_chat != null) handlers.Add(_chat);
        if (_tool != null) handlers.Add(_tool);
        if (_recall != null) handlers.Add(_recall);

        // 与 Bootstrap 共用闸门：先到先注册，避免双重触发
        if (handlers.Count == 0 || !MemoryHookRegistrationGate.TryClaim())
            return Array.Empty<IHookHandler>();

        return handlers;
    }

    public IEnumerable<ITool> GetTools()
    {
        if (_searchTool != null)
            yield return _searchTool;
        if (_writeTool != null)
            yield return _writeTool;
        if (_readTool != null)
            yield return _readTool;
    }

    public Task DisposeAsync()
    {
        _logger?.LogInformation("清理 {Name}", Name);
        return Task.CompletedTask;
    }
}
