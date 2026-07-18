using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Memory 插件入口，实现 IExtension 接口。
/// </summary>
public class MemoryExtension : IExtension
{
    /// <summary>插件 ID</summary>
    public string? Id => "seeing.agent.memory";

    /// <summary>版本号</summary>
    public string Version => "2.0.0";

    /// <summary>显示名称</summary>
    public string Name => "Seeing.Agent Memory";

    /// <summary>描述</summary>
    public string Description => "基于文件的记忆系统，支持混合检索和知识图谱";

    /// <summary>目标运行时</summary>
    public string Target => "server";

    private readonly List<IHookHandler> _hookHandlers = new();
    private ILogger? _logger;

    /// <summary>
    /// 注册服务到 DI 容器
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        // 服务已通过 AddMemoryServices() 注册
    }

    /// <summary>
    /// 初始化插件
    /// </summary>
    public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<MemoryExtension>();

        _logger?.LogInformation("初始化 {Name} v{Version} (state: {State})",
            Name, Version, meta.State);

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
        _hookHandlers.Clear();
        await Task.CompletedTask;
    }
}
