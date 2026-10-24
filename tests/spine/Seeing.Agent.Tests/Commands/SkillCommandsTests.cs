using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Commands;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// Skill 命令自包含会话操作测试：命令直接写入本次 user 消息，宿主不做写同步
/// </summary>
public class SkillCommandsTests
{
    private static SkillCatalogCore CreateCatalog(SkillInfo skill)
    {
        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        catalog.RegisterAsync(skill, "test").GetAwaiter().GetResult().Should().BeTrue();
        return catalog;
    }

    /// <summary>命令执行期解析目录所需的最小服务容器。</summary>
    private static IServiceProvider BuildServices(ISkillCatalog catalog) =>
        new ServiceCollection().AddSingleton(catalog).BuildServiceProvider();

    private static Mock<ISessionManager> CreateSessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        // 命令改为经 UpdateSessionAsync 持久化（走 SessionManager API）
        mock.Setup(m => m.UpdateSessionAsync(
                session.Id,
                It.IsAny<Action<SessionData>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Action<SessionData>, CancellationToken>((_, action, _) => action(session))
            .ReturnsAsync(session);
        return mock;
    }

    [Fact]
    public async Task LoadSkill_WhenExpanded_ShouldRewriteMatchingUserMessage()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/skill demo arg"));
        var skill = new SkillInfo
        {
            Name = "demo",
            Description = "d",
            Content = "skill $1 content"
        };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new SkillLoadCommand(catalog, sessionManager.Object);

        // Act
        var result = await command.ExecuteAsync(
            new CommandContext { SessionId = "s1", Input = "/skill demo arg", Arguments = "demo arg" },
            TestContext.Current.CancellationToken);

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue("命令修改了消息内容，UI 需刷新展示");
        result.RemoveCommandMessage.Should().BeFalse("命令声明保留命令消息（内容已替换为展开结果）");
        session.Messages[^1].Content.Should().Contain("skill arg content");
    }

    [Fact]
    public async Task LoadSkill_WhenInstructionMessageFollowsCommand_ShouldRewriteCommandNotInstruction()
    {
        // 回归：新架构下用户命令先落盘、项目指令随后注入，命令消息不再是末条。
        // 命令必须按原文定位目标消息，不得盲改末条（否则改坏指令消息且命令文本残留透传 LLM）。
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/skill demo arg"));
        session.AddMessage(SessionMessage.UserMessage("<project-instructions>...</project-instructions>"));

        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "skill $1 content" };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new SkillLoadCommand(catalog, sessionManager.Object);

        // Act
        var result = await command.ExecuteAsync(
            new CommandContext { SessionId = "s1", Input = "/skill demo arg", Arguments = "demo arg" },
            TestContext.Current.CancellationToken);

        // Assert：命令消息被替换，指令消息保持不变，命令文本不残留
        result.Success.Should().BeTrue();
        session.Messages.Should().Contain(m => m.Content.Contains("skill arg content"),
            "命令消息应被替换为技能展开内容");
        session.Messages.Should().Contain(m => m.Content.Contains("project-instructions"),
            "指令消息不得被误改");
        session.Messages.Should().NotContain(m => m.Content == "/skill demo arg",
            "命令文本不得残留（否则透传 LLM）");
    }

    [Fact]
    public async Task LoadSkill_WhenLastMessageNotUser_ShouldFailExplicitly()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.AssistantMessage("old"));
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "content" };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new SkillLoadCommand(catalog, sessionManager.Object);

        // Act
        var result = await command.ExecuteAsync(
            new CommandContext { SessionId = "s1", Input = "/skill demo arg", Arguments = "demo arg" },
            TestContext.Current.CancellationToken);

        // Assert：无匹配命令消息时明确报错（修复假成功），且不修改消息
        result.Success.Should().BeFalse();
        session.Messages[^1].Content.Should().Be("old");
    }

    [Fact]
    public async Task DynamicSkillCommand_WhenExpanded_ShouldRewriteMatchingUserMessage()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/demo arg"));
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "skill $1 content" };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new DynamicSkillCommand(sessionManager.Object, "demo");

        // Act
        var result = await command.ExecuteAsync(
            new CommandContext
            {
                SessionId = "s1",
                Input = "/demo arg",
                Arguments = "arg",
                Services = BuildServices(catalog)
            },
            TestContext.Current.CancellationToken);

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue();
        result.RemoveCommandMessage.Should().BeFalse("命令声明保留命令消息（内容已替换为展开结果）");
        session.Messages[^1].Content.Should().Contain("skill arg content");
    }

    [Fact]
    public async Task DynamicSkillCommand_WhenInstructionMessageFollowsCommand_ShouldRewriteCommandNotInstruction()
    {
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/demo arg"));
        session.AddMessage(SessionMessage.UserMessage("<project-instructions>...</project-instructions>"));

        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "skill $1 content" };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new DynamicSkillCommand(sessionManager.Object, "demo");

        var result = await command.ExecuteAsync(
            new CommandContext
            {
                SessionId = "s1",
                Input = "/demo arg",
                Arguments = "arg",
                Services = BuildServices(catalog)
            },
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        session.Messages.Should().Contain(m => m.Content.Contains("skill arg content"));
        session.Messages.Should().Contain(m => m.Content.Contains("project-instructions"));
        session.Messages.Should().NotContain(m => m.Content == "/demo arg");
    }

    [Fact]
    public async Task LoadSkill_WhenNoMessages_ShouldFailWithoutThrowing()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        var skill = new SkillInfo { Name = "demo", Description = "d", Content = "content" };
        var catalog = CreateCatalog(skill);
        var sessionManager = CreateSessionManager(session);
        var command = new SkillLoadCommand(catalog, sessionManager.Object);

        // Act
        var result = await command.ExecuteAsync(
            new CommandContext { SessionId = "s1", Input = "/skill demo arg", Arguments = "demo arg" },
            TestContext.Current.CancellationToken);

        // Assert：无消息时明确报错（修复假成功）
        result.Success.Should().BeFalse();
    }
}
