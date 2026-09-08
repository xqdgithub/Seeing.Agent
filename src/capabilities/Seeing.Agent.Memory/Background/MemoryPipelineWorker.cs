using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryPipelineWorker : BackgroundService, IModuleHostedService
{
    public const string ModuleId = "memory";

    private readonly IMemoryWorkQueue _queue;
    private readonly IMemoryPipeline _pipeline;
    private readonly MemoryModuleActivity _moduleActivity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<MemoryPipelineWorker> _logger;
    private readonly ModuleHostedRunGate _run = new();

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
            _logger.LogDebug("Memory module disabled; PipelineWorker HostedService no-op");
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
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
        finally
        {
            _run.End();
        }
    }
}
