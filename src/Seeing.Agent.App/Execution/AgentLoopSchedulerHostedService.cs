using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Models;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Llm;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// 将 IAgentLoopScheduler ResumeHandler 接到 ExecutionJobService；
/// 并将执行事件总线适配为 ISessionEventBus。
/// </summary>
public sealed class AgentLoopSchedulerHostedService : IHostedService
{
    private readonly IAgentLoopScheduler _scheduler;
    private readonly ExecutionJobService _jobs;
    private readonly ILogger<AgentLoopSchedulerHostedService> _logger;

    public AgentLoopSchedulerHostedService(
        IAgentLoopScheduler scheduler,
        ExecutionJobService jobs,
        ILogger<AgentLoopSchedulerHostedService> logger)
    {
        _scheduler = scheduler;
        _jobs = jobs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduler.RegisterResumeHandler(async (sessionId, ct) =>
        {
            _logger.LogInformation("Idle resume: submitting continuation for {SessionId}", sessionId);
            var result = await _jobs.SubmitAsync(
                sessionId,
                new ChatInput { Text = string.Empty },
                new ChatOptions { SkipUserMessagePersist = true });

            if (!result.Success || string.IsNullOrEmpty(result.ExecutionId))
            {
                _logger.LogWarning(
                    "Idle resume submit failed for {SessionId}: {Error}",
                    sessionId, result.Error);
                return;
            }

            await _jobs.WaitForExecutionAsync(result.ExecutionId, ct);
        });

        _logger.LogInformation("AgentLoopScheduler ResumeHandler registered");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// IExecutionEventPublisher → ISessionEventBus 适配。
/// </summary>
public sealed class ExecutionSessionEventBus : ISessionEventBus
{
    private readonly IExecutionEventPublisher _publisher;

    public ExecutionSessionEventBus(IExecutionEventPublisher publisher)
    {
        _publisher = publisher;
    }

    public void Publish(string sessionId, IMessageEvent evt) =>
        _publisher.Publish(sessionId, evt);
}
