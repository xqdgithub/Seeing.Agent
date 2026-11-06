using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 前缀缓存守卫的持久化：SchemaSnapshotEvent 的变化标记与增删项必须落进会话消息 metadata，
/// 否则刷新/重启后无法回答"这个会话的缓存从哪一轮开始断"。
/// </summary>
public class SchemaChangePersistenceTests
{
    [Fact]
    public void 有变化_应把标记与增删项写入会话元数据()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new SchemaSnapshotEvent
        {
            SessionId = session.Id,
            ExecutionId = "e1",
            ToolIds = ["read", "write"],
            SchemaChangedFromPreviousExecution = true,
            AddedToolIds = ["write"],
            RemovedToolIds = []
        });

        var payload = ReadPayload(session);

        payload["schemaChanged"].Should().Be(true);
        payload["addedToolIds"].Should().BeEquivalentTo(new[] { "write" });
    }

    [Fact]
    public void 无变化_标记应为false()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new SchemaSnapshotEvent
        {
            SessionId = session.Id,
            ExecutionId = "e1",
            ToolIds = ["read"]
        });

        ReadPayload(session)["schemaChanged"].Should().Be(false);
    }

    private static Dictionary<string, object> ReadPayload(SessionData session)
    {
        var metadata = session.GetActiveMessages()
            .Select(m => m.Metadata)
            .FirstOrDefault(m => m?.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey) == true);

        metadata.Should().NotBeNull();
        return (Dictionary<string, object>)metadata![ChatEventTracker.SchemaSnapshotMetadataKey];
    }
}
