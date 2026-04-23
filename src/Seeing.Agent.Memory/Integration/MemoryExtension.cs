using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Memory 插件入口，实现 IExtension 接口。
/// 参考 PluginsExtension.cs 的实现模式。
/// </summary>
public class MemoryExtension : IExtension
{
    /// <summary>插件 ID</summary>
    public string? Id => "seeing.agent.memory";

    /// <summary>版本号</summary>
    public string Version => "1.0.0";

    /// <summary>显示名称</summary>
    public string Name => "Seeing.Agent Memory";

    /// <summary>描述</summary>
    public string Description => "长期记忆存储与检索系统，支持语义/情景/程序三种记忆类型";

    /// <summary>目标运行时</summary>
    public string Target => "server";

    private readonly List<IHookHandler> _hookHandlers = new();
    private ILogger? _logger;
    private MemoryWriteQueue? _writeQueue;

    /// <summary>
    /// 注册服务到 DI 容器
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        // 注册核心服务（Singleton）
        services.AddSingleton<IMemoryManager, MemoryManager>();
        services.AddSingleton<MemoryOrchestrator>();
        services.AddSingleton<MemoryWriteQueue>();

        // 注册工具（Transient）
        services.AddTransient<MemoryTools>();

        // 注册 Hook Handler（Transient）
        services.AddTransient<MemoryHookHandler>();
    }

    /// <summary>
    /// 初始化插件
    /// </summary>
    public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        // 获取日志工厂
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<MemoryExtension>();

        _logger?.LogInformation("初始化 {Name} v{Version} (state: {State})",
            Name, Version, meta.State);

        // 加载默认 Memory 配置并记录来源与摘要信息
        var memoryOptions = MemoryConfigLoader.LoadDefault(context.WorkspaceRoot, _logger);

        // 尝试确定配置来源路径（优先用户级 -> 项目级）
        try
        {
            var userRel = MemoryConfigLoader.UserMemoryJsonPath;
            var userPath = MemoryConfigLoader.ExpandPath(userRel, context.WorkspaceRoot);
            if (File.Exists(userPath))
            {
                _logger?.LogInformation("MemoryConfigLoader loaded from {Path}", userPath);
            }
            else
            {
                var projRel = MemoryConfigLoader.ProjectMemoryJsonPath(context.WorkspaceRoot);
                var projPath = MemoryConfigLoader.ExpandPath(projRel, context.WorkspaceRoot);
                if (File.Exists(projPath))
                {
                    _logger?.LogInformation("MemoryConfigLoader loaded from {Path}", projPath);
                }
                else
                {
                    _logger?.LogInformation("MemoryConfigLoader loaded default memory config (no memory.json found)");
                }
            }
        }
        catch
        {
            _logger?.LogWarning("MemoryConfigLoader could not determine config load path");
        }

        // 配置摘要日志：MemoryStore 目录等关键字段
        _logger?.LogInformation("MemoryStore.Directory = {Dir}", memoryOptions.MemoryStore.MemoryDirectory);

        // 获取核心服务
        var hookManager = context.HookManager;
        _writeQueue = context.Services.GetRequiredService<MemoryWriteQueue>();
        var orchestrator = context.Services.GetRequiredService<MemoryOrchestrator>();

        // 创建 Hook Handler
        var hookHandlerLogger = loggerFactory.CreateLogger<MemoryHookHandler>();
        var hookHandler = new MemoryHookHandler(_writeQueue, orchestrator, hookHandlerLogger);

        _hookHandlers.Add(hookHandler);

        // 注册 Hook Handler 到 HookManager
        if (hookManager != null)
        {
            foreach (var handler in _hookHandlers)
            {
                hookManager.RegisterHandler(handler);
            }

            _logger?.LogInformation("已注册 {Count} 个 Hook Handler", _hookHandlers.Count);
        }

        // 启动后台写入队列
        if (_writeQueue != null)
        {
            _ = _writeQueue.StartProcessingAsync();
            _logger?.LogInformation("MemoryWriteQueue 已启动");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 获取提供的 Hook Handler
    /// </summary>
    public IEnumerable<IHookHandler> GetHookHandlers() => _hookHandlers;

    /// <summary>
    /// 获取提供的工具（通过注解发现，无需在此返回）
    /// </summary>
    public IEnumerable<ITool> GetTools() => Enumerable.Empty<ITool>();

    /// <summary>
    /// 清理资源
    /// </summary>
    public async Task DisposeAsync()
    {
        _logger?.LogInformation("清理 {Name}", Name);

        // 停止写入队列
        if (_writeQueue != null)
        {
            _writeQueue.StopProcessing();
            _writeQueue.Dispose();
        }

        _hookHandlers.Clear();

        await Task.CompletedTask;
    }
}
