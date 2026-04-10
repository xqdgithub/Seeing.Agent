using FluentAssertions;
using Seeing.Agent.Llm;
using Xunit;
using LlmChatMessage = Seeing.Agent.Llm.ChatMessage;

namespace Seeing.Agent.Tests.Llm;

public class ChatMessageMultimodalTests
{
    [Fact]
    public void LlmChatMessage_GetEffectiveParts_WhenPartsEmpty_UsesContentAsText()
    {
        // Arrange
        var msg = new LlmChatMessage { Role = ChatRole.User, Content = "hello" };

        // Act
        var parts = msg.GetEffectiveParts();

        // Assert
        parts.Should().HaveCount(1);
        parts[0].Type.Should().Be(ChatContentPart.KindText);
        parts[0].Text.Should().Be("hello");
    }

    [Fact]
    public void LlmChatMessage_GetEffectiveParts_WhenPartsSet_IgnoresContent()
    {
        // Arrange
        var msg = new LlmChatMessage
        {
            Role = ChatRole.User,
            Content = "ignored",
            Parts =
            [
                ChatContentPart.CreateText("hi"),
                ChatContentPart.CreateImageFromUrl("https://example.com/a.png")
            ]
        };

        // Act
        var parts = msg.GetEffectiveParts();

        // Assert
        parts.Should().HaveCount(2);
        parts[0].Text.Should().Be("hi");
        parts[1].Url.Should().Be("https://example.com/a.png");
    }

    [Fact]
    public void ChatMessage_GetEffectiveParts_WithParts_ReturnsParts()
    {
        // Arrange
        var msg = new ChatMessage
        {
            Role = ChatRole.User,
            Parts = [ChatContentPart.CreateFileFromBase64("YQ==", "application/pdf", "a.pdf")]
        };

        // Act
        var parts = msg.GetEffectiveParts();

        // Assert
        parts.Should().ContainSingle();
        parts[0].Type.Should().Be(ChatContentPart.KindFile);
        parts[0].MimeType.Should().Be("application/pdf");
    }
}
