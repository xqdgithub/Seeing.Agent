using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.App.Commands;
using Seeing.Agent.Skills;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// Skill 命令自包含会话操作测试：命令直接写入本次 user 消息，宿主不做写同步
/// </summary>
public class SkillCommandsTests
{
    private static SkillManager CreateSkillManager(SkillInfo skill)
    {
        var manager = new SkillManager(NullLogger<SkillManager>.Instance);
        manager.RegisterSkill(skill);
        return manager;
    }

    private static Mock<ISessionManager> CreateSessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        return mock;
    }

    [Fact]
    public async Task LoadSkill_WhenExpanded_ShouldRewriteLastUserMessage()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/skill demo"));
        var skill = new SkillInfo
        {
            Name = "demo",
            Description = "d",
            Content = "skill $1 content"
        };
        var skillManager = CreateSkillManager(skill);
        var sessionManager = CreateSessionManager(session);
        var commands = new SkillCommands(skillManager, sessionManager.Object);

        // Act
        var result = await commands.LoadSkill(
            new CommandContext { SessionId = "s1", Arguments = "demo arg" });

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue("命令修改了消息内容，UI 需刷新展示");
        result.RemoveCommandMessage.Should().BeFalse("命令声明保留命令消息（内容已替换为展开结果）");
        session.Messages[^1].Content.Should().Contain("skill arg content");
    }

    [Fact]
    public async Task LoadSkill_WhenLastMessageNotUser_ShouldNotRewrite()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.AssistantMessage("old"));
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "content" };
        var skillManager = CreateSkillManager(skill);
        var sessionManager = CreateSessionManager(session);
        var commands = new SkillCommands(skillManager, sessionManager.Object);

        // Act
        await commands.LoadSkill(new CommandContext { SessionId = "s1", Arguments = "demo arg" });

        // Assert
        session.Messages[^1].Content.Should().Be("old");
    }

    [Fact]
    public async Task DynamicSkillCommand_WhenExpanded_ShouldRewriteLastUserMessage()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/demo"));
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "skill $1 content" };
        var sessionManager = CreateSessionManager(session);
        var command = new DynamicSkillCommand(sessionManager.Object, skill);

        // Act
        var result = await command.ExecuteAsync(new CommandContext { SessionId = "s1", Arguments = "arg" });

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue();
        result.RemoveCommandMessage.Should().BeFalse("命令声明保留命令消息（内容已替换为展开结果）");
        session.Messages[^1].Content.Should().Contain("skill arg content");
    }

    [Fact]
    public async Task LoadSkill_WhenNoMessages_ShouldNotThrow()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "content" };
        var skillManager = CreateSkillManager(skill);
        var sessionManager = CreateSessionManager(session);
        var commands = new SkillCommands(skillManager, sessionManager.Object);

        // Act
        var result = await commands.LoadSkill(
            new CommandContext { SessionId = "s1", Arguments = "demo arg" });

        // Assert
        result.Success.Should().BeTrue();
    }
}