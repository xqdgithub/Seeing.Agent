using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Execution;

/// <summary>心跳只落一条已消费的助手结果；cron 保持「提示 + 结果」。</summary>
public class SessionScheduleDispatcherLayoutTests
{
    [Fact]
    public async Task AssistantOnly_WritesVisiblePromptAndResultBothOffHistory()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Heartbeat,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "一切正常",
            UserInput = "<system-reminder source=\"job\" kind=\"heartbeat\"><notice>心跳已触发</notice><task>检查状态</task></system-reminder>",
            SessionId = "s1",
            ReminderKind = SystemReminder.Kinds.Heartbeat,
            SenderId = "heartbeat",
            Layout = DispatchLayout.AssistantOnly
        });

        delivered.Should().HaveCount(2, "提示要可见，结果要留痕");

        // 提示：可见但不进 LLM 历史（None 由 ExecutionRunner 按 inbox.presentation=none 排除）
        delivered[0].Presentation.Should().Be(InboxPresentation.None);
        delivered[0].Delivery.Should().Be(InboxDelivery.Inject);
        delivered[0].Body.Should().Be("检查状态", "信封必须被解包成任务正文");
        delivered[0].Kind.Should().Be(SystemReminder.Kinds.Heartbeat);
        delivered[0].Sender.Id.Should().Be("heartbeat");

        // 结果：已消费的助手消息
        delivered[1].Presentation.Should().Be(InboxPresentation.Assistant);
        delivered[1].Body.Should().Be("一切正常");
        delivered[1].Kind.Should().Be(SystemReminder.Kinds.Heartbeat);
        delivered[1].Sender.Id.Should().Be("heartbeat");
    }

    [Fact]
    public async Task AssistantOnly_ShouldNotWriteUnreadSystemPrompt()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Heartbeat,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "结果",
            UserInput = "检查状态",
            SessionId = "s1",
            Layout = DispatchLayout.AssistantOnly
        });

        delivered.Should().NotContain(m => m.Presentation == InboxPresentation.System,
            "心跳不得在目标会话留下未读系统输入");
    }

    [Fact]
    public async Task PromptAndResult_KeepsPromptThenAssistant()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Cron,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "结果",
            UserInput = "<system-reminder source=\"job\" kind=\"cron\"><task>跑一次</task></system-reminder>",
            SessionId = "s1"
        });

        delivered.Should().HaveCount(2);
        delivered[0].Presentation.Should().Be(InboxPresentation.System);
        delivered[0].Kind.Should().Be(SystemReminder.Kinds.Cron);
        delivered[0].Sender.Id.Should().Be("cron");
        delivered[1].Presentation.Should().Be(InboxPresentation.Assistant);
        delivered[1].Kind.Should().Be(SystemReminder.Kinds.Cron);
        delivered[1].Sender.Id.Should().Be("cron");
    }

    [Fact]
    public async Task AssistantOnly_WithoutUserInput_StillWritesResult()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Heartbeat,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "结果",
            SessionId = "s1",
            Layout = DispatchLayout.AssistantOnly
        });

        delivered.Should().ContainSingle();
        delivered[0].Presentation.Should().Be(InboxPresentation.Assistant);
    }

    private static SessionScheduleDispatcher Build(List<InboxMessage> delivered)
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime.Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => delivered.Add(m))
            .ReturnsAsync(InboxResult.Ok("m1"));

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.EnsureSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new SessionData { Id = "s1" });

        // 真实渲染器：PromptAndResult 用例要验证信封解包后的 kind 透传
        var reminders = new Seeing.Agent.Core.Reminders.SystemReminderRendererService();

        return new SessionScheduleDispatcher(
            runtime.Object, sessions.Object, reminders, NullLogger<SessionScheduleDispatcher>.Instance);
    }
}
