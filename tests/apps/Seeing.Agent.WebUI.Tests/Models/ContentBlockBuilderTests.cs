using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Tests.Models;

/// <summary>分段内容块构建：Id 在 Loop 内唯一且跨次构建稳定。</summary>
public class ContentBlockBuilderTests
{
    private static MessageViewModel Assistant(string id, int step, string content, bool complete = true)
        => new() { Id = id, Role = "assistant", Step = step, Content = content, IsComplete = complete };

    [Fact]
    public void BuildLoopSegments_ShouldOrderByStep()
        => ContentBlockBuilder.BuildLoopSegments([
                Assistant("a2", 1, "第二步"), Assistant("a1", 0, "第一步")])
            .Select(s => s.Message.Id).Should().Equal("a1", "a2");

    [Fact]
    public void BuildLoopSegments_BlockIdsShouldBeUniqueAcrossMessages()
    {
        var ids = ContentBlockBuilder.BuildLoopSegments([
                Assistant("a1", 0, "第一步"), Assistant("a2", 1, "第二步")])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void BuildLoopSegments_DuplicateMessageId_BlockIdsShouldStillBeUnique()
    {
        var ids = ContentBlockBuilder.BuildLoopSegments([
                Assistant("same", 0, "一"), Assistant("same", 1, "二")])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void BuildLoopSegments_RepeatedCalls_BlockIdsShouldBeStable()
    {
        MessageViewModel[] input = [Assistant("a1", 0, "一"), Assistant("a2", 1, "二")];

        var first = ContentBlockBuilder.BuildLoopSegments(input).SelectMany(s => s.Blocks).Select(b => b.Id).ToList();
        var second = ContentBlockBuilder.BuildLoopSegments(input).SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        second.Should().Equal(first);
    }

    [Fact]
    public void BuildLoopSegments_StreamingToComplete_BlockIdsShouldStayStable()
    {
        var before = ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: false)])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();
        var after = ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: true)])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        after.Should().Equal(before);
    }

    [Fact]
    public void BuildLoopSegments_IncompleteMessage_TextBlockShouldBeStreaming()
        => ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: false)])
            .Single().Blocks.Should().Contain(b => b.Type == ContentBlockType.Text && b.IsStreaming);

    [Fact]
    public void BuildLoopSegments_CompleteMessage_ShouldHaveNoStreamingBlocks()
        => ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x")])
            .Single().Blocks.Should().OnlyContain(b => !b.IsStreaming);
}
