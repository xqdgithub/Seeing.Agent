using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// schema 单点纪律回归（2026-10-17 QQ 群垃圾输出根因）：
/// AgentJob 直连 IAgentExecutor 的旁路执行必须经 ISessionToolSchemaResolver
/// 携带会话级结算后的真实工具 schema，不得再发空 tools 的 LLM 请求。
/// </summary>
public class AgentJobToolSchemasTests
{
    [Fact]
    public async Task Execute_AgentTask_ShouldPassSessionSettledToolSchemasToExecutor()
    {
        // Arrange
        var toolSchemas = new[]
        {
            new FunctionToolSchema
            {
                Function = new FunctionSchema { Name = "current_time", Description = "获取当前时间" }
            }
        };
        var resolver = new Mock<ISessionToolSchemaResolver>();
        resolver
            .Setup(r => r.ResolveAsync("s-cron", It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionToolSchemaResolution(
                new[] { new ExposedTool("current_time", "", "current_time(): 获取当前时间", toolSchemas[0], 10, ToolExposureMode.Inline) },
                ["tools"],
                ToolExposureLayout.Flat,
                null,
                10,
                0));

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>((_, _, ctx, _) => capturedContext = ctx)
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = "s-cron",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var services = new ServiceCollection()
            .AddSingleton(resolver.Object)
            .BuildServiceProvider();

        var jobContextMock = CreateJobContextMock();
        var job = CreateJob(executor.Object, services);

        // Act
        await job.Execute(jobContextMock.Object);

        // Assert
        capturedContext.Should().NotBeNull(because: Describe(jobContextMock));
        capturedContext!.ToolSettlement!.InlineSchemas.Should().BeEquivalentTo(toolSchemas);
        resolver.Verify(
            r => r.ResolveAsync("s-cron", It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_AgentTask_WithoutResolver_ShouldFallBackToNullSchemas()
    {
        // Arrange：未注册 resolver（最小宿主）→ 空 schema 继续，不阻断任务
        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>((_, _, ctx, _) => capturedContext = ctx)
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = "s-cron",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var services = new ServiceCollection().BuildServiceProvider();

        var jobContextMock = CreateJobContextMock();
        var job = CreateJob(executor.Object, services);

        // Act
        await job.Execute(jobContextMock.Object);

        // Assert
        capturedContext.Should().NotBeNull(because: Describe(jobContextMock));
        capturedContext!.ToolSettlement.Should().BeNull();
    }

    [Fact]
    public async Task Execute_AgentTask_ShouldPromotePendingDiscoveryBeforeResolve()
    {
        // Arrange：旁路必须在结算前提升上一轮登记的发现，否则 cron 会话永远不内联。
        var writer = new RecordingExposureWriter();
        var resolver = new Mock<ISessionToolSchemaResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .Callback(() => writer.Promoted.Should().BeTrue("提升必须发生在结算之前"))
            .ReturnsAsync(SessionToolSchemaResolution.Empty);

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = "s-cron",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var services = new ServiceCollection()
            .AddSingleton(resolver.Object)
            .AddSingleton<ISessionToolExposureWriter>(writer)
            .BuildServiceProvider();

        var jobContextMock = CreateJobContextMock();
        var job = CreateJob(executor.Object, services);

        // Act
        await job.Execute(jobContextMock.Object);

        // Assert
        writer.Promoted.Should().BeTrue();
    }

    private sealed class RecordingExposureWriter : ISessionToolExposureWriter
    {
        public bool Promoted { get; private set; }

        public void PromotePending(Seeing.Session.Core.SessionData session) => Promoted = true;

        public Task<bool> PromotePendingAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Promoted = true;
            return Task.FromResult(true);
        }

        public void Commit(
            Seeing.Session.Core.SessionData session,
            IReadOnlyList<string>? discovered,
            bool escalated,
            int schemaTokens,
            int catalogTokens)
        {
        }
    }

    private static AgentJob CreateJob(IAgentExecutor executor, IServiceProvider services)
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync("build"))
            .ReturnsAsync(new AgentDefinition { Name = "build" });

        var selectionResolver = new Mock<IAgentSelectionResolver>();
        selectionResolver
            .Setup(r => r.ResolveAgentIdAsync("build", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("build");

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(@"E:\root\.seeing");

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("root");

        var hooks = new Mock<IHookManager>();
        hooks
            .Setup(h => h.TriggerBlockingAsync(
                It.IsAny<HookSpec>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HookResult(Continue: true));

        var reminders = new Mock<ISystemReminderRenderer>();
        reminders
            .Setup(r => r.Wrap(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns("wrapped");
        reminders
            .Setup(r => r.TryParse(It.IsAny<string>(), out It.Ref<SystemReminderParts>.IsAny))
            .Returns(false);

        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResult.Ok());

        return new AgentJob(
            executor,
            agentRegistry.Object,
            selectionResolver.Object,
            workspace.Object,
            world.Object,
            services,
            hooks.Object,
            reminders.Object,
            dispatcher.Object,
            [],
            NullLogger<AgentJob>.Instance);
    }

    private static Mock<IJobExecutionContext> CreateJobContextMock()
    {
        var dataMap = new JobDataMap
        {
            { "jobId", "job-1" },
            { "runId", "run-1" },
            { "taskType", ScheduleTaskTypes.Agent },
            { "sessionId", "s-cron" },
            { "agentId", "build" },
            { "prompt", "报时" }
        };

        var jobContext = new Mock<IJobExecutionContext>();
        jobContext.SetupAllProperties();
        jobContext.Setup(c => c.MergedJobDataMap).Returns(dataMap);
        jobContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        jobContext.Setup(c => c.JobDetail).Returns(new JobDetailImpl("job-1", "group", typeof(AgentJob)));
        jobContext.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        jobContext.Setup(c => c.ScheduledFireTimeUtc).Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        return jobContext;
    }

    private static string Describe(Mock<IJobExecutionContext> jobContext) =>
        jobContext.Object.Result switch
        {
            JobExecutionResult r => $"Success={r.Success}, Error={r.Error}, Output={r.Output}",
            _ => $"Result={jobContext.Object.Result}"
        };

    private static async IAsyncEnumerable<IMessageEvent> OneEvent(IMessageEvent messageEvent)
    {
        yield return messageEvent;
        await Task.CompletedTask;
    }
}
