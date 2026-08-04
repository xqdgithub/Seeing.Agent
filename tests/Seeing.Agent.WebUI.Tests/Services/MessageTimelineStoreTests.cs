using FluentAssertions;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageTimelineStoreTests
{
    private static SessionMessage Msg(
        string id, string role, string content,
        string? loopId = null, int step = 0,
        Dictionary<string, object>? metadata = null)
        => new()
        {
            Id = id,
            Role = role,
            Content = content,
            LoopId = loopId,
            Step = step,
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata
        };

    [Fact]
    public void ResetFromSession_ShouldGroupAssistantsByLoopId()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(
        [
            Msg("u1", "user", "hi"),
            Msg("a1", "assistant", "one", loopId: "L1", step: 0),
            Msg("a2", "assistant", "two", loopId: "L1", step: 1),
        ], "s1");

        store.Items.Should().HaveCount(2);
        store.Items[0].Kind.Should().Be(TimelineItemKind.User);
        store.Items[1].Kind.Should().Be(TimelineItemKind.AssistantTurn);
        store.Items[1].Key.Should().Be("L1");
        var turn = store.Items[1].Turn;
        turn.Should().NotBeNull();
        turn!.Messages.Should().HaveCount(2);
        turn.Messages[0].Content.Should().Be("one");
        turn.Messages[1].Content.Should().Be("two");
        turn.LoopId.Should().Be("L1");
        turn.LoopIndex.Should().Be(1);
    }

    [Fact]
    public void ResetFromSession_DuplicateId_ShouldKeepLast()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(
        [
            Msg("u1", "user", "first"),
            Msg("u1", "user", "last"),
        ], "s1");

        store.Items.Should().HaveCount(1);
        store.Items[0].Message!.Content.Should().Be("last");
    }

    [Fact]
    public void ResetFromSession_ShouldSkipToolRole()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(
        [
            Msg("a1", "assistant", "hello", loopId: "L1"),
            Msg("t1", "tool", "result"),
        ], "s1");

        store.Items.Should().HaveCount(1);
        store.Items[0].Kind.Should().Be(TimelineItemKind.AssistantTurn);
    }

    [Fact]
    public void ResetFromSession_AssistantWithoutLoopId_ShouldUseSingleKey()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(
        [
            Msg("a9", "assistant", "solo"),
        ], "s1");

        store.Items.Should().HaveCount(1);
        store.Items[0].Key.Should().Be(TimelineItem.AssistantKey(null, "a9"));
        store.Items[0].Key.Should().Be("single-a9");
    }

    [Fact]
    public void ResetFromSession_ShouldMapSpecialKinds()
    {
        var reminderContent = SystemReminderRenderer.Wrap("do it", "job", "cron");
        var store = new MessageTimelineStore();
        store.ResetFromSession(
        [
            Msg("r1", "user", reminderContent),
            Msg("c1", "assistant", "summary", metadata: new() { ["is_compaction_summary"] = true }),
            Msg("p1", "user", "instructions", metadata: new() { ["projectInstructions"] = true }),
            Msg("sys", "system", "note"),
        ], "s1");

        store.Items.Select(i => i.Kind).Should().Equal(
            TimelineItemKind.Reminder,
            TimelineItemKind.Compaction,
            TimelineItemKind.ProjectInstructions,
            TimelineItemKind.System);
    }

    [Fact]
    public void Changed_ShouldFireOnResetAndSync()
    {
        var store = new MessageTimelineStore();
        var count = 0;
        store.Changed += () => count++;

        store.ResetFromSession(
        [
            Msg("a1", "assistant", "x", loopId: "L1"),
        ], "s1");
        store.SyncAssistantMessage(Msg("a1", "assistant", "xy", loopId: "L1"), "s1", isComplete: false);

        count.Should().Be(2);
    }

    [Fact]
    public void SyncAssistantMessage_TwoAssistantsWithoutLoopId_ShouldCreateSeparateTurns()
    {
        var store = new MessageTimelineStore();

        store.SyncAssistantMessage(Msg("a1", "assistant", "first"), "s1", isComplete: false);
        store.SyncAssistantMessage(Msg("a2", "assistant", "second"), "s1", isComplete: false);

        store.Items.Should().HaveCount(2);
        store.Items[0].Kind.Should().Be(TimelineItemKind.AssistantTurn);
        store.Items[1].Kind.Should().Be(TimelineItemKind.AssistantTurn);
        store.Items[0].Key.Should().Be("single-a1");
        store.Items[1].Key.Should().Be("single-a2");
        store.Items[0].Turn!.Messages.Should().ContainSingle().Which.Content.Should().Be("first");
        store.Items[1].Turn!.Messages.Should().ContainSingle().Which.Content.Should().Be("second");
    }

    [Fact]
    public void ResetFromSession_ShouldBumpGeneration_SyncShouldNot()
    {
        var store = new MessageTimelineStore();
        store.Generation.Should().Be(0);

        store.ResetFromSession([Msg("u1", "user", "hi")], "s1");
        store.Generation.Should().Be(1);

        store.SyncAssistantMessage(Msg("a1", "assistant", "x", loopId: "L1"), "s1");
        store.Generation.Should().Be(1);

        store.ResetFromSession([Msg("u1", "user", "hi")], "s1");
        store.Generation.Should().Be(2);
    }

    [Fact]
    public void SyncAssistantMessage_ShouldPreserveIsExpanded_AndUpdateContent()
    {
        var store = new MessageTimelineStore();
        var withTool = Msg("a1", "assistant", "partial", loopId: "L1");
        withTool.ToolCalls =
        [
            new SessionToolCall
            {
                Id = "tc1",
                Name = "read",
                Status = "running",
                Arguments = "{}"
            }
        ];

        store.ResetFromSession([withTool], "s1");

        var tool = store.Items[0].Turn!.Messages[0].ToolCalls.Should().ContainSingle().Subject;
        tool.IsExpanded = true;
        var revBefore = store.Items[0].Revision;

        var updated = Msg("a1", "assistant", "partial + more", loopId: "L1");
        updated.ToolCalls =
        [
            new SessionToolCall
            {
                Id = "tc1",
                Name = "read",
                Status = "completed",
                Arguments = "{}",
                Result = "file contents"
            }
        ];

        store.SyncAssistantMessage(updated, "s1", isComplete: false);

        var msg = store.Items[0].Turn!.Messages.Should().ContainSingle().Subject;
        msg.Content.Should().Be("partial + more");
        msg.ToolCalls.Should().ContainSingle()
            .Which.IsExpanded.Should().BeTrue();
        msg.ToolCalls[0].Result.Should().Be("file contents");
        store.Items[0].Revision.Should().BeGreaterThan(revBefore);
    }

    [Fact]
    public void SyncAssistantMessage_Unchanged_ShouldNotIncrementRevision()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession([Msg("a1", "assistant", "same", loopId: "L1")], "s1");
        // Force incomplete so a subsequent identical sync is a true no-op.
        store.SyncAssistantMessage(Msg("a1", "assistant", "same", loopId: "L1"), "s1", isComplete: false);

        var rev = store.Items[0].Revision;
        var n = 0;
        store.Changed += () => n++;

        store.SyncAssistantMessage(Msg("a1", "assistant", "same", loopId: "L1"), "s1", isComplete: false);

        store.Items[0].Revision.Should().Be(rev);
        n.Should().Be(0);
    }

    [Fact]
    public void SyncAssistantMessage_ShouldMarkReasoningComplete_WhenContentStarts()
    {
        var store = new MessageTimelineStore();
        var reasoningOnly = Msg("a1", "assistant", "", loopId: "L1");
        reasoningOnly.ReasoningContent = "thinking...";

        store.SyncAssistantMessage(reasoningOnly, "s1", isComplete: false);
        store.Items[0].Turn!.Messages[0].IsReasoningComplete.Should().BeFalse();

        var withContent = Msg("a1", "assistant", "answer", loopId: "L1");
        withContent.ReasoningContent = "thinking...";
        store.SyncAssistantMessage(withContent, "s1", isComplete: false);

        var msg = store.Items[0].Turn!.Messages.Should().ContainSingle().Subject;
        msg.IsReasoningComplete.Should().BeTrue();
        msg.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void SyncAssistantMessage_ShouldLatchReasoningComplete()
    {
        var store = new MessageTimelineStore();
        var withContent = Msg("a1", "assistant", "answer", loopId: "L1");
        withContent.ReasoningContent = "thinking...";
        store.SyncAssistantMessage(withContent, "s1", isComplete: false);
        store.Items[0].Turn!.Messages[0].IsReasoningComplete.Should().BeTrue();

        // Later sync of same content should keep the latch.
        store.SyncAssistantMessage(withContent, "s1", isComplete: false);
        store.Items[0].Turn!.Messages[0].IsReasoningComplete.Should().BeTrue();
    }

    [Fact]
    public void DeriveIsReasoningComplete_ShouldBeTrue_WhenToolsStart()
    {
        var msg = Msg("a1", "assistant", "");
        msg.ReasoningContent = "plan";
        msg.ToolCalls = [new SessionToolCall { Id = "t1", Name = "read" }];

        MessageViewModelFactory.DeriveIsReasoningComplete(msg, isComplete: false).Should().BeTrue();
    }

    [Fact]
    public void ResetFromSession_ShouldUpdateTailHint()
    {
        var store = new MessageTimelineStore();
        store.TailKey.Should().BeNull();
        store.TailRevision.Should().Be(0);

        store.ResetFromSession(
        [
            Msg("u1", "user", "hi"),
            Msg("a1", "assistant", "yo", loopId: "L1"),
        ], "s1");

        store.TailKey.Should().Be("L1");
        store.TailRevision.Should().Be(store.Items[^1].Revision);
    }

    [Fact]
    public void SyncAssistantMessage_ShouldBumpTailRevision_WhenContentChanges()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Msg("a1", "assistant", "x", loopId: "L1"), "s1", isComplete: false);
        var rev = store.TailRevision;

        store.SyncAssistantMessage(Msg("a1", "assistant", "xy", loopId: "L1"), "s1", isComplete: false);
        store.TailKey.Should().Be("L1");
        store.TailRevision.Should().BeGreaterThan(rev);
    }

    [Fact]
    public void SyncAssistantMessage_ShouldTouch_WhenTaskStepsChange()
    {
        var store = new MessageTimelineStore();
        var first = Msg("a1", "assistant", "hi", loopId: "L1");
        first.ToolCalls =
        [
            new SessionToolCall
            {
                Id = "tc1",
                Name = "task",
                Status = "running",
                TaskSteps = [new SessionTaskStep { ToolCallId = "s1", Status = "running", Preview = "a" }]
            }
        ];
        store.SyncAssistantMessage(first, "s1", isComplete: false);
        var rev = store.Items[0].Revision;

        var second = Msg("a1", "assistant", "hi", loopId: "L1");
        second.ToolCalls =
        [
            new SessionToolCall
            {
                Id = "tc1",
                Name = "task",
                Status = "running",
                TaskSteps = [new SessionTaskStep { ToolCallId = "s1", Status = "completed", Preview = "a" }]
            }
        ];
        store.SyncAssistantMessage(second, "s1", isComplete: false);
        store.Items[0].Revision.Should().BeGreaterThan(rev);
    }

    [Fact]
    public void ReconcileAppendFromSession_ShouldAppendUserWithoutId_AndAssignId()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession([Msg("u1", "user", "hi")], "s1");

        var orphan = new SessionMessage
        {
            Role = "user",
            Content = "new question",
            CreatedAt = DateTime.UtcNow
        };
        orphan.Id.Should().BeNullOrEmpty();

        store.ReconcileAppendFromSession(
        [
            Msg("u1", "user", "hi"),
            orphan,
        ], "s1");

        store.Items.Should().HaveCount(2);
        store.Items[1].Kind.Should().Be(TimelineItemKind.User);
        store.Items[1].Message!.Content.Should().Be("new question");
        orphan.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ReconcileAppendFromSession_ShouldAppendMissingUserWithoutRebuild()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession([Msg("u1", "user", "hi")], "s1");
        var firstRef = store.Items[0];

        store.ReconcileAppendFromSession(
        [
            Msg("u1", "user", "hi"),
            Msg("u2", "user", "again"),
        ], "s1");

        store.Items.Should().HaveCount(2);
        ReferenceEquals(store.Items[0], firstRef).Should().BeTrue();
        store.Items[1].Key.Should().Be("u2");
        store.Items[1].Message!.Content.Should().Be("again");
    }
}
