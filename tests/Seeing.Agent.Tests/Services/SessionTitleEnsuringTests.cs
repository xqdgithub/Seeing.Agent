using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using Seeing.Agent.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Services
{
    public class SessionTitleEnsuringTests
    {
        [Theory]
        [InlineData("Session abc", true)]
        [InlineData("新会话", true)]
        [InlineData("New Session", true)]
        [InlineData("", true)]
        [InlineData("调试生产错误", false)]
        public void IsDefaultTitle_cases(string title, bool expected)
            => SessionTitleEnsuring.IsDefaultTitle(title).Should().Be(expected);

        [Fact]
        public void CleanTitle_truncates_to_15_chars_without_ellipsis()
        {
            var raw = "这是一个超过十五个字的超长标题内容继续";
            var cleaned = SessionTitleEnsuring.CleanTitle(raw);
            cleaned.Length.Should().BeLessThanOrEqualTo(15);
            cleaned.Should().NotContain("...");
        }

        [Fact]
        public void CleanTitle_takes_first_line_and_strips_quotes()
        {
            SessionTitleEnsuring.CleanTitle("\"短标题\"\n第二行").Should().Be("短标题");
        }

        [Fact]
        public void ShouldEnsure_false_when_disabled_or_fork_or_not_default_or_count_not_1()
        {
            SessionTitleEnsuring.ShouldEnsure(
                enabled: false, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: "p",
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.SubAgent, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "已有标题", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 2, userMessage: "hi").Should().BeFalse();

            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void ShouldEnsure_false_when_user_message_empty_or_whitespace(string userMessage)
        {
            SessionTitleEnsuring.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: userMessage).Should().BeFalse();
        }

        [Fact]
        public async Task TryEnsureAsync_writes_title_when_conditions_met()
        {
            var session = SessionData.Create();
            session.Title = "新会话";
            session.Kind = SessionKind.Root;
            session.Messages.Add(SessionMessage.UserMessage("debug 500 errors in production"));

            var sm = new Mock<ISessionManager>();
            sm.Setup(x => x.Get(session.Id)).Returns(session);
            sm.Setup(x => x.SetTitleAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback<string, string, CancellationToken>((_, t, _) => session.Title = t);

            var text = new Mock<ITextCompletion>();
            text.Setup(x => x.CompleteAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("调试生产500错误");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleEnsuring(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleEnsuring>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "debug 500 errors in production", "provider/model");

            title.Should().Be("调试生产500错误");
            session.Title.Should().Be("调试生产500错误");
            text.Verify(x => x.CompleteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                32,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task TryEnsureAsync_skips_when_user_already_renamed()
        {
            var session = SessionData.Create();
            session.Title = "新会话";
            session.Kind = SessionKind.Root;
            session.Messages.Add(SessionMessage.UserMessage("hi"));

            var sm = new Mock<ISessionManager>();
            sm.Setup(x => x.Get(session.Id)).Returns(session);

            var text = new Mock<ITextCompletion>();
            text.Setup(x => x.CompleteAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("问候")
                .Callback(() => session.Title = "我手动改的");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleEnsuring(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleEnsuring>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "hi", "m");

            title.Should().BeNull();
            sm.Verify(x => x.SetTitleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public void CountIntentionalUserMessages_excludes_project_instructions_and_synthetic()
        {
            var messages = new List<SessionMessage>
            {
                SessionMessage.UserMessage("injected AGENTS.md")
                    .WithMetadata(ProjectInstructions.MetadataKeys.ProjectInstructions, true),
                SessionMessage.UserMessage("synthetic note")
                    .WithMetadata("synthetic", true),
                SessionMessage.UserMessage("real user intent"),
                SessionMessage.AssistantMessage("reply"),
            };

            SessionTitleEnsuring.CountIntentionalUserMessages(messages).Should().Be(1);
            SessionTitleEnsuring.IsIntentionalUserMessage(messages[0]).Should().BeFalse();
            SessionTitleEnsuring.IsIntentionalUserMessage(messages[2]).Should().BeTrue();
        }

        [Fact]
        public async Task TryEnsureAsync_writes_title_when_only_extra_messages_are_injections()
        {
            var session = SessionData.Create();
            session.Title = "新会话";
            session.Kind = SessionKind.Root;
            session.Messages.Add(
                SessionMessage.UserMessage("<project-instructions>")
                    .WithMetadata(ProjectInstructions.MetadataKeys.ProjectInstructions, true));
            session.Messages.Add(SessionMessage.UserMessage("fix rate limiting"));

            var sm = new Mock<ISessionManager>();
            sm.Setup(x => x.Get(session.Id)).Returns(session);
            sm.Setup(x => x.SetTitleAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback<string, string, CancellationToken>((_, t, _) => session.Title = t);

            var text = new Mock<ITextCompletion>();
            text.Setup(x => x.CompleteAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("实现限流");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleEnsuring(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleEnsuring>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "implement rate limiting", "provider/model");

            title.Should().Be("实现限流");
            session.Title.Should().Be("实现限流");
        }
    }
}
