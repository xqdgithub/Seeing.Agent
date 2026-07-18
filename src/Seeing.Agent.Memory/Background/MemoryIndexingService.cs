using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Background;

/// <summary>
/// 记忆索引后台服务 - 文件变更扫漏兜底（主索引由 MemoryPipeline 完成）。
/// </summary>
public class MemoryIndexingService : BackgroundService
{
    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly ILogger<MemoryIndexingService>? _logger;
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
        ILogger<MemoryIndexingService>? logger = null)
    {
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger;

        // 变更入队串行处理，避免并发打共享 SqliteConnection
        _subscription = _fileStore.Changes.Subscribe(change =>
        {
            if (!_changes.Writer.TryWrite(change))
                _logger?.LogWarning("索引变更队列已关闭，丢弃: {Path}", change.Path);
        });
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("记忆索引后台服务已启动");

        try
        {
            var files = await _fileStore.ListAsync(ct: stoppingToken);
            if (files.Count > 0)
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
