using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Session.Core;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 投递目标矩阵回归（设计规格 2026-10-17-cron-conversation-target-and-binding-picker）：
/// 显式会话 + conversation（无绑定时挂上，已有绑定不覆盖）／仅 conversation（每次触发新建会话并挂上）／
/// 无 conversation（现状行为不变）；conversation 必须传入 DispatchRequest 供出站优先消费。
/// </summary>
public class ScheduleConversationTargetTests
{
    private const string ChannelId = "qq";
    private const string Kind = "group";
    private const string ExternalId = "gr-100";
    private const string Title = "测试群";

    private sealed class Capture
    {
        public AgentContext? Context;
        public DispatchRequest? Dispatch;
    }

    private sealed class Harness
    {
        public required Mock<ISessionManager> SessionManager { get; init; }
        public required Dictionary<string, SessionData> Sessions { get; init; }
        public required AgentJob Job { get; init; }
        public required Mock<IJobExecutionContext> JobContext { get; init; }
        public required Func<Mock<IJobExecutionContext>> SecondContext { get; init; }
        public required Capture State { get; init; }

        public List<string> CreatedSessionIds =>
            Sessions.Keys.Where(id => id.StartsWith("cron_", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public async Task Execute_ExplicitSessionWithConversation_ShouldAttachRouteWhenEmpty()
    {
        // Arrange：显式会话 s-cron + conversation → s-cron 无绑定时挂上
        var session = new SessionData { Id = "s-cron" };
        var harness = CreateHarness(
            sessions: new Dictionary<string, SessionData> { ["s-cron"] = session },
            dataMap: DataMap(sessionId: "s-cron", withConversation: true));

        // Act
        await harness.Job.Execute(harness.JobContext.Object);

        // Assert
        harness.State.Context.Should().NotBeNull();
        harness.State.Context!.SessionId.Should().Be("s-cron");
        session.ChannelRoute.Should().NotBeNull();
        session.ChannelRoute!.ChannelId.Should().Be(ChannelId);
        session.ChannelRoute.Kind.Should().Be(Kind);
        session.ChannelRoute.ExternalId.Should().Be(ExternalId);
        session.ChannelRoute.Title.Should().Be(Title);
        harness.SessionManager.Verify(m => m.SaveAsync("s-cron"), Times.Once);
    }

    [Fact]
    public async Task Execute_ExplicitSessionWithConversation_ShouldNotOverwriteExistingRoute()
    {
        // Arrange：s-cron 已有绑定（first-write-wins，与入站回填一致）
        var existing = new SessionChannelRoute { ChannelId = "wecom", Kind = "direct", ExternalId = "u-1" };
        var session = new SessionData { Id = "s-cron", ChannelRoute = existing };
        var harness = CreateHarness(
            sessions: new Dictionary<string, SessionData> { ["s-cron"] = session },
            dataMap: DataMap(sessionId: "s-cron", withConversation: true));

        // Act
        await harness.Job.Execute(harness.JobContext.Object);

        // Assert
        harness.State.Context!.SessionId.Should().Be("s-cron");
        session.ChannelRoute.Should().BeSameAs(existing);
        harness.SessionManager.Verify(m => m.SaveAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ConversationOnly_ShouldCreateNewSessionPerTrigger()
    {
        // Arrange：无 SessionId 键 + conversation → 每次触发新建会话并挂上
        var harness = CreateHarness(
            sessions: new Dictionary<string, SessionData>(),
            dataMap: DataMap(sessionId: null, withConversation: true));

        // Act：连续两次触发
        await harness.Job.Execute(harness.JobContext.Object);
        var firstSessionId = harness.State.Context!.SessionId;
        await harness.Job.Execute(harness.SecondContext().Object);
        var secondSessionId = harness.State.Context!.SessionId;

        // Assert：两次各自新建（id 不同），均挂上 conversation
        var pattern = new Regex(@"^cron_job-1_\d{14}_[0-9a-f]{6}$");
        firstSessionId.Should().MatchRegex(pattern.ToString());
        secondSessionId.Should().MatchRegex(pattern.ToString());
        secondSessionId.Should().NotBe(firstSessionId);

        harness.Sessions.Should().ContainKey(firstSessionId).WhoseValue.ChannelRoute!.ExternalId.Should().Be(ExternalId);
        harness.Sessions.Should().ContainKey(secondSessionId).WhoseValue.ChannelRoute!.ExternalId.Should().Be(ExternalId);
    }

    [Fact]
    public async Task Execute_NoConversation_ShouldKeepLegacyBehavior()
    {
        // Arrange：无 conversation 键 → 不碰会话目录（现状），session id 原样
        var harness = CreateHarness(
            sessions: new Dictionary<string, SessionData>(),
            dataMap: DataMap(sessionId: "s-cron", withConversation: false));

        // Act
        await harness.Job.Execute(harness.JobContext.Object);

        // Assert
        harness.State.Context!.SessionId.Should().Be("s-cron");
        harness.SessionManager.Verify(
            m => m.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
        harness.CreatedSessionIds.Should().BeEmpty(because: "无 conversation 时不应新建会话");
    }

    [Fact]
    public async Task Execute_WithConversation_ShouldPassConversationToDispatcher()
    {
        // Arrange：conversation 必须随 DispatchRequest 传递（出站优先消费）
        var harness = CreateHarness(
            sessions: new Dictionary<string, SessionData> { ["s-cron"] = new SessionData { Id = "s-cron" } },
            dataMap: DataMap(sessionId: "s-cron", withConversation: true));

        // Act
        await harness.Job.Execute(harness.JobContext.Object);

        // Assert
        harness.State.Dispatch.Should().NotBeNull();
        harness.State.Dispatch!.Conversation.Should().NotBeNull();
        harness.State.Dispatch.Conversation!.ChannelId.Should().Be(ChannelId);
        harness.State.Dispatch.Conversation.Kind.Should().Be(Kind);
        harness.State.Dispatch.Conversation.ExternalId.Should().Be(ExternalId);
        harness.State.Dispatch.Conversation.Title.Should().Be(Title);
        harness.State.Dispatch.SessionId.Should().Be("s-cron");
    }

    private static JobDataMap DataMap(string? sessionId, bool withConversation)
    {
        var dataMap = new JobDataMap
        {
            { "jobId", "job-1" },
            { "runId", "run-1" },
            { "taskType", ScheduleTaskTypes.Agent },
            { "agentId", "build" },
            { "prompt", "播报" },
            { "timeoutSeconds", "30" }
        };

        if (sessionId is not null)
            dataMap[JobDataKeys.SessionId] = sessionId;

        if (withConversation)
        {
            dataMap[JobDataKeys.ConversationChannelId] = ChannelId;
            dataMap[JobDataKeys.ConversationKind] = Kind;
            dataMap[JobDataKeys.ConversationExternalId] = ExternalId;
            dataMap[JobDataKeys.ConversationTitle] = Title;
        }

        return dataMap;
    }

    private static Harness CreateHarness(Dictionary<string, SessionData> sessions, JobDataMap dataMap)
    {
        var state = new Capture();

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>((_, _, ctx, _) => state.Context = ctx)
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = state.Context?.SessionId ?? "s-cron",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DispatchRequest, CancellationToken>((request, _) => state.Dispatch = request)
            .ReturnsAsync(DispatchResult.Ok());

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string id, string? _, string? _, string? _) =>
            {
                if (!sessions.TryGetValue(id, out var session))
                {
                    session = new SessionData { Id = id };
                    sessions[id] = session;
                }
                return session;
            });
        sessionManager
            .Setup(m => m.SetTitleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new Harness
        {
            SessionManager = sessionManager,
            Sessions = sessions,
            Job = CreateJob(executor.Object, dispatcher.Object, sessionManager.Object),
            JobContext = CreateJobContextMock(dataMap),
            SecondContext = () => CreateJobContextMock(dataMap),
            State = state
        };
    }

    private static AgentJob CreateJob(
        IAgentExecutor executor,
        IScheduledJobDispatcher dispatcher,
        ISessionManager sessionManager)
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

        var services = new ServiceCollection()
            .AddSingleton(sessionManager)
            .BuildServiceProvider();

        return new AgentJob(
            executor,
            agentRegistry.Object,
            selectionResolver.Object,
            workspace.Object,
            world.Object,
            services,
            hooks.Object,
            reminders.Object,
            dispatcher,
            [],
            NullLogger<AgentJob>.Instance);
    }

    private static Mock<IJobExecutionContext> CreateJobContextMock(JobDataMap dataMap)
    {
        var jobContext = new Mock<IJobExecutionContext>();
        jobContext.SetupAllProperties();
        jobContext.Setup(c => c.MergedJobDataMap).Returns(dataMap);
        jobContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        jobContext.Setup(c => c.JobDetail).Returns(new JobDetailImpl("job-1", "group", typeof(AgentJob)));
        jobContext.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        jobContext.Setup(c => c.ScheduledFireTimeUtc).Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        return jobContext;
    }

    private static async IAsyncEnumerable<IMessageEvent> OneEvent(IMessageEvent messageEvent)
    {
        yield return messageEvent;
        await Task.CompletedTask;
    }
}
