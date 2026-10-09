using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests;

/// <summary>
/// 消息内容不可变并发契约（publish-on-write）测试。
/// 见 docs/superpowers/specs/2026-10-08-session-message-immutable-concurrency-design.md
/// </summary>
public class SessionMessageImmutabilityTests
{
    [Fact]
    public void ConcurrentPublishOnWrite_And_Enumerate_DoesNotThrow()
    {
        var msg = new SessionMessage
        {
            Role = MessageRole.Assistant,
            ToolCalls = ImmutableList<SessionToolCall>.Empty
        };

        var stop = false;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
                msg.ToolCalls = msg.ToolCalls!.Add(new SessionToolCall { Id = $"t{i}", Name = "read" });
        });

        var reader = Task.Run(() =>
        {
            while (!stop)
            {
                var snapshot = msg.ToolCalls;
                _ = snapshot?.Count(s => s.Name == "read");
            }
        });

        writer.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        stop = true;
        reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        msg.ToolCalls!.Should().HaveCount(20_000);
    }

    [Fact]
    public void JsonRoundTrip_PreservesCollections()
    {
        var original = new SessionMessage
        {
            Id = "m1",
            Role = MessageRole.Assistant,
            Content = "hi",
            Parts = ImmutableList.Create(SessionContentPart.CreateText("hi")),
            Metadata = ImmutableDictionary<string, object>.Empty.SetItem("k", "v"),
            ToolCalls = ImmutableList.Create(new SessionToolCall
            {
                Id = "t1",
                Name = "read",
                Status = "success",
                TaskSteps = ImmutableList.Create(new SessionTaskStep
                {
                    StepKind = "tool_complete",
                    ToolName = "read"
                }),
                Metadata = ImmutableDictionary<string, object>.Empty.SetItem("x", "y")
            })
        };

        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<SessionMessage>(json)!;

        back.ToolCalls!.Should().HaveCount(1);
        back.ToolCalls![0].TaskSteps!.Should().HaveCount(1);
        back.Parts!.Should().HaveCount(1);
        back.Metadata.Should().ContainKey("k");
    }
}
