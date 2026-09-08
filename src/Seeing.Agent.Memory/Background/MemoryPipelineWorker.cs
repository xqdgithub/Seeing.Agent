using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryPipelineWorker : BackgroundService
{
    public const string ModuleId = "memory";

    private readonly IMemoryWorkQueue _queue;
    private readonly IMemoryPipeline _pipeline;
    private readonly MemoryModuleActivity _moduleActivity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<MemoryPipelineWorker> _logger;

    public MemoryPipelineWorker(
        IMemoryWorkQueue queue,
        IMemoryPipeline pipeline,
        MemoryModuleActivity moduleActivity,
        ILogger<MemoryPipelineWorker> logger,
        IModuleCatalog? catalog = null)
    {
        _queue = queue;
        _pipeline = pipeline;
        _moduleActivity = moduleActivity;
        _logger = logger;
        _catalog = catalog;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger.LogDebug("Memory module disabled; PipelineWorker HostedService no-op");
            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryPipelineWorker started");
        await foreach (var batch in _queue.ReadAllAsync(stoppingToken))
        {
            if (!_moduleActivity.IsActive)
                break;

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
