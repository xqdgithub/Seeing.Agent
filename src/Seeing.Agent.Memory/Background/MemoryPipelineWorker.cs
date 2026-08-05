using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryPipelineWorker : BackgroundService
{
    private readonly IMemoryWorkQueue _queue;
    private readonly IMemoryPipeline _pipeline;
    private readonly ILogger<MemoryPipelineWorker> _logger;

    public MemoryPipelineWorker(
        IMemoryWorkQueue queue,
        IMemoryPipeline pipeline,
        ILogger<MemoryPipelineWorker> logger)
    {
        _queue = queue;
        _pipeline = pipeline;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryPipelineWorker started");
        await foreach (var batch in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await _pipeline.ProcessBatchAsync(batch, stoppingToken);
                if (result.StoredCount == 0)
                    _logger.LogDebug("Pipeline skipped batch {Id}: {Reason}", batch.Id, result.Reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline failed for batch {Id}", batch.Id);
            }
        }
    }
}
