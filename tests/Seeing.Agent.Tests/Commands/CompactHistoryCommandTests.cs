using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.App.Commands.BuiltIn;
using Seeing.Agent.Compression;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// /compact 命令测试
/// </summary>
public class CompactHistoryCommandTests
{
    [Fact]
    public async Task CompactHistory_ShouldReturnNeedsRefreshAndNotContinue()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        session.Messages.Add(SessionMessage.AssistantMessage("b"));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult("摘要文本", new[] { SessionMessage.UserMessage("a") }, 50));

        var compression = new CompressionService(summarizer.Object, sessionManager.Object, new CompressionOptions());
        var commands = new BuiltInCommands(sessionManager.Object, Mock.Of<ICommandRegistry>(), compression);

        // Act
        var result = await commands.CompactHistory(new CommandContext
        {
            SessionId = session.Id,
            Arguments = ""
        });

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue();
        result.ShouldContinue.Should().BeFalse();
    }
}