using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory.Background;

/// <summary>
/// 记忆索引后台服务 - 文件变更扫漏兜底（主索引由 MemoryPipeline 完成）。
/// </summary>
public class MemoryIndexingService : BackgroundService, IModuleHostedService
{
    public const string ModuleId = "memory";
    internal const string DeactivateSentinelPath = "__memory_index_deactivate__";

    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly MemoryModuleActivity _moduleActivity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<MemoryIndexingService>? _logger;
    private readonly ModuleHostedRunGate _run = new();
    private readonly Channel<FileChangeEventArgs> _changes =
        Channel.CreateUnbounded<FileChangeEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly IDisposable? _subscription;

    public MemoryIndexingService(
        IFileStore fileStore,
        IMemoryIndex index,
        MemoryModuleActivity moduleActivity,
        ILogger<MemoryIndexingService>? logger = null,
        IModuleCatalog? catalog = null)
    {
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _moduleActivity = moduleActivity ?? throw new ArgumentNullException(nameof(moduleActivity));
        _logger = logger;
        _catalog = catalog;

        // 变更入队串行处理，避免并发打共享 SqliteConnection
        _subscription = _fileStore.Changes.Subscribe(change =>
        {
            if (!_moduleActivity.IsActive)
                return;
            if (!_changes.Writer.TryWrite(change))
                _logger?.LogWarning("索引变更队列已关闭，丢弃: {Path}", change.Path);
        });

        _moduleActivity.RegisterWake(() =>
            _changes.Writer.TryWrite(new FileChangeEventArgs(DeactivateSentinelPath, FileChangeType.Deleted)));
    }

    /// <inheritdoc cref="IModuleHostedService.ModuleId" />
    string IModuleHostedService.ModuleId => ModuleId;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning)
            return Task.CompletedTask;

        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger?.LogDebug("Memory module disabled; IndexingService HostedService no-op");
            return Task.CompletedTask;
        }

        if (!_run.TryBegin())
            return Task.CompletedTask;

        try
        {
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            _run.End();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.End();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger?.LogInformation("记忆索引后台服务已启动");

            try
            {
                var files = await _fileStore.ListAsync(ct: stoppingToken);
                if (files.Count > 0 && _moduleActivity.IsActive)
                {
                    await _index.IndexBatchAsync(files, stoppingToken);
                    _logger?.LogInformation("初始索引完成: {Count} 个文件", files.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "初始索引失败");
            }

            try
            {
                await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken))
                {
                    if (change.Path == DeactivateSentinelPath || !_moduleActivity.IsActive)
                        break;

                    try
                    {
                        await HandleChangeAsync(change);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "处理文件变更失败: {Path}", change.Path);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shut down
            }
        }
        finally
        {
            _run.End();
        }
    }

    private async Task HandleChangeAsync(FileChangeEventArgs change)
    {
        _logger?.LogDebug("处理文件变更: {Path} ({ChangeType})", change.Path, change.Type);

        switch (change.Type)
        {
            case FileChangeType.Created:
            case FileChangeType.Modified:
                var node = await _fileStore.ReadAsync(change.Path);
                if (node != null)
                    await _index.IndexAsync(node);
                break;

            case FileChangeType.Deleted:
                await _index.RemoveAsync(change.Path);
                break;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _changes.Writer.TryComplete();
        base.Dispose();
    }
}
