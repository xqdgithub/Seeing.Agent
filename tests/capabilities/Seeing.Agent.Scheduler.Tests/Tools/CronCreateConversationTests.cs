using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Tools;

/// <summary>cron_create 的出站 conversation 暴露（新建与修改均可用）。</summary>
public class CronCreateConversationTests
{
    private static JsonElement Args(string rawJson) => JsonDocument.Parse(rawJson).RootElement.Clone();

    private static (Mock<IScheduleManager> Manager, Func<ScheduledJobSpec?> Captured) CreateManager()
    {
        ScheduledJobSpec? captured = null;
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);
        return (manager, () => captured);
    }

    private static Task<ToolResult> RunAsync(Mock<IScheduleManager> manager, string rawJson) =>
        new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object)
            .ExecuteAsync(Args(rawJson), new ToolContext { SessionId = "sess-1" });

    [Fact]
    public async Task ExplicitConversation_ShouldLandOnDispatchTarget()
    {
        var (manager, captured) = CreateManager();

        var result = await RunAsync(manager, """
            {
              "taskType": "text",
              "prompt": "交日报",
              "schedule": { "type": "cron", "cron": "0 18 * * *" },
              "conversation": { "channelId": "qq", "kind": "GROUP", "externalId": "123456", "title": "研发群" }
            }
            """);

        result.Success.Should().BeTrue(result.Error);
        var job = captured()!;
        job.Dispatch.Target.SessionId.Should().Be("sess-1");
        job.Dispatch.Target.Conversation.Should().NotBeNull();
        job.Dispatch.Target.Conversation!.ChannelId.Should().Be("qq");
        job.Dispatch.Target.Conversation.Kind.Should().Be("group", "kind 应归一化为小写");
        job.Dispatch.Target.Conversation.ExternalId.Should().Be("123456");
        job.Dispatch.Target.Conversation.Title.Should().Be("研发群");
        job.Dispatch.Target.Conversation.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task ConversationWithSnakeCaseAndAliases_ShouldSucceed()
    {
        var (manager, captured) = CreateManager();

        var result = await RunAsync(manager, """
            {
              "taskType": "text",
              "prompt": "ping",
              "schedule": { "type": "interval", "every": "6h" },
              "conversation": { "channel_id": "wecom", "kind": "direct", "chat_id": "u-1", "name": "张三" }
            }
            """);

        result.Success.Should().BeTrue(result.Error);
        var conversation = captured()!.Dispatch.Target.Conversation!;
        conversation.ChannelId.Should().Be("wecom");
        conversation.ExternalId.Should().Be("u-1");
        conversation.Title.Should().Be("张三");
    }

    [Fact]
    public async Task OmittedConversation_ShouldLeaveNullForSessionSnapshot()
    {
        var (manager, captured) = CreateManager();

        var result = await RunAsync(manager, """
            { "taskType": "text", "prompt": "ping", "schedule": { "type": "interval", "every": "6h" } }
            """);

        result.Success.Should().BeTrue(result.Error);
        captured()!.Dispatch.Target.Conversation.Should().BeNull("省略时由 ScheduleManager 从会话绑定快照");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("{\"channelId\":\"\",\"kind\":\"\",\"externalId\":\"\",\"title\":\"\"}")]
    public async Task EmptyConversationPlaceholder_ShouldBeTreatedAsNotProvided(string placeholder)
    {
        var (manager, captured) = CreateManager();

        var result = await RunAsync(manager, $$"""
            {
              "taskType": "text",
              "prompt": "ping",
              "schedule": { "type": "interval", "every": "6h" },
              "conversation": {{placeholder}}
            }
            """);

        result.Success.Should().BeTrue($"空占位不应导致失败：{result.Error}");
        captured()!.Dispatch.Target.Conversation.Should().BeNull("默认不填，交给会话绑定快照");
    }

    [Fact]
    public async Task IncompleteConversation_ShouldFailWithMissingFields()
    {
        var (manager, _) = CreateManager();

        var result = await RunAsync(manager, """
            {
              "taskType": "text",
              "prompt": "ping",
              "schedule": { "type": "interval", "every": "6h" },
              "conversation": { "channelId": "qq", "kind": "group" }
            }
            """);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("externalId");
    }

    [Fact]
    public async Task InvalidKind_ShouldFail()
    {
        var (manager, _) = CreateManager();

        var result = await RunAsync(manager, """
            {
              "taskType": "text",
              "prompt": "ping",
              "schedule": { "type": "interval", "every": "6h" },
              "conversation": { "channelId": "qq", "kind": "room", "externalId": "1" }
            }
            """);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("direct", "错误信息应给出合法取值");
    }

    [Fact]
    public async Task CreateResult_ShouldEchoOutboundTarget()
    {
        var (manager, _) = CreateManager();

        var result = await RunAsync(manager, """
            {
              "taskType": "text",
              "prompt": "交日报",
              "schedule": { "type": "cron", "cron": "0 18 * * *" },
              "conversation": { "channelId": "qq", "kind": "group", "externalId": "123456", "title": "研发群" }
            }
            """);

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("出站目标");
        result.Output.Should().Contain("qq/group/123456");
        result.Output.Should().Contain("研发群");
    }
}
