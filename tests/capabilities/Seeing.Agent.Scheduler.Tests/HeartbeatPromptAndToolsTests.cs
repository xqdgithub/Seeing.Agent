using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 心跳执行必须把提示词投递给会话（用户要能看到这轮在心做什么），
/// 且投递布局必须是 <see cref="DispatchLayout.AssistantOnly"/>。
/// <para>
/// 旧行为只落一条助手结果、提示词根本不落盘，用户看到的就是「莫名其妙一条汇报」。
/// 工具视图的存在性由 <see cref="ScheduleSessionEnsuredBeforeSchemaTests"/> 守卫
/// （会话不存在 → 结算为空 → 模型文本表演工具调用）。
/// </para>
/// </summary>
public class HeartbeatPromptAndToolsTests
{
    [Fact]
    public async Task Heartbeat_Run_ShouldDeliverPromptWithHeartbeatIdentity()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var recorder = new RecordingDispatcher();
        var (manager, _, _) = SchedulerIntegrationTestsFactory.Create(ws, dispatcher: recorder);

        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var service = new HeartbeatService(manager, repo, NullLogger<HeartbeatService>.Instance);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await service.UpdateAsync(new HeartbeatUpdate
            {
                Enabled = true,
                Prompt = "检查当前时间并汇报",
                Every = "6h",
                SessionId = "hb-tools",
                Agent = "test-agent"
            }, TestContext.Current.CancellationToken);

            var trigger = await service.RunNowAsync(TestContext.Current.CancellationToken);
            trigger.Should().BeOfType<TriggerResult.Accepted>();

            await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            recorder.Requests.Should().ContainSingle();
            var request = recorder.Requests[0];

            // 提示词必须随投递进入会话（None 承载：可见、不进 LLM 历史）
            request.UserInput.Should().NotBeNullOrWhiteSpace(
                "提示词必须随投递进入会话，否则用户看不到这轮心跳在做什么");
            request.UserInput!.Should().Contain("检查当前时间并汇报");

            request.Layout.Should().Be(DispatchLayout.AssistantOnly);
            request.Source.Should().Be(ScheduleSources.Heartbeat);
            request.ReminderKind.Should().Be(SystemReminder.Kinds.Heartbeat);
            request.SenderId.Should().Be("heartbeat");
            request.SessionId.Should().Be("hb-tools");
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingDispatcher : IScheduledJobDispatcher
    {
        public List<DispatchRequest> Requests { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            Completed.TrySetResult();
            return Task.FromResult(DispatchResult.Ok());
        }
    }
}
