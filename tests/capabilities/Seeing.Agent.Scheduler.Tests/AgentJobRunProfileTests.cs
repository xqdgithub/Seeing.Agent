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
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>AgentJob 必须由执行画像决定身份；缺画像时保持既有 cron 行为。</summary>
public class AgentJobRunProfileTests
{
    [Fact]
    public async Task Execute_WithoutProfile_KeepsCronIdentity()
    {
        var observed = new List<Observed>();
        var (job, dispatched) = Build(profile: null, observed);

        await job.Execute(CreateContext(new JobDataMap { ["prompt"] = "跑一次", ["sessionId"] = "s1" }).Object);

        dispatched.Should().ContainSingle();
        dispatched[0].Source.Should().Be(ScheduleSources.Cron);
        dispatched[0].Layout.Should().Be(DispatchLayout.PromptAndResult);
        dispatched[0].ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
        dispatched[0].SenderId.Should().Be("cron");

        observed.Where(o => o.HookSource is not null)
            .Should().NotBeEmpty()
            .And.OnlyContain(o => o.HookSource == ScheduleSources.Cron);
        observed.Where(o => o.WrapKind is not null)
            .Should().NotBeEmpty()
            .And.OnlyContain(o => o.WrapKind == SystemReminder.Kinds.Cron);
    }

    [Fact]
    public async Task Execute_WithHeartbeatProfile_UsesHeartbeatIdentityAndAssistantOnly()
    {
        var observed = new List<Observed>();
        var (job, dispatched) = Build(ScheduleRunProfile.Heartbeat, observed);

        await job.Execute(CreateContext(new JobDataMap { ["prompt"] = "报时", ["sessionId"] = "s1" }).Object);

        dispatched.Should().ContainSingle();
        dispatched[0].Source.Should().Be(ScheduleSources.Heartbeat);
        dispatched[0].Layout.Should().Be(DispatchLayout.AssistantOnly);
        dispatched[0].ReminderKind.Should().Be(SystemReminder.Kinds.Heartbeat);
        dispatched[0].SenderId.Should().Be("heartbeat");

        observed.Where(o => o.HookSource is not null)
            .Should().NotBeEmpty()
            .And.OnlyContain(o => o.HookSource == ScheduleSources.Heartbeat);
        observed.Where(o => o.WrapKind is not null)
            .Should().NotBeEmpty()
            .And.OnlyContain(o => o.WrapKind == SystemReminder.Kinds.Heartbeat);
    }

    [Fact]
    public async Task Execute_WithHeartbeatProfile_ResultSourceIsHeartbeat()
    {
        var observed = new List<Observed>();
        var (job, dispatched) = Build(ScheduleRunProfile.Heartbeat, observed);

        var context = CreateContext(new JobDataMap { ["prompt"] = "报时", ["sessionId"] = "s1" });
        await job.Execute(context.Object);

        var result = context.Object.Result as JobExecutionResult;
        result.Should().NotBeNull();
        result!.Source.Should().Be(ScheduleSources.Heartbeat,
            "执行历史直接抄 JobExecutionResult.Source，画像必须落到这里");
        dispatched.Should().ContainSingle();
    }

    private sealed record Observed(string? HookSource, string? WrapKind);

    private static ScheduleRunProfile? s_pendingProfile;

    private static (AgentJob Job, List<DispatchRequest> Dispatched) Build(
        ScheduleRunProfile? profile,
        List<Observed> observed)
    {
        s_pendingProfile = profile;
        var dispatched = new List<DispatchRequest>();

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> __, AgentContext ___, CancellationToken ____) =>
                Stream());

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });

        var selection = new Mock<IAgentSelectionResolver>();
        selection.Setup(r => r.ResolveAgentIdAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("build");

        var workspace = new Mock<IWorkspaceProvider>();
        // GetProjectRoot() 是扩展方法（由 ProjectSeeingDirectory 派生），只能 setup 属性
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(Path.Combine("root", ".seeing"));

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("root");

        var hooks = new Mock<IHookManager>();
        hooks.Setup(h => h.TriggerBlockingAsync(
                It.IsAny<HookSpec>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(
                (HookSpec _, string _, IReadOnlyDictionary<string, object?> payload,
                    IDictionary<string, object?> _, CancellationToken _) =>
                {
                    observed.Add(new Observed(payload.TryGetValue("source", out var s) ? s?.ToString() : null, null));
                    return Task.FromResult(new HookResult(Continue: true));
                });

        var reminders = new Mock<ISystemReminderRenderer>();
        reminders.Setup(r => r.Wrap(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string text, string _, string kind, string? _) =>
            {
                observed.Add(new Observed(null, kind));
                return $"<envelope kind=\"{kind}\">{text}</envelope>";
            });
        reminders.Setup(r => r.TryParse(It.IsAny<string>(), out It.Ref<SystemReminderParts>.IsAny))
            .Returns(false);

        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DispatchRequest, CancellationToken>((r, _) => dispatched.Add(r))
            .ReturnsAsync(DispatchResult.Ok());

        var services = new ServiceCollection().BuildServiceProvider();

        var job = new AgentJob(
            executor.Object,
            registry.Object,
            selection.Object,
            workspace.Object,
            world.Object,
            services,
            hooks.Object,
            reminders.Object,
            dispatcher.Object,
            [],
            NullLogger<AgentJob>.Instance);

        return (job, dispatched);
    }

    private static Mock<IJobExecutionContext> CreateContext(JobDataMap data)
    {
        if (!data.ContainsKey(JobDataKeys.RunProfile) && s_pendingProfile is not null)
        {
            data[JobDataKeys.RunProfile] = System.Text.Json.JsonSerializer.Serialize(
                s_pendingProfile, SeeingJson.Readable);
        }

        var ctx = new Mock<IJobExecutionContext>();
        ctx.SetupAllProperties();
        ctx.Setup(c => c.MergedJobDataMap).Returns(data);
        ctx.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        ctx.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        ctx.Setup(c => c.JobDetail).Returns(new JobDetailImpl("job", "g", typeof(AgentJob)));
        return ctx;
    }

    private static async IAsyncEnumerable<IMessageEvent> Stream()
    {
        await Task.CompletedTask;
        yield return new StreamCompleteEvent
        {
            SessionId = "s1",
            Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
        };
    }
}
