using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Files.Watching;

namespace Seeing.Agent.Files.Hosting;

/// <summary>随 Generic Host / 模块 Activate 启停文件 watcher 池。</summary>
public sealed class FileWatcherHostedService : IHostedService, IModuleHostedService
{
    /// <summary>模块 id。</summary>
    public const string ModuleIdValue = "files.workspace";

    private readonly FileWatcherPool _pool;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<FileWatcherHostedService> _logger;
    private readonly ModuleHostedRunGate _run = new();

    /// <summary>注入。</summary>
    public FileWatcherHostedService(FileWatcherPool pool, ILogger<FileWatcherHostedService> logger, IModuleCatalog? catalog = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_run.IsRunning) return Task.CompletedTask;
        if (_catalog is not null && !_catalog.IsEnabled(ModuleIdValue))
        {
            _logger.LogDebug("files.workspace disabled; watcher hosted service no-op");
            return Task.CompletedTask;
        }
        _run.TryBegin();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 不要 Dispose 单例池：IModuleHostedService 契约要求 Deactivate 后能再次 Activate。
        // Dispose 会把池标为已释放，重新启用后 Subscribe 拿到的是已 Dispose 的 watcher，
        // 文件变更静默停更（历史症状：停用再启用文件面板不再刷新）。
        _pool.StopAll();
        _run.End();
        return Task.CompletedTask;
    }
}
