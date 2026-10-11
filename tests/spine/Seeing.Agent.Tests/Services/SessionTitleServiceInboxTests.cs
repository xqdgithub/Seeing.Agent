using FluentAssertions;
using Seeing.Agent.Core.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Services
{
    public class SessionTitleServiceInboxTests
    {
        [Theory]
        [InlineData("system")]
        [InlineData("assistant")]
        [InlineData("none")]
        [InlineData("System")]
        public void IsIntentionalUserMessage_excludes_non_user_inbox_presentation(string presentation)
        {
            var message = SessionMessage.UserMessage("inbox message")
                .WithMetadata("inbox.presentation", presentation);

            SessionTitleService.IsIntentionalUserMessage(message).Should().BeFalse();
        }

        [Fact]
        public void IsIntentionalUserMessage_includes_user_inbox_presentation()
        {
            var message = SessionMessage.UserMessage("inbox message")
                .WithMetadata("inbox.presentation", "user");

            SessionTitleService.IsIntentionalUserMessage(message).Should().BeTrue();
        }

        [Fact]
        public void IsIntentionalUserMessage_includes_message_without_inbox_key()
        {
            var message = SessionMessage.UserMessage("plain user message");

            SessionTitleService.IsIntentionalUserMessage(message).Should().BeTrue();
        }

        [Fact]
        public void IsIntentionalUserMessage_still_excludes_synthetic()
        {
            var message = SessionMessage.UserMessage("synthetic")
                .WithMetadata("synthetic", true);

            SessionTitleService.IsIntentionalUserMessage(message).Should().BeFalse();
        }
    }
}
