using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Services
{
    public class SessionTitleServiceTests
    {
        [Theory]
        [InlineData("Session abc", true)]
        [InlineData("新会话", true)]
        [InlineData("New Session", true)]
        [InlineData("", true)]
        [InlineData("调试生产错误", false)]
        public void IsDefaultTitle_cases(string title, bool expected)
            => SessionTitleService.IsDefaultTitle(title).Should().Be(expected);

        [Fact]
        public void CleanTitle_truncates_to_15_chars_without_ellipsis()
        {
            var raw = "这是一个超过十五个字的超长标题内容继续";
            var cleaned = SessionTitleService.CleanTitle(raw);
            cleaned.Length.Should().BeLessThanOrEqualTo(15);
            cleaned.Should().NotContain("...");
        }

        [Fact]
        public void CleanTitle_takes_first_line_and_strips_quotes()
        {
            SessionTitleService.CleanTitle("\"短标题\"\n第二行").Should().Be("短标题");
        }

        [Fact]
        public void ShouldEnsure_false_when_disabled_or_fork_or_subagent()
        {
            SessionTitleService.ShouldEnsure(
                enabled: false, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: "p",
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.SubAgent, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();
        }

        [Fact]
        public void ShouldEnsure_true_for_default_title_under_10_or_every_10th()
        {
            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeTrue();

            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 2, userMessage: "hi").Should().BeTrue();

            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "已有标题", realUserCount: 10, userMessage: "hi").Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void ShouldEnsure_false_when_user_message_empty_or_whitespace(string userMessage)
        {
            SessionTitleService.ShouldEnsure(
                enabled: true, kind: SessionKind.Root, parentId: null,
                title: "新会话", realUserCount: 1, userMessage: userMessage).Should().BeFalse();
        }

        [Fact]
        public void BuildTitleHistory_merges_consecutive_users_and_skips_tool()
        {
            var session = SessionData.Create();
            session.Messages.Add(
                SessionMessage.UserMessage("<project-instructions>")
                    .WithMetadata(ProjectInstructions.MetadataKeys.ProjectInstructions, true));
            session.Messages.Add(SessionMessage.UserMessage("implement rate limiting"));
            session.Messages.Add(SessionMessage.AssistantMessage("ok"));
            session.Messages.Add(new SessionMessage
            {
                Role = "tool",
                Content = "tool output",
                ToolCallId = "t1"
            });

            var history = SessionTitleService.BuildTitleHistory(session);
            history.Should().HaveCount(2);
            history[0].Role.Should().Be(ChatRole.User);
            history[0].Content.Should().Contain("<project-instructions>");
            history[0].Content.Should().Contain("implement rate limiting");
            history[1].Role.Should().Be(ChatRole.Assistant);
            history[1].Content.Should().Be("ok");
        }

        [Fact]
        public void ShouldWriteTitle_allows_refresh_every_10()
        {
            SessionTitleService.ShouldWriteTitle("新会话", 1).Should().BeTrue();
            SessionTitleService.ShouldWriteTitle("已有标题", 1).Should().BeFalse();
            SessionTitleService.ShouldWriteTitle("已有标题", 10).Should().BeTrue();
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
                    It.IsAny<List<ChatMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("调试生产500错误");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleService(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleService>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "debug 500 errors in production", "provider/model");

            title.Should().Be("调试生产500错误");
            session.Title.Should().Be("调试生产500错误");
            text.Verify(x => x.CompleteAsync(
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(),
                4096,
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
                    It.IsAny<List<ChatMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("问候")
                .Callback(() => session.Title = "我手动改的");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleService(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleService>.Instance);
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

            SessionTitleService.CountIntentionalUserMessages(messages).Should().Be(1);
            SessionTitleService.IsIntentionalUserMessage(messages[0]).Should().BeFalse();
            SessionTitleService.IsIntentionalUserMessage(messages[2]).Should().BeTrue();
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
                    It.IsAny<List<ChatMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("实现限流");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleService(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleService>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "implement rate limiting", "provider/model");

            title.Should().Be("实现限流");
            session.Title.Should().Be("实现限流");
        }

        [Fact]
        public async Task TryEnsureAsync_overwrites_on_every_10th_user_message()
        {
            var session = SessionData.Create();
            session.Title = "旧标题";
            session.Kind = SessionKind.Root;
            for (var i = 0; i < 10; i++)
                session.Messages.Add(SessionMessage.UserMessage($"msg-{i}"));

            var sm = new Mock<ISessionManager>();
            sm.Setup(x => x.Get(session.Id)).Returns(session);
            sm.Setup(x => x.SetTitleAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback<string, string, CancellationToken>((_, t, _) => session.Title = t);

            var text = new Mock<ITextCompletion>();
            text.Setup(x => x.CompleteAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<ChatMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("新主题标题");

            var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
            opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

            var svc = new SessionTitleService(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleService>.Instance);
            var title = await svc.TryEnsureAsync(session.Id, "msg-9", "provider/model");

            title.Should().Be("新主题标题");
            session.Title.Should().Be("新主题标题");
        }
    }
}
