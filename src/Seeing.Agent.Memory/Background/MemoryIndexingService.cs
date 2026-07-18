using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Background;

/// <summary>
/// 记忆索引后台服务 - 监听文件变更并自动更新索引
/// </summary>
public class MemoryIndexingService : BackgroundService
{
    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly ILogger<MemoryIndexingService>? _logger;
    private readonly IDisposable? _subscription;

    public MemoryIndexingService(
        IFileStore fileStore,
        IMemoryIndex index,
        ILogger<MemoryIndexingService>? logger = null)
    {
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger;

        // 订阅文件变更事件
        _subscription = _fileStore.Changes.Subscribe(async change =>
        {
            try
            {
                await HandleChangeAsync(change);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "处理文件变更失败: {Path}", change.Path);
            }
        });
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("记忆索引后台服务已启动");

        // 初始索引
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

        // 保持服务运行
        await Task.Delay(Timeout.Infinite, stoppingToken);
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
                {
                    await _index.IndexAsync(node);
                }
                break;

            case FileChangeType.Deleted:
                await _index.RemoveAsync(change.Path);
                break;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
