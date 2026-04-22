using System.Collections.Generic;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests
{
    /// <summary>
    /// SessionData 单元测试：验证 SessionData 是否可替代 SessionEntry 的消息操作
    /// </summary>
    public class SessionDataTests
    {
        [Fact]
        public void AddMessage_ShouldAddMessageToMessages()
        {
            // Arrange
            var data = SessionData.Create();
            var msg = SessionMessage.UserMessage("Hello");

            // Act
            data.AddMessage(msg);

            // Assert
            data.Messages.Should().HaveCount(1);
            data.Messages[0].Content.Should().Be("Hello");
            data.Messages[0].Role.Should().Be(MessageRole.User);
        }

        [Fact]
        public void GetMessages_ShouldReturnAllMessagesInOrder()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.SystemMessage("Sys"));
            data.AddMessage(SessionMessage.UserMessage("User"));
            data.AddMessage(SessionMessage.AssistantMessage("Assistant"));

            // Act
            var messages = data.Messages;

            // Assert
            messages.Should().HaveCount(3);
            messages[0].Role.Should().Be(MessageRole.System);
            messages[1].Role.Should().Be(MessageRole.User);
            messages[2].Role.Should().Be(MessageRole.Assistant);
        }

        [Fact]
        public void ClearMessages_ShouldClearAllMessages()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("One"));
            data.AddMessage(SessionMessage.UserMessage("Two"));

            // Act
            data.ClearMessages();

            // Assert
            data.Messages.Should().BeEmpty();
        }

        [Fact]
        public void Clone_ShouldBeIndependentCopy()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.SystemMessage("Sys"));
            data.SetContext("flag", 1);

            var clone = data.Clone();

            // Act: mutate original after clone
            data.AddMessage(SessionMessage.UserMessage("New"));
            data.SetContext("flag", 2);
            data.Context["extra"] = true;

            // Assert: clone should remain unaffected by subsequent mutations to original
            clone.Messages.Should().HaveCount(1);
            clone.Messages[0].Content.Should().Be("Sys");
            clone.GetContext<int>("flag").Should().Be(1);
            clone.Context.Should().NotContainKey("extra");
            data.Messages.Should().HaveCount(2);
        }
    }
}
