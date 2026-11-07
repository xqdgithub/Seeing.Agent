using FluentAssertions;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>流式渲染策略：进行中跳过 Markdown，完成后使用 Markdown。</summary>
public class StreamingRenderTests
{
    [Fact]
    public void ShouldUseMarkdown_StreamingText_ShouldBeFalse()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateText("x", 0, isComplete: false))
            .Should().BeFalse();

    [Fact]
    public void ShouldUseMarkdown_CompleteText_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateText("x", 0, isComplete: true))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_StreamingReasoning_ShouldBeFalse()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateReasoning("x", 0, isComplete: false))
            .Should().BeFalse();

    [Fact]
    public void ShouldUseMarkdown_CompleteReasoning_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateReasoning("x", 0, isComplete: true))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_ToolCall_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(
                ContentBlock.CreateToolCall(new() { Id = "t1", Name = "bash", Status = "success" }, 0))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_NullBlock_ShouldBeFalse()
        => RenderMarkdownPolicy.ShouldUseMarkdown(null!).Should().BeFalse();
}
