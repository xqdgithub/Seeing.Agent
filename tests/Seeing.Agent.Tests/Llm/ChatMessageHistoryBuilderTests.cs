using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ChatMessageHistoryBuilderTests
{
    [Theory]
    [InlineData(MessageRole.System, ChatRole.System)]
    [InlineData(MessageRole.User, ChatRole.User)]
    [InlineData(MessageRole.Assistant, ChatRole.Assistant)]
    [InlineData(MessageRole.Tool, ChatRole.Tool)]
    public void MapRole_ShouldMapKnownRoles(string role, string expected)
    {
        ChatMessageHistoryBuilder.MapRole(role).Should().Be(expected);
    }

    [Fact]
    public void MapRole_WhenUnknownRole_ShouldMapToUser()
    {
        ChatMessageHistoryBuilder.MapRole("unknown").Should().Be(ChatRole.User);
        ChatMessageHistoryBuilder.MapRole("").Should().Be(ChatRole.User);
    }

    [Fact]
    public void ExtractTextContent_ShouldPreferContent()
    {
        var msg = SessionMessage.UserMessageWithParts(new List<SessionContentPart>
        {
            new() { Type = ContentPartType.Text, Text = "parts 文本" }
        });
        msg.Content = "Content 优先";

        ChatMessageHistoryBuilder.ExtractTextContent(msg).Should().Be("Content 优先");
    }

    [Fact]
    public void ExtractTextContent_ShouldFallbackToPartsText()
    {
        var msg = SessionMessage.UserMessageWithParts(new List<SessionContentPart>
        {
            new() { Type = ContentPartType.Text, Text = "第一段" },
            new() { Type = ContentPartType.Image, Text = null },
            new() { Type = ContentPartType.Text, Text = "第二段" },
        });

        ChatMessageHistoryBuilder.ExtractTextContent(msg).Should().Be("第一段\n第二段");
    }

    [Fact]
    public void ExtractTextContent_WhenEmpty_ShouldReturnEmpty()
    {
        var msg = SessionMessage.UserMessage("");

        ChatMessageHistoryBuilder.ExtractTextContent(msg).Should().BeEmpty();
    }

    [Fact]
    public void BuildHistory_ShouldMapRoles_AndKeepToolMessagesByDefault()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"),
            SessionMessage.ToolMessage("工具结果", "tool-1"),
        };

        var history = ChatMessageHistoryBuilder.BuildHistory(messages);

        history.Should().HaveCount(3);
        history[0].Role.Should().Be(ChatRole.User);
        history[2].Role.Should().Be(ChatRole.Tool);
    }

    [Fact]
    public void BuildHistory_WhenSkipToolMessages_ShouldFilterTool()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("问题一"),
            SessionMessage.ToolMessage("工具结果", "tool-1"),
            SessionMessage.AssistantMessage("回答一"),
        };

        var history = ChatMessageHistoryBuilder.BuildHistory(messages, skipToolMessages: true);

        history.Should().HaveCount(2);
        history.Should().NotContain(m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public void BuildHistory_WhenSkipEmptyContent_ShouldFilterEmpty()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("有内容"),
            SessionMessage.UserMessage(""),
            SessionMessage.AssistantMessage("  "),
        };

        var history = ChatMessageHistoryBuilder.BuildHistory(messages, skipEmptyContent: true);

        history.Should().ContainSingle();
        history[0].Content.Should().Be("有内容");
    }

    [Fact]
    public void BuildHistory_WhenMergeConsecutiveRoles_ShouldMergeAdjacentSameRole()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("问题一"),
            SessionMessage.UserMessage("问题二"),
            SessionMessage.AssistantMessage("回答一"),
        };

        var history = ChatMessageHistoryBuilder.BuildHistory(messages, mergeConsecutiveRoles: true);

        history.Should().HaveCount(2);
        history[0].Content.Should().Be("问题一\n问题二");
        history[1].Role.Should().Be(ChatRole.Assistant);
    }
}
